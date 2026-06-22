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
        "self-update" => CmdSelfUpdate(rest),
        "auto-update" => CmdAutoUpdate(rest),
        "uninstall" or "remove" => CmdUninstall(rest),
        "enable" => CmdEnableDisable(rest, true),
        "disable" => CmdEnableDisable(rest, false),
        "tray" => CmdTray(rest),
        "set-setting" => CmdSetSetting(rest),
        "status" or "doctor" => CmdStatus(rest),
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
    bool arm64 = Arm64Enabled(a);
    bool applyApp = Flag(a, "--app-settings");

    Console.WriteLine($"Applying {profile.Mods.Count} mods to {install.RootPath}");
    int ok = 0, fail = 0;
    foreach (var m in profile.Mods)
    {
        try
        {
            InstallMod(install, store, m.Id, m.Version, m.Disabled, m.Settings, arm64);
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

    // No explicit settings -> the mod uses the defaults declared in its source.
    InstallMod(install, store, id, Opt(a, "--version") ?? "", Flag(a, "--disabled"), settings: null, Arm64Enabled(a));
    Console.WriteLine($"Installed {id}.");
    return 0;
}

/// <summary>Shared install/apply core: metadata -> download precompiled DLLs -> config -> settings.</summary>
static void InstallMod(WindhawkInstall install, IModStore store, string id, string version, bool disabled,
                       IReadOnlyDictionary<string, SettingValue>? settings, bool arm64)
{
    if (string.IsNullOrEmpty(version)) version = PrecompiledMods.LatestVersion(id);
    string source = PrecompiledMods.GetSource(id, version);
    var meta = ModMetadata.Parse(source);
    if (meta.Id != id)
        throw new InvalidOperationException($"source @id '{meta.Id}' does not match '{id}'");

    string dll = PrecompiledMods.Download(install.EngineModsPath, id, meta.Version, meta.Architecture, arm64);

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

    // Cache the source for reference (and a possible future local-compile mode).
    Directory.CreateDirectory(install.ModsSourcePath);
    File.WriteAllText(Path.Combine(install.ModsSourcePath, id + ".wh.cpp"), source);

    // Drop stale DLLs from previous installs.
    ModFiles.DeleteOld(install.EngineModsPath, id, ModFiles.SubfoldersFor(meta.Architecture, arm64), dll);
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

    bool arm64 = Arm64Enabled(a);
    int ok = 0, fail = 0;
    foreach (var o in outdated)
    {
        try
        {
            UpdateOne(install, store, o.id, o.to, o.enabled, noReload, arm64);
            Console.WriteLine($"  updated {o.id} -> {o.to}");
            ok++;
        }
        catch (Exception e) { Console.Error.WriteLine($"  FAIL {o.id}: {e.Message}"); fail++; }
    }
    Console.WriteLine($"Done: {ok} updated, {fail} failed.");
    return fail == 0 ? 0 : 1;
}

/// <summary>Update one mod to a version, doing the disable -> swap -> enable dance for a live engine.</summary>
static void UpdateOne(WindhawkInstall install, IModStore store,
                      string id, string version, bool enabled, bool noReload, bool arm64)
{
    bool dance = enabled && !noReload;
    if (dance)
    {
        store.EnableMod(id, false);          // tell the live engine to unload the old DLL
        System.Threading.Thread.Sleep(800);  // give it a moment to release the file
    }

    try
    {
        // settings = null preserves existing per-mod settings across the update.
        InstallMod(install, store, id, version, disabled: !enabled, settings: null, arm64);
    }
    finally
    {
        store.EnableMod(id, enabled); // "enable after" -> engine loads the new DLL
        if (dance) System.Threading.Thread.Sleep(150);
    }
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

// ---------------------------------------------------------------- self-update / auto-update
static int CmdSelfUpdate(string[] a)
{
    var install = ResolveTarget(Opt(a, "--root"));
    bool dryRun = Flag(a, "--dry-run");
    string repo = Opt(a, "--repo") ?? Fork.Repo;

    var settings = AppConfig.ReadSection(install, "Settings");
    string installed = settings.TryGetValue("ForkVersion", out var fv) ? fv.ToString() : "0.0.0";

    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
        var json = http.GetStringAsync($"https://api.github.com/repos/{repo}/releases/latest").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        string latest = (doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "").TrimStart('v', 'V');

        string? exeUrl = null;
        if (doc.RootElement.TryGetProperty("assets", out var assets))
            foreach (var asset in assets.EnumerateArray())
                if ((asset.GetProperty("name").GetString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                { exeUrl = asset.GetProperty("browser_download_url").GetString(); break; }

        if (string.IsNullOrEmpty(latest) || CompareVersions(latest, installed) <= 0)
        {
            Console.WriteLine($"App up to date (installed {installed}, latest {(latest.Length > 0 ? latest : "?")}).");
            return 0;
        }
        Console.WriteLine($"App update available: {installed} -> {latest}");
        if (dryRun) return 0;
        if (exeUrl is null) { Console.Error.WriteLine("  no installer .exe asset in latest release; deferring."); return 0; }

        return DoSelfUpdate(install, exeUrl, latest, settings);
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"  self-update check failed ({e.Message}); deferring.");
        return 0;
    }
}

static int DoSelfUpdate(WindhawkInstall install, string exeUrl, string version, Dictionary<string, SettingValue> settings)
{
    string work = Path.Combine(Path.GetTempPath(), "whxyz-update-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(work);
    string setupExe = Path.Combine(work, "whsetup.exe");

    Console.WriteLine("  downloading update...");
    using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) })
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
        using var resp = http.GetAsync(exeUrl, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        using var fs = File.Create(setupExe);
        resp.Content.CopyToAsync(fs).GetAwaiter().GetResult();
    }

    // The installer is a single self-contained, signed file — verifying it covers the
    // whole embedded payload. Refuse anything not validly signed by our cert.
    if (!Signature.IsSignedBy(setupExe, Fork.SigningCertThumbprint))
    {
        Console.Error.WriteLine("  update is not validly signed by the expected certificate; deferring.");
        try { Directory.Delete(work, true); } catch { }
        return 0;
    }

    // Launch the new installer detached, preserving the current configuration.
    var args = new List<string> { "--silent", "--install-dir", install.RootPath, "--add-defender-exclusion" };
    if (settings.TryGetValue("AutomaticUpdates", out var au) && au.IsInt && au.Int != 0) args.Add("--auto-updates");
    if (settings.TryGetValue("HideTrayIcon", out var ht) && ht.IsInt && ht.Int != 0) args.Add("--no-system-tray");

    var psi = new System.Diagnostics.ProcessStartInfo(setupExe) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var x in args) psi.ArgumentList.Add(x);
    System.Diagnostics.Process.Start(psi);
    Console.WriteLine($"  launched installer for {version}; the service will be replaced and restarted.");
    return 10; // signal: update launched
}

/// <summary>Self-update first; if no app update was launched, update mods.</summary>
static int CmdAutoUpdate(string[] a)
{
    int rc = CmdSelfUpdate(a);
    if (rc == 10) return 10; // app update launched -> the new version will update mods on startup
    return CmdUpdate(a);
}

// ---------------------------------------------------------------- tray (runtime show/hide)
static int CmdTray(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1 || (pos[0] != "show" && pos[0] != "hide"))
        throw new InvalidOperationException("usage: whcli tray <show|hide> [--root <dir>]");
    bool show = pos[0] == "show";
    var install = ResolveTarget(Opt(a, "--root"));

    // HideTrayIcon: 1 = hidden, 0 = shown. Persisted (also affects next startup).
    AppConfig.WriteSection(install, "Settings",
        new Dictionary<string, SettingValue> { ["HideTrayIcon"] = SettingValue.Of(show ? 0 : 1) });

    // Notify the running app to re-read settings so the change applies live.
    var exe = Path.Combine(install.RootPath, "windhawk.exe");
    if (File.Exists(exe))
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-app-settings-changed");
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  (couldn't notify the running app: {e.Message}; change applies on next start)");
        }
    }

    Console.WriteLine($"Tray icon {(show ? "shown" : "hidden")} (HideTrayIcon={(show ? 0 : 1)}).");
    return 0;
}

// ---------------------------------------------------------------- status (connectivity gate)
static int CmdStatus(string[] a)
{
    WindhawkInstall install;
    try { install = ResolveTarget(Opt(a, "--root")); }
    catch (Exception e) { Console.Error.WriteLine("[ ] install: " + e.Message); Console.WriteLine("NOT READY"); return 1; }

    Console.WriteLine($"Install: {install.RootPath}");
    Console.WriteLine($"Mode:    {(install.Portable ? "portable (INI)" : "service (registry " + install.RegSubKey + ")")}");

    bool ok = true;
    void Check(bool pass, string label) { Console.WriteLine($"[{(pass ? "x" : " ")}] {label}"); ok &= pass; }

    // Engine present.
    Check(File.Exists(Path.Combine(install.EnginePath, "64", "windhawk.dll")), $"engine: {install.EnginePath}");

    // Mod runtime libs present (precompiled mods link against these at load time).
    Check(File.Exists(Path.Combine(install.EngineModsPath, "64", "libc++.whl")), $"runtime libs: {install.EngineModsPath}\\64");

    // Storage writable.
    bool storageOk;
    try { Directory.CreateDirectory(install.EngineModsPath); storageOk = true; }
    catch { storageOk = false; }
    Check(storageOk, $"storage writable: {install.EngineModsPath}");

    // Service running (non-portable only).
    if (!install.Portable)
    {
        string svc = Opt(a, "--service") ?? "WindhawkCLI";
        string state = ServiceQuery.State(svc);
        Check(state == "running", $"service '{svc}': {state}");
    }

    Console.WriteLine(ok ? "READY" : "NOT READY");
    return ok ? 0 : 1;
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
          self-update [--dry-run]            Update the app itself (signed) from the release feed
          auto-update                        self-update if available, otherwise update mods
          uninstall <id>
          enable <id>
          disable <id>
          tray <show|hide>                   Show/hide the system tray icon at runtime
          set-setting <id> <name> <value>
          status [--service <name>]          Readiness check (exit 0 when ready)

        Browse the remote catalog (https://mods.windhawk.net/catalog.json):
          catalog [query] [--full] [--ids]    List all mods with names/descriptions
          search  [query]                     Alias for catalog (filter by query)

        Notes:
          * A "target" is a Windhawk root dir containing windhawk.ini (portable build).
          * Sources resolve from --bundle (or $WHCLI_BUNDLE, default ./mods); fetch from
            windhawk-mods is a fallback unless --no-fetch.
        """);
}

// Fork identity constants (used by self-update).
static class Fork
{
    public const string Repo = "hansonxyz/windhawk-cli";
    public const string SigningCertThumbprint = "94041D722C6A606BE3752408FED5693100A8047C";
}
