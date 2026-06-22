namespace Whcli;

/// <summary>
/// Fetches mods from the official Windhawk distribution as PRECOMPILED binaries — no
/// local compiler needed. Source (.wh.cpp) is downloaded only to read the mod's metadata
/// (version/architecture/include/exclude); the actual DLLs are downloaded prebuilt.
/// Mirrors how stock Windhawk installs mods by default (AlwaysCompileModsLocally=0).
/// </summary>
internal static class PrecompiledMods
{
    private const string ModsBase = "https://mods.windhawk.net/mods/";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("whcli");
        return h;
    }

    /// <summary>Download a specific version's source (small) for metadata parsing.</summary>
    public static string GetSource(string id, string version)
        => Http.GetStringAsync($"{ModsBase}{id}/{version}.wh.cpp").GetAwaiter().GetResult();

    /// <summary>Latest version of a mod from its versions.json.</summary>
    public static string LatestVersion(string id)
    {
        var json = Http.GetStringAsync($"{ModsBase}{id}/versions.json").GetAwaiter().GetResult();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        string ver = "";
        foreach (var e in doc.RootElement.EnumerateArray())
            if (e.TryGetProperty("version", out var v)) ver = v.GetString() ?? ver;
        if (ver.Length == 0) throw new InvalidOperationException($"no versions listed for '{id}'");
        return ver;
    }

    /// <summary>
    /// Download the precompiled DLLs for the mod's architectures into the engine mods
    /// folder; returns the generated library file name. Skips architectures with no build.
    /// </summary>
    public static string Download(string engineModsPath, string id, string version, string[] architectures, bool arm64)
    {
        var subs = ModFiles.SubfoldersFor(architectures, arm64);
        string dll = $"{id}_{version}_{Random.Shared.Next(100000, 1000000)}.dll";

        int got = 0;
        foreach (var sub in subs)
        {
            string url = $"{ModsBase}{id}/{version}_{sub}.dll";
            string dest = Path.Combine(engineModsPath, sub, dll);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                using var resp = Http.GetAsync(url).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    Console.Error.WriteLine($"    no precompiled {sub} build ({(int)resp.StatusCode}); skipping");
                    continue;
                }
                File.WriteAllBytes(dest, resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                got++;
            }
            catch (Exception e) { Console.Error.WriteLine($"    {sub} download failed: {e.Message}"); }
        }
        if (got == 0)
            throw new InvalidOperationException($"no precompiled binaries available for {id} {version}");
        return dll;
    }
}
