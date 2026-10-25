using System.Buffers.Binary;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>
/// Seasonal colours: the season of a date and a place, the colour change, the recolouring of BC1
/// blocks as X-Plane reads them, and the date from X-Plane.
/// </summary>
public sealed class SeasonsTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private static int Day(int month, int day) => new DateTime(2025, month, day).DayOfYear - 1;

    // ---------------------------------------------------------------- the season of a date and a place

    [Fact]
    public void January_April_July_and_October_are_four_different_seasons_at_51_north()
    {
        SeasonState jan = SeasonModel.For(Day(1, 15), 51), apr = SeasonModel.For(Day(4, 15), 51),
            jul = SeasonModel.For(Day(7, 15), 51), oct = SeasonModel.For(Day(10, 15), 51);

        Assert.True(jan.Bare > 0.95 && jan.Dormant > 0.95, $"January: {jan}");
        Assert.True(apr.Dormant < 0.05 && apr.Bare > 0.5, $"April: grass green, trees still bare: {apr}");
        Assert.True(jul.IsSummer, $"July: the pictures as they are: {jul}");
        Assert.True(oct.Autumn > 0.9 && oct.Bare < 0.1, $"October: leaves turning: {oct}");
    }

    [Fact]
    public void The_southern_hemisphere_has_the_opposite_season()
    {
        Assert.Equal(SeasonModel.For(Day(7, 15), 45), SeasonModel.For(Day(1, 14), -45));
        Assert.True(SeasonModel.For(Day(1, 15), -45).IsSummer);
        Assert.True(SeasonModel.For(Day(7, 15), -45).Bare > 0.9);
    }

    [Fact]
    public void The_tropics_keep_their_colours_and_the_year_changes_gradually()
    {
        Assert.True(SeasonModel.For(Day(1, 15), 10).IsSummer);
        for (int day = 0; day < 364; day++)
        {
            SeasonState a = SeasonModel.For(day, 51), b = SeasonModel.For(day + 1, 51);
            double step = Math.Max(Math.Max(Math.Abs(a.Spring - b.Spring), Math.Abs(a.Autumn - b.Autumn)), Math.Max(Math.Abs(a.Bare - b.Bare), Math.Abs(a.Dormant - b.Dormant)));
            Assert.True(step < 0.08, $"day {day}: {a} -> {b}");
        }
    }

    // ---------------------------------------------------------------- the colours

    private static readonly SeasonState Winter = SeasonModel.For(SeasonChoice.Winter), Autumn = SeasonModel.For(SeasonChoice.Autumn);

    private static double Hue((double R, double G, double B) c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)), min = Math.Min(c.R, Math.Min(c.G, c.B)), d = max - min;
        if (d == 0) return 0;
        double h = max == c.R ? 60 * ((c.G - c.B) / d % 6) : max == c.G ? 60 * ((c.B - c.R) / d + 2) : 60 * ((c.R - c.G) / d + 4);
        return h < 0 ? h + 360 : h;
    }

    [Theory]
    [InlineData(150, 60, 50)]   // a red roof
    [InlineData(120, 120, 118)] // a grey road
    [InlineData(40, 70, 120)]   // water
    [InlineData(200, 190, 170)] // sand
    [InlineData(20, 20, 20)]    // shadow
    public void Roofs_roads_water_and_sand_keep_their_colour_in_every_season(int r, int g, int b)
    {
        foreach (SeasonState state in new[] { Winter, Autumn, SeasonModel.For(SeasonChoice.Spring) })
        {
            var c = Seasons.Recolor(r, g, b, state);
            Assert.Equal((r, g, b), ((int)Math.Round(c.R), (int)Math.Round(c.G), (int)Math.Round(c.B)));
        }
    }

    [Fact]
    public void The_darkest_coolest_greens_stay_green_while_other_tree_crowns_turn_and_go_bare()
    {
        (double R, double G, double B) conifers = (25, 45, 25), broadleaf = (45, 75, 40);

        Assert.InRange(Hue(Seasons.Recolor(conifers.R, conifers.G, conifers.B, Winter)), 85, 130);
        Assert.InRange(Hue(Seasons.Recolor(broadleaf.R, broadleaf.G, broadleaf.B, Autumn)), 15, 60);
        Assert.InRange(Hue(Seasons.Recolor(broadleaf.R, broadleaf.G, broadleaf.B, Winter)), 15, 60);
    }

    [Fact]
    public void Lighter_greens_grass_and_fields_rest_olive_and_straw_in_winter()
    {
        (double R, double G, double B) crop = (110, 150, 70); // a green field

        Assert.InRange(Hue(Seasons.Recolor(crop.R, crop.G, crop.B, Winter)), 40, 70);
    }

    // ---------------------------------------------------------------- recolouring BC1 blocks

    /// <summary>A 16 x 16 DXT1 texture of 4 x 4 blocks in one colour each: greens, a red roof, grey road.</summary>
    private static byte[] Texture()
    {
        (byte, byte, byte)[] colours = [(45, 75, 40), (110, 150, 70), (150, 60, 50), (120, 120, 118)];
        byte[] rgb = new byte[16 * 16 * 3];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                var (r, g, b) = colours[(y / 4 * 4 + x / 4) % colours.Length];
                (rgb[(y * 16 + x) * 3], rgb[(y * 16 + x) * 3 + 1], rgb[(y * 16 + x) * 3 + 2]) = (r, g, b);
            }
        return Dds.Encode(rgb, 16, 16, 1, out _, out _);
    }

    [Fact]
    public void A_block_is_recoloured_as_its_colour_is_and_stays_an_opaque_block()
    {
        byte[] dds = Texture();
        byte[] before = Dds.DecodeLevel(dds.AsSpan(Dds.HeaderSize), 16, 16);

        ColourTable.Apply(dds, 0, 8, Seasons.TableFor(Winter)!);

        byte[] after = Dds.DecodeLevel(dds.AsSpan(Dds.HeaderSize), 16, 16);
        for (int block = Dds.HeaderSize; block < dds.Length; block += 8)
            Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(dds.AsSpan(block)) > BinaryPrimitives.ReadUInt16LittleEndian(dds.AsSpan(block + 2)),
                "every block keeps the opaque four-colour mode");
        for (int p = 0; p < 256; p++)
        {
            (double R, double G, double B) original = (before[p * 3], before[p * 3 + 1], before[p * 3 + 2]);
            var expected = Seasons.Recolor(original.R, original.G, original.B, Winter);
            Assert.InRange(after[p * 3], expected.R - 12, expected.R + 12);
            Assert.InRange(after[p * 3 + 1], expected.G - 12, expected.G + 12);
            Assert.InRange(after[p * 3 + 2], expected.B - 12, expected.B + 12);
        }
        Assert.Equal(before.AsSpan(8 * 3, 12), after.AsSpan(8 * 3, 12)); // the red roof's block, unchanged
    }

    [Fact]
    public void A_block_cut_by_the_end_of_a_read_stays_as_it_is_so_split_reads_always_match()
    {
        byte[] whole = Texture(), split = (byte[])whole.Clone(), original = (byte[])whole.Clone();
        ushort[] table = Seasons.TableFor(Winter)!;

        ColourTable.Apply(whole, 0, 8, table);
        // Two reads that cut the second block in half: it stays as made, both halves.
        int cut = Dds.HeaderSize + 8 + 3;
        ColourTable.Apply(split.AsSpan(0, cut), 0, 8, table);
        ColourTable.Apply(split.AsSpan(cut), cut, 8, table);

        Assert.Equal(original.AsSpan(Dds.HeaderSize + 8, 8), split.AsSpan(Dds.HeaderSize + 8, 8));
        Assert.Equal(whole.AsSpan(Dds.HeaderSize, 8), split.AsSpan(Dds.HeaderSize, 8));
        Assert.Equal(whole.AsSpan(Dds.HeaderSize + 16), split.AsSpan(Dds.HeaderSize + 16));
        Assert.Equal(original.AsSpan(0, Dds.HeaderSize), whole.AsSpan(0, Dds.HeaderSize)); // the header never changes
    }

    // ---------------------------------------------------------------- the date

    [Fact]
    public void X_Planes_date_is_taken_when_a_flight_starts_and_kept_for_the_next_run()
    {
        string file = _tree.PathOf("sim-date.txt");
        var clock = new SeasonClock(new SeasonOptions(true, 0.8, SeasonChoice.Auto), file);

        clock.OfferDate(Day(10, 27), newFlight: false); // the first date: taken
        Assert.Equal(Day(10, 27), clock.Day);
        clock.OfferDate(Day(1, 10), newFlight: false);  // changed in flight: kept for the next flight
        Assert.Equal(Day(10, 27), clock.Day);
        clock.OfferDate(Day(1, 10), newFlight: true);
        Assert.Equal(Day(1, 10), clock.Day);

        Assert.Equal(Day(1, 10), new SeasonClock(new SeasonOptions(true, 0.8, SeasonChoice.Auto), file).Day);
    }

    [Fact]
    public void A_texture_takes_the_table_of_its_season_and_none_in_summer()
    {
        Assert.True(TextureName.TryParseFile("21840_35584_BI16.dds", out TextureName name));

        Assert.Same(Seasons.TableFor(Winter), new SeasonClock(new SeasonOptions(true, 1, SeasonChoice.Winter), null).TableFor(name));
        Assert.Null(new SeasonClock(new SeasonOptions(true, 1, SeasonChoice.Summer), null).TableFor(name));
        Assert.Null(new SeasonClock(new SeasonOptions(true, 0, SeasonChoice.Winter), null).TableFor(name));
    }

    [Fact]
    public void The_settings_switch_set_and_strengthen_the_seasons()
    {
        SettingValues args = TestSettings.Of("seasons = on", "season = late-autumn", "season-strength = 60");
        Assert.Equal(new SeasonOptions(true, 0.6, SeasonChoice.LateAutumn), MountSettings.ParseSeasons(args));
        Assert.Equal(SeasonOptions.Off, MountSettings.ParseSeasons(TestSettings.Of()));
        Assert.Throws<SettingsException>(() => MountSettings.ParseSeasons(TestSettings.Of("season = monsoon")));
    }

    [Fact]
    public void An_older_settings_file_with_the_land_cover_setting_still_loads()
    {
        SettingValues args = TestSettings.Of("seasons = on", "season-land-cover = on");
        Assert.Equal(new SeasonOptions(true, SeasonOptions.DefaultStrength / 100, SeasonChoice.Auto), MountSettings.ParseSeasons(args));
    }
}
