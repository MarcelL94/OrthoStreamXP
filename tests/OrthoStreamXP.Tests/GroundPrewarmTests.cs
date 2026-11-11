using System.Globalization;
using System.Text;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Tests;

/// <summary>
/// The ground pre-warm: the flight plan's airports and navlog, the climb it covers, when it runs, and what it queues,
/// including plans without a TOC, with the alternate the same as the arrival, and with the arrival the same as the departure.
/// </summary>
public sealed class GroundPrewarmTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static readonly FlightOrigin Eddv = new("EDDV", 52.4611, 9.6851, 183), Eddf = new("EDDF", 50.0333, 8.5706, 364),
        Eddk = new("EDDK", 50.8659, 7.1427, 302), Eddh = new("EDDH", 53.6304, 9.9882, 53);

    // ---------------------------------------------------------------- a flight plan as SimBrief sends it

    private static string Airport(FlightOrigin a) =>
        string.Create(CultureInfo.InvariantCulture, $$"""{"icao_code":"{{a.Icao}}","pos_lat":"{{a.Lat}}","pos_long":"{{a.Lon}}","elevation":"{{a.ElevationFt}}"}""");

    private static string Fix(RoutePoint f) =>
        string.Create(CultureInfo.InvariantCulture,
            $$"""{"ident":"{{f.Ident}}","pos_lat":"{{f.Lat}}","pos_long":"{{f.Lon}}","altitude_feet":"{{f.AltitudeFt}}","time_total":"{{f.Minutes * 60}}","stage":"{{f.Stage}}"}""");

    /// <summary>An OFP: <paramref name="alternate"/> is written as SimBrief does, an object or (several) an array.</summary>
    private static string Ofp(FlightOrigin from, FlightOrigin? to, FlightOrigin[] alternates, params RoutePoint[] fixes)
    {
        var json = new StringBuilder("""{"fetch":{"status":"Success"},""");
        json.Append(CultureInfo.InvariantCulture, $"\"origin\":{Airport(from)},");
        if (to is not null) json.Append(CultureInfo.InvariantCulture, $"\"destination\":{Airport(to)},");
        if (alternates.Length == 1) json.Append(CultureInfo.InvariantCulture, $"\"alternate\":{Airport(alternates[0])},");
        else if (alternates.Length > 1) json.Append(CultureInfo.InvariantCulture, $"\"alternate\":[{string.Join(",", alternates.Select(Airport))}],");
        json.Append(fixes.Length == 1 ? $"\"navlog\":{{\"fix\":{Fix(fixes[0])}}}" : $"\"navlog\":{{\"fix\":[{string.Join(",", fixes.Select(Fix))}]}}");
        return json.Append('}').ToString();
    }

    /// <summary>EDDV to EDDF: due south-west, climbing 2,000 ft a minute at 6 NM a minute, TOC at FL360 after 18 minutes.</summary>
    private static RoutePoint[] ClimbSouth(bool toc = true, bool stages = true)
    {
        var fixes = new List<RoutePoint> { new("EDDV", Eddv.Lat, Eddv.Lon, 183, 0, stages ? "CLB" : "") };
        for (int minute = 3; minute <= 18; minute += 3)
        {
            double km = minute * 6 * 1.852;
            fixes.Add(new RoutePoint(minute == 18 && toc ? "TOC" : $"WP{minute}", Eddv.Lat - km / 111.2, Eddv.Lon - km / 2 / 68, minute * 2000, minute, stages ? "CLB" : ""));
        }
        fixes.Add(new RoutePoint("WP21", Eddv.Lat - 18 * 6 * 1.852 / 111.2 - 0.3, Eddv.Lon - 1, 36000, 21, stages ? "CRZ" : ""));
        fixes.Add(new RoutePoint("EDDF", Eddf.Lat, Eddf.Lon, 364, 45, stages ? "DSC" : ""));
        return [.. fixes];
    }

    [Fact]
    public void Reads_the_airports_and_the_navlog_of_a_flight_plan()
    {
        SimBriefAnswer answer = SimBriefClient.Parse(Ofp(Eddv, Eddf, [Eddk], ClimbSouth()));
        FlightPlan plan = answer.Plan!;

        Assert.Equal(Eddv, plan.Origin);
        Assert.Equal(Eddf, plan.Destination);
        Assert.Equal(Eddk, plan.Alternate);
        Assert.Equal(9, plan.Route.Count);
        Assert.Equal(("TOC", 36000.0, 18.0, "CLB"), (plan.Route[6].Ident, plan.Route[6].AltitudeFt, plan.Route[6].Minutes, plan.Route[6].Stage));

        // Several alternates come as an array: the first is taken. A one-point navlog comes as an object.
        FlightPlan two = SimBriefClient.Parse(Ofp(Eddv, Eddf, [Eddk, Eddh], ClimbSouth()[1])).Plan!;
        Assert.Equal(Eddk, two.Alternate);
        Assert.Single(two.Route);

        // No destination or alternate in the plan: none.
        FlightPlan bare = SimBriefClient.Parse(Ofp(Eddv, null, [], ClimbSouth()[1])).Plan!;
        Assert.Null(bare.Destination);
        Assert.Null(bare.Alternate);
    }

    // ---------------------------------------------------------------- the climb

    [Fact]
    public void The_climb_ends_at_the_top_of_climb()
    {
        ClimbRoute.Climb climb = ClimbRoute.Of(new FlightPlan(Eddv, Eddf, null, ClimbSouth()));

        Assert.Equal("the top of climb", climb.End);
        Assert.Equal(18, climb.Minutes, 3);
        Assert.InRange(climb.Nm, 115, 125); // 108 NM south and 54 NM east of it in 18 minutes: 121 NM along the track
        Assert.Equal(0, climb.Samples[0].HeightM);
        Assert.Equal((36000 - 183) * 0.3048, climb.Samples[^1].HeightM, 1); // above the departure, not the sea
        Assert.All(climb.Samples.Zip(climb.Samples.Skip(1)), p => Assert.InRange(p.Second.AlongKm - p.First.AlongKm, 0.1, ClimbRoute.StepKm + 0.01));
    }

    [Fact]
    public void A_plan_without_a_TOC_ends_its_climb_where_the_climb_stage_does()
    {
        // a) SimBrief plans without a TOC point: the stages still say where the climb ends.
        ClimbRoute.Climb climb = ClimbRoute.Of(new FlightPlan(Eddv, Eddf, null, ClimbSouth(toc: false)));

        Assert.Equal("the end of the climb", climb.End);
        Assert.Equal(18, climb.Minutes, 3); // the last CLB point, WP18; the CRZ point after it is left out
    }

    [Fact]
    public void A_plan_without_a_TOC_or_stages_ends_at_the_caps()
    {
        // a) Neither a TOC nor stages: 25 minutes of flight come first here (25 x 6 NM = 150 NM, under 200 NM).
        ClimbRoute.Climb byTime = ClimbRoute.Of(new FlightPlan(Eddv, Eddf, null, ClimbSouth(toc: false, stages: false)));
        Assert.Equal("25 min", byTime.End);
        Assert.Equal(25, byTime.Minutes, 3);

        // A plan without times, on to Zurich (250 NM): 200 NM along the route.
        RoutePoint[] untimed = [.. ClimbSouth(toc: false, stages: false).Select(f => f with { Minutes = 0 }), new("LSZH", 47.4647, 8.5492, 1416, 0, "")];
        ClimbRoute.Climb byDistance = ClimbRoute.Of(new FlightPlan(Eddv, Eddf, null, untimed));
        Assert.Equal("200 NM", byDistance.End);
        Assert.Equal(200, byDistance.Nm, 0);
    }

    [Fact]
    public void The_climb_covers_the_tiles_it_crosses_and_one_either_side_and_sees_its_height()
    {
        ClimbRoute.Climb climb = ClimbRoute.Of(new FlightPlan(Eddv, Eddf, null, ClimbSouth()));
        IReadOnlyList<(int Lat, int Lon)> tiles = ClimbRoute.Tiles(climb.Samples);

        Assert.Contains((52, 9), tiles); // the departure's
        Assert.Contains((53, 10), tiles); // a neighbour of it
        Assert.Contains((50, 8), tiles); // the tile of the top of climb
        Assert.DoesNotContain((50, 11), tiles); // two columns away from every tile the climb crosses

        // Beside the departure the aircraft is on the ground; under the top of climb it is 11 km up.
        Assert.InRange(ClimbRoute.Nearest(climb.Samples, Eddv.Lat, Eddv.Lon).DistanceM, 0, 1);
        var toc = climb.Samples[^1];
        var (distance, along) = ClimbRoute.Nearest(climb.Samples, toc.Lat, toc.Lon);
        Assert.True(distance >= toc.HeightM - 1, $"{distance} m from a point {toc.HeightM} m below the aircraft");
        Assert.True(along > 150);
    }

    // ---------------------------------------------------------------- when it runs

    [Fact]
    public void It_starts_after_half_a_minute_parked_at_the_departure_and_stops_for_the_takeoff_roll()
    {
        var watch = new GroundWatch(Eddv.Lat, Eddv.Lon);
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        GroundDecision At(double seconds, double? kt = null, double heightM = 3, bool inferred = false, double lat = 52.4611) =>
            watch.Update(lat, Eddv.Lon, kt, heightM, inferred, t0.AddSeconds(seconds));

        Assert.Equal(GroundDecision.None, At(0, inferred: true)); // placed from X-Plane's reads: not yet
        Assert.Equal(GroundDecision.None, At(1));
        Assert.Equal(GroundDecision.None, At(20, kt: 10)); // moving: the 30 s start again
        Assert.Equal(GroundDecision.None, At(21));
        Assert.Equal(GroundDecision.None, At(50));
        Assert.Equal(GroundDecision.Start, At(51.5));
        Assert.Equal(GroundDecision.None, At(300, kt: 15)); // taxiing goes on
        Assert.Equal(GroundDecision.Stop, At(400, kt: 60)); // the takeoff roll
        Assert.Equal(GroundDecision.None, At(2000)); // parked again (after landing): never a second time

        var away = new GroundWatch(Eddv.Lat, Eddv.Lon);
        Assert.Equal(GroundDecision.None, away.Update(Eddf.Lat, Eddf.Lon, 0, 3, false, t0));
        Assert.Equal(GroundDecision.None, away.Update(Eddf.Lat, Eddf.Lon, 0, 3, false, t0.AddMinutes(5))); // not at this plan's departure

        // The height is the camera's: an outside view 150 m up at the gate neither keeps it from starting nor stops it.
        var outside = new GroundWatch(Eddv.Lat, Eddv.Lon);
        outside.Update(Eddv.Lat, Eddv.Lon, 0, 150, false, t0);
        Assert.Equal(GroundDecision.Start, outside.Update(Eddv.Lat, Eddv.Lon, 0, 150, false, t0.AddSeconds(31)));
        Assert.Equal(GroundDecision.None, outside.Update(Eddv.Lat, Eddv.Lon, 0, 400, false, t0.AddSeconds(60)));
        Assert.Equal(GroundDecision.Stop, outside.Update(Eddv.Lat, Eddv.Lon, 45, 400, false, t0.AddSeconds(90)));

        // Paused in the air near the departure (no speed, 2 km up): not parked.
        var paused = new GroundWatch(Eddv.Lat, Eddv.Lon);
        paused.Update(Eddv.Lat, Eddv.Lon, 0, 2000, false, t0);
        Assert.Equal(GroundDecision.None, paused.Update(Eddv.Lat, Eddv.Lon, 0, 2000, false, t0.AddMinutes(2)));
    }

    [Fact]
    public void A_takeoff_roll_before_the_prewarm_ran_is_said_once()
    {
        var watch = new GroundWatch(Eddv.Lat, Eddv.Lon);
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        GroundDecision At(double seconds, double? kt = null, bool inferred = false) =>
            watch.Update(Eddv.Lat, Eddv.Lon, kt, 3, inferred, t0.AddSeconds(seconds));

        Assert.Equal(GroundDecision.None, At(0));
        Assert.Equal(GroundDecision.None, At(10, kt: 60, inferred: true)); // not X-Plane's position
        Assert.Equal(GroundDecision.Rolled, At(20, kt: 60)); // parked only 20 s: never started, but the roll counts
        Assert.Equal(GroundDecision.None, At(25, kt: 120)); // once
        Assert.Equal(GroundDecision.None, At(30, kt: 140));
    }

    [Fact]
    public void The_takeoff_roll_drops_the_departures_requests_whether_or_not_the_prewarm_ran()
    {
        var plan = Task.FromResult<FlightPlan?>(new FlightPlan(Eddv, Eddf, null, []));
        var t0 = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        CameraReport At(double seconds, double kt) =>
            new(Eddv.Lat, Eddv.Lon, 3, null, kt > 0 ? new Motion(kt / 1.943844, 270) : null, false, t0.AddSeconds(seconds));

        // Taxied out at once: the pre-warm never started, the departure's requests still go.
        int ground = 0, departure = 0;
        var target = new GroundTarget("pkg", _tree.PathOf("pkg"), (_, _, _) => (0, 0), () => ++ground, () => ++departure);
        var prewarm = new GroundPrewarm(plan, [target]);
        prewarm.OnCamera(At(0, 10));
        prewarm.OnCamera(At(60, 60));
        Assert.Equal((0, 1), (ground, departure));
        prewarm.OnCamera(At(70, 120));
        Assert.Equal((0, 1), (ground, departure)); // once

        // Parked long enough: the pre-warm ran, and the roll drops both.
        int ground2 = 0, departure2 = 0;
        var target2 = new GroundTarget("pkg", _tree.PathOf("pkg"), (_, _, _) => (0, 0), () => ++ground2, () => ++departure2);
        var ran = new GroundPrewarm(plan, [target2]);
        ran.OnCamera(At(0, 0));
        ran.OnCamera(At(31, 0)); // starts (queues nothing: the package has no scenery here)
        ran.OnCamera(At(600, 60));
        Assert.True(ground2 >= 1 && departure2 >= 1, $"ground {ground2}, departure {departure2}");
    }

    // ---------------------------------------------------------------- what it queues

    private sealed record Call(int Phase, int Textures, double FirstDistanceM);

    /// <summary>A package with one texture in each DSF tile named, and a target that records what it is asked to queue.</summary>
    private (GroundTarget Target, List<Call> Calls) Package(params (int Lat, int Lon)[] tiles)
    {
        int n = 0;
        foreach (var (lat, lon) in tiles)
        {
            string path = SceneryArea.DsfPath("pkg", lat, lon).Replace('\\', '/');
            _tree.WriteBytes(path, SceneryAreaTests.Dsf($"terrain/{21552 + 16 * n++}_34576_BI16.ter"));
        }
        var calls = new List<Call>();
        var target = new GroundTarget("pkg", _tree.PathOf("pkg"), (textures, place, phase) =>
        {
            calls.Add(new Call(phase, textures.Count, place(Eddv.Lat, Eddv.Lon).DistanceM));
            return (textures.Count, 0);
        }, () => 0, () => 0);
        return (target, calls);
    }

    private static readonly (int, int)[] Everywhere =
        [.. Enumerable.Range(48, 7).SelectMany(lat => Enumerable.Range(5, 8).Select(lon => (lat, lon)))];

    [Fact]
    public void Queues_the_arrival_then_the_alternate_then_the_climb()
    {
        var (target, calls) = Package(Everywhere);
        int queued = new GroundPrewarm(Task.FromResult<FlightPlan?>(null), [target]).Queue(new FlightPlan(Eddv, Eddf, Eddk, ClimbSouth()));

        Assert.Equal([GroundPrewarm.Arrival, GroundPrewarm.Alternate, GroundPrewarm.Route], calls.Select(c => c.Phase));
        Assert.Equal(12, calls[0].Textures); // the 3 x 4 tiles round EDDF
        Assert.Equal(12, calls[1].Textures); // and round EDDK
        Assert.True(calls[2].Textures > 12); // the tiles under the climb and either side
        Assert.InRange(calls[0].FirstDistanceM, 250_000, 300_000); // EDDV is 270 km from EDDF: placed from the arrival
        Assert.InRange(calls[2].FirstDistanceM, 0, 1); // the departure lies on the climb
        Assert.Equal(calls.Sum(c => c.Textures), queued);
    }

    [Fact]
    public void An_alternate_that_is_the_arrival_is_not_built_twice()
    {
        // b) SimBrief plans with the destination as the alternate.
        var (target, calls) = Package(Everywhere);
        new GroundPrewarm(Task.FromResult<FlightPlan?>(null), [target]).Queue(new FlightPlan(Eddv, Eddf, Eddf with { Icao = "" }, ClimbSouth()));

        Assert.Equal([GroundPrewarm.Arrival, GroundPrewarm.Route], calls.Select(c => c.Phase)); // the same place, even without its code
    }

    [Fact]
    public void An_arrival_that_is_the_departure_leaves_its_area_to_the_departure_prefetch()
    {
        // c) A local flight (a circuit, a test flight): back to the departure.
        RoutePoint[] circuit =
        [
            new("EDDV", Eddv.Lat, Eddv.Lon, 183, 0, "CLB"), new("WP1", Eddv.Lat + 0.1, Eddv.Lon, 3000, 3, "CLB"),
            new("TOC", Eddv.Lat + 0.2, Eddv.Lon + 0.1, 5000, 5, "CLB"), new("WP2", Eddv.Lat + 0.1, Eddv.Lon + 0.2, 5000, 8, "CRZ"),
            new("EDDV", Eddv.Lat, Eddv.Lon, 183, 15, "DSC"),
        ];
        var (target, calls) = Package(Everywhere);
        new GroundPrewarm(Task.FromResult<FlightPlan?>(null), [target]).Queue(new FlightPlan(Eddv, Eddv, Eddh, circuit));

        Assert.Equal([GroundPrewarm.Alternate, GroundPrewarm.Route], calls.Select(c => c.Phase)); // no arrival phase; EDDH still is

        // With the alternate the departure too, only the climb is left.
        var (only, onlyCalls) = Package(Everywhere);
        new GroundPrewarm(Task.FromResult<FlightPlan?>(null), [only]).Queue(new FlightPlan(Eddv, Eddv, Eddv, circuit));
        Assert.Equal([GroundPrewarm.Route], onlyCalls.Select(c => c.Phase));
    }

    [Fact]
    public void A_plan_without_a_navlog_still_gets_its_arrival_and_alternate()
    {
        var (target, calls) = Package(Everywhere);
        new GroundPrewarm(Task.FromResult<FlightPlan?>(null), [target]).Queue(new FlightPlan(Eddv, Eddf, Eddk, []));

        Assert.Equal([GroundPrewarm.Arrival, GroundPrewarm.Alternate], calls.Select(c => c.Phase));
    }
}
