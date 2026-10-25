using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>
/// Texture size = the detail --level-change downloads, capped by the level's own limit (4096 px at level 0 and above,
/// half per level below: the detail a ZL18 texture downloads), never enlarged.
/// </summary>
public sealed class TextureQualityTests
{
    [Theory]
    //          level  ZL16  ZL18
    [InlineData(-4, 256, 256)]
    [InlineData(-3, 512, 512)]
    [InlineData(-2, 1024, 1024)]  // Balanced
    [InlineData(-1, 2048, 2048)]  // Sharp
    [InlineData(0, 4096, 4096)]   // Ultra
    [InlineData(1, 4096, 4096)]   // Extra
    [InlineData(2, 4096, 4096)]   // never more than 4096
    public void The_size_follows_the_level(int level, int zl16, int zl18)
    {
        var plan = new TexturePlan(level);
        Assert.Equal((zl16, zl18), (plan.SizeFor(16), plan.SizeFor(18)));
    }

    [Theory]
    //          level  ZL16 detail / size  ZL18 detail / size
    [InlineData(-2, 2, 1)]
    [InlineData(-1, 2, 1)]
    [InlineData(0, 2, 1)]
    [InlineData(1, 2, 2)] // 4096 px is the largest: Extra's zoom 19 airports are shrunk too
    public void Textures_shrink_the_finer_detail_they_download(int level, int zl16, int zl18)
    {
        var plan = new TexturePlan(level);
        // The pixels a texture's tiles hold: 4096 at its own zoom (16 x 16 tiles of 256 px), twice as many per zoom finer.
        int Detail(int fileZoom) => (int)(4096 * Math.Pow(2, plan.FetchZoom(fileZoom) - fileZoom));
        Assert.Equal(zl16 * plan.SizeFor(16), Detail(16));
        Assert.Equal(zl18 * plan.SizeFor(18), Detail(18));
    }

    [Fact]
    public void Level_plus_one_downloads_zoom_17_and_19_and_builds_4096()
    {
        var plan = new TexturePlan(1);
        Assert.Equal((17, 4096), (plan.FetchZoom(16), plan.SizeFor(16)));
        Assert.Equal((19, 4096), (plan.FetchZoom(18), plan.SizeFor(18)));
        Assert.Equal("Extra_4096-4096", plan.FolderName);
    }

    [Theory]
    //          level  ZL18 zoom  ZL16 zoom
    [InlineData(-3, 15, 14)]
    [InlineData(-2, 16, 15)]
    [InlineData(-1, 17, 16)]
    [InlineData(0, 18, 17)]
    [InlineData(1, 19, 17)] // above level 0 ZL16 uses 16 + level
    public void ZL18_uses_18_plus_level_and_ZL16_one_level_finer_up_to_level_0(int level, int zl18, int zl16)
    {
        var plan = new TexturePlan(level);
        Assert.Equal(zl18, plan.FetchZoom(18)); // 18 + level
        Assert.Equal(zl16, plan.FetchZoom(16)); // 16 + (level + 1), 16 + level above 0
        Assert.Equal(17 + level, plan.FetchZoom(17)); // other zooms: zoom + level
    }

    [Fact]
    public void Sizes_by_file_name_and_the_log_line()
    {
        var plan = new TexturePlan(-2);
        Assert.Equal(1024, plan.SizeFor("21904_35824_BI16.dds"));
        Assert.Null(plan.SizeFor("water_transition.dds"));
        Assert.Equal("ZL16 → zoom 15, 1024 px; ZL18 → zoom 16, 1024 px", plan.Describe());
        Assert.Equal(2048, new TexturePlan(-2, TextureSizing.MaxSize).SizeFor(16)); // an explicit limit (tests) replaces the level's
    }

    [Theory]
    [InlineData(-2, "Balanced_1024-1024")]
    [InlineData(-1, "Sharp_2048-2048")]
    [InlineData(0, "Ultra_4096-4096")]
    [InlineData(1, "Extra_4096-4096")]
    [InlineData(-3, "level-3_512-512")]
    public void Each_level_has_its_own_cache_folder(int level, string folder) =>
        Assert.Equal(folder, new TexturePlan(level).FolderName);

    [Theory]
    [InlineData(-1, "87520_143440_BI18", "43760_71720_PX17")]
    [InlineData(0, "87520_143440_BI18", "87520_143440_PX18")]
    [InlineData(1, "87520_143440_BI18", "175040_286880_PX19")]
    [InlineData(-1, "21904_35824_BI16", "21904_35824_PX16")] // ZL16 downloads one zoom finer
    [InlineData(0, "21904_35824_BI16", "43808_71648_PX17")]
    [InlineData(-2, "21904_35824_ARC16", "10952_17912_PX15")] // the package's code is left out
    [InlineData(0, "43792_71648_BI17", "43792_71648_BI17")] // ZL17 is downloaded where ZL16 is: it keeps its name
    public void The_cache_names_a_texture_by_the_tiles_it_downloads(int level, string stem, string cacheStem)
    {
        Assert.True(TextureName.TryParseStem(stem, out TextureName name));
        Assert.Equal(cacheStem, new TexturePlan(level).CacheStem(name));
    }

    [Fact]
    public void A_cache_name_reads_back_as_its_texture_at_every_level()
    {
        TextureName[] names = [new(21904, 35824, "BI", 16), new(87520, 143440, "BI", 18), new(10944, 17904, "BI", 15), new(175040, 286880, "BI", 19)];
        for (int level = -4; level <= 2; level++)
        {
            var plan = new TexturePlan(level);
            foreach (TextureName name in names)
                Assert.Equal(name with { Code = TextureName.CacheCode }, plan.FromCacheStem(plan.CacheStem(name)));
            Assert.Equal(names.Length, names.Select(plan.CacheStem).Distinct().Count());
        }
        Assert.Null(new TexturePlan(0).FromCacheStem("21904_35824_BI16"));
        Assert.Null(new TexturePlan(0).FromCacheStem("43808_71649_PX17")); // not the corner of any texture
    }

    [Fact]
    public void A_cache_name_is_not_a_package_name() => Assert.False(TextureName.TryParseStem("87520_143440_PX18", out _));

    [Fact]
    public async Task Finer_detail_is_shrunk_to_the_size_when_built()
    {
        using var tree = new TempTree();
        using var tiles = new TileSource("synthetic", null, 8);
        string destination = tree.PathOf("21904_35824_BI16.dds");
        int size = new TexturePlan(-3).SizeFor(16); // 512 px from level -2's 1024 px of detail

        BuildResult result = await new TextureBuilder(tiles, 2).BuildAsync("21904_35824_BI16.dds", size, destination, levelChange: -2);

        Assert.Equal(16, result.Misses); // still downloads level -2's 16 tiles
        Assert.Equal((512, 512), Dds.ReadDimensions(destination));
    }
}
