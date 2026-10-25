using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>Load-ahead builds a loading DSF's textures between X-Plane opening its .ter files and asking for the textures.</summary>
public sealed class LoadAheadTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TileSource _tiles;
    private readonly SceneryRoots _roots;
    private readonly TextureScheduler _scheduler;
    private readonly TextureCatalog _catalog = new();
    private readonly SceneryOverlay _overlay;
    private readonly LoadAheadPrefetcher _loadAhead;

    public LoadAheadTests()
    {
        _tiles = new TileSource("test", FakeTileServer.Template, 32, handler: new FakeTileServer());
        _roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(-3, TextureSizing.MaxSize));
        Directory.CreateDirectory(_roots.Source);
        _scheduler = new TextureScheduler(_roots, new TextureBuilder(_tiles, 2),
            new TextureOptions("test", new TexturePlan(-3, TextureSizing.MaxSize), 4, Background: true, ProvisionalLod: 0, ProvisionalWaitSeconds: 0));
        _overlay = new SceneryOverlay(_roots, _scheduler, false, _catalog);
        _loadAhead = new LoadAheadPrefetcher(_catalog, _scheduler, _roots, inFlight: 8);
    }

    public void Dispose()
    {
        _loadAhead.Dispose();
        _scheduler.ShutdownAsync().GetAwaiter().GetResult();
        _scheduler.Dispose();
        _tiles.Dispose();
        _tree.Dispose();
    }

    /// <summary>X-Plane loading a DSF: it opens the terrain definitions through the mount.</summary>
    private void OpenTerrain(params string[] stems)
    {
        foreach (string stem in stems) _overlay.Open($@"\terrain\{stem}.ter");
    }

    [Fact]
    public async Task Textures_are_built_once_their_terrain_is_opened_and_then_found_ready()
    {
        _loadAhead.Start();
        string[] stems = ["21904_35824_BI16", "21904_35840_BI16", "21920_35824_BI16", "87616_143296_BI18"];
        OpenTerrain(stems);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (stems.Any(s => _roots.CachedFile(VirtualPath.Of("textures", s + ".dds")) is null) && DateTime.UtcNow < deadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);

        foreach (string stem in stems) _overlay.Resolve($@"\textures\{stem}.dds"); // X-Plane now asks for them
        string summary = _overlay.PrefetchSummary()!;
        Assert.Contains("queued 4 (load-ahead 4, flight path 0) and built 4 textures", summary, StringComparison.Ordinal);
        Assert.Contains("X-Plane requested 4 new textures: 4 ready by prefetch (100%)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_texture_nearest_X_Planes_latest_request_goes_first()
    {
        // A row of textures west to east (columns 16 apart are about 6 km apart).
        OpenTerrain("21904_35776_BI16", "21904_35792_BI16", "21904_35808_BI16", "21904_35872_BI16", "21904_35888_BI16");
        _catalog.NoteTextureRequested(VirtualPath.Of("textures", "21904_35888_BI16.dds")); // X-Plane works in the east

        Assert.Equal("21904_35872_BI16.dds", _loadAhead.TakeNext()?.Texture.Name);
        Assert.Equal("21904_35808_BI16.dds", _loadAhead.TakeNext()?.Texture.Name);
    }

    [Fact]
    public void Without_a_recent_request_the_aircraft_decides_the_order()
    {
        OpenTerrain("21904_35776_BI16", "21904_35872_BI16");
        var (lat, lon) = Tiles.TileToLatLon(35776 + 8, 21904 + 8, 16);
        _loadAhead.OnAircraftPosition(lat, lon);
        Assert.Equal("21904_35776_BI16.dds", _loadAhead.TakeNext()?.Texture.Name);
    }

    [Fact]
    public void A_texture_X_Plane_asks_for_leaves_the_waiting_list()
    {
        OpenTerrain("21904_35776_BI16");
        _catalog.NoteTextureRequested(VirtualPath.Of("textures", "21904_35776_BI16.dds"));
        Assert.Null(_loadAhead.TakeNext());
    }
}
