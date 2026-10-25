using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>The log must tell whether prefetch got a texture ready before X-Plane asked for it.</summary>
public sealed class PrefetchReportTests : IDisposable
{
    private const string Texture = "21904_35824_BI16.dds";
    private readonly TempTree _tree = new();
    private readonly TileSource _tiles;
    private readonly TextureScheduler _scheduler;
    private readonly SceneryOverlay _overlay;

    public PrefetchReportTests()
    {
        // Each tile takes 300 ms, so a 16-tile texture (ZL16 at level -3, all tiles in parallel) builds in about a third of a second.
        _tiles = new TileSource("test", FakeTileServer.Template, 32, handler: new FakeTileServer(delay: _ => TimeSpan.FromMilliseconds(300)));
        var roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(-3, TextureSizing.MaxSize));
        Directory.CreateDirectory(roots.Source);
        _scheduler = new TextureScheduler(roots, new TextureBuilder(_tiles, 2),
            new TextureOptions("test", new TexturePlan(-3, TextureSizing.MaxSize), 2, Background: true, ProvisionalLod: 0, ProvisionalWaitSeconds: 0));
        _overlay = new SceneryOverlay(roots, _scheduler, false);
    }

    public void Dispose()
    {
        _scheduler.ShutdownAsync().GetAwaiter().GetResult();
        _scheduler.Dispose();
        _tiles.Dispose();
        _tree.Dispose();
    }

    private string Summary => _overlay.PrefetchSummary()!;

    [Fact]
    public async Task A_texture_prefetch_finished_first_is_reported_as_ready_by_prefetch()
    {
        Assert.Equal(1, _scheduler.Prefetch([VirtualPath.Of("textures", Texture)]));
        // Wait for the prefetch build itself: shutting down now would drop it if it has not started yet.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (_scheduler.PrefetchBuilt == 0 && DateTime.UtcNow < deadline) await Task.Delay(20, TestContext.Current.CancellationToken);

        _overlay.Resolve($@"\textures\{Texture}");
        Assert.Contains("queued 1 (load-ahead 0, flight path 1) and built 1 textures", Summary, StringComparison.Ordinal);
        Assert.Contains("X-Plane requested 1 new textures: 1 ready by prefetch (100%)", Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_texture_prefetch_never_picked_is_reported_as_built_on_request()
    {
        _overlay.Resolve($@"\textures\{Texture}");
        _overlay.Resolve($@"\textures\{Texture}"); // the second step of the same open: not counted again
        Assert.Contains("queued 0 (load-ahead 0, flight path 0) and built 0 textures", Summary, StringComparison.Ordinal);
        Assert.Contains("X-Plane requested 1 new textures: 0 ready by prefetch (0%)", Summary, StringComparison.Ordinal);
        Assert.Contains("1 built on request (100%)", Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_texture_requested_while_prefetch_builds_it_is_reported_as_waited()
    {
        _scheduler.Prefetch([VirtualPath.Of("textures", Texture)]);
        await Task.Delay(100, TestContext.Current.CancellationToken); // the prefetch build is under way

        _overlay.Resolve($@"\textures\{Texture}");
        Assert.Contains("1 waited for a prefetch build (100%)", Summary, StringComparison.Ordinal);
    }
}
