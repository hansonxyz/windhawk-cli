using Microsoft.Win32;

namespace Whcli;

/// <summary>A mod setting value: either a 32-bit int (REG_DWORD) or a string (REG_SZ).</summary>
internal readonly struct SettingValue
{
    public bool IsInt { get; }
    public int Int { get; }
    public string Str { get; }

    private SettingValue(bool isInt, int i, string s) { IsInt = isInt; Int = i; Str = s; }
    public static SettingValue Of(int i) => new(true, i, "");
    public static SettingValue Of(string s) => new(false, 0, s);

    public override string ToString() => IsInt ? Int.ToString() : Str;
}

/// <summary>The subset of a mod's config we read/write (see modConfigUtils.ts CONFIG_FIELDS).</summary>
internal sealed class ModConfig
{
    public string LibraryFileName = "";
    public bool Disabled;
    public string[] Include = [];
    public string[] Exclude = [];
    public string[] Architecture = [];
    public string Version = "";
}

internal interface IModStore
{
    IEnumerable<string> GetInstalledModIds();
    ModConfig? GetModConfig(string id);
    void SetModConfig(string id, ModConfig cfg);
    Dictionary<string, SettingValue> GetModSettings(string id);
    void SetModSettings(string id, IReadOnlyDictionary<string, SettingValue> settings);
    void EnableMod(string id, bool enable);
    void DeleteMod(string id);
}

internal static class ModStoreHelpers
{
    public static int SettingsChangeTime()
        => (int)((DateTimeOffset.UtcNow.ToUnixTimeSeconds()) & 0x7fffffff);

    public static string[] SplitPipe(string? v) => string.IsNullOrEmpty(v) ? [] : v.Split('|');
    public static string JoinPipe(string[] v) => string.Join('|', v);
}

/// <summary>Registry backend (non-portable installs). Mirrors RegistryStorageBackend.</summary>
internal sealed class RegistryModStore : IModStore
{
    private readonly RegistryHive _hive;
    private readonly string _modsKey;

    public RegistryModStore(WindhawkInstall install)
    {
        _hive = install.RegHive;
        _modsKey = install.RegSubKey + "\\Engine\\Mods";
    }

    private RegistryKey Base() => RegistryKey.OpenBaseKey(_hive, RegistryView.Registry64);

    public IEnumerable<string> GetInstalledModIds()
    {
        using var b = Base();
        using var mods = b.OpenSubKey(_modsKey);
        if (mods is null) yield break;
        foreach (var name in mods.GetSubKeyNames())
        {
            using var k = mods.OpenSubKey(name);
            if (k?.GetValue("LibraryFileName") is string s && s.Length > 0)
                yield return name;
        }
    }

    public ModConfig? GetModConfig(string id)
    {
        using var b = Base();
        using var k = b.OpenSubKey(_modsKey + "\\" + id);
        if (k is null) return null;
        if (k.GetValue("LibraryFileName") is not string lib || lib.Length == 0) return null;

        return new ModConfig
        {
            LibraryFileName = lib,
            Disabled = (k.GetValue("Disabled") as int? ?? 0) != 0,
            Include = ModStoreHelpers.SplitPipe(k.GetValue("Include") as string),
            Exclude = ModStoreHelpers.SplitPipe(k.GetValue("Exclude") as string),
            Architecture = ModStoreHelpers.SplitPipe(k.GetValue("Architecture") as string),
            Version = k.GetValue("Version") as string ?? "",
        };
    }

    public void SetModConfig(string id, ModConfig cfg)
    {
        using var b = Base();
        using var k = b.CreateSubKey(_modsKey + "\\" + id, true);
        k.SetValue("LibraryFileName", cfg.LibraryFileName, RegistryValueKind.String);
        k.SetValue("Disabled", cfg.Disabled ? 1 : 0, RegistryValueKind.DWord);
        k.SetValue("Include", ModStoreHelpers.JoinPipe(cfg.Include), RegistryValueKind.String);
        k.SetValue("Exclude", ModStoreHelpers.JoinPipe(cfg.Exclude), RegistryValueKind.String);
        k.SetValue("Architecture", ModStoreHelpers.JoinPipe(cfg.Architecture), RegistryValueKind.String);
        k.SetValue("Version", cfg.Version, RegistryValueKind.String);
    }

    public Dictionary<string, SettingValue> GetModSettings(string id)
    {
        var result = new Dictionary<string, SettingValue>();
        using var b = Base();
        using var k = b.OpenSubKey(_modsKey + "\\" + id + "\\Settings");
        if (k is null) return result;
        foreach (var name in k.GetValueNames())
        {
            if (name.Length == 0) continue;
            var kind = k.GetValueKind(name);
            var val = k.GetValue(name);
            if (kind == RegistryValueKind.DWord && val is int i)
                result[name] = SettingValue.Of(i);
            else if (val is string s)
                result[name] = SettingValue.Of(s);
        }
        return result;
    }

    public void SetModSettings(string id, IReadOnlyDictionary<string, SettingValue> settings)
    {
        using var b = Base();
        b.DeleteSubKeyTree(_modsKey + "\\" + id + "\\Settings", throwOnMissingSubKey: false);
        using (var sk = b.CreateSubKey(_modsKey + "\\" + id + "\\Settings", true))
        {
            foreach (var (name, v) in settings)
            {
                if (v.IsInt)
                    sk.SetValue(name, unchecked((int)(uint)v.Int), RegistryValueKind.DWord);
                else
                    sk.SetValue(name, v.Str, RegistryValueKind.String);
            }
        }
        using var mk = b.CreateSubKey(_modsKey + "\\" + id, true);
        mk.SetValue("SettingsChangeTime", ModStoreHelpers.SettingsChangeTime(), RegistryValueKind.DWord);
    }

    public void EnableMod(string id, bool enable)
    {
        using var b = Base();
        using var k = b.CreateSubKey(_modsKey + "\\" + id, true);
        k.SetValue("Disabled", enable ? 0 : 1, RegistryValueKind.DWord);
    }

    public void DeleteMod(string id)
    {
        using var b = Base();
        b.DeleteSubKeyTree(_modsKey + "\\" + id, throwOnMissingSubKey: false);
    }
}

/// <summary>INI backend (portable installs). Mirrors IniStorageBackend; writes via the Win32 INI API.</summary>
internal sealed class IniModStore : IModStore
{
    private readonly string _modsPath;

    public IniModStore(WindhawkInstall install) => _modsPath = install.EngineModsPath;

    private string IniPath(string id) => Path.Combine(_modsPath, id + ".ini");

    public IEnumerable<string> GetInstalledModIds()
    {
        if (!Directory.Exists(_modsPath)) yield break;
        foreach (var f in Directory.EnumerateFiles(_modsPath, "*.ini"))
        {
            var id = Path.GetFileNameWithoutExtension(f);
            if (!string.IsNullOrEmpty(IniFile.GetString(f, "Mod", "LibraryFileName")))
                yield return id;
        }
    }

    public ModConfig? GetModConfig(string id)
    {
        var p = IniPath(id);
        if (!File.Exists(p)) return null;
        var lib = IniFile.GetString(p, "Mod", "LibraryFileName");
        if (string.IsNullOrEmpty(lib)) return null;
        return new ModConfig
        {
            LibraryFileName = lib,
            Disabled = (IniFile.GetInt(p, "Mod", "Disabled") ?? 0) != 0,
            Include = ModStoreHelpers.SplitPipe(IniFile.GetString(p, "Mod", "Include")),
            Exclude = ModStoreHelpers.SplitPipe(IniFile.GetString(p, "Mod", "Exclude")),
            Architecture = ModStoreHelpers.SplitPipe(IniFile.GetString(p, "Mod", "Architecture")),
            Version = IniFile.GetString(p, "Mod", "Version") ?? "",
        };
    }

    public void SetModConfig(string id, ModConfig cfg)
    {
        var p = IniPath(id);
        IniFile.SetString(p, "Mod", "LibraryFileName", cfg.LibraryFileName);
        IniFile.SetInt(p, "Mod", "Disabled", cfg.Disabled ? 1 : 0);
        IniFile.SetString(p, "Mod", "Include", ModStoreHelpers.JoinPipe(cfg.Include));
        IniFile.SetString(p, "Mod", "Exclude", ModStoreHelpers.JoinPipe(cfg.Exclude));
        IniFile.SetString(p, "Mod", "Architecture", ModStoreHelpers.JoinPipe(cfg.Architecture));
        IniFile.SetString(p, "Mod", "Version", cfg.Version);
    }

    public Dictionary<string, SettingValue> GetModSettings(string id)
    {
        var result = new Dictionary<string, SettingValue>();
        var p = IniPath(id);
        if (!File.Exists(p)) return result;
        foreach (var name in IniFile.GetKeys(p, "Settings"))
        {
            // INI is untyped; ints are decimal strings. Preserve int-ness when it round-trips.
            var s = IniFile.GetString(p, "Settings", name);
            if (s is null) continue;
            if (int.TryParse(s, out var i) && i.ToString() == s)
                result[name] = SettingValue.Of(i);
            else
                result[name] = SettingValue.Of(s);
        }
        return result;
    }

    public void SetModSettings(string id, IReadOnlyDictionary<string, SettingValue> settings)
    {
        var p = IniPath(id);
        IniFile.EnsureUnicodeFile(p);
        IniFile.DeleteSection(p, "Settings");
        foreach (var (name, v) in settings)
        {
            if (v.IsInt) IniFile.SetInt(p, "Settings", name, v.Int);
            else IniFile.SetString(p, "Settings", name, v.Str);
        }
        IniFile.SetInt(p, "Mod", "SettingsChangeTime", ModStoreHelpers.SettingsChangeTime());
    }

    public void EnableMod(string id, bool enable)
        => IniFile.SetInt(IniPath(id), "Mod", "Disabled", enable ? 0 : 1);

    public void DeleteMod(string id)
    {
        var p = IniPath(id);
        if (File.Exists(p)) File.Delete(p);
        var w = Path.Combine(_modsPath, "..", "ModsWritable", id + ".ini");
        if (File.Exists(w)) File.Delete(w);
    }
}
