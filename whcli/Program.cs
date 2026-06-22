// whcli — Windhawk CLI / migration tool (Native AOT)
using System.Runtime.InteropServices;
using System.Text.Json;
using Whcli;

const string Version = "0.1.0";

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
        "uninstall" or "remove" => CmdUninstall(rest),
        "enable" => CmdEnableDisable(rest, true),
        "disable" => CmdEnableDisable(rest, false),
        "set-setting" => CmdSetSetting(rest),
        "search" => CmdSearch(rest),
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

// ---------------------------------------------------------------- search
static int CmdSearch(string[] a)
{
    var pos = Positionals(a);
    string query = pos.Count > 0 ? pos[0] : "";

    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
    var json = http.GetStringAsync("https://api.github.com/repos/ramensoftware/windhawk-mods/contents/mods")
                   .GetAwaiter().GetResult();

    using var doc = JsonDocument.Parse(json);
    int n = 0;
    foreach (var item in doc.RootElement.EnumerateArray())
    {
        var name = item.GetProperty("name").GetString() ?? "";
        if (!name.EndsWith(".wh.cpp")) continue;
        var id = name[..^".wh.cpp".Length];
        if (query.Length == 0 || id.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(id);
            n++;
        }
    }
    Console.Error.WriteLine($"{n} mod(s)" + (query.Length > 0 ? $" matching '{query}'" : ""));
    return 0;
}

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
          uninstall <id>
          enable <id>
          disable <id>
          set-setting <id> <name> <value>
          search [query]

        Notes:
          * A "target" is a Windhawk root dir containing windhawk.ini (portable build).
          * Sources resolve from --bundle (or $WHCLI_BUNDLE, default ./mods); fetch from
            windhawk-mods is a fallback unless --no-fetch.
        """);
}
