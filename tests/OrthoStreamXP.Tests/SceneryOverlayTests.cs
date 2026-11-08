using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

public sealed class SceneryOverlayTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private SceneryOverlay Overlay(string? store = null)
    {
        Directory.CreateDirectory(_tree.PathOf("source"));
        return new SceneryOverlay(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"),
            store is null ? null : _tree.PathOf(store), new TexturePlan(-3, TextureSizing.MaxSize)), null, lazyTerrainListing: false);
    }

    [Theory]
    [InlineData("textures")]
    [InlineData("_textures")]
    public void A_dds_twin_of_a_png_mask_is_not_found_so_XPlane_loads_the_png(string folder)
    {
        string mask = _tree.WritePngHeader($"source/{folder}/20624_36576_ZL16.png", 1024);
        SceneryOverlay overlay = Overlay();

        var notFound = Assert.Throws<NtStatusException>(() => overlay.Resolve(@"\textures\20624_36576_ZL16.dds"));
        Assert.Equal(NtStatus.ObjectNameNotFound, notFound.Status);
        Assert.Equal(mask, overlay.Resolve(@"\textures\20624_36576_ZL16.png"));
    }

    [Fact]
    public void A_tiles_terrain_definition_opens_from_memory_and_other_definitions_from_the_package()
    {
        string library = _tree.WriteText("source/terrain/lib_terrain.ter", "A\n800\nTERRAIN\n");
        SceneryOverlay overlay = Overlay();

        OverlayEntry tile = overlay.Open(@"\terrain\21904_35824_BI16.ter");
        Assert.Null(tile.Backing);
        Assert.Contains("LOAD_CENTER", System.Text.Encoding.UTF8.GetString(tile.Content!), StringComparison.Ordinal);
        Assert.Same(tile.Content, overlay.TerrainDefinition(VirtualPath.Of("terrain", "21904_35824_BI16.ter")));
        Assert.Empty(Directory.EnumerateFiles(_tree.PathOf("cache/terrain")));

        Assert.Equal(library, overlay.Open(@"\terrain\lib_terrain.ter").Backing);
    }

    [Fact]
    public void A_dds_the_package_ships_beside_its_png_is_served()
    {
        _tree.WritePngHeader("source/textures/water_transition.png", 64);
        string dds = _tree.WriteText("source/textures/water_transition.dds", "DDS ");
        Assert.Equal(dds, Overlay().Resolve(@"\textures\water_transition.dds"));
    }

    [Fact]
    public void Textures_merges_the_cache_textures_and_underscore_textures_and_hides_the_latter_at_the_root()
    {
        _tree.WriteText("source/textures/a.png", "a");
        _tree.WriteText("source/_textures/b.png", "b");
        _tree.WriteText("source/Earth nav data/x.dsf", "x");
        _tree.WriteText("cache/textures/c.dds", "c");
        SceneryOverlay overlay = Overlay();

        Assert.Equal(["a.png", "b.png", "c.dds"], overlay.List(VirtualPath.Of("textures")).Select(e => e.Name));
        Assert.Equal(["Earth nav data", "terrain", "textures"], overlay.List(VirtualPath.Parse(@"\")).Select(e => e.Name));
    }

    [Theory]
    [InlineData("textures")]
    [InlineData("_textures")]
    public void The_packages_own_photo_textures_are_hidden_and_never_served_but_its_pngs_are(string folder)
    {
        _tree.WriteText($"source/{folder}/21904_35824_BI16.dds", "Ortho4XP photo");
        string mask = _tree.WritePngHeader($"source/{folder}/21904_35824_ZL16.png", 1024);
        string transition = _tree.WritePngHeader($"source/{folder}/water_transition.png", 64);
        string other = _tree.WriteText($"source/{folder}/custom.dds", "not a photo texture");
        SceneryOverlay overlay = Overlay();

        Assert.Equal(["21904_35824_ZL16.png", "custom.dds", "water_transition.png"],
            overlay.List(VirtualPath.Of("textures")).Select(e => e.Name));
        var notFound = Assert.Throws<NtStatusException>(() => overlay.Resolve(@"\textures\21904_35824_BI16.dds"));
        Assert.Equal(NtStatus.ObjectNameNotFound, notFound.Status);
        Assert.Equal(mask, overlay.Resolve(@"\textures\21904_35824_ZL16.png"));
        Assert.Equal(transition, overlay.Resolve(@"\textures\water_transition.png"));
        Assert.Equal(other, overlay.Resolve(@"\textures\custom.dds"));
    }

    [Fact]
    public void A_downloaded_photo_texture_is_served_over_the_packages_own_copy()
    {
        _tree.WriteText("source/textures/87520_143440_BI18.dds", "Ortho4XP photo");
        string cached = _tree.WriteText("cache/textures/10940_17930_PX15.dds", "DDS "); // Overlay() is at level -3
        Assert.Equal(cached, Overlay().Resolve(@"\textures\87520_143440_BI18.dds"));
    }

    [Fact]
    public void A_lazy_store_dds_is_served_in_place_without_copying()
    {
        string stored = _tree.WriteText("store/21904_35824_BI16.dds", "DDS of another size");
        SceneryOverlay overlay = Overlay(store: "store");

        Assert.Equal(stored, overlay.Resolve(@"\textures\21904_35824_BI16.dds"));
        Assert.False(File.Exists(_tree.PathOf("cache/textures/21904_35824_BI16.dds")));
    }

    [Fact]
    public void A_photo_texture_is_kept_under_the_zoom_it_is_downloaded_at_and_served_by_its_package_name()
    {
        var roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(-1));
        VirtualPath texture = VirtualPath.Of("textures", "87520_143440_BI18.dds");
        Assert.Equal(_tree.PathOf(@"cache\textures\43760_71720_PX17.dds"), roots.CachePath(texture));
        Assert.Equal(_tree.PathOf(@"cache\.xpstream\mips\43760_71720_PX17.mip2"), roots.ChainPath(new TextureName(87520, 143440, "BI", 18), 2));
        Assert.Equal(_tree.PathOf(@"cache\textures\water_transition.png"), roots.CachePath(VirtualPath.Of("textures", "water_transition.png")));

        Directory.CreateDirectory(_tree.PathOf("source"));
        string cached = _tree.WriteText("cache/textures/10940_17930_PX15.dds", "DDS "); // Overlay() is at level -3
        Assert.Equal(cached, Overlay().Resolve(@"\textures\87520_143440_BI18.dds")); // no texture is built without a provider
    }

    [Fact]
    public void Textures_cached_under_package_names_are_renamed_once()
    {
        _tree.WriteText("cache/textures/87520_143440_BI18.dds", "texture");
        _tree.WriteText("cache/.xpstream/mips/87520_143440_BI18.mip2", "chain");
        _tree.WriteText("cache/textures/43792_71648_BI17.dds", "ZL17 keeps its name");
        _tree.WriteText("cache/textures/c.dds", "not a photo texture");
        var roots = new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(-1));

        roots.RenameToCacheNames();

        Assert.Equal(["43760_71720_PX17.dds", "43792_71648_BI17.dds", "c.dds"],
            Directory.EnumerateFiles(_tree.PathOf("cache/textures")).Select(Path.GetFileName).Order());
        Assert.Equal("texture", File.ReadAllText(_tree.PathOf("cache/textures/43760_71720_PX17.dds")));
        Assert.True(File.Exists(_tree.PathOf("cache/.xpstream/mips/43760_71720_PX17.mip2")));
    }

    [Theory]
    [InlineData(@"\textures\..\secret")]
    [InlineData(@"\textures\a.dds:stream")]
    public void Traversal_and_stream_names_are_refused(string fileName)
    {
        var refused = Assert.Throws<NtStatusException>(() => VirtualPath.Parse(fileName));
        Assert.Equal(NtStatus.AccessDenied, refused.Status);
    }
}
