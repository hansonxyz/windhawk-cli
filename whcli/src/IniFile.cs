using System.Runtime.InteropServices;
using System.Text;

namespace Whcli;

/// <summary>
/// Read/write Windhawk's portable INI files through the SAME Win32 API the engine
/// uses (Get/WritePrivateProfileStringW), guaranteeing byte-for-byte compatibility.
/// Files are UTF-16LE with a BOM (the engine creates them that way; the profile API
/// only writes Unicode if the file already begins with a BOM).
/// String escaping mirrors IniFileSettings::SetString in
/// windhawk/src/windhawk/shared/portable_settings.cpp.
/// </summary>
internal static partial class IniFile
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetPrivateProfileStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetPrivateProfileString(
        string? lpAppName, string? lpKeyName, string? lpDefault,
        [Out] char[] lpReturnedString, uint nSize, string lpFileName);

    [LibraryImport("kernel32.dll", EntryPoint = "WritePrivateProfileStringW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WritePrivateProfileString(
        string lpAppName, string? lpKeyName, string? lpString, string lpFileName);

    /// <summary>Ensure the INI file exists with a UTF-16LE BOM so the API writes Unicode.</summary>
    public static void EnsureUnicodeFile(string path)
    {
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Write exactly the BOM bytes 0xFF 0xFE, matching the engine.
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        fs.Write([0xFF, 0xFE], 0, 2);
    }

    public static string? GetString(string path, string section, string key)
    {
        for (uint size = 256; ; size += 256)
        {
            var buf = new char[size];
            // A sentinel default lets us distinguish "missing" from "empty string".
            uint ret = GetPrivateProfileString(section, key, "￿￿", buf, size, path);
            if (ret == size - 1)
                continue; // possibly truncated, grow

            var value = new string(buf, 0, (int)ret);
            return value == "￿￿" ? null : value;
        }
    }

    public static int? GetInt(string path, string section, string key)
    {
        var s = GetString(path, section, key);
        if (s is null) return null;
        // Engine parses with std::stol(s, 0, 0): accepts decimal and 0x-hex.
        s = s.Trim();
        try
        {
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return (int)Convert.ToInt64(s, 16);
            return (int)long.Parse(s);
        }
        catch { return null; }
    }

    public static void SetString(string path, string section, string key, string value)
    {
        EnsureUnicodeFile(path);
        if (!WritePrivateProfileString(section, key, Escape(value), path))
            throw new IOException($"WritePrivateProfileString failed for [{section}] {key} in {path} (err {Marshal.GetLastWin32Error()})");
    }

    public static void SetInt(string path, string section, string key, int value)
        => SetString(path, section, key, value.ToString());

    /// <summary>Delete an entire section (key = null).</summary>
    public static void DeleteSection(string path, string section)
    {
        if (!File.Exists(path)) return;
        WritePrivateProfileString(section, null, null, path);
    }

    /// <summary>Enumerate value names in a section.</summary>
    public static List<string> GetKeys(string path, string section)
    {
        var result = new List<string>();
        for (uint size = 1024; ; size += 1024)
        {
            var buf = new char[size];
            uint ret = GetPrivateProfileString(section, null, "", buf, size, path);
            if (ret == size - 2)
                continue; // double-null list possibly truncated, grow

            // Returned buffer is a sequence of null-terminated names, ending in an extra null.
            int start = 0;
            for (int i = 0; i < ret; i++)
            {
                if (buf[i] == '\0')
                {
                    if (i > start)
                        result.Add(new string(buf, start, i - start));
                    start = i + 1;
                }
            }
            return result;
        }
    }

    /// <summary>Port of IniFileSettings::SetString escaping (quote trimmable/quoted values, flatten newlines).</summary>
    private static string Escape(string s)
    {
        if (s.Length == 0)
            return s;

        bool canBeTrimmed = s[0] <= ' ' || s[^1] <= ' ';
        bool isQuoted = s.Length >= 2 && s[0] == s[^1] && (s[0] == '"' || s[0] == '\'');
        bool hasNewlines = s.IndexOfAny(['\r', '\n']) >= 0;

        if (!canBeTrimmed && !isQuoted && !hasNewlines)
            return s;

        var sb = new StringBuilder(s.Length + 2);
        if (canBeTrimmed || isQuoted)
            sb.Append('"');

        if (hasNewlines)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\r')
                {
                    sb.Append(' ');
                    if (i + 1 < s.Length && s[i + 1] == '\n') i++;
                }
                else if (c == '\n')
                    sb.Append(' ');
                else
                    sb.Append(c);
            }
        }
        else
        {
            sb.Append(s);
        }

        if (canBeTrimmed || isQuoted)
            sb.Append('"');

        return sb.ToString();
    }
}
