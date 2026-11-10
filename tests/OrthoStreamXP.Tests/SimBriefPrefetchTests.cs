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
        FlightOrigin? origin = await new SimBriefClient(http).LatestOriginAsync(user, TestContext.Current.CancellationToken);
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

    private async Task<(int Queued, List<VirtualPath> Prefetched)> Run(Func<VirtualPath, bool> isCached, FlightOrigin? origin = null,
        (double, double)? aircraft = null)
    {
        string package = Package(AtAirport, Around, FarAway);
        var prefetched = new List<VirtualPath>();
        var target = new DepartureTarget("pkg", package, isCached, textures =>
        {
            prefetched.AddRange(textures);
            return textures.Count;
        });
        var prefetch = new DeparturePrefetch(_ => Task.FromResult<FlightOrigin?>(origin ?? new FlightOrigin("EDDM", Lat, Lon)), () => aircraft, [target], radiusKm: 10);
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
    public async Task No_flight_plan_queues_nothing()
    {
        var prefetch = new DeparturePrefetch(_ => Task.FromResult<FlightOrigin?>(null), () => null,
            [new DepartureTarget("pkg", Package(AtAirport), _ => false, _ => throw new InvalidOperationException("nothing to queue"))], radiusKm: 10);

        Assert.Equal(0, await prefetch.RunAsync(TestContext.Current.CancellationToken));
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
