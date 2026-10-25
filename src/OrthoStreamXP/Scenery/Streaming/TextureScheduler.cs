using System.Collections.Concurrent;
using System.Diagnostics;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>Why a texture is being built.</summary>
internal enum BuildReason
{
    /// <summary>X-Plane asked for it.</summary>
    Request,
    /// <summary>Path prefetch: it lies ahead of the aircraft.</summary>
    Prefetch,
    /// <summary>Load-ahead: X-Plane opened its terrain definition while loading a DSF and will ask for it shortly.</summary>
    LoadAhead,
}

internal sealed record TextureOptions(
    string Provider,
    TexturePlan Textures,
    int DdsBuildWorkers,
    bool Background,
    int ProvisionalLod,
    double ProvisionalWaitSeconds);

/// <summary>
/// Decides when DDS textures are built: one build per texture at a time, at most --dds-build-workers
/// at once, an optional bounded background queue, and provisional low-detail images for cold starts.
/// </summary>
internal sealed class TextureScheduler : IDisposable
{
    private readonly SceneryRoots _roots;
    private readonly TextureBuilder _builder;
    private readonly TextureOptions _options;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _textureLocks = new();
    private readonly SemaphoreSlim _buildSlots;
    private readonly SemaphoreSlim _backgroundWorkers;
    private readonly Lock _pendingGate = new();
    private readonly Dictionary<string, (Task Task, BuildReason Reason)> _pending = [];
    private readonly ConcurrentDictionary<string, byte> _prefetched = new();
    private readonly int[] _queuedBy = new int[Enum.GetValues<BuildReason>().Length];
    private readonly int _queueLimit;
    private readonly CancellationTokenSource _shutdown = new();

    public TextureScheduler(SceneryRoots roots, TextureBuilder builder, TextureOptions options)
    {
        _roots = roots;
        _builder = builder;
        _options = options with
        {
            ProvisionalLod = Math.Clamp(options.ProvisionalLod, 0, 3),
            ProvisionalWaitSeconds = Math.Max(0, options.ProvisionalWaitSeconds),
        };
        _buildSlots = new SemaphoreSlim(options.DdsBuildWorkers);
        _backgroundWorkers = new SemaphoreSlim(options.DdsBuildWorkers);
        _queueLimit = Math.Max(32, options.DdsBuildWorkers * 4);
    }

    public bool BackgroundEnabled => _options.Background;

    /// <summary>Textures queued this session for the given reason.</summary>
    public int Queued(BuildReason reason) => Volatile.Read(ref _queuedBy[(int)reason]);

    /// <summary>Background builds queued or running.</summary>
    public int PendingCount
    {
        get { lock (_pendingGate) return _pending.Count; }
    }

    /// <summary>Textures path prefetch or load-ahead finished building this session.</summary>
    public int PrefetchBuilt => _prefetched.Count;

    /// <summary>Whether path prefetch or load-ahead built this texture during this session.</summary>
    public bool WasPrefetched(VirtualPath path) => _prefetched.ContainsKey(path.Key);

    /// <summary>Why the background build of this texture was started, or null when none is queued or running.</summary>
    public BuildReason? PendingReason(VirtualPath path)
    {
        lock (_pendingGate) return _pending.TryGetValue(path.Key, out var pending) ? pending.Reason : null;
    }

    public static string Describe(BuildReason reason) => reason switch
    {
        BuildReason.Prefetch => "prefetch",
        BuildReason.LoadAhead => "load-ahead",
        _ => "X-Plane request",
    };

    public static bool IsAhead(BuildReason reason) => reason is BuildReason.Prefetch or BuildReason.LoadAhead;

    private SemaphoreSlim LockFor(string key) => _textureLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));

    /// <summary>Builds a requested DDS into the cache; returns its path, or null when it cannot be made.</summary>
    public async Task<string?> GenerateAsync(VirtualPath path, BuildReason reason = BuildReason.Request) =>
        (await BuildIntoCacheAsync(path, reason).ConfigureAwait(false)).Path;

    /// <summary>The cached texture, and whether this call built it (false when another build got there first).</summary>
    private async Task<(string? Path, bool Built)> BuildIntoCacheAsync(VirtualPath path, BuildReason reason)
    {
        if (!path.IsDds) return (null, false);

        long requestStarted = Stopwatch.GetTimestamp();
        SemaphoreSlim textureLock = LockFor(path.Key);
        await textureLock.WaitAsync().ConfigureAwait(false);
        try
        {
            double sameTextureWait = Stopwatch.GetElapsedTime(requestStarted).TotalSeconds;
            if (_roots.CachedFile(path) is { } cached) return (cached, false);

            long slotStarted = Stopwatch.GetTimestamp();
            await _buildSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                double buildSlotWait = Stopwatch.GetElapsedTime(slotStarted).TotalSeconds;
                Log.Detail($"Generating texture {path.Name} with provider {_options.Provider} ({Describe(reason)})");
                string destination = _roots.CachePath(path);
                var (size, level) = SizeAndLevel(path);
                BuildResult r = await _builder.BuildAsync(path.Name, size, destination, 0, level).ConfigureAwait(false);
                Log.Detail($"Texture performance {path.Name}: reason={(IsAhead(reason) ? Describe(reason) : "request")} same_texture_wait={sameTextureWait:F2}s build_slot_wait={buildSlotWait:F2}s " +
                         $"ram_tile_hits={r.Hits} ram_tile_misses={r.Misses} provider_calls={r.Calls} fallback_tiles={r.Fallbacks} " +
                         $"download_worker={r.DownloadS:F2}s fetch_stitch_wall={r.FetchStitchS:F2}s " +
                         $"resize={r.ResizeS:F2}s mipmap_resize={r.MipmapS:F2}s bc1_encode={r.BC1EncodeS:F2}s dds_encode={r.EncodeS:F2}s " +
                         $"dds_cache_write={r.WriteS:F2}s total={Stopwatch.GetElapsedTime(requestStarted).TotalSeconds:F2}s bytes={r.Bytes}");
                return (destination, true);
            }
            finally
            {
                _buildSlots.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to generate texture {path.Name}", ex);
            return (null, false);
        }
        finally
        {
            textureLock.Release();
        }
    }

    /// <summary>
    /// Queues a background build. Request-side background mode requires a local fallback DDS to serve
    /// meanwhile; prefetch and provisional builds do not. Returns whether a build is queued or running.
    /// </summary>
    public bool QueueBackground(VirtualPath path, bool requireFallback = true, BuildReason reason = BuildReason.Request)
    {
        if (!_options.Background || !path.IsDds) return false;
        if (requireFallback && _roots.FindTexture(path) is null) return false;
        if (_roots.CachedFile(path) is not null) return false;

        lock (_pendingGate)
        {
            if (_pending.ContainsKey(path.Key)) return true;
            if (_shutdown.IsCancellationRequested) return false;
            if (_pending.Count >= _queueLimit)
            {
                Log.Detail($"Background texture queue full; skipping {path.Name} for now");
                return requireFallback;
            }
            long queuedAt = Stopwatch.GetTimestamp();
            // Task.Run: the build never starts inline, so it cannot finish before it is registered here.
            _pending[path.Key] = (Task.Run(() => RunBackgroundAsync(path, queuedAt, reason)), reason);
            Interlocked.Increment(ref _queuedBy[(int)reason]);
            Log.Detail($"Queued background texture {path.Name} ({Describe(reason)}, pending={_pending.Count})");
        }
        return true;
    }

    private async Task RunBackgroundAsync(VirtualPath path, long queuedAt, BuildReason reason)
    {
        try
        {
            await _backgroundWorkers.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                double queueWait = Stopwatch.GetElapsedTime(queuedAt).TotalSeconds;
                var (built, byThisBuild) = await BuildIntoCacheAsync(path, reason).ConfigureAwait(false);
                if (built is null)
                {
                    Log.Warning($"Background texture generation did not produce {path.Name}");
                }
                else
                {
                    // Recorded before the build leaves the pending list, so a request always sees one or the other.
                    if (byThisBuild && IsAhead(reason)) _prefetched.TryAdd(path.Key, 0);
                    Log.Detail($"Background texture ready {path.Name} ({Describe(reason)}, queue_wait={queueWait:F2}s)");
                    DeleteProvisional(path);
                }
            }
            finally
            {
                _backgroundWorkers.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Unmounting: queued builds that had not started are dropped.
        }
        finally
        {
            lock (_pendingGate) _pending.Remove(path.Key);
        }
    }

    /// <summary>The full texture replaces the provisional one for every later open, so the stand-in can go.</summary>
    private void DeleteProvisional(VirtualPath path)
    {
        try
        {
            File.Delete(_roots.ProvisionalPath(path)); // X-Plane may still read it: handles allow delete-on-close
        }
        catch (IOException)
        {
            // Removed on a later run instead.
        }
    }

    private Task? PendingBuild(VirtualPath path)
    {
        lock (_pendingGate) return _pending.TryGetValue(path.Key, out var pending) ? pending.Task : null;
    }

    /// <summary>
    /// Cold-run helper: give X-Plane a low-detail image quickly while the full DDS builds. The full build
    /// is queued first and given --provisional-wait seconds; if it is not done by then, a same-size DDS is
    /// built from coarser tiles (far fewer downloads) outside the normal cache path, so the finished full
    /// DDS replaces it for every later open. Once a provisional image exists it is returned at once:
    /// X-Plane opens a file in two steps (security check, then open), and neither should wait again.
    /// </summary>
    public async Task<string?> ProvisionalAsync(VirtualPath path)
    {
        if (_options.ProvisionalLod < 1 || !_options.Background || !path.IsUnder("textures")
            || !TextureName.IsTextureFileName(path.Name))
            return null;
        if (!QueueBackground(path, requireFallback: false)) return null;
        if (PendingBuild(path) is not { } pending) return _roots.CachedFile(path);

        string destination = _roots.ProvisionalPath(path);
        if (ProvisionalReady(path, destination)) return destination;

        Task timeout = Task.Delay(TimeSpan.FromSeconds(_options.ProvisionalWaitSeconds));
        if (await Task.WhenAny(pending, timeout).ConfigureAwait(false) == pending)
            return _roots.CachedFile(path); // null when the full build failed: the normal path retries

        SemaphoreSlim provisionalLock = LockFor("PROVISIONAL\\" + path.Key);
        await provisionalLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ProvisionalReady(path, destination))
            {
                var (size, level) = SizeAndLevel(path);
                await _builder.BuildAsync(path.Name, size, destination, _options.ProvisionalLod, level).ConfigureAwait(false);
                Log.Detail($"Serving provisional (zoom-{_options.ProvisionalLod}) {path.Name} while the full texture builds");
            }
            return destination;
        }
        catch (Exception ex)
        {
            Log.Error($"Provisional build failed for {path.Name}", ex);
            return null;
        }
        finally
        {
            provisionalLock.Release();
        }
    }

    private bool ProvisionalReady(VirtualPath path, string destination) => File.Exists(destination) && _roots.DdsMatchesSize(path, destination);

    /// <summary>The size and level change this texture is built with (they depend on its own zoom).</summary>
    private (int Size, int Level) SizeAndLevel(VirtualPath path) =>
        TextureName.TryParseFile(path.Name, out TextureName name)
            ? (_options.Textures.SizeFor(name.Zoom), _options.Textures.LevelFor(name.Zoom))
            : throw new InvalidDataException($"Unsupported DDS texture name: {path.Name}");

    /// <summary>Queues nearby scenery textures without requiring a local DDS fallback.</summary>
    public int Prefetch(IEnumerable<VirtualPath> textures) =>
        textures.Count(path => _roots.CachedFile(path) is not null || QueueBackground(path, requireFallback: false, BuildReason.Prefetch));

    /// <summary>Drops queued background builds and waits for the running ones to finish.</summary>
    public async Task ShutdownAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        Task[] running;
        lock (_pendingGate) running = [.. _pending.Values.Select(p => p.Task)];
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _buildSlots.Dispose();
        _backgroundWorkers.Dispose();
        foreach (SemaphoreSlim textureLock in _textureLocks.Values) textureLock.Dispose();
    }
}
