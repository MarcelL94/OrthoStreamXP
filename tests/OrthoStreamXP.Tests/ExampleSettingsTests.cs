using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>The shipped examples\orthostreamxp*.ini files must load and stay within the mount's limits.</summary>
public sealed class ExampleSettingsTests
{
    private static string ExampleFile(string name) => Path.Join(AppContext.BaseDirectory, "examples", name);

    [Theory]
    [InlineData("orthostreamxp-2.ini", -2)]
    [InlineData("orthostreamxp-1.ini", -1)]
    [InlineData("orthostreamxp-0.ini", 0)]
    [InlineData("orthostreamxp+1.ini", 1)]
    public void Example_loads_with_its_level_and_valid_values(string name, int level)
    {
        SettingValues settings = SettingsOptions.Schema.ReadFile(ExampleFile(name));

        Assert.Equal(level, settings.Int("level-change", 99));
        Assert.Equal(@"D:\OrthoStreamXP\Cache", settings.Required("cache")); // one cache for every level
        Assert.InRange(settings.Int("download-workers", 0), 1, 256);
        Assert.InRange(settings.Int("encoder-workers", 0), 1, 16);
        Assert.InRange(settings.Int("filesystem-threads", 0), 2, 512);
        Assert.InRange(settings.Int("dds-build-workers", 0), 1, 64);
        Assert.InRange(settings.Int("provisional-lod", -1), 0, 3);
        Assert.True(settings.Flag("track-aircraft"), "provisional images and prefetch need background mode");

        // A provisional image for the common ZL16 textures, when used, should cost 16-64 tiles: quick, yet not a blur.
        int lod = settings.Int("provisional-lod", 0);
        int zl16Level = new TexturePlan(level, TextureSizing.MaxSize).LevelFor(16);
        if (lod > 0) Assert.InRange(Math.Max(-4, zl16Level - lod), -2, -1);
    }
}
