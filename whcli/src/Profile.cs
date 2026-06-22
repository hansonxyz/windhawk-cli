using System.Text;
using System.Text.Json;

namespace Whcli;

internal sealed class ProfileMod
{
    public string Id = "";
    public string Version = "";
    public bool Disabled;
    public Dictionary<string, SettingValue> Settings = new();
}

/// <summary>
/// A portable snapshot of a set of mods (+ their settings) and optional app/engine
/// settings, used to migrate/provision a Windhawk install. Serialized with explicit
/// Utf8JsonWriter / JsonDocument (no reflection) so it is Native-AOT safe.
/// </summary>
internal sealed class Profile
{
    public const string Schema = "whcli/profile@1";

    public string WindhawkVersion = "";
    public string ExportedFrom = "";
    public Dictionary<string, SettingValue>? App;
    public Dictionary<string, SettingValue>? Engine;
    public List<ProfileMod> Mods = new();

    public void Save(string path)
    {
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("schema", Schema);
        w.WriteString("windhawkVersion", WindhawkVersion);
        w.WriteString("exportedFrom", ExportedFrom);

        if (App is not null) { w.WritePropertyName("app"); WriteSettings(w, App); }
        if (Engine is not null) { w.WritePropertyName("engine"); WriteSettings(w, Engine); }

        w.WritePropertyName("mods");
        w.WriteStartArray();
        foreach (var m in Mods)
        {
            w.WriteStartObject();
            w.WriteString("id", m.Id);
            w.WriteString("version", m.Version);
            w.WriteBoolean("disabled", m.Disabled);
            w.WritePropertyName("settings");
            WriteSettings(w, m.Settings);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteSettings(Utf8JsonWriter w, Dictionary<string, SettingValue> settings)
    {
        w.WriteStartObject();
        foreach (var (k, v) in settings)
        {
            if (v.IsInt) w.WriteNumber(k, v.Int);
            else w.WriteString(k, v.Str);
        }
        w.WriteEndObject();
    }

    public static Profile Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var p = new Profile
        {
            WindhawkVersion = root.TryGetProperty("windhawkVersion", out var wv) ? wv.GetString() ?? "" : "",
            ExportedFrom = root.TryGetProperty("exportedFrom", out var ef) ? ef.GetString() ?? "" : "",
        };
        if (root.TryGetProperty("app", out var app) && app.ValueKind == JsonValueKind.Object)
            p.App = ReadSettings(app);
        if (root.TryGetProperty("engine", out var eng) && eng.ValueKind == JsonValueKind.Object)
            p.Engine = ReadSettings(eng);

        if (root.TryGetProperty("mods", out var mods) && mods.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mods.EnumerateArray())
            {
                var pm = new ProfileMod
                {
                    Id = m.GetProperty("id").GetString() ?? "",
                    Version = m.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    Disabled = m.TryGetProperty("disabled", out var d) && d.ValueKind == JsonValueKind.True,
                };
                if (m.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object)
                    pm.Settings = ReadSettings(s);
                p.Mods.Add(pm);
            }
        }
        return p;
    }

    private static Dictionary<string, SettingValue> ReadSettings(JsonElement obj)
    {
        var result = new Dictionary<string, SettingValue>();
        foreach (var prop in obj.EnumerateObject())
        {
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.Number:
                    result[prop.Name] = prop.Value.TryGetInt32(out var i)
                        ? SettingValue.Of(i)
                        : SettingValue.Of(prop.Value.GetRawText());
                    break;
                case JsonValueKind.True: result[prop.Name] = SettingValue.Of(1); break;
                case JsonValueKind.False: result[prop.Name] = SettingValue.Of(0); break;
                case JsonValueKind.String: result[prop.Name] = SettingValue.Of(prop.Value.GetString() ?? ""); break;
            }
        }
        return result;
    }
}
