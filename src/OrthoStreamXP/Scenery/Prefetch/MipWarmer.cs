using System.Diagnostics;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Load-ahead for streamed textures. X-Plane opens every terrain definition of a DSF 20-45 s before it reads the
/// textures; in that time each texture is fetched to the level the paging model says X-Plane will want: the
/// camera's distance, shortened by how far the aircraft closes in on it in <see cref="LeadSeconds"/>. Next to the
/// camera that is full detail, in the middle distance a reduced zoom, and far away a single tile of the low-zoom
/// base, so a cold start downloads about one small tile per far texture. Before X-Plane sends a position, every
/// texture gets its base tile. The flight-path prefetch adds whole textures through the same queue.
/// <para>
/// Next to the camera goes first, then by distance. Builds of many tiles are limited to <c>heavy</c> at once (each
/// holds its picture in memory while it builds); builds of a few tiles and base tiles run many at a time, each kind
/// in places of its own, so neither holds up the other.
/// </para>
/// </summary>
internal sealed class MipWarmer : IDisposable
{
    /// <summary>
    /// A level is fetched when the aircraft will need it this soon. X-Plane asks for full detail the moment a texture
    /// crosses its line; a full-detail fetch takes 1-2 s, and the plan is renewed only every 1.5 km (27 s at 110 kt),
    /// so the lead must cover both with room to spare (3.3 km at 110 kt, 14 km at 450 kt).
    /// </summary>
    public const double LeadSeconds = 60;
    /// <summary>
    /// Base tiles at once. They have places of their own: a far texture X-Plane reads without its base tile gets one
    /// flat colour, and in one test flight the middle-distance builds of a newly loaded scenery tile, sharing these
    /// places, kept 48 far textures from their base tile for the 5 s until X-Plane read them.
    /// </summary>
    private const int BaseInFlight = 64;
    /// <summary>
    /// A build of at most this many tiles (one for level 4 of a ZL18 texture, 2 x 2 for level 3) is small. Counted
    /// with the full-size builds, the hundreds of middle-distance textures of a newly loaded scenery tile were fetched
    /// a few at a time, and X-Plane's reads of them waited 5 s.
    /// </summary>
    internal const long SmallTiles = 4;
    /// <summary>Small builds at once: as many as the streamer lets builds ahead of X-Plane run.</summary>
    private const int SmallInFlight = TextureStreamer.DefaultMaxBuilds / 2;

    /// <summary>The places a load-ahead build takes, each kind with its own limit.</summary>
    internal enum BuildKind { Base, Small, Heavy }
    /// <summary>How often the progress is logged while X-Plane loads scenery, and otherwise.</summary>
    private static readonly TimeSpan LoadingReportInterval = TimeSpan.FromSeconds(10), QuietReportInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A texture to warm; <paramref name="Mip"/> and <paramref name="Priority"/> fixed for the flight-path prefetch.
    /// <paramref name="Recheck"/>: queued again because the aircraft moved, not because X-Plane loaded it.
    /// </summary>
    private readonly record struct Request(VirtualPath Texture, int? Mip = null, int? Priority = null, bool Recheck = false);

    private readonly TextureCatalog _catalog;
    private readonly TextureStreamer _streamer;
    private readonly ViewTracker _view;
    private readonly int _heavyLimit;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Request> _waiting = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Task? _loop;
    /// <summary>Builds running, by <see cref="BuildKind"/>.</summary>
    private readonly int[] _running = new int[3];
    private int _queued, _alreadyHad;
    private (int Known, int Waiting, int Queued, int AlreadyHad) _reported;
    private TimeSpan _reportedAt;

    /// <param name="heavy">Builds of more than <see cref="SmallTiles"/> tiles at once.</param>
    /// <param name="warmLoadedScenery">Load-ahead: warm every texture X-Plane loads; off, only the flight-path prefetch comes here.</param>
    public MipWarmer(TextureCatalog catalog, TextureStreamer streamer, ViewTracker view, int heavy, bool warmLoadedScenery = true)
    {
        _catalog = catalog;
        _streamer = streamer;
        _view = view;
        _heavyLimit = Math.Max(1, heavy);
        WarmsLoadedScenery = warmLoadedScenery;
        if (!warmLoadedScenery) return;
        catalog.EntryAdded += OnEntryAdded;
        view.Moved += OnMoved;
    }

    public bool WarmsLoadedScenery { get; }

    /// <summary>Warm builds started this session.</summary>
    public int Queued => Volatile.Read(ref _queued);

    public void Start()
    {
        _loop = Task.Run(RunAsync);
        if (WarmsLoadedScenery)
            Log.Info($"Load-ahead on: each texture of the scenery X-Plane loads is fetched to the level it will need " +
                     $"({_heavyLimit} detailed textures, {SmallInFlight} of a few tiles and {BaseInFlight} base tiles at a time)");
    }

    private void OnEntryAdded(TextureCatalog.Entry entry) => Add(new Request(entry.Texture));

    /// <summary>A new position: what every loaded texture will need has changed.</summary>
    private void OnMoved(CameraReport camera)
    {
        lock (_gate)
            foreach (TextureCatalog.Entry entry in _catalog.Entries)
                _waiting.TryAdd(entry.Texture.Key, new Request(entry.Texture, Recheck: true));
        Wake();
    }

    /// <summary>The flight-path prefetch: whole textures along the predicted path. Returns how many were queued.</summary>
    public int Prefetch(IReadOnlyList<VirtualPath> textures)
    {
        lock (_gate)
            foreach (VirtualPath texture in textures)
                _waiting[texture.Key] = new Request(texture, 0, FetchPriority.Warm);
        Wake();
        return textures.Count;
    }

    private void Add(Request request)
    {
        lock (_gate) _waiting.TryAdd(request.Texture.Key, request);
        Wake();
    }

    private void Wake()
    {
        if (_wake.CurrentCount == 0) _wake.Release();
    }

    /// <summary>The level to fetch and its priority, and the distance (for the order); null when it is not a photo texture.</summary>
    internal (StreamedTexture Texture, int Mip, int Priority, double Distance)? Target(VirtualPath path, int? mip = null, int? priority = null)
    {
        if (_streamer.Get(path) is not { } t) return null;
        if (_view.Current is not { } camera)
            return (t, mip ?? t.BaseMip, priority ?? FetchPriority.Background, double.MaxValue);
        double distance = ViewTracker.DistanceM(camera, t.Lat, t.Lon);
        if (mip is int fixedMip) return (t, fixedMip, priority ?? FetchPriority.Warm, distance);

        double soon = distance;
        if (camera.Motion is { SpeedMps: >= PrefetchPlanner.MovingSpeedMps } motion)
        {
            double bearing = Geo.BearingDeg(camera.Lat, camera.Lon, t.Lat, t.Lon);
            double closing = motion.SpeedMps * Math.Cos((bearing - motion.TrackDeg) * Math.PI / 180);
            soon = Math.Max(0, distance - Math.Max(0, closing) * LeadSeconds);
        }
        int wanted = _streamer.Model.MipFor(soon, t.SizeM, t.Layout.Size);
        int target = Math.Min(wanted, t.BaseMip);
        int targetPriority = TextureStreamer.BandOf(t, wanted) switch
        {
            Band.Near => FetchPriority.NearWarm,
            Band.Middle => FetchPriority.Warm + wanted,
            _ => FetchPriority.Background,
        };
        return (t, target, targetPriority, distance);
    }

    private async Task RunAsync()
    {
        CancellationToken stop = _stop.Token;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                StartWhatFits();
                ReportProgress();
                await _wake.WaitAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Unmounting.
        }
    }

    /// <summary>Starts the most urgent waiting textures while there is room for them.</summary>
    internal void StartWhatFits()
    {
        Request[] waiting;
        lock (_gate) waiting = [.. _waiting.Values];
        if (waiting.Length == 0) return;
        var ranked = waiting
            .Select(r => (Request: r, Target: Target(r.Texture, r.Mip, r.Priority)))
            .OrderBy(x => x.Target?.Priority ?? int.MaxValue).ThenBy(x => x.Target?.Distance ?? double.MaxValue);
        foreach (var (request, target) in ranked)
        {
            if (Full(BuildKind.Base) && Full(BuildKind.Small) && Full(BuildKind.Heavy)) break;
            if (target is not { } t)
            {
                Remove(request);
                continue;
            }
            BuildKind kind = KindOf(t.Texture, t.Mip);
            if (Full(kind)) continue;
            if (_streamer.RealFrom(t.Texture) <= t.Mip)
            {
                Remove(request);
                if (!request.Recheck) Interlocked.Increment(ref _alreadyHad);
                continue;
            }
            if (_streamer.Warm(request.Texture, t.Mip, t.Priority) is not { } build)
            {
                Remove(request);
                continue;
            }
            Remove(request);
            Interlocked.Increment(ref _queued);
            if (build.IsCompleted) continue;
            Interlocked.Increment(ref _running[(int)kind]);
            build.ContinueWith(_ =>
            {
                Interlocked.Decrement(ref _running[(int)kind]);
                Wake();
            }, TaskScheduler.Default);
        }
    }

    private bool Full(BuildKind kind) => Volatile.Read(ref _running[(int)kind]) >= kind switch
    {
        BuildKind.Base => BaseInFlight,
        BuildKind.Small => SmallInFlight,
        _ => _heavyLimit,
    };

    /// <summary>Which places fetching <paramref name="mip"/> takes: a base tile, a build of a few tiles, or of many.</summary>
    internal static BuildKind KindOf(StreamedTexture texture, int mip) =>
        mip >= texture.BaseMip ? BuildKind.Base
        : TextureStreamer.BuildTiles(texture.Name, texture.ZoomFor(mip)) <= SmallTiles ? BuildKind.Small
        : BuildKind.Heavy;

    private void Remove(Request request)
    {
        lock (_gate)
            if (_waiting.TryGetValue(request.Texture.Key, out Request current) && current == request) _waiting.Remove(request.Texture.Key);
    }

    /// <summary>
    /// Logs the progress when it changed: every few seconds while X-Plane loads scenery (new textures known), else
    /// every few minutes. The aircraft's moves queue every texture again, so the waiting ones are counted after a pass.
    /// </summary>
    private void ReportProgress()
    {
        int waiting;
        lock (_gate) waiting = _waiting.Count;
        var now = (Known: _catalog.Count, Waiting: waiting, Queued: Volatile.Read(ref _queued), AlreadyHad: Volatile.Read(ref _alreadyHad));
        if (now == _reported) return;
        TimeSpan interval = now.Known != _reported.Known ? LoadingReportInterval : QuietReportInterval;
        if (_clock.Elapsed - _reportedAt < interval) return;
        (_reported, _reportedAt) = (now, _clock.Elapsed);
        Log.Info($"Load-ahead: {Count(now.Known, "texture")} of the loaded scenery known, {now.Waiting} waiting for a place; " +
                 $"{Count(now.Queued, "build")} started so far, {now.AlreadyHad} had what they needed when X-Plane loaded them");
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    public void Stop()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _loop?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        if (WarmsLoadedScenery)
        {
            _catalog.EntryAdded -= OnEntryAdded;
            _view.Moved -= OnMoved;
        }
        _stop.Dispose();
        _wake.Dispose();
    }
}
