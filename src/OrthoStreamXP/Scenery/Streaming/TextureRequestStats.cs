namespace OrthoStreamXP.Scenery;

/// <summary>How X-Plane's first request for a texture this session was answered.</summary>
internal enum RequestOutcome
{
    /// <summary>Already built by load-ahead or path prefetch: X-Plane did not wait. This is prefetch working.</summary>
    ReadyByPrefetch,
    /// <summary>Already in the cache from an earlier session or request.</summary>
    ReadyInCache,
    /// <summary>Prefetch had started it, but X-Plane still waited for it to finish.</summary>
    WaitedForPrefetch,
    /// <summary>Prefetch had not picked it: built while X-Plane waited.</summary>
    BuiltOnRequest,
    /// <summary>A coarse stand-in was served while the full texture builds.</summary>
    Provisional,
    /// <summary>The package's own copy was served while the download builds.</summary>
    LocalCopy,
    /// <summary>Served level by level: each read gets the best data at that moment (see the streaming summary).</summary>
    Streamed,
    /// <summary>Nothing could be built; X-Plane got the package's copy or nothing.</summary>
    Unavailable,
}

/// <summary>Counts how X-Plane's texture requests were answered, to show whether prefetch keeps ahead.</summary>
internal sealed class TextureRequestStats
{
    private readonly long[] _counts = new long[Enum.GetValues<RequestOutcome>().Length];
    private long _waitedForPrefetchTicks, _builtOnRequestTicks;

    public void Record(RequestOutcome outcome, TimeSpan waited)
    {
        Interlocked.Increment(ref _counts[(int)outcome]);
        if (outcome == RequestOutcome.WaitedForPrefetch) Interlocked.Add(ref _waitedForPrefetchTicks, waited.Ticks);
        if (outcome == RequestOutcome.BuiltOnRequest) Interlocked.Add(ref _builtOnRequestTicks, waited.Ticks);
    }

    public long Count(RequestOutcome outcome) => Interlocked.Read(ref _counts[(int)outcome]);

    public long Total => Enum.GetValues<RequestOutcome>().Sum(Count);

    /// <summary>One log line: Prefetch summary: ... X-Plane requested 380 new textures: 300 ready by prefetch (79%), ...</summary>
    public string Summary(int loadAheadQueued, int pathQueued, int prefetchBuilt)
    {
        long total = Total;
        string Share(RequestOutcome outcome) => total == 0 ? "" : $" ({100.0 * Count(outcome) / total:F0}%)";
        string Average(RequestOutcome outcome, long ticks) =>
            Count(outcome) == 0 ? "" : $", avg {TimeSpan.FromTicks(Interlocked.Read(ref ticks) / Count(outcome)).TotalSeconds:F1} s";
        return $"Prefetch summary: queued {loadAheadQueued + pathQueued} (load-ahead {loadAheadQueued}, flight path {pathQueued}) and built {prefetchBuilt} textures; " +
               $"X-Plane requested {total} new textures: " +
               $"{Count(RequestOutcome.ReadyByPrefetch)} ready by prefetch{Share(RequestOutcome.ReadyByPrefetch)}, " +
               $"{Count(RequestOutcome.ReadyInCache)} already cached{Share(RequestOutcome.ReadyInCache)}, " +
               $"{Count(RequestOutcome.WaitedForPrefetch)} waited for a prefetch build{Share(RequestOutcome.WaitedForPrefetch)}{Average(RequestOutcome.WaitedForPrefetch, _waitedForPrefetchTicks)}, " +
               $"{Count(RequestOutcome.BuiltOnRequest)} built on request{Share(RequestOutcome.BuiltOnRequest)}{Average(RequestOutcome.BuiltOnRequest, _builtOnRequestTicks)}, " +
               $"{Count(RequestOutcome.Provisional)} provisional, {Count(RequestOutcome.LocalCopy)} local copies, " +
               $"{Count(RequestOutcome.Unavailable)} unavailable";
    }
}
