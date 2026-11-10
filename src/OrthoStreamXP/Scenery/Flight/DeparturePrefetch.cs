namespace OrthoStreamXP.Scenery;

/// <summary>A mounted package as the departure prefetch sees it: where its terrain definitions are, what its cache has, and its build queue.</summary>
/// <param name="IsCached">True when the full-detail texture is in the cache already (nothing to fetch).</param>
/// <param name="Prefetch">Queues whole textures ahead of X-Plane (the load-ahead's or the build scheduler's queue).</param>
internal sealed record DepartureTarget(string Name, string Source, Func<VirtualPath, bool> IsCached, Func<IReadOnlyList<VirtualPath>, int> Prefetch);

/// <summary>
/// Builds the textures around the departure airport of the latest SimBrief flight plan as the mount starts, before
/// X-Plane loads the scenery there: X-Plane's first load at the gate then finds them in the cache. Textures the cache
/// has already are left alone; X-Plane's own reads still come first in the download queue.
/// </summary>
/// <param name="origin">The flight plan's departure airport, or null when there is none.</param>
/// <param name="aircraft">Where the aircraft is now, when X-Plane has sent a position.</param>
internal sealed class DeparturePrefetch(Func<CancellationToken, Task<FlightOrigin?>> origin, Func<(double Lat, double Lon)?> aircraft,
    IReadOnlyList<DepartureTarget> targets, double radiusKm)
{
    /// <summary>The most textures queued per package: a 10 km circle holds a few hundred ZL18 textures at most.</summary>
    public const int MaxTextures = 600;
    /// <summary>An aircraft farther than this from the plan's origin is somewhere else: the flight plan is not this flight's.</summary>
    public const double StalePlanKm = 50;

    /// <summary>Queues the departure textures the cache does not have; returns how many were queued.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        if (await origin(ct).ConfigureAwait(false) is not { } from) return 0;
        string place = from.Icao.Length > 0 ? from.Icao : $"{from.Lat:F3}, {from.Lon:F3}";
        if (aircraft() is { } at && Geo.DistanceM(at.Lat, at.Lon, from.Lat, from.Lon) > StalePlanKm * 1000)
        {
            Log.Info($"SimBrief: {place} is the latest flight plan's departure, but the aircraft is more than {StalePlanKm:0} km away; nothing prefetched");
            return 0;
        }

        int queued = 0;
        foreach (DepartureTarget target in targets)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<VirtualPath> near = AirportTextures.Near(target.Source, from.Lat, from.Lon, radiusKm, MaxTextures);
            if (near.Count == 0)
            {
                Log.Info($"SimBrief: {target.Name} has no photo textures listed within {radiusKm:0.#} km of {place}");
                continue;
            }
            VirtualPath[] missing = [.. near.Where(t => !target.IsCached(t))];
            int cached = near.Count - missing.Length;
            if (missing.Length == 0)
            {
                Log.Info($"SimBrief: {place} - all {near.Count} textures within {radiusKm:0.#} km already cached ({target.Name})");
                continue;
            }
            int added = target.Prefetch(missing);
            queued += added;
            Log.Info($"SimBrief: {place} - {near.Count} textures within {radiusKm:0.#} km: {cached} already cached, {added} queued ({target.Name})");
        }
        return queued;
    }
}
