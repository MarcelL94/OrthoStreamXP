using System.Diagnostics;
using System.Net;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public async Task A_tile_is_downloaded_and_decoded()
    {
        var server = new FakeTileServer();
        using var tiles = new TileSource("test", FakeTileServer.Template, 4, handler: server);
        byte[] rgb = await tiles.GetTileAsync(1, 2, 3, priority: false, new TileStats());
        Assert.Equal(256 * 256 * 3, rgb.Length);
        Assert.Equal([40, 90, 160], rgb[..3]);
    }

    [Fact]
    public async Task A_missing_tile_is_not_retried()
    {
        var server = new FakeTileServer(status: _ => HttpStatusCode.NotFound);
        using var tiles = new TileSource("test", FakeTileServer.Template, 4, attempts: 4, handler: server);
        await Assert.ThrowsAsync<IOException>(() => tiles.GetTileAsync(1, 2, 3, false, new TileStats()));
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_on_the_next_server()
    {
        var server = new FakeTileServer(status: _ => HttpStatusCode.ServiceUnavailable);
        using var tiles = new TileSource("test", FakeTileServer.Template, 4, attempts: 3, handler: server);
        await Assert.ThrowsAsync<IOException>(() => tiles.GetTileAsync(1, 2, 3, false, new TileStats()));
        Assert.Equal(["t3.test", "t4.test", "t5.test"], server.Requests.Select(u => u.Host)); // (x + y + attempt) % 8
    }

    [Fact]
    public async Task Concurrent_builds_share_one_download_per_tile()
    {
        var server = new FakeTileServer(delay: _ => TimeSpan.FromMilliseconds(200));
        using var tiles = new TileSource("test", FakeTileServer.Template, 4, handler: server);
        var stats = new TileStats();
        await Task.WhenAll(tiles.GetTileAsync(5, 5, 10, false, stats), tiles.GetTileAsync(5, 5, 10, false, stats));
        Assert.Single(server.Requests);
        Assert.Equal((1, 1), (stats.Misses, stats.Hits));
    }

    [Fact]
    public async Task A_provisional_image_is_returned_at_once_on_the_second_open()
    {
        // Full-detail tiles (zoom 14 for a ZL16 name at level -3: 16 + (-3 + 1)) are slow; provisional ones (zoom 13) are quick.
        var server = new FakeTileServer(delay: zoom => zoom == 14 ? TimeSpan.FromSeconds(3) : TimeSpan.Zero);
        using var tiles = new TileSource("test", FakeTileServer.Template, 32, handler: server);
        var roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(-3, TextureSizing.MaxSize));
        using var scheduler = new TextureScheduler(roots, new TextureBuilder(tiles, 2),
            new TextureOptions("test", new TexturePlan(-3, TextureSizing.MaxSize), 2, Background: true, ProvisionalLod: 1, ProvisionalWaitSeconds: 0.5));
        VirtualPath texture = VirtualPath.Of("textures", "21904_35824_BI16.dds");

        string? first = await scheduler.ProvisionalAsync(texture); // the security check: waits, then builds the stand-in
        var second = Stopwatch.StartNew();
        string? again = await scheduler.ProvisionalAsync(texture); // the open itself must not wait again
        second.Stop();

        Assert.NotNull(first);
        Assert.Equal(first, again);
        Assert.True(second.Elapsed < TimeSpan.FromSeconds(0.3), $"second open took {second.Elapsed.TotalSeconds:F2}s");
        await scheduler.ShutdownAsync();
        Assert.False(File.Exists(first), "the stand-in is removed once the full texture is built");
        Assert.NotNull(roots.CachedFile(texture));
    }
}
