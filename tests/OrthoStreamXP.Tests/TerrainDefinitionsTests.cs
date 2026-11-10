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
        _terrain = new TerrainDefinitions(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(0, TextureSizing.MaxSize)), decals: null);
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
        var terrain = new TerrainDefinitions(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache2"), null, new TexturePlan(-3, TextureSizing.MaxSize)), decals: null);
        string text = Encoding.UTF8.GetString(terrain.Resolve(VirtualPath.Of("terrain", "21904_35824_BI16_sea_overlay.ter"))!);
        Assert.Contains("LOAD_CENTER 39.18969 23.49976 1895 1024\n", text, StringComparison.Ordinal);
        Assert.Contains("LOAD_CENTER_BORDER 39.18969 23.49976 1895 1024\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
    }

    private const string Tile = "100000_222560_BI18";
    private const string Library = "lib/g10/decals/maquify_2_green_key.dcl";

    private TerrainDefinitions WithDecals(int strength, string? xplane = null) =>
        new(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(0, TextureSizing.MaxSize)), new TerrainDecals(xplane, strength));

    private static string Resolve(TerrainDefinitions terrain, string name) =>
        Encoding.UTF8.GetString(terrain.Resolve(VirtualPath.Of("terrain", name)) ?? throw new InvalidOperationException("no definition"));

    private void WritePackageDefinition() =>
        _tree.WriteText($"source/terrain/{Tile}.ter",
            $"A\r\n800\r\nTERRAIN\r\n\r\nLOAD_CENTER 39.18969 125.65063 1895 4096\r\nBASE_TEX_NOWRAP ../textures/{Tile}.dds\r\n" +
            $"DECAL_LIB {Library}\r\n  decal_params 1 2 3\r\nNO_ALPHA\r\n");

    /// <summary>An X-Plane folder with the decal package: library.txt, the .dcl and its 2x1 RGBA texture.</summary>
    private string FakeXPlane()
    {
        const string package = "xplane/Resources/default scenery/1000 decals";
        _tree.WriteText($"{package}/library.txt", $"A\n800\nLIBRARY\n\nEXPORT\t{Library}\t\tmaquify_2_green_key.dcl\n");
        _tree.WriteText($"{package}/maquify_2_green_key.dcl",
            "A\r\n1000\r\nDECAL\r\n\r\nDECAL_PARAMS_PROJ\t25\t25\t0.8\t\t-12.0\t20.5\t-8.0\t0\t0.0\t0.0\t\t12\t-20\t8\t-0.0\t0.0\t1.5\ttextures/shrub_dirt.png\r\n\r\n\r\n# a comment\r\n");
        Directory.CreateDirectory(_tree.PathOf($"{package}/textures"));
        File.WriteAllBytes(_tree.PathOf($"{package}/textures/shrub_dirt.png"), Png([0, 0, 0, 0, 255, 128, 64, 255]));
        return _tree.PathOf("xplane");
    }

    private static byte[] Png(byte[] rgba)
    {
        var info = new SkiaSharp.SKImageInfo(rgba.Length / 4, 1, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        rgba.CopyTo(bitmap.GetPixelSpan());
        using SkiaSharp.SKData png = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    private static byte[] Pixels(byte[] png)
    {
        using var codec = SkiaSharp.SKCodec.Create(SkiaSharp.SKData.CreateCopy(png));
        var info = new SkiaSharp.SKImageInfo(codec.Info.Width, codec.Info.Height, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bitmap = new SkiaSharp.SKBitmap(info);
        codec.GetPixels(info, bitmap.GetPixels());
        return bitmap.GetPixelSpan().ToArray();
    }

    [Fact]
    public void At_0_percent_a_generated_definition_has_no_decal_library()
    {
        Assert.Equal(
            "A\n800\nTERRAIN\n\nLOAD_CENTER 74.50816 18.85254 2613 4096\nBASE_TEX_NOWRAP ../textures/11952_36192_BI16.dds\nNO_ALPHA\n",
            Resolve(WithDecals(0), "11952_36192_BI16.ter"));
    }

    [Fact]
    public void At_0_percent_the_package_definitions_decal_lines_are_removed_and_the_rest_kept()
    {
        WritePackageDefinition();
        Assert.Equal(
            $"A\n800\nTERRAIN\n\nLOAD_CENTER 39.18969 125.65063 1895 4096\nBASE_TEX_NOWRAP ../textures/{Tile}.dds\nNO_ALPHA\n",
            Resolve(WithDecals(0), $"{Tile}.ter"));
    }

    [Fact]
    public void At_100_percent_the_package_definitions_decal_library_is_kept()
    {
        WritePackageDefinition();
        Assert.Contains($"DECAL_LIB {Library}\n", Resolve(WithDecals(100, FakeXPlane()), $"{Tile}.ter"), StringComparison.Ordinal);
    }

    [Fact]
    public void In_between_the_library_is_inlined_with_a_faded_texture_the_mount_serves()
    {
        WritePackageDefinition();
        string xplane = FakeXPlane();
        var decals = new TerrainDecals(xplane, 50);
        var terrain = new TerrainDefinitions(new SceneryRoots(_tree.PathOf("source"), _tree.PathOf("cache"), null, new TexturePlan(0, TextureSizing.MaxSize)), decals);

        string definition = Resolve(terrain, $"{Tile}.ter");
        Assert.DoesNotContain("DECAL_LIB", definition, StringComparison.Ordinal);
        Assert.Contains(
            $"BASE_TEX_NOWRAP ../textures/{Tile}.dds\n" +
            $"DECAL_PARAMS_PROJ\t25\t25\t0.8\t-12.0\t20.5\t-8.0\t0\t0.0\t0.0\t12\t-20\t8\t-0.0\t0.0\t1.5\t../{TerrainDecals.Folder}/shrub_dirt.png\n" +
            "  decal_params 1 2 3\nNO_ALPHA\n", definition, StringComparison.Ordinal);

        // Every channel, alpha too (a greyscale decal, not transparency), keeps half its distance from mid-grey.
        byte[] texture = decals.Texture(VirtualPath.Of(TerrainDecals.Folder, "shrub_dirt.png"))!;
        Assert.Equal([64, 64, 64, 64, 192, 128, 96, 192], Pixels(texture));
        Assert.Null(decals.Texture(VirtualPath.Of(TerrainDecals.Folder, "other.png")));
    }

    [Fact]
    public void In_between_without_the_X_Plane_folder_the_library_is_kept()
    {
        WritePackageDefinition();
        Assert.Contains($"DECAL_LIB {Library}\n", Resolve(WithDecals(50), $"{Tile}.ter"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_with_lines_that_cannot_be_faded_is_kept()
    {
        WritePackageDefinition();
        string xplane = FakeXPlane();
        File.AppendAllText(_tree.PathOf("xplane/Resources/default scenery/1000 decals/maquify_2_green_key.dcl"), "NORMAL_DECAL_PARAMS 1 2 normal.png\n");
        Assert.Contains($"DECAL_LIB {Library}\n", Resolve(WithDecals(50, xplane), $"{Tile}.ter"), StringComparison.Ordinal);
    }

    [Fact]
    public void Fading_to_100_percent_keeps_the_image()
    {
        byte[] rgba = [0, 10, 200, 255, 128, 127, 129, 1];
        Assert.Equal(rgba, Pixels(TerrainDecals.Fade(Png(rgba), 100)));
        Assert.Equal([128, 128, 128, 128, 128, 128, 128, 128], Pixels(TerrainDecals.Fade(Png(rgba), 0)));
    }

    [Fact]
    public void The_ground_detail_setting_is_0_unless_raised_and_stays_within_0_to_100()
    {
        MountSettings Parse(params string[] extra) => MountSettings.FromValues(TestSettings.Of(
            [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", .. extra]));
        Assert.Equal(0, Parse().GroundDetail);
        Assert.Equal(40, Parse("ground-detail = 40").GroundDetail);
        Assert.Throws<SettingsException>(() => Parse("ground-detail = 101"));
        Assert.Throws<SettingsException>(() => Parse("ground-detail = -1"));
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
