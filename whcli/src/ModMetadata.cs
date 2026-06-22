using System.Text.RegularExpressions;

namespace Whcli;

/// <summary>
/// Parses the <c>==WindhawkMod==</c> metadata block from a mod's .wh.cpp source.
/// Mirrors the relevant parts of modSourceUtils.ts (extractMetadataRaw/extractMetadata).
/// We only need the fields used for installation.
/// </summary>
internal sealed partial class ModMetadata
{
    public string Id { get; private init; } = "";
    public string Version { get; private init; } = "";
    public string[] Include { get; private init; } = [];
    public string[] Exclude { get; private init; } = [];
    public string[] Architecture { get; private init; } = [];
    public string? CompilerOptions { get; private init; }

    // Non-anchored + explicit newline so it is robust to LF/CRLF and .NET's $ semantics.
    [GeneratedRegex(@"//[ \t]+==WindhawkMod==[^\r\n]*\r?\n([\s\S]+?)//[ \t]+==/WindhawkMod==")]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"^//[ \t]+@(_?[a-zA-Z]+)(?::([a-z]{2}(?:-[A-Z]{2})?))?[ \t]+(.*)$")]
    private static partial Regex LineRegex();

    public static ModMetadata Parse(string source)
    {
        var block = BlockRegex().Match(source);
        if (!block.Success)
            throw new InvalidOperationException("Couldn't find a metadata block in the source code");

        var single = new Dictionary<string, string>();
        var multi = new Dictionary<string, List<string>>();

        foreach (var rawLine in block.Groups[1].Value.Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0) continue;

            var m = LineRegex().Match(line);
            if (!m.Success)
            {
                var t = line.Length > 20 ? line[..17] + "..." : line;
                throw new InvalidOperationException("Couldn't parse metadata line: " + t);
            }

            string key = m.Groups[1].Value;
            // Ignore localization suffix for our purposes; ignore forward-compat "_" keys.
            if (key.StartsWith('_')) continue;
            string value = m.Groups[3].Value;

            switch (key)
            {
                case "include":
                case "exclude":
                case "architecture":
                    (multi.TryGetValue(key, out var list) ? list : multi[key] = new()).Add(value);
                    break;
                default:
                    single[key] = value; // last wins; we only read id/version/compilerOptions
                    break;
            }
        }

        string id = single.GetValueOrDefault("id", "");
        if (id.Length == 0)
            throw new InvalidOperationException("Mod id must be specified in the source code");
        if (!IdRegex().IsMatch(id))
            throw new InvalidOperationException("Mod id must only contain 0-9, a-z, and hyphen");

        return new ModMetadata
        {
            Id = id,
            Version = single.GetValueOrDefault("version", ""),
            CompilerOptions = single.GetValueOrDefault("compilerOptions"),
            Include = multi.GetValueOrDefault("include")?.ToArray() ?? [],
            Exclude = multi.GetValueOrDefault("exclude")?.ToArray() ?? [],
            Architecture = multi.GetValueOrDefault("architecture")?.ToArray() ?? [],
        };
    }

    [GeneratedRegex("^[0-9a-z-]+$")]
    private static partial Regex IdRegex();
}
