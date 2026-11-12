namespace OrthoStreamXP.Scenery;

/// <summary>
/// The most the SimBrief prefetch (every package together) may download in one
/// session (<c>simbrief-download-limit</c>, R4): the scenery around an airport can mean thousands of tiles, and a provider
/// that rate-limits or blocks the program would hit every user. Spent by the prefetch's own builds only, in the
/// phases' order (their priorities); load-ahead and X-Plane's reads never count. Builds already running finish.
/// </summary>
internal sealed class PrefetchBudget(long limitBytes)
{
    private long _spent;
    private int _reported;

    public long LimitBytes { get; } = limitBytes;

    public long SpentBytes => Interlocked.Read(ref _spent);

    public bool Exhausted => SpentBytes >= LimitBytes;

    /// <summary>A prefetch build downloaded <paramref name="bytes"/> (from its build thread).</summary>
    public void Spend(long bytes) => Interlocked.Add(ref _spent, bytes);

    /// <summary>True the first time only: the "used up" line is logged once a session, whichever package sees it first.</summary>
    public bool FirstToReport() => Interlocked.Exchange(ref _reported, 1) == 0;
}
