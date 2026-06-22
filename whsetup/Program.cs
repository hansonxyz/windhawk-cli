// whsetup — installer for the WindhawkCLI fork (Native AOT).
// Interactive by default; flags for unattended install. Installs a LocalSystem
// service, applies config, starts it, and verifies readiness via whcli.
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using Whsetup;

const string Version = "1.0.1";
const string ServiceName = "WindhawkCLI";
const string DisplayName = "Windhawk CLI";
const string RegSubKey = @"SOFTWARE\WindhawkCLI";
const string DefaultDir = @"C:\Program Files\WindhawkCLI";
const string AppDataToken = @"%ProgramData%\WindhawkCLI";

try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

try
{
    return Run(args);
}
catch (Exception e)
{
    Console.Error.WriteLine("error: " + e.Message);
    return 1;
}

int Run(string[] a)
{
    if (Has(a, "-h") || Has(a, "--help")) { Help(); return 0; }
    if (Has(a, "--version")) { Console.WriteLine(Version); return 0; }

    RequireAdmin();

    if (Has(a, "--uninstall")) return Uninstall(Opt(a, "--install-dir") ?? DefaultDir);

    bool silent = Has(a, "--silent") || Has(a, "-S");
    string dir = Opt(a, "--install-dir") ?? DefaultDir;
    bool autoUpdates = Has(a, "--auto-updates");
    bool noTray = Has(a, "--no-system-tray");
    bool addExclusion = Has(a, "--add-defender-exclusion");

    if (!silent)
    {
        Console.WriteLine($"Windhawk CLI installer {Version}\n");
        dir = Ask("Install location", dir);
        autoUpdates = AskYesNo("Enable automatic mod updates", autoUpdates);
        noTray = AskYesNo("Hide the system tray icon", noTray);
        addExclusion = AskYesNo("Add a Windows Defender exclusion for the install (recommended for this tool)", addExclusion);
        Console.WriteLine($"\nInstall to: {dir}\n  auto-updates: {autoUpdates}\n  no-system-tray: {noTray}\n  defender-exclusion: {addExclusion}");
        if (!AskYesNo("Proceed", true)) { Console.WriteLine("Cancelled."); return 1; }
    }

    // Refuse to clobber an existing install unless explicitly replacing it.
    bool update = Has(a, "--update") || Has(a, "--force");
    if (InstallExists(dir) && !update)
    {
        if (silent)
        {
            Console.Error.WriteLine($"A WindhawkCLI install already exists (dir: {dir}). " +
                "Pass --update to replace it, or --uninstall to remove it first.");
            return 1;
        }
        if (!AskYesNo("An existing WindhawkCLI install was found. Replace it?", true))
        { Console.WriteLine("Cancelled."); return 1; }
    }

    return Install(dir, autoUpdates, noTray, addExclusion);
}

// An install is "present" if our registry hive exists or the install dir has windhawk.exe.
bool InstallExists(string dir)
{
    using var k = Registry.LocalMachine.OpenSubKey(RegSubKey);
    return k != null || File.Exists(Path.Combine(dir, "windhawk.exe"));
}

int Install(string dir, bool autoUpdates, bool noTray, bool addExclusion)
{
    string payload = ResolvePayload();
    if (!File.Exists(Path.Combine(payload, "windhawk.exe")))
        throw new FileNotFoundException($"payload missing windhawk.exe (looked in {payload})");
    if (!File.Exists(Path.Combine(payload, "whcli.exe")))
        throw new FileNotFoundException($"payload missing whcli.exe ({payload})");

    string engineVer = Path.GetFileName(Directory.GetDirectories(Path.Combine(payload, "Engine"))[0]);
    Console.WriteLine($"Engine version: {engineVer}");

    // Add the Defender exclusion FIRST, so the service never runs un-excluded.
    if (addExclusion)
    {
        Console.WriteLine("Adding Windows Defender exclusions (install dir, ProgramData, windhawk.exe, whcli.exe)...");
        AddDefenderExclusions(dir);
    }

    Console.WriteLine("Stopping/removing any existing service...");
    ServiceControl.StopAndDelete(ServiceName);

    // The service alone doesn't hold windhawk.exe — the tray/daemon (windhawk.exe
    // -tray-only) does. Stop any running app from this dir so the binary can be replaced
    // (otherwise an update/reinstall silently keeps the old windhawk.exe). It is relaunched
    // by the service after install.
    StopRunningApp(dir);

    Console.WriteLine($"Copying files to {dir}...");
    CopyDir(payload, dir, skipFileName: "windhawk.ini");

    // During a self-update the OLD whcli.exe that launched us may still be exiting and
    // hold a lock; CopyDir tolerates in-use files by skipping them, which would leave a
    // stale binary. Retry these two with a short backoff so an update never lags a version.
    foreach (var exe in new[] { "whcli.exe", "windhawk.exe" })
        RetryCopy(Path.Combine(payload, exe), Path.Combine(dir, exe));

    Console.WriteLine("Writing windhawk.ini...");
    File.WriteAllText(Path.Combine(dir, "windhawk.ini"),
        "\r\n[Storage]\r\n" +
        "Portable=0\r\n" +
        "CompilerPath=Compiler\r\n" +
        $"EnginePath=Engine\\{engineVer}\r\n" +
        "UIPath=UI\r\n" +
        $"AppDataPath={AppDataToken}\r\n" +
        $"RegistryKey=HKLM\\{RegSubKey}\r\n",
        new System.Text.UTF8Encoding(false));

    // The ENGINE reads its own engine.ini (in Engine\<ver>\), NOT windhawk.ini. The
    // payload ships the portable one (Portable=1, relative AppDataPath) which points the
    // service engine at a non-existent portable folder -> zero mods load. Rewrite it for
    // the non-portable layout: engine AppDataPath/RegistryKey both include the \Engine
    // segment (the engine reads mods from <AppDataPath>\Mods\<arch> and <RegistryKey>\Mods).
    Console.WriteLine("Writing engine.ini...");
    string engineAppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WindhawkCLI", "Engine");
    File.WriteAllText(Path.Combine(dir, "Engine", engineVer, "engine.ini"),
        "\r\n[Storage]\r\n" +
        "Portable=0\r\n" +
        $"AppDataPath={engineAppData}\r\n" +
        $"RegistryKey=HKLM\\{RegSubKey}\\Engine\r\n",
        new System.Text.UTF8Encoding(false));

    Console.WriteLine("Writing settings...");
    using (var settings = RegistryKeyOpenOrCreate(RegSubKey + @"\Settings"))
    {
        settings.SetValue("AutomaticUpdates", autoUpdates ? 1 : 0, RegistryValueKind.DWord);
        settings.SetValue("HideTrayIcon", noTray ? 1 : 0, RegistryValueKind.DWord);
        settings.SetValue("ForkVersion", Version, RegistryValueKind.String);
    }

    // No compiler is bundled, so place the mod runtime libs (libc++/libunwind/shim)
    // that precompiled mods link against at load time.
    string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindhawkCLI");
    string runtimeLibs = Path.Combine(payload, "RuntimeLibs");
    if (Directory.Exists(runtimeLibs))
    {
        Console.WriteLine("Placing mod runtime libs...");
        foreach (var subDir in Directory.GetDirectories(runtimeLibs))
        {
            var dst = Path.Combine(appData, "Engine", "Mods", Path.GetFileName(subDir));
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(subDir))
                try { File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); }
                catch (IOException) { /* in use from a prior install; existing copy is fine */ }
        }
    }

    Console.WriteLine($"Registering service '{ServiceName}'...");
    string binPath = $"\"{Path.Combine(dir, "windhawk.exe")}\" -service";
    ServiceControl.Create(ServiceName, DisplayName, binPath);

    Console.WriteLine("Starting service...");
    ServiceControl.Start(ServiceName);

    Console.WriteLine("Verifying readiness...");
    if (!VerifyReady(dir))
    {
        Console.Error.WriteLine("WARNING: service started but readiness check did not pass in time.");
        return 2;
    }

    Console.WriteLine($"\nDone. Windhawk CLI is installed and running.\nManage mods with: \"{Path.Combine(dir, "whcli.exe")}\" --root \"{dir}\" --service {ServiceName} <command>");
    return 0;
}

bool VerifyReady(string dir)
{
    string whcli = Path.Combine(dir, "whcli.exe");
    for (int i = 0; i < 15; i++)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(whcli)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "status", "--root", dir, "--service", ServiceName })
            psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode == 0) return true;
        Thread.Sleep(1000);
    }
    return false;
}

int Uninstall(string dir)
{
    Console.WriteLine($"Stopping/removing service '{ServiceName}'...");
    ServiceControl.StopAndDelete(ServiceName);
    Thread.Sleep(1000);

    try { RemoveDefenderExclusions(dir); Console.WriteLine("Removed Windows Defender exclusions (if present)."); }
    catch (Exception e) { Console.Error.WriteLine("  (could not remove Defender exclusions: " + e.Message + ")"); }

    if (Directory.Exists(dir))
    {
        Console.WriteLine($"Removing {dir}...");
        try { Directory.Delete(dir, true); }
        catch (Exception e) { Console.Error.WriteLine($"  could not fully remove {dir}: {e.Message}"); }
    }
    Console.WriteLine($"Done. (Left in place: %ProgramData%\\WindhawkCLI and HKLM\\{RegSubKey} — delete manually to purge mods/settings.)");
    return 0;
}

// ---------------------------------------------------------------- helpers
void RequireAdmin()
{
    using var id = WindowsIdentity.GetCurrent();
    var p = new WindowsPrincipal(id);
    if (!p.IsInRole(WindowsBuiltInRole.Administrator))
        throw new InvalidOperationException("Administrator rights are required (installs a service). Re-run elevated.");
}

static RegistryKey RegistryKeyOpenOrCreate(string subKey)
{
    using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
    return hklm.CreateSubKey(subKey, true);
}

// Single-file installer: extract the embedded payload.zip to a temp dir and return it.
// Falls back to a `payload/` folder next to the exe for dev builds with no embed.
static string ResolvePayload()
{
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
    if (stream is null)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "payload");
        if (Directory.Exists(dir)) return dir;
        throw new FileNotFoundException("This whsetup.exe has no embedded payload and no payload\\ folder beside it.");
    }
    Console.WriteLine("Extracting payload...");
    string baseTmp = Path.Combine(Path.GetTempPath(), "whxyz-setup-" + Guid.NewGuid().ToString("N"));
    string zip = baseTmp + ".zip";
    string outDir = Path.Combine(baseTmp, "payload");
    Directory.CreateDirectory(baseTmp);
    using (var fs = File.Create(zip)) stream.CopyTo(fs);
    System.IO.Compression.ZipFile.ExtractToDirectory(zip, outDir);
    try { File.Delete(zip); } catch { }
    return outDir;
}

// Stop any windhawk.exe (tray/daemon) running from the install dir so its files unlock.
// It is relaunched by the service after install.
static void StopRunningApp(string dir)
{
    string exe = Path.Combine(dir, "windhawk.exe");
    if (!File.Exists(exe)) return;

    // Ask a running instance to exit gracefully (targets our daemon via its named objects).
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe, "-exit -wait")
        { UseShellExecute = false, CreateNoWindow = true };
        System.Diagnostics.Process.Start(psi)?.WaitForExit(10000);
    }
    catch { }

    // Force-kill anything still running from this dir.
    foreach (var p in System.Diagnostics.Process.GetProcessesByName("windhawk"))
    {
        try
        {
            string? path = p.MainModule?.FileName;
            if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            { p.Kill(); p.WaitForExit(5000); }
        }
        catch { /* access denied / exited / cross-bitness — skip */ }
        finally { p.Dispose(); }
    }
}

// Copy a single file, retrying while the destination is briefly locked (up to ~10s).
static void RetryCopy(string src, string dst, int attempts = 20, int delayMs = 500)
{
    if (!File.Exists(src)) return;
    for (int i = 0; ; i++)
    {
        try { File.Copy(src, dst, true); return; }
        catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && i < attempts)
        { System.Threading.Thread.Sleep(delayMs); }
    }
}

static void CopyDir(string src, string dst, string? skipFileName = null)
{
    Directory.CreateDirectory(dst);
    foreach (var file in Directory.GetFiles(src))
    {
        var name = Path.GetFileName(file);
        if (skipFileName is not null && string.Equals(name, skipFileName, StringComparison.OrdinalIgnoreCase)) continue;
        var target = Path.Combine(dst, name);
        try
        {
            File.Copy(file, target, true);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Destination is locked (e.g. an engine DLL still loaded from a prior
            // same-version install). Keep the existing copy; it'll be replaced on
            // the next reboot/restart. Real version upgrades use a new folder and
            // never hit this.
            Console.WriteLine($"  note: '{name}' is in use; keeping the existing file.");
        }
    }
    foreach (var sub in Directory.GetDirectories(src))
        CopyDir(sub, Path.Combine(dst, Path.GetFileName(sub)));
}

static void AddDefenderExclusions(string dir)
{
    string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindhawkCLI");
    RunPowerShell($"Add-MpPreference -ExclusionPath '{dir}','{pd}'; Add-MpPreference -ExclusionProcess 'windhawk.exe','whcli.exe'");
}

static void RemoveDefenderExclusions(string dir)
{
    string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindhawkCLI");
    RunPowerShell($"Remove-MpPreference -ExclusionPath '{dir}','{pd}'; Remove-MpPreference -ExclusionProcess 'windhawk.exe','whcli.exe'");
}

static void RunPowerShell(string command)
{
    var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", command })
        psi.ArgumentList.Add(arg);
    using var p = System.Diagnostics.Process.Start(psi)
        ?? throw new InvalidOperationException("failed to launch powershell.exe");
    p.StandardOutput.ReadToEnd();
    string err = p.StandardError.ReadToEnd();
    p.WaitForExit();
    if (p.ExitCode != 0)
        throw new InvalidOperationException("Defender preference command failed: " + err.Trim());
}

static bool Has(string[] a, string flag) => Array.IndexOf(a, flag) >= 0;
static string? Opt(string[] a, string name)
{
    for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
    return null;
}

static string Ask(string prompt, string def)
{
    Console.Write($"{prompt} [{def}]: ");
    var s = Console.ReadLine();
    return string.IsNullOrWhiteSpace(s) ? def : s.Trim();
}

static bool AskYesNo(string prompt, bool def)
{
    Console.Write($"{prompt}? [{(def ? "Y/n" : "y/N")}]: ");
    var s = Console.ReadLine()?.Trim().ToLowerInvariant();
    if (string.IsNullOrEmpty(s)) return def;
    return s is "y" or "yes";
}

void Help()
{
    Console.WriteLine($"""
        whsetup {Version} — Windhawk CLI installer

        Usage: whsetup [options]
          (no options)            Interactive install
          --silent, -S            Unattended install (no prompts)
          --auto-updates          Enable automatic mod updates
          --no-system-tray        Hide the system tray icon
          --add-defender-exclusion  Add Windows Defender exclusions for the install
                                  (install dir, ProgramData data dir, windhawk.exe,
                                  whcli.exe). Needed because the engine's process
                                  injection trips Defender's behavioral heuristics.
          --install-dir <path>    Install location (default: {DefaultDir})
          --update, --force       Replace an existing install (required if one is present)
          --uninstall             Stop+remove the service and delete the install dir

        Must run elevated (installs the '{ServiceName}' LocalSystem service).
        After install it starts the service and verifies readiness via whcli,
        so scripts can immediately run whcli to install mods.
        """);
}
