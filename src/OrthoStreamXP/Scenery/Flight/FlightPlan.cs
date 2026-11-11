namespace OrthoStreamXP.Scenery;

/// <summary>An airport of a flight plan; <paramref name="ElevationFt"/> is 0 when the plan does not say.</summary>
internal sealed record FlightOrigin(string Icao, double Lat, double Lon, double ElevationFt = 0);

/// <summary>A point of the flight plan's navlog: where, how high (feet above sea level), when, and in which stage.</summary>
/// <param name="Minutes">Flight time from takeoff to here.</param>
/// <param name="Stage">SimBrief's stage: CLB, CRZ or DSC; empty when the plan has none.</param>
internal sealed record RoutePoint(string Ident, double Lat, double Lon, double AltitudeFt, double Minutes, string Stage);

/// <summary>The latest SimBrief flight plan as far as the scenery goes: the airports and the route.</summary>
/// <param name="Generated">When the pilot generated it (SimBrief's <c>params.time_generated</c>); null when the plan does not say.</param>
internal sealed record FlightPlan(FlightOrigin Origin, FlightOrigin? Destination, FlightOrigin? Alternate, IReadOnlyList<RoutePoint> Route,
    DateTimeOffset? Generated = null)
{
    /// <summary>
    /// An older plan is taken for an earlier flight: its departure is not prefetched. At mount start X-Plane usually
    /// sends no position yet, so the age is the only check there is.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(6);

    /// <summary>How long ago it was generated; null when unknown.</summary>
    public TimeSpan? AgeAt(DateTimeOffset now) => Generated is { } at ? now - at : null;

    /// <summary>Two airports this close share their scenery (the 3 x 4 tiles X-Plane loads round each are the same).</summary>
    public const double SamePlaceKm = 10;

    public static bool SamePlace(FlightOrigin a, FlightOrigin b) =>
        (a.Icao.Length > 0 && a.Icao.Equals(b.Icao, StringComparison.OrdinalIgnoreCase))
        || Geo.DistanceM(a.Lat, a.Lon, b.Lat, b.Lon) < SamePlaceKm * 1000;
}
