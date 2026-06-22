// whcli — Windhawk CLI / migration tool (Native AOT)
using System.Runtime.InteropServices;
using System.Text.Json;
using Whcli;

const string Version = "0.2.0";

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* redirected console */ }

try
{
    return Run(args);
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

static int Run(string[] args)
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        PrintHelp();
        return 0;
    }
    if (args[0] is "--version" or "-v")
    {
        Console.WriteLine(Version);
        return 0;
    }

    string cmd = args[0];
    string[] rest = args[1..];

    return cmd switch
    {
        "export" => CmdExport(rest),
        "apply" => CmdApply(rest),
        "list" => CmdList(rest),
        "install" => CmdInstall(rest),
        "update" => CmdUpdate(rest),
        "uninstall" or "remove" => CmdUninstall(rest),
        "enable" => CmdEnableDisable(rest, true),
        "disable" => CmdEnableDisable(rest, false),
        "set-setting" => CmdSetSetting(rest),
        "catalog" or "search" => CmdCatalog(rest),
        _ => Unknown(cmd),
    };
}

static int Unknown(string cmd)
{
    Console.Error.WriteLine($"whcli: unknown command '{cmd}' (try --help)");
    return 2;
}

// ---------------------------------------------------------------- export
static int CmdExport(string[] a)
{
    string? from = Opt(a, "--from");
    string outPath = Opt(a, "--out") ?? "profile.json";
    string bundle = ResolveBundle(Opt(a, "--bundle"));
    bool noBundle = Flag(a, "--no-bundle");

    var install = from is not null
        ? WindhawkInstall.FromRoot(from)
        : WindhawkInstall.AutoDetectInstalled()
            ?? throw new InvalidOperationException("Couldn't auto-detect an installed Windhawk; pass --from <root>.");

    Console.WriteLine($"Exporting from {(install.Portable ? install.RootPath : "registry " + install.RegSubKey)}");

    var store = install.OpenStore();
    var profile = new Profile
    {
        WindhawkVersion = Path.GetFileName(install.EnginePath),
        ExportedFrom = install.Portable ? install.RootPath : install.RegSubKey,
        App = AppConfig.ReadSection(install, "Settings"),
        Engine = AppConfig.ReadSection(install, "Engine\\Settings"),
    };

    if (!noBundle) Directory.CreateDirectory(bundle);

    foreach (var id in store.GetInstalledModIds().OrderBy(x => x, StringComparer.Ordinal))
    {
        var cfg = store.GetModConfig(id)!;
        var settings = store.GetModSettings(id);
        profile.Mods.Add(new ProfileMod { Id = id, Version = cfg.Version, Disabled = cfg.Disabled, Settings = settings });

        string state = cfg.Disabled ? "disabled" : "enabled";
        Console.WriteLine($"  + {id} v{cfg.Version} ({state}, {settings.Count} settings)");

        if (!noBundle)
        {
            var srcPath = Path.Combine(install.ModsSourcePath, id + ".wh.cpp");
            if (File.Exists(srcPath))
                File.Copy(srcPath, Path.Combine(bundle, id + ".wh.cpp"), true);
            else
                Console.Error.WriteLine($"    warning: source not found for {id} at {srcPath} (won't be bundled)");
        }
    }

    profile.Save(outPath);
    Console.WriteLine($"Wrote {profile.Mods.Count} mods to {outPath}" + (noBundle ? "" : $"; sources bundled in {bundle}"));
    return 0;
}

// ---------------------------------------------------------------- apply
static int CmdApply(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1) throw new InvalidOperationException("usage: whcli apply <profile.json> [--root <dir>]");
    var profile = Profile.Load(pos[0]);

    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    var sources = new SourceProvider(ResolveBundle(Opt(a, "--bundle")), allowFetch: !Flag(a, "--no-fetch"));
    var compiler = new ModCompiler(install, Arm64Enabled(a));
    bool applyApp = Flag(a, "--app-settings");

    Console.WriteLine($"Applying {profile.Mods.Count} mods to {install.RootPath}");
    int ok = 0, fail = 0;
    foreach (var m in profile.Mods)
    {
        try
        {
            InstallMod(install, store, sources, compiler, m.Id, m.Version, m.Disabled, m.Settings);
            Console.WriteLine($"  ok  {m.Id} v{m.Version} ({(m.Disabled ? "disabled" : "enabled")}, {m.Settings.Count} settings)");
            ok++;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  FAIL {m.Id}: {e.Message}");
            fail++;
        }
    }

    if (applyApp && profile.App is { Count: > 0 })
    {
        AppConfig.WriteSection(install, "Settings", profile.App);
        Console.WriteLine($"Applied {profile.App.Count} app settings.");
    }
    else if (profile.App is { Count: > 0 })
    {
        Console.WriteLine($"({profile.App.Count} app settings captured in profile; re-run with --app-settings to apply them.)");
    }
    if (profile.Engine is { Count: > 0 })
        Console.WriteLine($"({profile.Engine.Count} engine settings captured in profile; not auto-applied.)");

    Console.WriteLine($"Done: {ok} ok, {fail} failed. Windhawk applies changes automatically (or restart it).");
    return fail == 0 ? 0 : 1;
}

// ---------------------------------------------------------------- install
static int CmdInstall(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1) throw new InvalidOperationException("usage: whcli install <mod-id> [--version v] [--root <dir>] [--disabled]");
    string id = pos[0];

    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    var sources = new SourceProvider(ResolveBundle(Opt(a, "--bundle")), allowFetch: !Flag(a, "--no-fetch"));
    var compiler = new ModCompiler(install, Arm64Enabled(a));

    // No explicit settings -> the mod uses the defaults declared in its source.
    InstallMod(install, store, sources, compiler, id, Opt(a, "--version"), Flag(a, "--disabled"), settings: null);
    Console.WriteLine($"Installed {id}.");
    return 0;
}

/// <summary>Shared install/apply core: source -> compile -> config -> settings -> source cache.</summary>
static void InstallMod(WindhawkInstall install, IModStore store, SourceProvider sources, ModCompiler compiler,
                       string id, string? pinnedVersion, bool disabled, IReadOnlyDictionary<string, SettingValue>? settings)
{
    var (source, _) = sources.Get(id, pinnedVersion);
    var meta = ModMetadata.Parse(source);
    if (meta.Id != id)
        throw new InvalidOperationException($"source @id '{meta.Id}' does not match '{id}'");

    string dll = compiler.CompileMod(id, meta.Version, meta.Include, source, meta.Architecture, meta.CompilerOptions);

    store.SetModConfig(id, new ModConfig
    {
        LibraryFileName = dll,
        Disabled = disabled,
        Include = meta.Include,
        Exclude = meta.Exclude,
        Architecture = meta.Architecture,
        Version = meta.Version,
    });

    if (settings is not null)
        store.SetModSettings(id, settings);

    // Cache the source so the UI can display/recompile the mod.
    Directory.CreateDirectory(install.ModsSourcePath);
    File.WriteAllText(Path.Combine(install.ModsSourcePath, id + ".wh.cpp"), source);

    // Drop stale builds from previous installs so the mods folder stays clean.
    compiler.CleanupOldFiles(id, meta.Architecture, dll);
}

// ---------------------------------------------------------------- update
static int CmdUpdate(string[] a)
{
    var install = ResolveTarget(Opt(a, "--root"));
    bool dryRun = Flag(a, "--dry-run");
    bool noReload = Flag(a, "--no-reload"); // skip the disable/enable dance (engine not running yet)
    var store = install.OpenStore();

    var latest = FetchCatalogVersions();

    var outdated = new List<(string id, string from, string to, bool enabled)>();
    foreach (var id in store.GetInstalledModIds())
    {
        var cfg = store.GetModConfig(id);
        if (cfg is null) continue;
        if (!latest.TryGetValue(id, out var newVer)) continue; // local-only mod, not in catalog
        if (CompareVersions(newVer, cfg.Version) > 0)
            outdated.Add((id, cfg.Version, newVer, !cfg.Disabled));
    }

    if (outdated.Count == 0) { Console.WriteLine("All mods up to date."); return 0; }

    Console.WriteLine($"{outdated.Count} update(s) available:");
    foreach (var o in outdated)
        Console.WriteLine($"  {o.id}  {o.from} -> {o.to}" + (o.enabled ? "" : " (disabled)"));
    if (dryRun) return 0;

    var compiler = new ModCompiler(install, Arm64Enabled(a));
    int ok = 0, fail = 0;
    foreach (var o in outdated)
    {
        try
        {
            UpdateOne(install, store, compiler, o.id, o.enabled, noReload);
            Console.WriteLine($"  updated {o.id} -> {o.to}");
            ok++;
        }
        catch (Exception e) { Console.Error.WriteLine($"  FAIL {o.id}: {e.Message}"); fail++; }
    }
    Console.WriteLine($"Done: {ok} updated, {fail} failed.");
    return fail == 0 ? 0 : 1;
}

/// <summary>Update one mod, optionally doing the disable -> swap -> enable dance for a live engine.</summary>
static void UpdateOne(WindhawkInstall install, IModStore store, ModCompiler compiler,
                      string id, bool enabled, bool noReload)
{
    bool dance = enabled && !noReload;
    if (dance)
    {
        store.EnableMod(id, false);          // tell the live engine to unload the old DLL
        System.Threading.Thread.Sleep(800);  // give it a moment to release the file
    }

    try
    {
        string source = FetchLatestSource(id);
        var meta = ModMetadata.Parse(source);
        if (meta.Id != id)
            throw new InvalidOperationException($"source @id '{meta.Id}' != '{id}'");

        string dll = compiler.CompileMod(id, meta.Version, meta.Include, source, meta.Architecture, meta.CompilerOptions);
        store.SetModConfig(id, new ModConfig
        {
            LibraryFileName = dll,
            Disabled = dance ? true : !enabled, // keep disabled during the swap when dancing
            Include = meta.Include,
            Exclude = meta.Exclude,
            Architecture = meta.Architecture,
            Version = meta.Version,
        });
        // Settings are intentionally preserved (not touched) across an update.
        Directory.CreateDirectory(install.ModsSourcePath);
        File.WriteAllText(Path.Combine(install.ModsSourcePath, id + ".wh.cpp"), source);
        compiler.CleanupOldFiles(id, meta.Architecture, dll);
    }
    finally
    {
        // Restore the original enabled state. For a live update this is the "enable after"
        // step that makes the engine load the new DLL; on failure it restores the old version.
        store.EnableMod(id, enabled);
        if (dance) System.Threading.Thread.Sleep(150);
    }
}

static string FetchLatestSource(string id)
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
    return http.GetStringAsync($"https://mods.windhawk.net/mods/{id}.wh.cpp").GetAwaiter().GetResult();
}

static Dictionary<string, string> FetchCatalogVersions()
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
    var json = http.GetStringAsync("https://mods.windhawk.net/catalog.json").GetAwaiter().GetResult();
    using var doc = JsonDocument.Parse(json);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var m in doc.RootElement.GetProperty("mods").EnumerateObject())
        result[m.Name] = Str(m.Value.GetProperty("metadata"), "version");
    return result;
}

/// <summary>Compare dotted numeric versions (e.g. 1.3.10 vs 1.3.2). Returns -1/0/1.</summary>
static int CompareVersions(string a, string b)
{
    var pa = a.Split('.');
    var pb = b.Split('.');
    int n = Math.Max(pa.Length, pb.Length);
    for (int i = 0; i < n; i++)
    {
        string sa = i < pa.Length ? pa[i] : "0";
        string sb = i < pb.Length ? pb[i] : "0";
        if (int.TryParse(sa, out var ia) && int.TryParse(sb, out var ib))
        {
            if (ia != ib) return ia < ib ? -1 : 1;
        }
        else
        {
            int c = string.CompareOrdinal(sa, sb);
            if (c != 0) return c < 0 ? -1 : 1;
        }
    }
    return 0;
}

// ---------------------------------------------------------------- list / enable / disable / uninstall / set-setting
static int CmdList(string[] a)
{
    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    var ids = store.GetInstalledModIds().OrderBy(x => x, StringComparer.Ordinal).ToList();
    if (ids.Count == 0) { Console.WriteLine("(no mods installed)"); return 0; }
    foreach (var id in ids)
    {
        var cfg = store.GetModConfig(id)!;
        Console.WriteLine($"{(cfg.Disabled ? "[ ]" : "[x]")} {id,-36} v{cfg.Version}");
    }
    return 0;
}

static int CmdEnableDisable(string[] a, bool enable)
{
    var pos = Positionals(a);
    if (pos.Count < 1) throw new InvalidOperationException($"usage: whcli {(enable ? "enable" : "disable")} <mod-id> [--root <dir>]");
    var store = ResolveTarget(Opt(a, "--root")).OpenStore();
    store.EnableMod(pos[0], enable);
    Console.WriteLine($"{(enable ? "Enabled" : "Disabled")} {pos[0]}.");
    return 0;
}

static int CmdUninstall(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1) throw new InvalidOperationException("usage: whcli uninstall <mod-id> [--root <dir>]");
    var install = ResolveTarget(Opt(a, "--root"));
    install.OpenStore().DeleteMod(pos[0]);
    var src = Path.Combine(install.ModsSourcePath, pos[0] + ".wh.cpp");
    if (File.Exists(src)) File.Delete(src);
    ModFiles.DeleteOld(install.EngineModsPath, pos[0], ModFiles.AllSubfolders(Arm64Enabled(a)), null);
    Console.WriteLine($"Uninstalled {pos[0]}.");
    return 0;
}

static int CmdSetSetting(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 3) throw new InvalidOperationException("usage: whcli set-setting <mod-id> <name> <value> [--root <dir>]");
    string id = pos[0], name = pos[1], raw = pos[2];
    var store = ResolveTarget(Opt(a, "--root")).OpenStore();

    var settings = store.GetModSettings(id);
    settings[name] = int.TryParse(raw, out var i) ? SettingValue.Of(i) : SettingValue.Of(raw);
    store.SetModSettings(id, settings);
    Console.WriteLine($"Set {id}.{name} = {raw}");
    return 0;
}

// ---------------------------------------------------------------- catalog / search
static int CmdCatalog(string[] a)
{
    var pos = Positionals(a);
    string query = pos.Count > 0 ? pos[0] : "";
    bool full = Flag(a, "--full");
    bool idsOnly = Flag(a, "--ids");

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
    // The official mod catalog: { app, mods: { "<id>": { metadata: {...} } } }.
    var json = http.GetStringAsync("https://mods.windhawk.net/catalog.json").GetAwaiter().GetResult();
    using var doc = JsonDocument.Parse(json);

    var rows = new List<(string id, string name, string ver, string author, string desc)>();
    int total = 0;
    foreach (var m in doc.RootElement.GetProperty("mods").EnumerateObject())
    {
        total++;
        var meta = m.Value.GetProperty("metadata");
        string name = Str(meta, "name"), ver = Str(meta, "version"),
               author = Str(meta, "author"), desc = Str(meta, "description");
        if (query.Length > 0)
        {
            string hay = $"{m.Name} {name} {desc} {author}";
            if (!hay.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
        }
        rows.Add((m.Name, name, ver, author, desc));
    }
    rows.Sort((x, y) => string.CompareOrdinal(x.id, y.id));

    foreach (var r in rows)
    {
        if (idsOnly) { Console.WriteLine(r.id); continue; }
        Console.WriteLine($"{r.id}  v{r.ver}" + (r.author.Length > 0 ? $"  ({r.author})" : ""));
        var d = r.desc.Replace("\r", " ").Replace("\n", " ").Trim();
        if (!full && d.Length > 100) d = d[..97] + "...";
        var line = r.name + (d.Length > 0 ? "  —  " + d : "");
        if (line.Length > 0) Console.WriteLine("    " + line);
    }
    Console.Error.WriteLine($"{rows.Count} mod(s)" + (query.Length > 0 ? $" matching '{query}'" : "") + $" of {total} in catalog");
    return 0;
}

static string Str(JsonElement e, string name)
    => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

// ---------------------------------------------------------------- helpers
static WindhawkInstall ResolveTarget(string? rootOpt)
{
    string? root = rootOpt ?? Environment.GetEnvironmentVariable("WHCLI_ROOT");
    if (root is null)
    {
        if (File.Exists("windhawk.ini")) root = ".";
        else throw new InvalidOperationException(
            "No target. Pass --root <dir> or set WHCLI_ROOT (a dir containing windhawk.ini).");
    }
    return WindhawkInstall.FromRoot(root);
}

static string ResolveBundle(string? opt)
    => opt ?? Environment.GetEnvironmentVariable("WHCLI_BUNDLE") ?? "mods";

static bool Arm64Enabled(string[] a)
    => Flag(a, "--arm64") || RuntimeInformation.OSArchitecture == Architecture.Arm64;

static string? Opt(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++)
        if (a[i] == name) return a[i + 1];
    return null;
}

static bool Flag(string[] a, string name) => Array.IndexOf(a, name) >= 0;

static List<string> Positionals(string[] a)
{
    var known = new HashSet<string>(StringComparer.Ordinal)
    { "--from", "--out", "--bundle", "--root", "--version" };
    var result = new List<string>();
    for (int i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--"))
        {
            if (known.Contains(a[i])) i++; // skip its value
            continue;
        }
        result.Add(a[i]);
    }
    return result;
}

static void PrintHelp()
{
    Console.WriteLine($"""
        whcli {Version} — Windhawk CLI / migration tool

        Usage: whcli <command> [options]

        Profiles (migration / provisioning):
          export [--from <root>] [--out profile.json] [--bundle <dir>] [--no-bundle]
                                  Snapshot installed mods+settings to a profile and bundle sources.
          apply <profile.json> [--root <dir>] [--bundle <dir>] [--no-fetch] [--app-settings] [--arm64]
                                  Install+compile+configure every mod in a profile into a target.

        Single mods (target = --root <dir> or $WHCLI_ROOT):
          list
          install <id> [--version v] [--disabled] [--no-fetch] [--arm64]
          update [--dry-run] [--no-reload]   Upgrade outdated mods to catalog latest
          uninstall <id>
          enable <id>
          disable <id>
          set-setting <id> <name> <value>

        Browse the remote catalog (https://mods.windhawk.net/catalog.json):
          catalog [query] [--full] [--ids]    List all mods with names/descriptions
          search  [query]                     Alias for catalog (filter by query)

        Notes:
          * A "target" is a Windhawk root dir containing windhawk.ini (portable build).
          * Sources resolve from --bundle (or $WHCLI_BUNDLE, default ./mods); fetch from
            windhawk-mods is a fallback unless --no-fetch.
        """);
}
