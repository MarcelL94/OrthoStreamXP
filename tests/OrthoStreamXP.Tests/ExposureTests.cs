using OrthoStreamXP.Gui;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>The exposure: a lookup table that darkens the photos in linear light, its setting, and the preview of the colour settings.</summary>
public sealed class ExposureTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static (double R, double G, double B) Through(ushort[] table, double r, double g, double b) =>
        ColourTable.Decode565(table[ColourTable.Encode565(r, g, b)]);

    [Fact]
    public void Full_exposure_changes_nothing_and_less_is_clamped_to_one_stop()
    {
        Assert.Null(Exposure.TableFor(100));
        Assert.Null(Exposure.TableFor(120));
        Assert.Same(Exposure.TableFor(Exposure.Min), Exposure.TableFor(10));
    }

    [Fact]
    public void Half_the_light_is_one_stop_darker_in_linear_light_and_black_stays_black()
    {
        ushort[] half = Exposure.TableFor(50)!;

        var white = Through(half, 255, 255, 255);
        double expected = Exposure.ToSrgb(0.5) * 255; // about 188, not 128: the shadows are not crushed
        Assert.InRange(white.R, expected - 9, expected + 9);
        Assert.InRange(white.G, expected - 5, expected + 5);
        Assert.Equal((0.0, 0.0, 0.0), Through(half, 0, 0, 0));
    }

    [Fact]
    public void Less_exposure_is_darker_for_every_colour_and_keeps_its_order()
    {
        ushort[] darker = Exposure.TableFor(80)!, darkest = Exposure.TableFor(50)!;
        foreach (var (r, g, b) in new[] { (60.0, 100.0, 50.0), (150.0, 60.0, 50.0), (200.0, 190.0, 170.0), (120.0, 120.0, 118.0) })
        {
            var original = ColourTable.Decode565(ColourTable.Encode565(r, g, b));
            var a = Through(darker, r, g, b);
            var c = Through(darkest, r, g, b);
            Assert.True(a.R <= original.R && a.G <= original.G && a.B <= original.B, $"80 % of {(r, g, b)}: {a}");
            Assert.True(c.R <= a.R && c.G <= a.G && c.B <= a.B, $"50 % of {(r, g, b)}: {c} brighter than 80 %: {a}");
        }
    }

    [Fact]
    public void The_setting_is_100_unless_lowered_and_never_below_50()
    {
        MountSettings Parse(params string[] extra)
        {
            Directory.CreateDirectory(_tree.PathOf("source"));
            return MountSettings.FromValues(TestSettings.Of(
                [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", "texture-provider = bing", .. extra]));
        }
        Assert.Equal(100, Parse().Exposure);
        Assert.Equal(85, Parse("exposure = 85").Exposure);
        Assert.Throws<SettingsException>(() => Parse("exposure = 40"));
        Assert.Throws<SettingsException>(() => Parse("exposure = 101"));
    }

    // ---------------------------------------------------------------- the preview

    /// <summary>A cache with one finished ZL16 photo texture (256 px, a summer green) and one streaming level that is not offered.</summary>
    private string Cache()
    {
        byte[] rgb = new byte[256 * 256 * 3];
        for (int i = 0; i < rgb.Length; i += 3) (rgb[i], rgb[i + 1], rgb[i + 2]) = (60, 100, 50);
        _tree.WriteBytes("cache/level0_256-256/Pack/textures/21840_35584_BI16.dds", Dds.Encode(rgb, 256, 256, 1, out _, out _));
        _tree.WriteBytes("cache/level0_256-256/Pack/.xpstream/mips/21840_35600_BI16.dds", [1, 2, 3]);
        Directory.CreateDirectory(_tree.PathOf("history"));
        return _tree.PathOf("cache");
    }

    private static double Brightness(byte[] rgb) => rgb.Average(v => (double)v);

    [Fact]
    public void The_preview_offers_the_finished_photos_and_shows_them_unchanged_without_settings()
    {
        using var preview = new ColourPreview(Cache(), _tree.PathOf("history"));

        PreviewPhoto photo = Assert.Single(preview.FindPhotos(TestContext.Current.CancellationToken));
        PreviewPicture picture = preview.Render(photo, new ColourAdjustments(false, 1, SeasonOptions.Off, 100))!;

        Assert.Equal(256, picture.Size);
        Assert.Equal(picture.Before, picture.After);
        Assert.Contains("No colour change", Assert.Single(picture.Notes), StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_shows_the_season_and_the_exposure_as_X_Plane_gets_them()
    {
        using var preview = new ColourPreview(Cache(), _tree.PathOf("history"));
        PreviewPhoto photo = preview.FindPhotos(TestContext.Current.CancellationToken)[0];

        PreviewPicture darker = preview.Render(photo, new ColourAdjustments(false, 1, SeasonOptions.Off, 50))!;
        PreviewPicture winter = preview.Render(photo, new ColourAdjustments(false, 1, new SeasonOptions(true, 1, SeasonChoice.Winter), 100))!;
        PreviewPicture matched = preview.Render(photo, new ColourAdjustments(true, 1, SeasonOptions.Off, 100))!;

        Assert.True(Brightness(darker.After) < Brightness(darker.Before) - 20);
        Assert.NotEqual(winter.Before, winter.After);
        Assert.True(winter.After[0] > winter.After[1], "winter: the green turns brown"); // red above green
        Assert.Equal(matched.Before, matched.After); // no low-zoom base in this cache: nothing to match against
        Assert.Contains(matched.Notes, n => n.Contains("low-zoom base", StringComparison.Ordinal));
    }
}
