using System.Diagnostics;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Builds the textures of the scenery X-Plane is loading before X-Plane asks for them.
/// <para>
/// X-Plane loads a DSF in two steps: it first opens all of its terrain definitions (.ter), then 20-45 s
/// later asks for the textures one after another. Each request used to wait for its own download, so a DSF
/// took tens of seconds to load. Here every texture is queued the moment its .ter is opened, and the build
/// workers make them in parallel, so most of X-Plane's requests find a finished texture.
/// </para>
/// Only a few builds are handed to the scheduler at a time; the rest wait here, and the next one is always
/// the texture nearest to where X-Plane is loading right now (its latest texture request, otherwise the
/// aircraft), so the textures it will ask for soonest come first.
/// </summary>
internal sealed class LoadAheadPrefetcher : IDisposable
{
    private static readonly TimeSpan RequestFocusLifetime = TimeSpan.FromSeconds(5), ReportInterval = TimeSpan.FromSeconds(10);

    private readonly TextureCatalog _catalog;
    private readonly TextureScheduler _scheduler;
    private readonly SceneryRoots _roots;
    private readonly int _inFlight;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, TextureCatalog.Entry> _waiting = [];
    private readonly SemaphoreSlim _added = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private (double Lat, double Lon, TimeSpan At)? _requestFocus, _aircraft;
    private Task? _loop;
    private int _queued, _alreadyBuilt;

    /// <param name="inFlight">How many builds to keep queued in the scheduler; the rest wait here in priority order.</param>
    public LoadAheadPrefetcher(TextureCatalog catalog, TextureScheduler scheduler, SceneryRoots roots, int inFlight)
    {
        _catalog = catalog;
        _scheduler = scheduler;
        _roots = roots;
        _inFlight = Math.Max(1, inFlight);
        catalog.EntryAdded += OnEntryAdded;
        catalog.TextureRequested += OnTextureRequested;
    }

    public void Start()
    {
        _loop = Task.Run(RunAsync);
        Log.Info($"Load-ahead on: textures of the scenery X-Plane loads are built before it asks for them ({_inFlight} queued at a time)");
    }

    public void OnAircraftPosition(double lat, double lon)
    {
        lock (_gate) _aircraft = (lat, lon, _clock.Elapsed);
    }

    private void OnTextureRequested(TextureCatalog.Entry entry)
    {
        lock (_gate)
        {
            _requestFocus = (entry.Lat, entry.Lon, _clock.Elapsed);
            _waiting.Remove(entry.Texture.Key); // being built on request now
        }
    }

    private void OnEntryAdded(TextureCatalog.Entry entry)
    {
        lock (_gate) _waiting[entry.Texture.Key] = entry;
        _added.Release();
    }

    /// <summary>Where X-Plane is loading: its latest texture request if recent, otherwise the aircraft.</summary>
    private (double Lat, double Lon)? Focus()
    {
        TimeSpan now = _clock.Elapsed;
        if (_requestFocus is { } request && now - request.At < RequestFocusLifetime) return (request.Lat, request.Lon);
        if (_aircraft is { } aircraft) return (aircraft.Lat, aircraft.Lon);
        return _requestFocus is { } older ? (older.Lat, older.Lon) : null;
    }

    /// <summary>Removes and returns the waiting texture nearest the focus (the oldest when there is none).</summary>
    internal TextureCatalog.Entry? TakeNext()
    {
        lock (_gate)
        {
            if (_waiting.Count == 0) return null;
            TextureCatalog.Entry? best = null;
            if (Focus() is { } focus)
            {
                double cosLat = Math.Cos(focus.Lat * Math.PI / 180), bestDistance = double.MaxValue;
                foreach (TextureCatalog.Entry entry in _waiting.Values)
                {
                    double dLat = entry.Lat - focus.Lat, dLon = (entry.Lon - focus.Lon) * cosLat;
                    double distance = dLat * dLat + dLon * dLon;
                    if (distance < bestDistance) (best, bestDistance) = (entry, distance);
                }
            }
            best ??= _waiting.Values.First();
            _waiting.Remove(best.Value.Texture.Key);
            return best;
        }
    }

    private async Task RunAsync()
    {
        CancellationToken stop = _stop.Token;
        TimeSpan lastReport = TimeSpan.Zero;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                ReportProgress(ref lastReport);
                if (_scheduler.PendingCount >= _inFlight)
                {
                    await Task.Delay(50, stop).ConfigureAwait(false);
                    continue;
                }
                if (TakeNext() is not { } next)
                {
                    await _added.WaitAsync(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                    continue;
                }
                if (_roots.CachedFile(next.Texture) is not null)
                {
                    _alreadyBuilt++;
                }
                else if (_scheduler.QueueBackground(next.Texture, requireFallback: false, BuildReason.LoadAhead))
                {
                    _queued++;
                }
                else
                {
                    // The background queue is full of other work: put it back and try again shortly.
                    lock (_gate) _waiting.TryAdd(next.Texture.Key, next);
                    await Task.Delay(200, stop).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Unmounting.
        }
    }

    private void ReportProgress(ref TimeSpan lastReport)
    {
        int waiting;
        lock (_gate) waiting = _waiting.Count;
        if (waiting == 0 || _clock.Elapsed - lastReport < ReportInterval) return;
        lastReport = _clock.Elapsed;
        Log.Info($"Load-ahead: {waiting} textures of the loading scenery waiting, {_queued} queued so far, " +
                 $"{_alreadyBuilt} were already built ({_catalog.Count} known)");
    }

    public void Stop()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _loop?.Wait(TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        Stop();
        _catalog.EntryAdded -= OnEntryAdded;
        _catalog.TextureRequested -= OnTextureRequested;
        _stop.Dispose();
        _added.Dispose();
    }
}
