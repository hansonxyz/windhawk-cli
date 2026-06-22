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

    // `whcli <command> --help` (or -h) prints detailed help for that one command.
    if (rest.Contains("-h") || rest.Contains("--help")) return PrintCommandHelp(cmd);

    // Serialize mutating operations machine-wide so concurrent automations don't
    // interleave registry/file writes or race the engine's live reload. Read-only
    // commands (list/status/catalog/mod-status/export) run without the lock.
    if (!IsMutating(cmd)) return Dispatch(cmd, rest);

    using var mtx = new System.Threading.Mutex(false, @"Global\WindhawkCLI-cli");
    bool held = false;
    try
    {
        try { held = mtx.WaitOne(TimeSpan.FromSeconds(60)); }
        catch (AbandonedMutexException) { held = true; }
        if (!held)
        {
            Console.Error.WriteLine("whcli: another operation is in progress; try again later.");
            return 3;
        }
        return Dispatch(cmd, rest);
    }
    finally { if (held) mtx.ReleaseMutex(); }
}

// Only commands that write mod config/files take the lock. start/stop/restart are service
// control (no config mutation) and must NOT hold it — the service's own pre-engine-start
// updater needs the lock, so a start that held it would deadlock its own startup.
static bool IsMutating(string c) => c is "apply" or "install" or "install-local" or "install-cache" or "update"
    or "self-update" or "auto-update" or "uninstall" or "remove" or "enable" or "disable"
    or "set-setting" or "tray";

static int Dispatch(string cmd, string[] rest) => cmd switch
{
    "export" => CmdExport(rest),
    "apply" => CmdApply(rest),
    "list" => CmdList(rest),
    "install" => CmdInstall(rest),
    "install-local" => CmdInstallLocal(rest),
    "install-cache" => CmdInstallCache(rest),
    "export-cache" => CmdExportCache(rest),
    "update" => CmdUpdate(rest),
    "self-update" => CmdSelfUpdate(rest),
    "auto-update" => CmdAutoUpdate(rest),
    "uninstall" or "remove" => CmdUninstall(rest),
    "enable" => CmdEnableDisable(rest, true),
    "disable" => CmdEnableDisable(rest, false),
    "start" or "stop" or "restart" => CmdService(cmd, rest),
    "mod-status" => CmdModStatus(rest),
    "tray" => CmdTray(rest),
    "set-setting" => CmdSetSetting(rest),
    "status" or "doctor" => CmdStatus(rest),
    "catalog" or "search" => CmdCatalog(rest),
    _ => Unknown(cmd),
};

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
    if (pos.Count < 1) throw new InvalidOperationException("usage: whcli install <mod-id> [<mod-id>...] [--version v] [--root <dir>] [--disabled]");

    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    string? version = Opt(a, "--version");
    if (pos.Count > 1 && !string.IsNullOrEmpty(version))
        throw new InvalidOperationException("--version can only be used when installing a single mod");
    bool disabled = Flag(a, "--disabled");
    bool arm64 = Arm64Enabled(a);

    // Install every requested mod. A failure on one does NOT stop the rest (configs are
    // written per mod and the engine live-reloads); the service is started once at the end.
    var succeeded = new List<string>();
    var failures = new List<(string id, string error)>();
    foreach (var id in pos)
    {
        try
        {
            // No explicit settings -> the mod uses the defaults declared in its source.
            InstallMod(install, store, id, version ?? "", disabled, settings: null, arm64);
            Console.WriteLine($"  ok    {id}");
            succeeded.Add(id);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"  FAIL  {id}: {e.Message}");
            failures.Add((id, e.Message));
        }
    }

    if (succeeded.Count > 0) EnsureServiceRunning(install, a);

    // Status summary: list every successful and failed mod.
    Console.WriteLine();
    Console.WriteLine($"Install summary: {succeeded.Count} succeeded, {failures.Count} failed (of {pos.Count}).");
    if (succeeded.Count > 0)
        Console.WriteLine("  succeeded: " + string.Join(", ", succeeded));
    if (failures.Count > 0)
    {
        Console.WriteLine("  failed:");
        foreach (var f in failures)
            Console.WriteLine($"    {f.id}: {f.error}");
    }

    // Non-zero if any failed, so automation can retry.
    return failures.Count > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- install-local
static int CmdInstallLocal(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1)
        throw new InvalidOperationException("usage: whcli install-local <dir|.wh.cpp> [--root <dir>] [--disabled] [--arm64]");
    string srcArg = pos[0];

    // Resolve the .wh.cpp source file and the directory that holds the precompiled DLLs.
    string srcFile, srcDir;
    if (Directory.Exists(srcArg))
    {
        srcDir = Path.GetFullPath(srcArg);
        var cpps = Directory.GetFiles(srcDir, "*.wh.cpp");
        if (cpps.Length == 0) throw new InvalidOperationException($"no .wh.cpp found in '{srcDir}'");
        if (cpps.Length > 1) throw new InvalidOperationException($"multiple .wh.cpp in '{srcDir}'; point to a specific file");
        srcFile = cpps[0];
    }
    else if (File.Exists(srcArg))
    {
        srcFile = Path.GetFullPath(srcArg);
        srcDir = Path.GetDirectoryName(srcFile)!;
    }
    else throw new InvalidOperationException($"source not found: '{srcArg}'");

    string source = File.ReadAllText(srcFile);
    var meta = ModMetadata.Parse(source);
    if (string.IsNullOrEmpty(meta.Id)) throw new InvalidOperationException($"'{srcFile}' is missing an @id header");

    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    bool arm64 = Arm64Enabled(a);

    string dll = PrecompiledMods.CopyLocal(install.EngineModsPath, srcDir, meta.Id, meta.Version, meta.Architecture, arm64);

    store.SetModConfig(meta.Id, new ModConfig
    {
        LibraryFileName = dll,
        Disabled = Flag(a, "--disabled"),
        Include = meta.Include,
        Exclude = meta.Exclude,
        Architecture = meta.Architecture,
        Version = meta.Version,
    });

    Directory.CreateDirectory(install.ModsSourcePath);
    File.WriteAllText(Path.Combine(install.ModsSourcePath, meta.Id + ".wh.cpp"), source);
    ModFiles.DeleteOld(install.EngineModsPath, meta.Id, ModFiles.SubfoldersFor(meta.Architecture, arm64), dll);

    Console.WriteLine($"Installed {meta.Id} v{meta.Version} from local source ({srcDir}).");
    EnsureServiceRunning(install, a);
    return 0;
}

// ---------------------------------------------------------------- install-cache (offline bundle)
static int CmdInstallCache(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1)
        throw new InvalidOperationException("usage: whcli install-cache <cache-dir> [--root <dir>] [--arm64]");
    string cacheDir = pos[0];
    if (!Directory.Exists(cacheDir)) throw new InvalidOperationException($"cache dir not found: {cacheDir}");

    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    bool arm64 = Arm64Enabled(a);

    var modDirs = Directory.GetDirectories(cacheDir).OrderBy(x => x, StringComparer.Ordinal).ToList();
    if (modDirs.Count == 0) throw new InvalidOperationException($"no mod subdirectories found in cache: {cacheDir}");

    // Install every cached mod; a failure on one does not stop the rest.
    var succeeded = new List<string>();
    var failures = new List<(string id, string error)>();
    foreach (var md in modDirs)
    {
        string id0 = Path.GetFileName(md);
        try
        {
            var cpps = Directory.GetFiles(md, "*.wh.cpp");
            if (cpps.Length == 0) throw new InvalidOperationException("cache entry has no .wh.cpp source");
            string source = File.ReadAllText(cpps[0]);
            var meta = ModMetadata.Parse(source);
            if (string.IsNullOrEmpty(meta.Id)) throw new InvalidOperationException("source is missing an @id header");

            // Optional per-mod default configuration.
            bool disabled = false;
            Dictionary<string, SettingValue>? settings = null;
            string cfgPath = Path.Combine(md, ModCache.ConfigFileName);
            if (File.Exists(cfgPath)) (disabled, settings) = ModCache.ReadConfig(cfgPath);

            string dll = PrecompiledMods.CopyLocal(install.EngineModsPath, md, meta.Id, meta.Version, meta.Architecture, arm64);
            store.SetModConfig(meta.Id, new ModConfig
            {
                LibraryFileName = dll,
                Disabled = disabled,
                Include = meta.Include,
                Exclude = meta.Exclude,
                Architecture = meta.Architecture,
                Version = meta.Version,
            });
            if (settings is not null) store.SetModSettings(meta.Id, settings);

            Directory.CreateDirectory(install.ModsSourcePath);
            File.WriteAllText(Path.Combine(install.ModsSourcePath, meta.Id + ".wh.cpp"), source);
            ModFiles.DeleteOld(install.EngineModsPath, meta.Id, ModFiles.SubfoldersFor(meta.Architecture, arm64), dll);

            Console.WriteLine($"  ok    {meta.Id} v{meta.Version}" + (disabled ? " (disabled)" : "") +
                              (settings is { Count: > 0 } ? $" ({settings.Count} settings)" : ""));
            succeeded.Add(meta.Id);
        }
        catch (Exception e) { Console.Error.WriteLine($"  FAIL  {id0}: {e.Message}"); failures.Add((id0, e.Message)); }
    }

    if (succeeded.Count > 0) EnsureServiceRunning(install, a);

    Console.WriteLine();
    Console.WriteLine($"Cache install summary: {succeeded.Count} succeeded, {failures.Count} failed (of {modDirs.Count}).");
    if (succeeded.Count > 0) Console.WriteLine("  succeeded: " + string.Join(", ", succeeded));
    if (failures.Count > 0)
    {
        Console.WriteLine("  failed:");
        foreach (var f in failures) Console.WriteLine($"    {f.id}: {f.error}");
    }

    // If AutomaticUpdates is on, pull latest mod versions now (best-effort; offline cache
    // gets the user online quickly, then this brings everything current). Offline = no-op.
    var appSettings = AppConfig.ReadSection(install, "Settings");
    bool autoUp = appSettings.TryGetValue("AutomaticUpdates", out var au) && au.IsInt && au.Int != 0;
    if (autoUp && succeeded.Count > 0)
    {
        Console.WriteLine("\nAutomaticUpdates is on — updating installed mods to latest...");
        try { CmdUpdate(new[] { "--root", install.RootPath }); }
        catch (Exception e) { Console.Error.WriteLine($"  (mod update skipped: {e.Message})"); }
    }

    return failures.Count > 0 ? 1 : 0;
}

// ---------------------------------------------------------------- export-cache (offline bundle)
static int CmdExportCache(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1)
        throw new InvalidOperationException("usage: whcli export-cache <target-dir> (must not exist) [--root <dir>]");
    string target = pos[0];
    if (Directory.Exists(target) || File.Exists(target))
        throw new InvalidOperationException($"target '{target}' already exists; choose a path that does not exist.");

    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    var ids = store.GetInstalledModIds().OrderBy(x => x, StringComparer.Ordinal).ToList();
    if (ids.Count == 0) throw new InvalidOperationException("no installed mods to export.");

    Directory.CreateDirectory(target);
    int ok = 0;
    var warnings = new List<string>();
    foreach (var id in ids)
    {
        var cfg = store.GetModConfig(id);
        if (cfg is null) continue;
        string modDir = Path.Combine(target, id);
        Directory.CreateDirectory(modDir);

        // Source (.wh.cpp) — required to reimport the mod's metadata.
        string src = Path.Combine(install.ModsSourcePath, id + ".wh.cpp");
        if (File.Exists(src)) File.Copy(src, Path.Combine(modDir, id + ".wh.cpp"), true);
        else warnings.Add($"{id}: source .wh.cpp not found — this cache entry won't be importable");

        // Compiled DLLs -> <version>_<sub>.dll (the convention install-cache/install-local read).
        int dllCount = 0;
        foreach (var sub in ModFiles.AllSubfolders(true))
        {
            string dllSrc = Path.Combine(install.EngineModsPath, sub, cfg.LibraryFileName);
            if (File.Exists(dllSrc)) { File.Copy(dllSrc, Path.Combine(modDir, $"{cfg.Version}_{sub}.dll"), true); dllCount++; }
        }
        if (dllCount == 0) warnings.Add($"{id}: no compiled DLLs found to export");

        // config.json — enabled/disabled + user settings.
        var settings = store.GetModSettings(id);
        ModCache.WriteConfig(Path.Combine(modDir, ModCache.ConfigFileName), cfg.Version, cfg.Disabled, settings);

        Console.WriteLine($"  exported {id} v{cfg.Version} ({(cfg.Disabled ? "disabled" : "enabled")}, {dllCount} dll, {settings.Count} settings)");
        ok++;
    }
    foreach (var w in warnings) Console.Error.WriteLine($"  warning: {w}");
    Console.WriteLine($"Exported {ok} mod(s) to {target}");
    Console.WriteLine($"Reinstall this cache on another machine with:  whcli install-cache \"{target}\"");
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

    // Optional positional mod ids restrict the update to those mods; no ids = all mods.
    var only = new HashSet<string>(Positionals(a), StringComparer.OrdinalIgnoreCase);
    if (only.Count > 0)
    {
        var installedIds = new HashSet<string>(store.GetInstalledModIds(), StringComparer.OrdinalIgnoreCase);
        foreach (var req in only)
            if (!installedIds.Contains(req)) Console.Error.WriteLine($"  (not installed, skipping: {req})");
    }

    var latest = FetchCatalogVersions();

    var outdated = new List<(string id, string from, string to, bool enabled)>();
    foreach (var id in store.GetInstalledModIds())
    {
        if (only.Count > 0 && !only.Contains(id)) continue;
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
    if (pos.Count < 1) throw new InvalidOperationException($"usage: whcli {(enable ? "enable" : "disable")} <mod-id> [<mod-id>...] [--root <dir>]");
    var install = ResolveTarget(Opt(a, "--root"));
    var store = install.OpenStore();
    int failed = 0;
    foreach (var id in pos)
    {
        try { store.EnableMod(id, enable); Console.WriteLine($"{(enable ? "Enabled" : "Disabled")} {id}."); }
        catch (Exception e) { Console.Error.WriteLine($"  {id} failed: {e.Message}"); failed++; }
    }
    if (enable && failed < pos.Count) EnsureServiceRunning(install, a);
    if (failed > 0) return 1;
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
    var args = new List<string> { "--silent", "--update", "--install-dir", install.RootPath, "--add-defender-exclusion" };
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

// ---------------------------------------------------------------- service control
static string SvcName(string[] a) => Opt(a, "--service") ?? "WindhawkCLI";

static int CmdService(string cmd, string[] a)
{
    var install = ResolveTarget(Opt(a, "--root"));
    if (install.Portable)
        throw new InvalidOperationException($"'{cmd}' applies to a service install; this target is portable.");
    string svc = SvcName(a);
    bool ok; string err;
    switch (cmd)
    {
        case "start": ok = ServiceControl.Start(svc, out err); break;
        case "stop":  ok = ServiceControl.Stop(svc, out err); break;
        default:      ok = ServiceControl.Restart(svc, out err); break; // restart, valid from stopped
    }
    if (!ok) { Console.Error.WriteLine($"{cmd} '{svc}' failed: {err}"); return 1; }
    Console.WriteLine($"Service '{svc}' {(cmd == "stop" ? "stopped" : "running")}.");
    return 0;
}

/// <summary>Best-effort: ensure the service is running after a mod becomes active (always-on model).</summary>
static void EnsureServiceRunning(WindhawkInstall install, string[] a)
{
    if (install.Portable) return;
    string svc = SvcName(a);
    string state = ServiceQuery.State(svc);
    if (state == "running") return;
    if (state == "not-installed") return; // no service to start (CLI used standalone)
    if (ServiceControl.Start(svc, out var err))
        Console.WriteLine($"Started service '{svc}'.");
    else
        Console.Error.WriteLine($"  (could not start '{svc}': {err}; mods are configured and load when it starts)");
}

// ---------------------------------------------------------------- mod-status
static int CmdModStatus(string[] a)
{
    var pos = Positionals(a);
    if (pos.Count < 1) throw new InvalidOperationException("usage: whcli mod-status <mod-id> [--root <dir>]");
    string id = pos[0];

    var install = ResolveTarget(Opt(a, "--root"));
    var cfg = install.OpenStore().GetModConfig(id);
    if (cfg is null) { Console.Error.WriteLine($"mod '{id}' is not installed."); return 1; }

    string Join(string[] v) => v.Length == 0 ? "" : string.Join(", ", v);
    Console.WriteLine($"Mod:          {id}");
    Console.WriteLine($"Version:      {cfg.Version}");
    Console.WriteLine($"State:        {(cfg.Disabled ? "disabled" : "enabled")}");
    Console.WriteLine($"Library:      {cfg.LibraryFileName}");
    Console.WriteLine($"Architecture: {(cfg.Architecture.Length == 0 ? "(all)" : Join(cfg.Architecture))}");
    Console.WriteLine($"Include:      {Join(cfg.Include)}");
    if (cfg.Exclude.Length > 0) Console.WriteLine($"Exclude:      {Join(cfg.Exclude)}");

    Console.WriteLine("Compiled DLLs:");
    foreach (var sub in ModFiles.AllSubfolders(true))
    {
        string p = Path.Combine(install.EngineModsPath, sub, cfg.LibraryFileName);
        Console.WriteLine($"  [{(File.Exists(p) ? "x" : " ")}] {sub}\\{cfg.LibraryFileName}");
    }

    Console.WriteLine("Loaded in processes (live):");
    var hits = LiveModProcesses(cfg.LibraryFileName);
    if (hits.Count == 0)
        Console.WriteLine("  (none — not currently injected, or the engine isn't running)");
    else
        foreach (var h in hits) Console.WriteLine($"  {h}");

    if (!install.Portable)
        Console.WriteLine($"Service:      {ServiceQuery.State(SvcName(a))}");
    return 0;
}

/// <summary>Scan running processes for a mod's loaded DLL (real proof it's injected).</summary>
static List<string> LiveModProcesses(string libraryFileName)
{
    var result = new List<string>();
    foreach (var p in System.Diagnostics.Process.GetProcesses())
    {
        try
        {
            foreach (System.Diagnostics.ProcessModule m in p.Modules)
                if (string.Equals(m.ModuleName, libraryFileName, StringComparison.OrdinalIgnoreCase))
                { result.Add($"{p.ProcessName} (PID {p.Id})"); break; }
        }
        catch { /* access denied / exited — skip */ }
        finally { p.Dispose(); }
    }
    result.Sort();
    return result;
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
               whcli <command> --help      Detailed help for one command
               whcli                       (no args) shows this help

        Profiles (migration / provisioning):
          export [--from <root>] [--out profile.json]   Snapshot installed mods+settings to a profile
          apply <profile.json> [--app-settings] [--arm64]  Install+configure every mod from a profile (precompiled)

        Offline mod cache (self-contained bundle; no network needed to install):
          export-cache <dir>                 Export installed mods + DLLs + settings to a NEW dir (must not exist)
          install-cache <dir>                Install every mod from such a cache; if auto-updates are on,
                                             update mods to latest right afterward

        Single mods (target = --root <dir> or $WHCLI_ROOT):
          list
          install <id> [<id>...] [--version v] [--disabled] [--arm64]
                                             Install one or more PRECOMPILED mods from the catalog
          install-local <dir|.wh.cpp> [--disabled] [--arm64]
                                             Install a precompiled mod from a local folder
          update [<id>...] [--dry-run]       Upgrade mods to catalog latest (no ids = all installed)
          self-update [--dry-run]            Update the app itself (signed) from the release feed
          auto-update                        self-update if available, otherwise update mods
          uninstall <id>
          enable <id> [<id>...]              (also starts the service)
          disable <id> [<id>...]
          mod-status <id>                    Config, compiled DLLs, and live injected processes
          tray <show|hide>                   Show/hide the system tray icon at runtime
          set-setting <id> <name> <value>
          status [--service <name>]          Readiness check (exit 0 when ready)

        Service control (need elevation):
          start | stop | restart [--service <name>]   Control the engine service (restart valid from stopped)

        Browse the remote catalog (https://mods.windhawk.net/catalog.json):
          catalog [query] [--full] [--ids]   List catalog mods; 'search' is an alias

        Run 'whcli <command> --help' for detailed usage of any command.
        Project: https://github.com/hansonxyz/windhawk-cli
        """);
}

// Detailed per-command help (whcli <command> --help). Returns an exit code.
static int PrintCommandHelp(string cmd)
{
    string? help = cmd switch
    {
        "install" => """
            whcli install <mod-id> [<mod-id>...] [options]

            Install one or more mods PRECOMPILED from the catalog (https://mods.windhawk.net).
            Each mod's config is written and the running engine live-reloads it; the service is
            started once at the end. Installation CONTINUES past a failed mod and prints a
            summary listing every succeeded and failed mod; the exit code is non-zero if any
            failed (so automation can retry).

            Options:
              --version <v>    Pin a version (single mod only; default = latest).
              --disabled       Install but leave the mod disabled.
              --arm64          Also fetch the ARM64 build.
              --root <dir>     Target install (or $WHCLI_ROOT; default = the WindhawkCLI service).
            """,
        "install-local" => """
            whcli install-local <dir|.wh.cpp> [options]

            Install a single PRECOMPILED mod from local files (no network). Point at either a
            folder containing one <id>.wh.cpp, or the .wh.cpp file directly. The precompiled
            DLLs must sit beside it, named "<version>_<sub>.dll" (e.g. 1.3.10_64.dll) or
            "<sub>.dll" (e.g. 64.dll), where <sub> is 32 / 64 / arm64.

            Options:
              --disabled       Install but leave the mod disabled.
              --arm64          Also install the ARM64 build if present.
              --root <dir>     Target install.
            """,
        "install-cache" => """
            whcli install-cache <cache-dir> [options]

            Install EVERY mod from an offline cache directory (produced by 'export-cache'). The
            cache has one subfolder per mod (named by mod id), each containing:
              - <id>.wh.cpp            the mod source (for metadata)
              - <version>_<sub>.dll    precompiled DLLs (e.g. 1.3.10_64.dll), one per arch
              - config.json            (optional) { "version", "disabled", "settings": {...} }

            Installs continue past failures and print a succeeded/failed summary (non-zero exit
            if any failed). No network is needed. If AutomaticUpdates is enabled on the target,
            mods are updated to catalog latest immediately afterward (best-effort; offline = skip).

            Options:
              --arm64          Also install ARM64 builds present in the cache.
              --root <dir>     Target install.
            """,
        "export-cache" => """
            whcli export-cache <target-dir> [options]

            Export all installed mods to a self-contained offline cache that 'install-cache' can
            reinstall. <target-dir> MUST NOT already exist (errors if it does). Produces one
            subfolder per mod with its source (.wh.cpp), its compiled DLLs (as <version>_<sub>.dll),
            and a config.json capturing enabled/disabled + the mod's settings. Use this to seed an
            offline provisioning cache so first install doesn't depend on the Windhawk servers.

            Options:
              --root <dir>     Source install to export from.
            """,
        "update" => """
            whcli update [<mod-id>...] [options]

            Upgrade mods to the catalog's latest version. With no ids, every installed mod is
            checked; otherwise only the named ids. Settings are preserved across the upgrade.

            Options:
              --dry-run        Show what would update, change nothing.
              --no-reload      Skip the live disable/swap/enable dance (used before the engine starts).
              --root <dir>     Target install.
            """,
        "self-update" => """
            whcli self-update [--dry-run]

            Check the GitHub release feed (hansonxyz/windhawk-cli) for a newer windhawk-cli, and
            if found, download the signed installer, verify its Authenticode signature against the
            pinned certificate, and launch it to replace this install. Defers if the signature is
            invalid. --dry-run only reports availability.
            """,
        "auto-update" => """
            whcli auto-update [--root <dir>]

            Self-update the app if a newer signed release exists; otherwise update all mods to
            catalog latest. This is what the service runs on a schedule when AutomaticUpdates is on.
            """,
        "apply" => """
            whcli apply <profile.json> [options]

            Install + configure every mod listed in a profile.json (from 'export'). Mods are
            fetched PRECOMPILED from the catalog. Continues past failures.

            Options:
              --app-settings   Also apply the app/engine settings captured in the profile.
              --arm64          Also fetch ARM64 builds.
              --root <dir>     Target install.
            """,
        "export" => """
            whcli export [options]

            Snapshot an install's mods + settings to a profile.json (and bundle the mod sources).
            For a full offline bundle incl. compiled DLLs, use 'export-cache' instead.

            Options:
              --from <root>    Export from this install (default: auto-detect installed).
              --out <file>     Output path (default: profile.json).
              --bundle <dir>   Where to copy mod sources (default: ./mods).
              --no-bundle      Don't copy sources.
            """,
        "list" => "whcli list [--root <dir>]\n\nList installed mods with their enabled/disabled state and version.",
        "mod-status" => """
            whcli mod-status <mod-id> [--root <dir>]

            Show a mod's config (version, enabled/disabled, include/exclude, architectures), which
            compiled DLLs are present per arch, and — most usefully — which running processes
            currently have the mod's DLL loaded (proof it's actually injected and working).
            """,
        "enable" or "disable" => """
            whcli enable|disable <mod-id> [<mod-id>...] [--root <dir>]

            Enable or disable one or more installed mods. 'enable' also starts the service. The
            running engine live-applies the change.
            """,
        "uninstall" or "remove" => "whcli uninstall <mod-id> [--root <dir>]\n\nRemove a mod's config and its compiled DLLs. The engine live-unloads it.",
        "set-setting" => """
            whcli set-setting <mod-id> <name> <value> [--root <dir>]

            Set a single mod setting. Numeric values are stored as integers, everything else as
            strings. Array-style keys (e.g. items[0]) are supported. The engine live-applies it.
            """,
        "start" or "stop" or "restart" => """
            whcli start | stop | restart [--service <name>] [--root <dir>]

            Control the engine Windows service (needs elevation). 'restart' is valid even from a
            stopped state. 'stop' waits for the service process to fully exit before returning.
            Default service name: WindhawkCLI.
            """,
        "status" or "doctor" => """
            whcli status [--service <name>] [--root <dir>]

            Readiness check: verifies the engine, mod runtime libs, writable storage, and (for a
            service install) that the service is running. Exits 0 only when everything is READY —
            use it as a gate in provisioning scripts.
            """,
        "catalog" or "search" => """
            whcli catalog [query] [--full] [--ids]

            Browse the official mod catalog (https://mods.windhawk.net/catalog.json). Optional
            query filters by id/name/description/author. --ids prints only ids; --full prints
            untruncated descriptions. 'search' is an alias.
            """,
        "tray" => "whcli tray <show|hide> [--root <dir>]\n\nShow or hide the system-tray icon at runtime (also persists for next startup).",
        _ => null,
    };

    if (help is null)
    {
        Console.Error.WriteLine($"whcli: no detailed help for '{cmd}' (try: whcli --help)");
        return 2;
    }
    Console.WriteLine(help);
    return 0;
}

// Fork identity constants (used by self-update).
static class Fork
{
    public const string Repo = "hansonxyz/windhawk-cli";
    public const string SigningCertThumbprint = "94041D722C6A606BE3752408FED5693100A8047C";
}
