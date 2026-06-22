using System.Text.Json;

namespace Whcli;

/// <summary>
/// Read/write a per-mod <c>config.json</c> inside an offline mod cache. The cache is a
/// directory with one subfolder per mod (named by mod id), each containing the mod's
/// source (<c>&lt;id&gt;.wh.cpp</c>), its precompiled DLLs (<c>&lt;version&gt;_&lt;sub&gt;.dll</c>),
/// and an optional <c>config.json</c> with the desired enabled/disabled state + settings.
/// Settings are serialized exactly like Profile (Utf8JsonWriter / JsonDocument; AOT-safe).
/// </summary>
internal static class ModCache
{
    public const string ConfigFileName = "config.json";

    public static void WriteConfig(string path, string version, bool disabled,
                                   IReadOnlyDictionary<string, SettingValue> settings)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("version", version);
        w.WriteBoolean("disabled", disabled);
        w.WritePropertyName("settings");
        w.WriteStartObject();
        foreach (var (k, v) in settings)
        {
            if (v.IsInt) w.WriteNumber(k, v.Int);
            else w.WriteString(k, v.Str);
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }

    public static (bool disabled, Dictionary<string, SettingValue> settings) ReadConfig(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        bool disabled = root.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True;
        var settings = new Dictionary<string, SettingValue>();
        if (root.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in s.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.Number:
                        settings[prop.Name] = prop.Value.TryGetInt32(out var i)
                            ? SettingValue.Of(i) : SettingValue.Of(prop.Value.GetRawText());
                        break;
                    case JsonValueKind.True: settings[prop.Name] = SettingValue.Of(1); break;
                    case JsonValueKind.False: settings[prop.Name] = SettingValue.Of(0); break;
                    case JsonValueKind.String: settings[prop.Name] = SettingValue.Of(prop.Value.GetString() ?? ""); break;
                }
            }
        }
        return (disabled, settings);
    }
}
