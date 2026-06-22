using System.Text.RegularExpressions;

namespace Whcli;

/// <summary>
/// Cleanup of compiled mod DLLs (port of modFilesUtils.ts deleteOldModFiles/deleteModFiles).
/// Keeps the engine mods folders free of stale builds so re-apply is idempotent.
/// </summary>
internal static partial class ModFiles
{
    [GeneratedRegex(@"(^|_)[0-9]+$")]
    private static partial Regex SuffixRegex();

    public static HashSet<string> SubfoldersFor(string[] architectures, bool arm64)
    {
        var set = new HashSet<string>();
        var archs = architectures.Length > 0 ? architectures : ["x86", "x86-64"];
        foreach (var a in archs)
        {
            switch (a)
            {
                case "x86": set.Add("32"); break;
                case "x86-64": set.Add("64"); if (arm64) set.Add("arm64"); break;
                case "amd64": set.Add("64"); break;
                case "arm64": if (arm64) set.Add("arm64"); break;
                default: throw new InvalidOperationException($"Unsupported architecture: {a}");
            }
        }
        return set;
    }

    public static string[] AllSubfolders(bool arm64) => arm64 ? ["32", "64", "arm64"] : ["32", "64"];

    public static void DeleteOld(string engineModsPath, string modId, IEnumerable<string> subfolders, string? currentDllName)
    {
        string prefix = modId + "_";
        foreach (var sub in subfolders)
        {
            var dir = Path.Combine(engineModsPath, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, prefix + "*.dll"))
            {
                var name = Path.GetFileName(f);
                if (currentDllName is not null && name == currentDllName) continue;
                var mid = name[prefix.Length..^".dll".Length];
                if (!SuffixRegex().IsMatch(mid)) continue;
                try { File.Delete(f); } catch { /* may be in use */ }
            }
        }
    }
}
