using System.Net;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Tests;

/// <summary>The connection test: real tiles in rounds of more at once, and the fewest at once that fill the line.</summary>
public sealed class ConnectionTestTests : IDisposable
{
    private readonly TempTree _tree = new();
    private const long Gb = 1L << 30;

    public void Dispose() => _tree.Dispose();

    /// <summary>
    /// A server that answers 20 KB tiles in 80-240 ms (160 on average), but only so many at once: more wait their turn,
    /// like a full line. The times differ from tile to tile, as on a real server: with one time for all, every place
    /// would finish together on the same timer tick (15.6 ms on Windows), the tiles would arrive in a few waves, and a
    /// round's steady speed would be off by about 10 % depending on where its 10th and 90th percent fall among them.
    /// </summary>
    private sealed class LimitedServer(int atOnce, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly SemaphoreSlim _line = new(atOnce);
        private int _requests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref _requests);
            await _line.WaitAsync(cancellationToken);
            try
            {
                await Task.Delay(80 + n * 37 % 9 * 20, cancellationToken);
                return new HttpResponseMessage(status) { Content = new ByteArrayContent(new byte[20_000]) };
            }
            finally
            {
                _line.Release();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _line.Dispose();
            base.Dispose(disposing);
        }
    }

    private static ConnectionRound Round(int atOnce, double mbits, int failed = 0) =>
        new(atOnce, 100, failed, (long)(mbits * 1e6 / 8), 1, 0.1, mbits);

    [Fact]
    public void The_suggestion_is_the_fewest_at_once_within_90_percent_of_the_best()
    {
        Assert.Equal(64, ConnectionTest.Suggest([Round(16, 20), Round(32, 38), Round(64, 60), Round(128, 62), Round(256, 63)]));
        // A round where the server refused some does not count, however fast.
        Assert.Equal(32, ConnectionTest.Suggest([Round(16, 20), Round(32, 40), Round(64, 80, failed: 10)]));
        Assert.Equal(0, ConnectionTest.Suggest([Round(16, 0, failed: 100)]));
    }

    [Fact]
    public void The_test_stops_when_more_at_once_is_no_faster_or_the_server_refuses()
    {
        Assert.False(ConnectionTest.Enough([Round(16, 20)]));
        Assert.False(ConnectionTest.Enough([Round(16, 20), Round(32, 40)]));          // twice as fast: try more
        Assert.True(ConnectionTest.Enough([Round(16, 20), Round(32, 40), Round(64, 41)])); // under 5 % faster: the line is full
        Assert.True(ConnectionTest.Enough([Round(16, 20), Round(32, 40), Round(64, 80, failed: 6)])); // refused: stop
    }

    [Fact]
    public async Task A_server_that_takes_32_at_once_ends_the_test_early_with_a_suggestion_near_32()
    {
        // Real timing, so only what holds on any machine: on a slow one, 64 at once can be a little faster than 32 (the
        // server's places do not wait between downloads), but 128 and 256 never are.
        using var server = new LimitedServer(32);
        ConnectionResult result = await ConnectionTest.RunAsync(new ImageryProvider("test", FakeTileServer.Template), handler: server,
            cancel: TestContext.Current.CancellationToken);

        Assert.InRange(result.Rounds.Count, 3, 4); // stopped before 256
        Assert.InRange(result.Suggested, 32, 64);
        Assert.InRange(result.TileSeconds, 0.08, 1.0); // 64 at once wait their turn at the server too
        Assert.Contains($"{result.Suggested} downloads at once", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_refuses_suggests_nothing()
    {
        using var server = new LimitedServer(64, HttpStatusCode.Forbidden);
        ConnectionResult result = await ConnectionTest.RunAsync(new ImageryProvider("test", FakeTileServer.Template), handler: server,
            cancel: TestContext.Current.CancellationToken);

        Assert.Single(result.Rounds);
        Assert.Equal(0, result.Suggested);
        Assert.Contains("could not download", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_result_is_kept_beside_the_settings_file()
    {
        string path = ConnectionResult.PathFor(_tree.PathOf("orthostreamxp.ini"));
        Assert.EndsWith("orthostreamxp.connection.json", path, StringComparison.Ordinal);
        var result = new ConnectionResult(new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Utc), "bing", [Round(16, 30), Round(32, 55)], 32);

        result.Save(path);

        ConnectionResult loaded = ConnectionResult.Load(path)!;
        Assert.Equal((32, "bing", 2), (loaded.Suggested, loaded.Provider, loaded.Rounds.Count));
        Assert.Equal(55, loaded.Mbits, 1);
        Assert.False(loaded.Decided); // a new test: its suggestion is offered

        (loaded with { Decided = true }).Save(path);
        Assert.True(ConnectionResult.Load(path)!.Decided); // decided: not offered again, after a restart too
    }

    [Fact]
    public void Before_a_flight_the_tested_speed_chooses_the_preset()
    {
        var test = new ConnectionResult(DateTime.UtcNow, "bing", [Round(64, 60), Round(128, 120)], 128);
        Recommendation recommendation = PresetAdvisor.Recommend(16 * Gb, GraphicsMemory.TestFlight, [], test);

        Assert.Equal(QualityPreset.Extra, recommendation.Preset);
        Assert.Contains("connection test measured 120 Mbit/s", recommendation.Reason, StringComparison.Ordinal);
        Assert.Equal(QualityPreset.Sharp, PresetAdvisor.Recommend(16 * Gb, GraphicsMemory.TestFlight, [],
            new ConnectionResult(DateTime.UtcNow, "bing", [Round(64, 30)], 64)).Preset);
    }
}
