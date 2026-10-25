using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

public sealed class PrefetchTests
{
    private const double Lat = 51.0, Lon = 17.0;

    /// <summary>A catalog entry the given distance north and east of the aircraft.</summary>
    private static TextureCatalog.Entry At(string name, double northKm, double eastKm) =>
        new(VirtualPath.Of("textures", name + ".dds"),
            Lat + northKm * 1000 / 110_574,
            Lon + eastKm * 1000 / (111_320 * Math.Cos(Lat * Math.PI / 180)));

    private static readonly TextureCatalog.Entry[] Around =
    [
        At("north3", 3, 0), At("south3", -3, 0), At("north15", 15, 0), At("east15", 0, 15), At("south20", -20, 0),
    ];

    private static string[] Names(IReadOnlyList<VirtualPath> selected) => [.. selected.Select(p => Path.GetFileNameWithoutExtension(p.Name))];

    [Fact]
    public void Moving_covers_a_corridor_ahead_nearest_first_and_ranks_behind_last()
    {
        // 60 m/s (117 kt) north for 300 s: an 18 km path, 5 km either side.
        var selected = PrefetchPlanner.Select(Around, Lat, Lon, new Motion(60, 0), radiusKm: 5, lookaheadSeconds: 300, maxTextures: 10);
        Assert.Equal(["north3", "north15", "south3"], Names(selected)); // east15 is off the path, south20 too far behind
    }

    [Fact]
    public void Slow_or_stationary_covers_a_circle_nearest_first()
    {
        var selected = PrefetchPlanner.Select(Around, Lat, Lon, motion: null, radiusKm: 5, lookaheadSeconds: 300, maxTextures: 10);
        Assert.Equal(2, selected.Count);
        Assert.Equal(["north3", "south3"], Names(selected).Order());

        var taxiing = PrefetchPlanner.Select(Around, Lat, Lon, new Motion(5, 0), radiusKm: 5, lookaheadSeconds: 300, maxTextures: 10);
        Assert.Equal(2, taxiing.Count); // below 20 kt the path is not predicted
    }

    [Fact]
    public void The_texture_limit_keeps_the_soonest() =>
        Assert.Equal(["north3"], Names(PrefetchPlanner.Select(Around, Lat, Lon, new Motion(60, 0), 5, 300, maxTextures: 1)));

    [Fact]
    public void The_catalog_learns_textures_from_the_terrain_definitions_XPlane_opens()
    {
        var catalog = new TextureCatalog();
        catalog.AddTerrain(VirtualPath.Of("terrain", "21904_35824_BI16.ter"));
        catalog.AddTerrain(VirtualPath.Of("terrain", "21904_35824_BI16_water_overlay.ter")); // same texture
        catalog.AddTerrain(VirtualPath.Of("terrain", "21904_35824_BI16_sea_overlay.ter"));   // same texture
        catalog.AddTerrain(VirtualPath.Of("terrain", "87616_143296_BI18.ter"));             // zoom 18 near an airport
        catalog.AddTerrain(VirtualPath.Of("terrain", "lib_terrain.ter"));                   // not a photo tile
        catalog.AddTerrain(VirtualPath.Of("textures", "21920_35824_BI16.dds"));             // not a terrain definition

        Assert.Equal(["21904_35824_BI16.dds", "87616_143296_BI18.dds"], catalog.Entries.Select(e => e.Texture.Name).Order());
        TextureCatalog.Entry wroclaw = catalog.Entries.Single(e => e.Texture.Name.StartsWith("21904", StringComparison.Ordinal));
        Assert.Equal(51.1, wroclaw.Lat, 1);
        Assert.Equal(16.8, wroclaw.Lon, 1);
    }

    [Fact]
    public void The_overlay_feeds_the_catalog_as_terrain_is_opened()
    {
        using var tree = new TempTree();
        Directory.CreateDirectory(tree.PathOf("source"));
        var catalog = new TextureCatalog();
        var overlay = new SceneryOverlay(new SceneryRoots(tree.PathOf("source"), tree.PathOf("cache"), null, new TexturePlan(-3, TextureSizing.MaxSize)), null, false, catalog);

        overlay.Open(@"\terrain\21904_35824_BI16_sea_overlay.ter");
        Assert.Equal("21904_35824_BI16.dds", Assert.Single(catalog.Entries).Texture.Name);
    }

    [Fact]
    public void Motion_comes_from_successive_positions()
    {
        var tracker = new MotionTracker();
        for (int second = 0; second <= 4; second++)
            Assert.False(tracker.Add(TimeSpan.FromSeconds(second), Lat + second * 100.0 / 110_574, Lon)); // 100 m/s north

        Motion motion = tracker.Current ?? throw new InvalidOperationException("no motion");
        Assert.InRange(motion.SpeedMps, 98, 102); // the test's metres-per-degree and the great-circle formula differ slightly
        Assert.True(motion.TrackDeg is < 1 or > 359);
    }

    [Fact]
    public void Standing_still_has_no_motion_and_a_jump_starts_afresh()
    {
        var tracker = new MotionTracker();
        tracker.Add(TimeSpan.Zero, Lat, Lon);
        tracker.Add(TimeSpan.FromSeconds(5), Lat, Lon);
        Assert.Null(tracker.Current);

        Assert.True(tracker.Add(TimeSpan.FromSeconds(6), 54.58, 18.52)); // a new flight 400 km away
        Assert.Null(tracker.Current);
    }
}
