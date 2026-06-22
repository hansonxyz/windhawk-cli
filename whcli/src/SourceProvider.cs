namespace Whcli;

/// <summary>
/// Resolves a mod's .wh.cpp source. Prefers a pinned copy bundled in the repo
/// (reproducible, offline); falls back to fetching the latest from the official
/// windhawk-mods repository (with a warning, since that may differ from the
/// pinned version).
/// </summary>
internal sealed class SourceProvider
{
    private const string RawBase = "https://raw.githubusercontent.com/ramensoftware/windhawk-mods/main/mods/";

    private readonly string _bundledDir;
    private readonly bool _allowFetch;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public SourceProvider(string bundledDir, bool allowFetch)
    {
        _bundledDir = bundledDir;
        _allowFetch = allowFetch;
    }

    public string BundledPath(string id) => Path.Combine(_bundledDir, id + ".wh.cpp");

    /// <summary>Returns the source and whether it came from the bundled copy.</summary>
    public (string source, bool bundled) Get(string id, string? expectedVersion)
    {
        var bundled = BundledPath(id);
        if (File.Exists(bundled))
        {
            var src = File.ReadAllText(bundled);
            if (expectedVersion is not null)
            {
                var v = SafeVersion(src);
                if (v is not null && v != expectedVersion)
                    Console.Error.WriteLine($"  warning: bundled {id} is v{v}, profile pins v{expectedVersion}");
            }
            return (src, true);
        }

        if (!_allowFetch)
            throw new FileNotFoundException($"No bundled source for '{id}' at {bundled} (and fetch disabled)");

        Console.Error.WriteLine($"  fetching {id} from windhawk-mods (not bundled)...");
        var fetched = Http.GetStringAsync(RawBase + id + ".wh.cpp").GetAwaiter().GetResult();
        if (expectedVersion is not null)
        {
            var v = SafeVersion(fetched);
            if (v is not null && v != expectedVersion)
                Console.Error.WriteLine($"  warning: fetched {id} is v{v}, profile pins v{expectedVersion} (upstream moved on)");
        }
        return (fetched, false);
    }

    private static string? SafeVersion(string src)
    {
        try { return ModMetadata.Parse(src).Version; }
        catch { return null; }
    }
}
