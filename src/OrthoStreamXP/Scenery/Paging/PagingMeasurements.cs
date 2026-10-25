using System.Diagnostics;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// What a streamer learns about X-Plane's paging from the levels it reads: the camera distance at which each texture's
/// wanted level changed (a measurement of the paging model's k) and every placed read (for a fit of k).
/// </summary>
internal sealed class PagingMeasurements
{
    private static readonly TimeSpan PagingStep = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly List<double> _estimates = [];
    private readonly List<(double X, int Mip)> _samples = [];

    /// <summary>
    /// Notes that X-Plane wants level <paramref name="mip"/> of <paramref name="t"/>. When its wanted level changes
    /// between two paging steps, that distance measures k.
    /// </summary>
    public void Note(StreamedTexture t, int mip, double? distance)
    {
        long now = Stopwatch.GetTimestamp();
        int previous;
        long previousAt;
        bool previousPlaced;
        lock (t.Gate)
        {
            if (t.FinestWanted == StreamedTexture.Unknown || mip < t.FinestWanted) t.FinestWanted = mip;
            t.WantedAt = now;
            (previous, previousAt, previousPlaced) = (t.LastWanted, t.LastReadAt, t.LastPlaced);
            bool sameLoad = previous != StreamedTexture.Unknown && Stopwatch.GetElapsedTime(previousAt, now) < PagingStep;
            // The same load reads a texture coarse first, then finer: only its finest level is a paging step.
            if (sameLoad && mip >= previous) return;
            (t.LastWanted, t.LastPlaced) = (mip, distance is not null);
            if (!sameLoad) t.LastReadAt = now;
        }
        if (distance is not { } d) return;
        lock (_gate)
            if (_samples.Count < 200_000) _samples.Add((PagingModel.X(d, t.SizeM, t.Layout.Size), mip));
        // A change between two steps with the camera known at both crossed a boundary (not the loading screen's coarse read).
        if (previous == StreamedTexture.Unknown || previous == mip || !previousPlaced) return;
        if (Stopwatch.GetElapsedTime(previousAt, now) < PagingStep) return;
        lock (_gate)
            if (_estimates.Count < 100_000) _estimates.Add(PagingModel.Estimate(d, Math.Min(previous, mip), t.SizeM, t.Layout.Size));
    }

    /// <summary>The paging model's k measured this session: (median, count), or null before any mip change was seen.</summary>
    public (double Median, int Count)? MeasuredK()
    {
        double[] estimates;
        lock (_gate) estimates = [.. _estimates];
        if (estimates.Length == 0) return null;
        Array.Sort(estimates);
        return (estimates[estimates.Length / 2], estimates.Length);
    }

    /// <summary>A fit of k to every placed read, or null when there are too few.</summary>
    public PagingFit? Fit()
    {
        lock (_gate) return PagingCalibration.Fit(_samples);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _estimates.Clear();
            _samples.Clear();
        }
    }
}
