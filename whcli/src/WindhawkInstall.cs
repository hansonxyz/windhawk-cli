using Microsoft.Win32;

namespace Whcli;

/// <summary>
/// Resolves a Windhawk installation from its windhawk.ini (mirrors
/// vscode-windhawk/src/storagePaths.ts): portable vs registry storage, plus the
/// engine/compiler/appdata paths used by every other component.
/// </summary>
internal sealed class WindhawkInstall
{
    public string RootPath { get; }
    public bool Portable { get; }
    public string AppDataPath { get; }
    public string EnginePath { get; }
    public string CompilerPath { get; }
    public string UIPath { get; }

    // Non-portable (registry) storage only:
    public RegistryHive RegHive { get; }
    public string RegSubKey { get; } = "";

    public string EngineModsPath => Path.Combine(AppDataPath, "Engine", "Mods");
    public string EngineModsWritablePath => Path.Combine(AppDataPath, "Engine", "ModsWritable");
    public string ModsSourcePath => Path.Combine(AppDataPath, "ModsSource");

    private WindhawkInstall(string root)
    {
        RootPath = root;
        string iniPath = Path.Combine(root, "windhawk.ini");
        if (!File.Exists(iniPath))
            throw new FileNotFoundException($"windhawk.ini not found in {root}");

        string Get(string key)
        {
            var v = IniFile.GetString(iniPath, "Storage", key);
            return v ?? "";
        }

        Portable = ParseIntFlag(Get("Portable"));

        string Resolve(string p) => Path.GetFullPath(Path.Combine(root, ExpandEnv(p)));

        AppDataPath = Resolve(Get("AppDataPath"));
        EnginePath = Resolve(Get("EnginePath"));
        CompilerPath = Resolve(Get("CompilerPath"));
        UIPath = Resolve(Get("UIPath"));

        if (!Portable)
        {
            string regKey = Get("RegistryKey");
            int i = regKey.IndexOf('\\');
            string hive = i == -1 ? regKey : regKey[..i];
            RegSubKey = i == -1 ? "" : regKey[(i + 1)..];
            RegHive = hive switch
            {
                "HKEY_LOCAL_MACHINE" or "HKLM" => RegistryHive.LocalMachine,
                "HKEY_CURRENT_USER" or "HKCU" => RegistryHive.CurrentUser,
                "HKEY_USERS" or "HKU" => RegistryHive.Users,
                _ => throw new InvalidOperationException($"Unsupported registry hive: {hive}")
            };
        }
    }

    public static WindhawkInstall FromRoot(string root) => new(Path.GetFullPath(root));

    /// <summary>Best-effort auto-detect of the system-installed Windhawk.</summary>
    public static WindhawkInstall? AutoDetectInstalled()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windhawk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windhawk"),
        })
        {
            if (File.Exists(Path.Combine(candidate, "windhawk.ini")))
                return FromRoot(candidate);
        }
        return null;
    }

    public IModStore OpenStore()
        => Portable
            ? new IniModStore(this)
            : new RegistryModStore(this);

    private static bool ParseIntFlag(string s) => int.TryParse(s, out var v) && v != 0;

    private static string ExpandEnv(string p) =>
        System.Text.RegularExpressions.Regex.Replace(p, "%([^%]+)%", m =>
            Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
}
