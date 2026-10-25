using System.Text;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>Generated .ter files must match AutoOrtho's own files byte for byte (expected texts are copied from z_ao_eur).</summary>
public sealed class TerrainDefinitionsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly TerrainDefinitions _terrain;

    public TerrainDefinitionsTests()
    {
        Directory.CreateDirectory(_tree.PathOf("source"));
        _terrain = new TerrainDefinitions(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(0, TextureSizing.MaxSize)));
    }

    public void Dispose() => _tree.Dispose();

    private string Generate(string name) =>
        Encoding.UTF8.GetString(_terrain.Resolve(VirtualPath.Of("terrain", name)) ?? throw new InvalidOperationException("no definition"));

    [Fact]
    public void Base_definition_matches_AutoOrtho()
    {
        Assert.Equal(
            "A\n800\nTERRAIN\n\nLOAD_CENTER 74.50816 18.85254 2613 4096\nBASE_TEX_NOWRAP ../textures/11952_36192_BI16.dds\n" +
            "DECAL_LIB lib/g10/decals/maquify_2_green_key.dcl\nNO_ALPHA\n",
            Generate("11952_36192_BI16.ter"));
    }

    [Fact]
    public void Water_overlay_matches_AutoOrtho()
    {
        Assert.Equal(
            "A\n800\nTERRAIN\n\nLOAD_CENTER 39.18969 -8.07495 1895 4096\nBASE_TEX_NOWRAP ../textures/100000_125184_BI18.dds\n" +
            "BORDER_TEX ../textures/water_transition.png\nWET\nNO_SHADOW\n",
            Generate("100000_125184_BI18_water_overlay.ter"));
    }

    [Theory]
    [InlineData("textures")]
    [InlineData("_textures")] // XPME base packages keep their masks here
    public void Sea_overlay_uses_the_tiles_own_mask_and_its_size(string maskFolder)
    {
        _tree.WritePngHeader($"source/{maskFolder}/100000_148176_ZL18.png", 256);
        Assert.Equal(
            "A\n800\nTERRAIN\n\nLOAD_CENTER 39.18969 23.49976 1895 4096\nBASE_TEX_NOWRAP ../textures/100000_148176_BI18.dds\n" +
            "LOAD_CENTER_BORDER 39.18969 23.49976 1895 256\nBORDER_TEX ../textures/100000_148176_ZL18.png\nWET\nNO_SHADOW\n",
            Generate("100000_148176_BI18_sea_overlay.ter"));
    }

    [Fact]
    public void Sea_overlay_without_a_mask_still_exists_with_the_generic_water_blend()
    {
        // A missing .ter would make X-Plane reject the whole DSF.
        string definition = Generate("100000_148176_BI18_sea_overlay.ter");
        Assert.Contains("BORDER_TEX ../textures/water_transition.png\nWET\nNO_SHADOW\n", definition, StringComparison.Ordinal);
        Assert.DoesNotContain("LOAD_CENTER_BORDER", definition, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_definition_gets_the_texture_size_but_keeps_the_border_size()
    {
        _tree.WriteText("source/terrain/21904_35824_BI16_sea_overlay.ter",
            "A\r\n800\r\nTERRAIN\r\n\r\nLOAD_CENTER 39.18969 23.49976 1895 4096\r\nBASE_TEX_NOWRAP ../textures/x.dds\r\n" +
            "LOAD_CENTER_BORDER 39.18969 23.49976 1895 1024\r\nBORDER_TEX ../textures/x.png\r\n");
        var terrain = new TerrainDefinitions(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache2"), null, new TexturePlan(-3, TextureSizing.MaxSize)));
        string text = Encoding.UTF8.GetString(terrain.Resolve(VirtualPath.Of("terrain", "21904_35824_BI16_sea_overlay.ter"))!);
        Assert.Contains("LOAD_CENTER 39.18969 23.49976 1895 1024\n", text, StringComparison.Ordinal);
        Assert.Contains("LOAD_CENTER_BORDER 39.18969 23.49976 1895 1024\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }

    [Theory]
    [InlineData("terrain", "not_a_tile.ter")]
    [InlineData("terrain", "11953_36192_BI16.ter")] // row not on the 16-tile grid
    [InlineData("textures", "11952_36192_BI16.dds")]
    public void Other_paths_are_not_terrain_definitions(string folder, string name) =>
        Assert.Null(_terrain.Resolve(VirtualPath.Of(folder, name)));

    [Fact]
    public void A_definition_is_produced_once_per_session_and_never_written_to_the_cache()
    {
        byte[] first = _terrain.Resolve(VirtualPath.Of("terrain", "11952_36192_BI16.ter"))!;
        Assert.Same(first, _terrain.Resolve(VirtualPath.Of("terrain", "11952_36192_BI16.ter")));
        Assert.False(Directory.Exists(_tree.PathOf("cache/terrain")) && Directory.EnumerateFiles(_tree.PathOf("cache/terrain")).Any());
    }

    [Fact]
    public void Copies_earlier_versions_wrote_into_the_cache_are_removed()
    {
        _tree.WriteText("cache/terrain/11952_36192_BI16.ter", "old");
        _tree.WriteText("cache/textures/11952_36192_BI16.dds", "DDS ");
        TerrainDefinitions.RemoveCachedCopies(_tree.PathOf("cache"));
        Assert.Empty(Directory.EnumerateFiles(_tree.PathOf("cache/terrain")));
        Assert.True(File.Exists(_tree.PathOf("cache/textures/11952_36192_BI16.dds")));
    }
}
