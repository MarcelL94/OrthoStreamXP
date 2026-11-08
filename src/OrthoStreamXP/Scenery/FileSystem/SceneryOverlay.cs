using System.Diagnostics;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>What a path opens as: a backing file, a photo texture streamed level by level, or a terrain definition made in memory.</summary>
internal readonly record struct OverlayEntry(string? Backing, StreamedTexture? Streamed, byte[]? Content = null);

/// <summary>
/// Resolves a virtual path to what X-Plane reads: the cache overlays the source package, terrain definitions
/// are adjusted or synthesized on demand in memory, and DDS textures are generated or provisioned as needed, or,
/// with a <see cref="TextureStreamer"/>, served level by level.
/// </summary>
internal sealed class SceneryOverlay
{
    /// <summary>Cache bookkeeping, plus _textures, which is shown merged into textures instead.</summary>
    private static readonly string[] HiddenRootEntries = [".xpstream", ".orthostreamxp-logs", "_textures"];

    private readonly SceneryRoots _roots;
    private readonly TerrainDefinitions _terrain;
    private readonly TextureScheduler? _textures;
    private readonly TextureStreamer? _streamer;
    private readonly TextureCatalog? _catalog;
    private readonly bool _lazyTerrainListing;
    private readonly Lock _requestLogLock = new();
    private readonly HashSet<string> _terrainRequests = [];
    private readonly HashSet<string> _textureRequests = [];
    private readonly TextureRequestStats _requestStats = new();

    /// <param name="catalog">Learns the loaded scenery's textures from the terrain definitions X-Plane opens (for prefetch).</param>
    /// <param name="streamer">Serves photo textures level by level instead of building them whole (then <paramref name="textures"/> is null).</param>
    public SceneryOverlay(SceneryRoots roots, TextureScheduler? textures, bool lazyTerrainListing, TextureCatalog? catalog = null,
        TextureStreamer? streamer = null)
    {
        _roots = roots;
        _terrain = new TerrainDefinitions(roots);
        _textures = textures;
        _streamer = streamer;
        _catalog = catalog;
        _lazyTerrainListing = lazyTerrainListing;
        // Keep virtual terrain and textures directories available even when the source omits them
        // (some packages have no terrain\ and only _textures\).
        Directory.CreateDirectory(Path.Join(roots.Cache, "terrain"));
        Directory.CreateDirectory(Path.Join(roots.Cache, "textures"));
    }

    public TextureStreamer? Streamer => _streamer;

    public OverlayEntry Open(string fileName) => Open(VirtualPath.Parse(fileName));

    /// <summary>
    /// What X-Plane gets for a path. A tile's terrain definition is made in memory. With streaming, a photo texture
    /// is streamed level by level unless it is already complete in the cache or the lazy store has a copy (both
    /// are served as they are; the package's own photo copies are ignored); everything else, and every texture
    /// without streaming, is a backing file from
    /// <see cref="Resolve(VirtualPath)"/>.
    /// </summary>
    public OverlayEntry Open(VirtualPath path)
    {
        if (_terrain.Resolve(path) is { } definition)
        {
            LogRequest(path);
            _catalog?.AddTerrain(path);
            return new OverlayEntry(null, null, definition);
        }

        // A PNG beside it means a mask: only the package's own DDS is ever served for that name (see Resolve).
        if (_streamer?.Get(path) is not { } streamed || _roots.FindTexture(path.WithExtension(".png")) is not null)
            return new OverlayEntry(Resolve(path), null);

        // X-Plane opens a file in two steps (security check, then open); only the first request is reported.
        bool first = FirstTextureRequest(path);
        if (first) _catalog?.NoteTextureRequested(path);
        if (_roots.CachedFile(path) is { } cached)
        {
            if (first) ReportTextureRequest(path, RequestOutcome.ReadyInCache, TimeSpan.Zero);
            return new OverlayEntry(cached, null);
        }
        if (_roots.FindTexture(path) is { } local)
        {
            if (first) ReportTextureRequest(path, RequestOutcome.LocalCopy, TimeSpan.Zero);
            _streamer.Warm(path, 0, FetchPriority.Background); // the cache's own version for later opens
            return new OverlayEntry(local, null);
        }
        if (first) ReportTextureRequest(path, RequestOutcome.Streamed, TimeSpan.Zero);
        return new OverlayEntry(null, streamed);
    }

    /// <summary>A tile's terrain definition as X-Plane reads it, without counting it as a request (for directory listings).</summary>
    public byte[]? TerrainDefinition(VirtualPath path) => _terrain.Resolve(path);

    public string Resolve(string fileName) => Resolve(VirtualPath.Parse(fileName));

    /// <summary>The backing file for a path (a tile's terrain definition has none: <see cref="Open(VirtualPath)"/> makes it).</summary>
    public string Resolve(VirtualPath path)
    {
        LogRequest(path);
        // X-Plane asks for name.dds before name.png. The package's PNGs are alpha masks (..._ZL16.png sea
        // masks, water_transition.png): a DDS must never be built for them, or X-Plane would use a photo as
        // the mask and show it over the sea. Serve only a DDS the package itself ships; otherwise "not
        // found" makes X-Plane load the PNG.
        if (path.IsDds && _roots.FindTexture(path.WithExtension(".png")) is not null)
            return _roots.FindTexture(path) ?? throw new NtStatusException(NtStatus.ObjectNameNotFound);

        // X-Plane opens a file in two steps (security check, then open); only the first request is reported.
        bool report = _textures is not null && path.IsDds && TextureName.IsTextureFileName(path.Name) && FirstTextureRequest(path);
        if (report) _catalog?.NoteTextureRequested(path);
        long started = Stopwatch.GetTimestamp();
        RequestOutcome outcome = RequestOutcome.Unavailable;
        try
        {
            return ResolveFile(path, out outcome);
        }
        finally
        {
            if (report) ReportTextureRequest(path, outcome, Stopwatch.GetElapsedTime(started));
        }
    }

    private string ResolveFile(VirtualPath path, out RequestOutcome outcome)
    {
        outcome = RequestOutcome.Unavailable;
        if (_roots.CachedFile(path) is { } cached)
        {
            outcome = _textures?.WasPrefetched(path) == true ? RequestOutcome.ReadyByPrefetch : RequestOutcome.ReadyInCache;
            return cached;
        }
        if (_textures is not null)
        {
            RequestOutcome built = _textures.PendingReason(path) is { } reason && TextureScheduler.IsAhead(reason)
                ? RequestOutcome.WaitedForPrefetch
                : RequestOutcome.BuiltOnRequest;
            // Serve the known-good local DDS now; later opens use the generated cache copy.
            if (_textures.BackgroundEnabled && _textures.QueueBackground(path) && _roots.FindTexture(path) is { } local)
            {
                outcome = RequestOutcome.LocalCopy;
                return local;
            }
            if (_textures.ProvisionalAsync(path).GetAwaiter().GetResult() is { } provisional)
            {
                outcome = provisional == _roots.ProvisionalPath(path) ? RequestOutcome.Provisional : built;
                return provisional;
            }
            if (_textures.GenerateAsync(path).GetAwaiter().GetResult() is { } generated)
            {
                outcome = built;
                return generated;
            }
        }
        // A cached file (or directory) replaces the source path with the same name; the lazy texture store's
        // DDS is served as is when nothing could be generated.
        return _roots.FindCached(path)
               ?? _roots.FindInSource(path)
               ?? _roots.FindTexture(path)
               ?? throw new NtStatusException(NtStatus.ObjectNameNotFound);
    }

    /// <summary>Logs how X-Plane's first request for a texture was answered: the evidence that prefetch works or not.</summary>
    private void ReportTextureRequest(VirtualPath path, RequestOutcome outcome, TimeSpan waited)
    {
        _requestStats.Record(outcome, waited);
        double s = waited.TotalSeconds;
        string how = outcome switch
        {
            RequestOutcome.ReadyByPrefetch => "ready, prefetched in advance",
            RequestOutcome.ReadyInCache => "ready, already in the cache",
            RequestOutcome.WaitedForPrefetch => $"waited {s:F1} s for the prefetch build already running",
            RequestOutcome.BuiltOnRequest => $"built on request, X-Plane waited {s:F1} s",
            RequestOutcome.Provisional => $"provisional image served after {s:F1} s",
            RequestOutcome.LocalCopy => "package's own copy served while the download builds",
            RequestOutcome.Streamed => "streamed level by level",
            _ => "could not be built; served from the package if it has one",
        };
        Log.Detail($"X-Plane requested texture {path.Display}: {how}");
    }

    /// <summary>How X-Plane's texture requests were answered this session.</summary>
    public TextureRequestStats RequestStats => _requestStats;

    /// <summary>The prefetch (or streaming) summary line, for the periodic and final log.</summary>
    public string? PrefetchSummary() =>
        _streamer is not null ? _streamer.Summary()
        : _textures is null ? null
        : _requestStats.Summary(_textures.Queued(BuildReason.LoadAhead), _textures.Queued(BuildReason.Prefetch), _textures.PrefetchBuilt);

    /// <summary>A number that changes whenever the summary would, so an unchanged summary need not be logged again.</summary>
    public long SummaryVersion => _requestStats.Total + (_streamer?.SummaryVersion ?? 0) + (_textures is null ? 0
        : _textures.Queued(BuildReason.LoadAhead) + _textures.Queued(BuildReason.Prefetch) + _textures.PrefetchBuilt);

    /// <summary>
    /// Merged listing of a directory: source entries, replaced by same-named cache entries. The textures
    /// directory merges the source's textures\ and _textures\, without the package's own photo textures;
    /// _textures itself is not shown.
    /// </summary>
    public IReadOnlyList<FileSystemInfo> List(VirtualPath directory)
    {
        if (_lazyTerrainListing && directory.Is("terrain"))
            return []; // DSF references terrain definitions by path; X-Plane opens them directly.
        var entries = new Dictionary<string, FileSystemInfo>(StringComparer.OrdinalIgnoreCase);
        // Lowest priority first, so later folders replace same-named entries.
        foreach (string path in _roots.SourceFolders(directory).Reverse())
            AddEntries(entries, directory, path, fromSource: true);
        AddEntries(entries, directory, _roots.CachePath(directory), fromSource: false);
        return [.. entries.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static void AddEntries(Dictionary<string, FileSystemInfo> entries, VirtualPath directory, string path, bool fromSource)
    {
        var folder = new DirectoryInfo(path);
        if (!folder.Exists) return;
        foreach (FileSystemInfo child in folder.EnumerateFileSystemInfos())
        {
            if (directory.IsRoot && HiddenRootEntries.Contains(child.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (fromSource && SceneryRoots.IsPackagePhoto(directory.Child(child.Name))) continue;
            entries[child.Name] = child;
        }
    }

    public (int Terrain, int Textures) RequestCounts()
    {
        lock (_requestLogLock) return (_terrainRequests.Count, _textureRequests.Count);
    }

    /// <summary>
    /// Logs the first request for each terrain definition, and for each texture when nothing is generated
    /// (with a texture provider, <see cref="ReportTextureRequest"/> logs textures with how they were answered).
    /// </summary>
    private void LogRequest(VirtualPath path)
    {
        HashSet<string>? seen = path.IsTerrain ? _terrainRequests : path.IsDds && _textures is null && _streamer is null ? _textureRequests : null;
        if (seen is null) return;
        lock (_requestLogLock)
            if (!seen.Add(path.Key)) return;
        Log.Detail(path.IsTerrain ? $"X-Plane requested terrain definition {path.Display}" : $"X-Plane requested texture {path.Display}");
    }

    private bool FirstTextureRequest(VirtualPath path)
    {
        lock (_requestLogLock) return _textureRequests.Add(path.Key);
    }
}
