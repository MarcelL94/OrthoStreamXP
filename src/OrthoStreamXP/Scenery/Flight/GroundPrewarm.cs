namespace OrthoStreamXP.Scenery;

internal enum GroundDecision { None, Start, Stop }

/// <summary>
/// When the ground pre-warm runs: once, after the aircraft has stood still at the flight plan's departure for a while
/// (an airliner spends 10-15 minutes there before pushback), until it rolls for takeoff. Taxiing does not stop it; a
/// position X-Plane did not send (placed from its reads) never starts it. The height known is the camera's, which an
/// outside view lifts well above the aircraft: so it only keeps a paused aircraft in the air from starting it, and the
/// stop goes by ground speed alone (no takeoff without it).
/// </summary>
internal sealed class GroundWatch(double departureLat, double departureLon)
{
    public const double ParkedKt = 3, RollingKt = 40, OnGroundM = 300, NearDepartureKm = 50;
    public static readonly TimeSpan ParkedFor = TimeSpan.FromSeconds(30);

    private enum State { Waiting, Running, Done }

    private State _state;
    private DateTime? _parkedSince;

    /// <param name="speedKt">Ground speed; null when X-Plane's positions have not moved enough to tell (standing still).</param>
    /// <param name="heightM">The camera's height above the ground.</param>
    public GroundDecision Update(double lat, double lon, double? speedKt, double heightM, bool inferred, DateTime at)
    {
        double speed = speedKt ?? 0;
        switch (_state)
        {
            case State.Running:
                if (speed <= RollingKt) return GroundDecision.None;
                _state = State.Done;
                return GroundDecision.Stop;
            case State.Waiting:
                bool parked = !inferred && speed <= ParkedKt && heightM <= OnGroundM
                              && Geo.DistanceM(departureLat, departureLon, lat, lon) <= NearDepartureKm * 1000;
                if (!parked)
                {
                    _parkedSince = null;
                    return GroundDecision.None;
                }
                _parkedSince ??= at;
                if (at - _parkedSince < ParkedFor) return GroundDecision.None;
                _state = State.Running;
                return GroundDecision.Start;
            default:
                return GroundDecision.None;
        }
    }
}

/// <summary>A mounted package as the ground pre-warm sees it: its DSFs, and how to queue and drop textures in its load-ahead.</summary>
/// <param name="Prefetch">Queues textures placed by (distance X-Plane will see them from, order) at a phase (<see cref="MipWarmer.PrefetchGround"/>).</param>
/// <param name="Cancel">Drops the ground pre-warm's textures still waiting; returns how many.</param>
internal sealed record GroundTarget(string Name, string Source,
    Func<IReadOnlyList<VirtualPath>, Func<double, double, (double DistanceM, double Order)>, int, (int Queued, int Had)> Prefetch,
    Func<int> Cancel);

/// <summary>
/// The ground pre-warm: while the aircraft stands at the departure, the scenery of the rest of the flight is built into
/// the cache, below everything X-Plane and the departure need: first the arrival and the alternate (where missing
/// photos are noticed first), each as X-Plane will want it from the gate there; then the climb from the departure to
/// the top of climb (<see cref="ClimbRoute"/>), each texture as X-Plane will want it when the aircraft passes.
/// </summary>
internal sealed class GroundPrewarm(Task<FlightPlan?> plan, IReadOnlyList<GroundTarget> targets)
{
    public const int Arrival = 0, Alternate = 1, Route = 2;

    private GroundWatch? _watch;
    private volatile bool _stopped;

    /// <summary>Every position X-Plane sends (from its UDP thread): starts or stops the pre-warm when the watch says so.</summary>
    public void OnCamera(CameraReport camera)
    {
        if (!plan.IsCompletedSuccessfully || plan.Result is not { } flight) return;
        _watch ??= new GroundWatch(flight.Origin.Lat, flight.Origin.Lon);
        switch (_watch.Update(camera.Lat, camera.Lon, camera.Motion?.SpeedMps * 1.943844, camera.HeightAglM, camera.Inferred, camera.ReceivedUtc))
        {
            case GroundDecision.Start:
                _ = Task.Run(() =>
                {
                    try
                    {
                        Queue(flight);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Warning($"Ground pre-warm failed ({ex.Message})");
                    }
                    if (_stopped) Cancel(); // the aircraft rolled while it was being queued
                });
                break;
            case GroundDecision.Stop:
                _stopped = true;
                Cancel();
                break;
        }
    }

    /// <summary>Queues the three phases in every package; returns how many textures were queued.</summary>
    internal int Queue(FlightPlan flight)
    {
        Log.Info($"Ground pre-warm: the aircraft stands at {Name(flight.Origin)}; building the rest of the flight's scenery until it rolls for takeoff");
        int queued = 0;
        foreach (GroundTarget target in targets)
        {
            if (flight.Destination is { } to)
            {
                if (FlightPlan.SamePlace(to, flight.Origin))
                    Log.Info($"Ground pre-warm: the arrival {Name(to)} is the departure; its scenery is the departure's ({target.Name})");
                else
                    queued += Area(target, to, Arrival, "arrival");
            }
            if (flight.Alternate is { } alternate)
            {
                if (flight.Destination is { } arrival && FlightPlan.SamePlace(alternate, arrival))
                    Log.Info($"Ground pre-warm: the alternate {Name(alternate)} is the arrival; nothing more to build ({target.Name})");
                else if (FlightPlan.SamePlace(alternate, flight.Origin))
                    Log.Info($"Ground pre-warm: the alternate {Name(alternate)} is the departure; its scenery is the departure's ({target.Name})");
                else
                    queued += Area(target, alternate, Alternate, "alternate");
            }
            queued += Climb(target, flight);
        }
        return queued;
    }

    private int Area(GroundTarget target, FlightOrigin airport, int phase, string what)
    {
        if (_stopped) return 0;
        IReadOnlyList<VirtualPath> textures = SceneryArea.Textures(target.Source, airport.Lat, airport.Lon, out int tiles);
        if (textures.Count == 0)
        {
            Log.Info($"Ground pre-warm: {target.Name} has no readable scenery tiles around the {what} {Name(airport)}");
            return 0;
        }
        var (queued, had) = target.Prefetch(textures, (lat, lon) =>
        {
            double d = Geo.DistanceM(airport.Lat, airport.Lon, lat, lon);
            return (d, d);
        }, phase);
        Log.Info($"Ground pre-warm: {what} {Name(airport)} - {textures.Count} textures in {tiles} scenery tiles: {had} already have what X-Plane will want, {queued} queued ({target.Name})");
        return queued;
    }

    private int Climb(GroundTarget target, FlightPlan flight)
    {
        if (_stopped) return 0;
        ClimbRoute.Climb climb = ClimbRoute.Of(flight);
        if (climb.Samples.Count < 2)
        {
            Log.Info($"Ground pre-warm: the flight plan has no route from {Name(flight.Origin)}; the climb is left to load-ahead ({target.Name})");
            return 0;
        }
        IReadOnlyList<VirtualPath> textures = SceneryArea.TexturesIn(target.Source, ClimbRoute.Tiles(climb.Samples), out int tiles);
        var (queued, had) = target.Prefetch(textures, (lat, lon) => ClimbRoute.Nearest(climb.Samples, lat, lon), Route);
        Log.Info($"Ground pre-warm: the climb to {climb.End} ({climb.Nm:0} NM, {climb.Minutes:0} min) - {textures.Count} textures in {tiles} scenery tiles: " +
                 $"{had} already have what X-Plane will want, {queued} queued ({target.Name})");
        return queued;
    }

    private void Cancel()
    {
        int dropped = targets.Sum(t => t.Cancel());
        Log.Info($"Ground pre-warm: the aircraft is rolling for takeoff; {dropped} textures still waiting were dropped, load-ahead takes over");
    }

    private static string Name(FlightOrigin airport) => airport.Icao.Length > 0 ? airport.Icao : $"{airport.Lat:F3}, {airport.Lon:F3}";
}
