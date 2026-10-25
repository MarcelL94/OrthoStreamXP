namespace OrthoStreamXP.Scenery;

/// <summary>
/// A streamer's numbers for the flight tuning. Waits are X-Plane's reads that waited for a build; "start" ones are
/// near and middle reads while a flight starts (near-wait), "flight" ones all others (max-wait). Needed: the seconds
/// each waiting read needed until its real picture was there, including those that got a stand-in first.
/// </summary>
internal sealed record StreamingFacts(long Reads, long RealReads, long StandInReads, long FlatReads,
    long StartWaits, long StartTimedOut, IReadOnlyList<double> StartNeeded,
    long FlightWaits, long FlightTimedOut, IReadOnlyList<double> FlightNeeded,
    double ModelK, (double Median, int Count)? MeasuredK)
{
    /// <summary>The facts of several packages' streamers as one.</summary>
    public static StreamingFacts? Combine(IReadOnlyList<StreamingFacts> all)
    {
        if (all.Count == 0) return null;
        if (all.Count == 1) return all[0];
        var measured = all.Where(f => f.MeasuredK is not null).Select(f => f.MeasuredK!.Value).ToList();
        int count = measured.Sum(m => m.Count);
        return new StreamingFacts(all.Sum(f => f.Reads), all.Sum(f => f.RealReads), all.Sum(f => f.StandInReads), all.Sum(f => f.FlatReads),
            all.Sum(f => f.StartWaits), all.Sum(f => f.StartTimedOut), [.. all.SelectMany(f => f.StartNeeded)],
            all.Sum(f => f.FlightWaits), all.Sum(f => f.FlightTimedOut), [.. all.SelectMany(f => f.FlightNeeded)],
            all[0].ModelK, count == 0 ? null : (measured.Sum(m => m.Median * m.Count) / count, count));
    }
}
