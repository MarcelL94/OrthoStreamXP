namespace OrthoStreamXP.Scenery;

/// <summary>Where the paging model fits the reads best: k in [Low, High), agreeing with that share of the reads.</summary>
internal sealed record PagingFit(double K, double Low, double High, double Agreement, int Samples);

/// <summary>Fits the paging model's k to the levels X-Plane read at known distances.</summary>
internal static class PagingCalibration
{
    /// <summary>
    /// The k that agrees with the most reads. X-Plane reading level n at x (see <see cref="PagingModel.X"/>) means
    /// 2^(n-1) k &lt;= x &lt; 2^n k (full detail has no lower end), so x / 2^n &lt; k &lt;= x / 2^(n-1). Every k is scored
    /// by how many reads it agrees with; the best range and its middle (geometric) are returned. Null with too few reads.
    /// </summary>
    public static PagingFit? Fit(IReadOnlyCollection<(double X, int Mip)> samples, int minimum = 20)
    {
        if (samples.Count < minimum) return null;
        // Walking k upwards: passing a lower bound fixes one disagreement, passing an upper bound adds one.
        var bounds = new List<(double Value, int Change)>();
        foreach (var (x, mip) in samples)
        {
            bounds.Add((x / Math.Pow(2, mip), -1));
            if (mip >= 1) bounds.Add((x / Math.Pow(2, mip - 1), +1));
        }
        bounds.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : a.Change.CompareTo(b.Change));
        int disagree = samples.Count, best = int.MaxValue;
        double low = 0, high = 0;
        for (int i = 0; i < bounds.Count; i++)
        {
            disagree += bounds[i].Change;
            double next = i + 1 < bounds.Count ? bounds[i + 1].Value : double.PositiveInfinity;
            if (disagree < best && next > bounds[i].Value) (best, low, high) = (disagree, bounds[i].Value, next);
        }
        double k = double.IsPositiveInfinity(high) ? low * 1.25 : Math.Sqrt(low * high);
        return new PagingFit(k, low, high, 1 - (double)best / samples.Count, samples.Count);
    }
}
