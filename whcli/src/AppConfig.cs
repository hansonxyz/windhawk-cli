using Microsoft.Win32;

namespace Whcli;

/// <summary>
/// Read/write Windhawk's app-level config sections (mirrors StorageManager::GetAppConfig):
/// portable -> AppData/settings.ini [section]; non-portable -> &lt;RegSubKey&gt;\section.
/// "section" may be a nested path like "Engine\\Settings".
/// </summary>
internal static class AppConfig
{
    public static Dictionary<string, SettingValue> ReadSection(WindhawkInstall install, string section)
    {
        var result = new Dictionary<string, SettingValue>();
        if (install.Portable)
        {
            var ini = Path.Combine(install.AppDataPath, "settings.ini");
            if (!File.Exists(ini)) return result;
            foreach (var name in IniFile.GetKeys(ini, section))
            {
                var s = IniFile.GetString(ini, section, name);
                if (s is null) continue;
                result[name] = int.TryParse(s, out var i) && i.ToString() == s
                    ? SettingValue.Of(i) : SettingValue.Of(s);
            }
        }
        else
        {
            using var b = RegistryKey.OpenBaseKey(install.RegHive, RegistryView.Registry64);
            using var k = b.OpenSubKey(install.RegSubKey + "\\" + section);
            if (k is null) return result;
            foreach (var name in k.GetValueNames())
            {
                if (name.Length == 0) continue;
                var val = k.GetValue(name);
                if (k.GetValueKind(name) == RegistryValueKind.DWord && val is int i)
                    result[name] = SettingValue.Of(i);
                else if (val is string s)
                    result[name] = SettingValue.Of(s);
            }
        }
        return result;
    }

    /// <summary>Set the given values in a section (merge; existing unrelated keys are left intact).</summary>
    public static void WriteSection(WindhawkInstall install, string section, IReadOnlyDictionary<string, SettingValue> values)
    {
        if (install.Portable)
        {
            var ini = Path.Combine(install.AppDataPath, "settings.ini");
            IniFile.EnsureUnicodeFile(ini);
            foreach (var (name, v) in values)
            {
                if (v.IsInt) IniFile.SetInt(ini, section, name, v.Int);
                else IniFile.SetString(ini, section, name, v.Str);
            }
        }
        else
        {
            using var b = RegistryKey.OpenBaseKey(install.RegHive, RegistryView.Registry64);
            using var k = b.CreateSubKey(install.RegSubKey + "\\" + section, true);
            foreach (var (name, v) in values)
            {
                if (v.IsInt) k.SetValue(name, unchecked((int)(uint)v.Int), RegistryValueKind.DWord);
                else k.SetValue(name, v.Str, RegistryValueKind.String);
            }
        }
    }
}
