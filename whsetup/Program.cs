// whsetup — installer for the WindhawkXYZ fork (Native AOT).
// Interactive by default; flags for unattended install. Installs a LocalSystem
// service, applies config, starts it, and verifies readiness via whcli.
using System.Security.Principal;
using Microsoft.Win32;
using Whsetup;

const string Version = "0.1.0";
const string ServiceName = "WindhawkXYZ";
const string DisplayName = "Windhawk XYZ";
const string RegSubKey = @"SOFTWARE\WindhawkXYZ";
const string DefaultDir = @"C:\Program Files\WindhawkXYZ";
const string AppDataToken = @"%ProgramData%\WindhawkXYZ";

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
        Console.WriteLine($"Windhawk XYZ installer {Version}\n");
        dir = Ask("Install location", dir);
        autoUpdates = AskYesNo("Enable automatic mod updates", autoUpdates);
        noTray = AskYesNo("Hide the system tray icon", noTray);
        addExclusion = AskYesNo("Add a Windows Defender exclusion for the install (recommended for this tool)", addExclusion);
        Console.WriteLine($"\nInstall to: {dir}\n  auto-updates: {autoUpdates}\n  no-system-tray: {noTray}\n  defender-exclusion: {addExclusion}");
        if (!AskYesNo("Proceed", true)) { Console.WriteLine("Cancelled."); return 1; }
    }

    return Install(dir, autoUpdates, noTray, addExclusion);
}

int Install(string dir, bool autoUpdates, bool noTray, bool addExclusion)
{
    string payload = Path.Combine(AppContext.BaseDirectory, "payload");
    if (!File.Exists(Path.Combine(payload, "windhawk.exe")))
        throw new FileNotFoundException($"payload not found next to whsetup.exe (looked in {payload})");
    if (!File.Exists(Path.Combine(payload, "whcli.exe")))
        throw new FileNotFoundException($"whcli.exe missing from payload ({payload})");

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

    Console.WriteLine($"Copying files to {dir}...");
    CopyDir(payload, dir, skipFileName: "windhawk.ini");

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

    Console.WriteLine("Writing settings...");
    using (var settings = RegistryKeyOpenOrCreate(RegSubKey + @"\Settings"))
    {
        settings.SetValue("AutomaticUpdates", autoUpdates ? 1 : 0, RegistryValueKind.DWord);
        settings.SetValue("HideTrayIcon", noTray ? 1 : 0, RegistryValueKind.DWord);
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

    Console.WriteLine($"\nDone. Windhawk XYZ is installed and running.\nManage mods with: \"{Path.Combine(dir, "whcli.exe")}\" --root \"{dir}\" --service {ServiceName} <command>");
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
    Console.WriteLine($"Done. (Left in place: %ProgramData%\\WindhawkXYZ and HKLM\\{RegSubKey} — delete manually to purge mods/settings.)");
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
    string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindhawkXYZ");
    RunPowerShell($"Add-MpPreference -ExclusionPath '{dir}','{pd}'; Add-MpPreference -ExclusionProcess 'windhawk.exe','whcli.exe'");
}

static void RemoveDefenderExclusions(string dir)
{
    string pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindhawkXYZ");
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
        whsetup {Version} — Windhawk XYZ installer

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
          --uninstall             Stop+remove the service and delete the install dir

        Must run elevated (installs the '{ServiceName}' LocalSystem service).
        After install it starts the service and verifies readiness via whcli,
        so scripts can immediately run whcli to install mods.
        """);
}
