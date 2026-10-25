namespace OrthoStreamXP.Scenery;

/// <summary>
/// How long X-Plane's reads waited for a build, by <see cref="Band"/>. "Start" waits are near and middle reads while a
/// flight starts (near-wait), "flight" ones all others (max-wait). Needed: the seconds each waiting read needed until
/// its real picture was there, including those that got a stand-in first.
/// </summary>
internal sealed class ReadWaitStats
{
    private const int MaxNeededSamples = 100_000;

    private readonly long[] _waits = new long[3], _waitTicks = new long[3], _timedOut = new long[3];
    private long _startWaits, _startTimedOut;
    private readonly List<double> _startNeeded = [], _flightNeeded = [];

    /// <summary>Notes a read of <paramref name="band"/> that waited <paramref name="waited"/>; <paramref name="done"/> is false when it timed out.</summary>
    public void Record(Band band, bool starting, TimeSpan waited, bool done)
    {
        Interlocked.Increment(ref _waits[(int)band]);
        Interlocked.Add(ref _waitTicks[(int)band], waited.Ticks);
        if (starting) Interlocked.Increment(ref _startWaits);
        if (done) return;
        Interlocked.Increment(ref _timedOut[(int)band]);
        if (starting) Interlocked.Increment(ref _startTimedOut);
    }

    /// <summary>How long a read would have had to wait for the real picture: what the tuning sets the waits from.</summary>
    public void NoteNeeded(bool starting, TimeSpan needed)
    {
        List<double> list = starting ? _startNeeded : _flightNeeded;
        lock (list)
            if (list.Count < MaxNeededSamples) list.Add(needed.TotalSeconds);
    }

    /// <summary>The waits at a flight's start and after it. The Far band never waits, so only Near and Middle count.</summary>
    public (long StartWaits, long StartTimedOut, IReadOnlyList<double> StartNeeded, long FlightWaits, long FlightTimedOut, IReadOnlyList<double> FlightNeeded) Snapshot()
    {
        long waits = Read(_waits, Band.Near) + Read(_waits, Band.Middle);
        long timedOut = Read(_timedOut, Band.Near) + Read(_timedOut, Band.Middle);
        long startWaits = Interlocked.Read(ref _startWaits), startTimedOut = Interlocked.Read(ref _startTimedOut);
        return (startWaits, startTimedOut, Copy(_startNeeded), waits - startWaits, timedOut - startTimedOut, Copy(_flightNeeded));
    }

    /// <summary>For the summary line: "near 3 (avg 1.2 s, 1 timed out)".</summary>
    public string Describe(Band band)
    {
        string name = band.ToString().ToLowerInvariant();
        long n = Read(_waits, band);
        return n == 0 ? $"{name} 0"
            : $"{name} {n} (avg {TimeSpan.FromTicks(Read(_waitTicks, band) / n).TotalSeconds:F1} s, {Read(_timedOut, band)} timed out)";
    }

    private static long Read(long[] counters, Band band) => Interlocked.Read(ref counters[(int)band]);

    private static double[] Copy(List<double> list)
    {
        lock (list) return [.. list];
    }
}
