namespace OrthoStreamXP.Scenery;

/// <summary>Ground speed (m/s) and track (degrees true) over the last few seconds.</summary>
internal readonly record struct Motion(double SpeedMps, double TrackDeg);

/// <summary>
/// Works out ground speed and track from successive X-Plane position reports, so no extra Data Output rows
/// are needed. A jump faster than any aircraft (a new flight or a teleport) starts the history afresh.
/// </summary>
internal sealed class MotionTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10), MinimumSpan = TimeSpan.FromSeconds(2);
    private const double MinimumTravelM = 20, TeleportSpeedMps = 400;

    private readonly Queue<(TimeSpan Time, double Lat, double Lon)> _history = new();

    /// <summary>Adds a report; returns true when it was a jump to somewhere else (history restarted).</summary>
    public bool Add(TimeSpan time, double lat, double lon)
    {
        bool jumped = false;
        if (_history.Count > 0)
        {
            var last = _history.Last();
            double seconds = Math.Max(0.05, (time - last.Time).TotalSeconds);
            if (Geo.DistanceM(last.Lat, last.Lon, lat, lon) / seconds > TeleportSpeedMps)
            {
                _history.Clear();
                jumped = true;
            }
        }
        _history.Enqueue((time, lat, lon));
        while (_history.Count > 1 && time - _history.Peek().Time > Window) _history.Dequeue();
        return jumped;
    }

    /// <summary>Speed and track, or null while stationary or before there is enough history.</summary>
    public Motion? Current
    {
        get
        {
            if (_history.Count < 2) return null;
            var first = _history.Peek();
            var last = _history.Last();
            TimeSpan span = last.Time - first.Time;
            double travelled = Geo.DistanceM(first.Lat, first.Lon, last.Lat, last.Lon);
            if (span < MinimumSpan || travelled < MinimumTravelM) return null;
            return new Motion(travelled / span.TotalSeconds, Geo.BearingDeg(first.Lat, first.Lon, last.Lat, last.Lon));
        }
    }
}

/// <summary>
/// Chooses which known textures to build next. Moving, it covers a corridor along the predicted path
/// (speed x look-ahead time long, the prefetch radius wide either side) and ranks textures by how soon the
/// aircraft reaches them. Slow or stationary, it covers a circle of the prefetch radius, nearest first.
/// </summary>
internal static class PrefetchPlanner
{
    /// <summary>Below this (about 20 kt: taxiing, hovering) the path is not worth predicting.</summary>
    public const double MovingSpeedMps = 10;

    public static IReadOnlyList<VirtualPath> Select(IEnumerable<TextureCatalog.Entry> entries, double lat, double lon,
        Motion? motion, double radiusKm, double lookaheadSeconds, int maxTextures)
    {
        double radiusM = radiusKm * 1000;
        double pathM = motion is { SpeedMps: >= MovingSpeedMps } m ? m.SpeedMps * lookaheadSeconds : 0;
        double track = (motion?.TrackDeg ?? 0) * Math.PI / 180;
        double dirX = Math.Sin(track), dirY = Math.Cos(track); // east, north
        double metresPerDegLon = 111_320 * Math.Cos(lat * Math.PI / 180), metresPerDegLat = 110_574;

        var ranked = new List<(double Score, VirtualPath Texture)>();
        foreach (TextureCatalog.Entry entry in entries)
        {
            double x = (entry.Lon - lon) * metresPerDegLon, y = (entry.Lat - lat) * metresPerDegLat;
            double along = x * dirX + y * dirY;
            double t = Math.Clamp(along, 0, pathM);
            double lateral = Math.Sqrt(Math.Pow(x - dirX * t, 2) + Math.Pow(y - dirY * t, 2));
            if (lateral > radiusM) continue;
            // Ahead: distance along the path plus the step aside. Behind a moving aircraft: wanted least.
            double score = pathM > 0 && along < 0 ? 2 * lateral + pathM : t + lateral;
            ranked.Add((score, entry.Texture));
        }
        return [.. ranked.OrderBy(r => r.Score).Take(maxTextures).Select(r => r.Texture)];
    }
}

/// <summary>Great-circle helpers.</summary>
internal static class Geo
{
    private const double EarthRadiusM = 6_371_000.0, Rad = Math.PI / 180.0;

    public static double DistanceM(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Rad, p2 = lat2 * Rad, dp = p2 - p1, dl = (lon2 - lon1) * Rad;
        double a = Math.Pow(Math.Sin(dp / 2), 2) + Math.Cos(p1) * Math.Cos(p2) * Math.Pow(Math.Sin(dl / 2), 2);
        return EarthRadiusM * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1 - a)));
    }

    public static double BearingDeg(double lat1, double lon1, double lat2, double lon2)
    {
        double p1 = lat1 * Rad, p2 = lat2 * Rad, dl = (lon2 - lon1) * Rad;
        double y = Math.Sin(dl) * Math.Cos(p2);
        double x = Math.Cos(p1) * Math.Sin(p2) - Math.Sin(p1) * Math.Cos(p2) * Math.Cos(dl);
        return (Math.Atan2(y, x) / Rad + 360.0) % 360.0;
    }
}
