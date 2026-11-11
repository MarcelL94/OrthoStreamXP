using System.Net;
using System.Text;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>The SimBrief flight plan's departure: reading the OFP, picking the package's textures there, and skipping the cached ones.</summary>
public sealed class SimBriefPrefetchTests : IDisposable
{
    private const double Lat = 48.353783, Lon = 11.786086; // EDDM
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private const string Ofp = """
        {"fetch":{"userid":"123456","static_id":{},"status":"Success","time":"0.01"},
         "origin":{"icao_code":"EDDM","iata_code":"MUC","pos_lat":"48.353783","pos_long":"11.786086"},
         "destination":{"icao_code":"EGLL","pos_lat":"51.4775","pos_long":"-0.461389"}}
        """;

    // ---------------------------------------------------------------- the client

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static async Task<(FlightOrigin? Origin, Uri? Asked)> Fetch(string user, HttpStatusCode status, string body)
    {
        using var handler = new StubHandler(status, body);
        using var http = new HttpClient(handler);
        FlightOrigin? origin = (await new SimBriefClient(http).LatestPlanAsync(user, TestContext.Current.CancellationToken))?.Origin;
        return (origin, handler.Asked);
    }

    [Fact]
    public async Task Reads_the_origin_of_the_latest_flight_plan()
    {
        var (origin, asked) = await Fetch("pilot", HttpStatusCode.OK, Ofp);

        Assert.Equal(new FlightOrigin("EDDM", Lat, Lon), origin);
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?username=pilot&json=1", asked!.AbsoluteUri);
    }

    [Fact]
    public void A_number_is_the_pilot_id_anything_else_the_username()
    {
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?userid=123456&json=1", SimBriefClient.UrlFor(" 123456 ").AbsoluteUri);
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?username=Jo%20Doe&json=1", SimBriefClient.UrlFor("Jo Doe").AbsoluteUri);
    }

    [Fact]
    public async Task An_unknown_user_or_a_failing_server_gives_no_origin()
    {
        var (unknown, _) = await Fetch("nobody", HttpStatusCode.BadRequest, """{"fetch":{"status":"Error: Unknown UserID"}}""");
        var (down, _) = await Fetch("pilot", HttpStatusCode.InternalServerError, "oops");
        var (garbled, _) = await Fetch("pilot", HttpStatusCode.OK, "<html>");
        var (noOrigin, _) = await Fetch("pilot", HttpStatusCode.OK, """{"fetch":{"status":"Success"}}""");

        Assert.Null(unknown);
        Assert.Null(down);
        Assert.Null(garbled);
        Assert.Null(noOrigin);
    }

    [Fact]
    public async Task The_connection_test_names_the_flight_or_says_why_there_is_none()
    {
        using var ok = new StubHandler(HttpStatusCode.OK, Ofp);
        using var unknown = new StubHandler(HttpStatusCode.BadRequest, """{"fetch":{"status":"Error: Unknown UserID"}}""");
        using var okHttp = new HttpClient(ok);
        using var unknownHttp = new HttpClient(unknown);
        CancellationToken ct = TestContext.Current.CancellationToken;

        SimBriefAnswer found = await new SimBriefClient(okHttp).FetchAsync("pilot", ct);
        SimBriefAnswer missing = await new SimBriefClient(unknownHttp).FetchAsync("nobody", ct);
        SimBriefAnswer empty = await new SimBriefClient(okHttp).FetchAsync("  ", ct);

        Assert.Equal("EGLL", found.Destination);
        Assert.Contains("EDDM → EGLL", Gui.SimBriefTestViewModel.Describe(found), StringComparison.Ordinal);
        Assert.Equal("No flight plan: Unknown UserID.", Gui.SimBriefTestViewModel.Describe(missing));
        Assert.Null(empty.Origin);
        Assert.NotNull(empty.Problem);
    }

    // ---------------------------------------------------------------- the package's textures there

    private string Package(params TextureName[] textures)
    {
        foreach (TextureName name in textures) _tree.WriteText($"pkg/terrain/{name.Stem}.ter", "A\n800\nTERRAIN\n");
        Directory.CreateDirectory(_tree.PathOf("pkg"));
        return _tree.PathOf("pkg");
    }

    private static readonly TextureName AtAirport = TextureName.At(Lat, Lon, "BI", 18);
    private static readonly TextureName Around = TextureName.At(Lat + 0.03, Lon, "BI", 16); // about 3.5 km north
    private static readonly TextureName FarAway = TextureName.At(Lat + 0.3, Lon, "BI", 16); // about 33 km north

    private static string[] Stems(IEnumerable<VirtualPath> textures) => [.. textures.Select(t => Path.GetFileNameWithoutExtension(t.Name))];

    [Fact]
    public void Picks_the_package_textures_within_the_radius_nearest_first()
    {
        string package = Package(FarAway, Around, AtAirport);
        _tree.WriteText($"pkg/terrain/{AtAirport.Stem}_sea_overlay.ter", "A\n800\nTERRAIN\n"); // the same texture again

        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(AirportTextures.Near(package, Lat, Lon, radiusKm: 10, max: 100)));
        Assert.Equal([AtAirport.Stem], Stems(AirportTextures.Near(package, Lat, Lon, radiusKm: 10, max: 1)));
        Assert.Empty(AirportTextures.Near(_tree.PathOf("nothing"), Lat, Lon, radiusKm: 10, max: 100));
    }

    // ---------------------------------------------------------------- what is queued

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Clock = new FixedClock(Now);

    private static Func<CancellationToken, Task<FlightPlan?>> PlanFrom(FlightOrigin origin, DateTimeOffset? generated = null) =>
        _ => Task.FromResult<FlightPlan?>(new FlightPlan(origin, null, null, [], generated));

    private async Task<(int Queued, List<VirtualPath> Prefetched)> Run(Func<VirtualPath, bool> isCached, FlightOrigin? origin = null,
        (double, double)? aircraft = null, DateTimeOffset? generated = null)
    {
        string package = Package(AtAirport, Around, FarAway);
        var prefetched = new List<VirtualPath>();
        var target = new DepartureTarget("pkg", package, isCached, textures =>
        {
            prefetched.AddRange(textures);
            return textures.Count;
        });
        var prefetch = new DeparturePrefetch(PlanFrom(origin ?? new FlightOrigin("EDDM", Lat, Lon), generated), () => aircraft, [target], radiusKm: 10, Clock);
        return (await prefetch.RunAsync(TestContext.Current.CancellationToken), prefetched);
    }

    [Fact]
    public async Task Only_textures_the_cache_does_not_have_are_queued()
    {
        var (queued, prefetched) = await Run(t => t.Name.StartsWith(AtAirport.Stem, StringComparison.Ordinal));

        Assert.Equal(1, queued);
        Assert.Equal([Around.Stem], Stems(prefetched));
    }

    [Fact]
    public async Task Nothing_is_queued_when_everything_is_cached()
    {
        var (queued, prefetched) = await Run(_ => true);

        Assert.Equal(0, queued);
        Assert.Empty(prefetched);
    }

    [Fact]
    public async Task An_aircraft_somewhere_else_means_the_flight_plan_is_not_this_flight()
    {
        var (queued, prefetched) = await Run(_ => false, aircraft: (51.4775, -0.461389));
        var (nearby, _) = await Run(_ => false, aircraft: (Lat + 0.01, Lon));

        Assert.Equal(0, queued);
        Assert.Empty(prefetched);
        Assert.Equal(2, nearby);
    }

    [Fact]
    public async Task A_plan_generated_more_than_six_hours_ago_is_taken_for_an_earlier_flight()
    {
        // R11: at mount start X-Plane usually sends no position, so the plan's age is the check.
        var (old, prefetched) = await Run(_ => false, generated: Now - TimeSpan.FromHours(7));
        var (recent, _) = await Run(_ => false, generated: Now - TimeSpan.FromHours(2));
        var (unknown, _) = await Run(_ => false); // a plan without the time: used

        Assert.Equal(0, old);
        Assert.Empty(prefetched);
        Assert.Equal(2, recent);
        Assert.Equal(2, unknown);
    }

    [Fact]
    public void Reads_when_the_plan_was_generated()
    {
        const string withTime = """{"fetch":{"status":"Success"},"params":{"request_id":"1","time_generated":"1762790400"},"origin":{"icao_code":"EDDM","pos_lat":"48.35","pos_long":"11.78"}}""";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762790400), SimBriefClient.Parse(withTime).Plan!.Generated);
        Assert.Null(SimBriefClient.Parse(Ofp).Plan!.Generated); // no params
        const string garbled = """{"params":{"time_generated":"soon"},"origin":{"icao_code":"EDDM","pos_lat":"48.35","pos_long":"11.78"}}""";
        Assert.Null(SimBriefClient.Parse(garbled).Plan!.Generated);
    }

    [Fact]
    public async Task No_flight_plan_queues_nothing()
    {
        var prefetch = new DeparturePrefetch(_ => Task.FromResult<FlightPlan?>(null), () => null,
            [new DepartureTarget("pkg", Package(AtAirport), _ => false, _ => throw new InvalidOperationException("nothing to queue"))], radiusKm: 10);

        Assert.Equal(0, await prefetch.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task With_streaming_the_whole_area_XPlane_loads_is_queued_and_without_its_DSFs_the_circle()
    {
        string package = Package(AtAirport, Around, FarAway);
        _tree.WriteBytes("pkg/Earth nav data/+40+010/+48+011.dsf", SceneryAreaTests.Dsf($"terrain/{AtAirport.Stem}.ter", $"terrain/{FarAway.Stem}.ter"));
        var area = new List<VirtualPath>();
        var whole = new List<VirtualPath>();
        DepartureTarget Target(string source) => new("pkg", source, _ => false, t => { whole.AddRange(t); return t.Count; },
            (textures, _, _) => { area.AddRange(textures); return (textures.Count - 1, 1); });
        CancellationToken ct = TestContext.Current.CancellationToken;

        int queued = await new DeparturePrefetch(PlanFrom(new FlightOrigin("EDDM", Lat, Lon)), () => null, [Target(package)], 10).RunAsync(ct);
        Assert.Equal(1, queued);
        Assert.Equal([AtAirport.Stem, FarAway.Stem], Stems(area)); // as the DSF names them, far ones too
        Assert.Empty(whole);

        // A package without its DSFs there (or 7-Zip ones): the textures within the radius, whole.
        Directory.Delete(_tree.PathOf("pkg/Earth nav data"), recursive: true);
        area.Clear();
        await new DeparturePrefetch(PlanFrom(new FlightOrigin("EDDM", Lat, Lon)), () => null, [Target(package)], 10).RunAsync(ct);
        Assert.Empty(area);
        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(whole));
    }

    // ---------------------------------------------------------------- the settings

    [Fact]
    public void The_settings_are_off_by_default_and_the_radius_is_checked()
    {
        MountSettings Parse(params string[] extra)
        {
            Directory.CreateDirectory(_tree.PathOf("source"));
            return MountSettings.FromValues(TestSettings.Of(
                [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", "texture-provider = bing", .. extra]));
        }

        Assert.Null(Parse().SimBriefUser);
        Assert.Equal(MountSettings.DefaultSimBriefRadiusKm, Parse().SimBriefRadiusKm);
        Assert.Equal("pilot", Parse("simbrief-user = pilot ", "simbrief-radius-km = 20").SimBriefUser);
        Assert.Equal(20, Parse("simbrief-radius-km = 20").SimBriefRadiusKm);
        Assert.Throws<SettingsException>(() => Parse("simbrief-radius-km = 0"));
        Assert.Throws<SettingsException>(() => Parse("simbrief-radius-km = 51"));
    }
}
