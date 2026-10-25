using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>Photos from different captures are evened out against the low-zoom base, place by place, brightness and tint only.</summary>
public sealed class ColourMatchTests : IDisposable
{
    private readonly TempTree _tree = new();
    private static readonly TextureName Wroclaw = new(21840, 35872, "BI", 16);
    private const int Size = 256;
    /// <summary>The base everywhere, and a photo with the usual relation to it (ColourMatch.DefaultTarget).</summary>
    private static readonly (int R, int G, int B) BaseColour = (90, 110, 60), UsualPhoto = (65, 89, 47), GreyPhoto = (110, 110, 110);

    public void Dispose() => _tree.Dispose();

    private static byte[] Picture(Func<int, int, (int R, int G, int B)> colour, int size = Size)
    {
        byte[] rgb = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var (r, g, b) = colour(x, y);
                (rgb[(y * size + x) * 3], rgb[(y * size + x) * 3 + 1], rgb[(y * size + x) * 3 + 2]) = ((byte)r, (byte)g, (byte)b);
            }
        return rgb;
    }

    private static byte[] Resize(byte[] rgb, int size, int to) => size == to ? rgb : Images.Resize(rgb, size, size, to, to, 1);

    private ColourMatcher Matcher(string? file = null) =>
        new(_ => (Picture((_, _) => BaseColour), Size), Resize, file is null ? null : _tree.PathOf(file));

    /// <summary>A whole DDS of the picture, with the correction applied as X-Plane reads it, and its level 0 decoded again.</summary>
    private static byte[] Served(byte[] rgb, TextureColour? colour)
    {
        var layout = DdsLayout.For(Size);
        byte[] file = [.. layout.Header, .. Dds.EncodeChain(rgb, layout, 0, 1)];
        colour?.Apply(file, 0, 8, Size);
        return Dds.DecodeLevel(file.AsSpan(Dds.HeaderSize, layout.LengthOf(0)), Size, Size);
    }

    private static (double R, double G, double B) Mean(byte[] rgb, int x0, int x1)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = 0; y < Size; y++)
            for (int x = x0; x < x1; x++, n++)
                (r, g, b) = (r + rgb[(y * Size + x) * 3], g + rgb[(y * Size + x) * 3 + 1], b + rgb[(y * Size + x) * 3 + 2]);
        return (r / n, g / n, b / n);
    }

    [Theory]
    [InlineData(16, 4)]
    [InlineData(18, 1)]
    [InlineData(15, 8)]
    [InlineData(19, 1)]
    public void A_cell_is_a_zoom_14_tile(int zoom, int cells) => Assert.Equal(cells, ColourMatch.CellsAcross(new TextureName(0, 0, "BI", zoom)));

    [Fact]
    public void A_grey_capture_moves_towards_the_usual_colours_and_the_usual_one_stays()
    {
        // The left half of the texture is a usual capture, the right half a grey one (cells are a quarter wide).
        byte[] photo = Picture((x, _) => x < Size / 2 ? UsualPhoto : GreyPhoto);
        TextureColour colour = Matcher().ForPhoto(Wroclaw, 1, size => (Resize(photo, Size, size), size))!;

        byte[] before = Served(photo, null), after = Served(photo, colour);
        var usual = Mean(after, 0, Size / 4);
        var usualBefore = Mean(before, 0, Size / 4);
        Assert.InRange(usual.R - usualBefore.R, -4, 4);
        Assert.InRange(usual.G - usualBefore.G, -4, 4);
        Assert.InRange(usual.B - usualBefore.B, -4, 4);

        // Moved by what is beyond a usual difference, and the change is capped: much nearer its neighbour, not all the way.
        var grey = Mean(after, Size * 3 / 4, Size);
        Assert.InRange(grey.R, UsualPhoto.R, 80);
        Assert.InRange(grey.G, UsualPhoto.G, 97);
        Assert.InRange(grey.B, UsualPhoto.B, 75);
    }

    [Fact]
    public void A_usual_capture_is_left_alone_though_its_fields_differ_from_the_base()
    {
        // Cell by cell up to 10 % lighter or darker than the usual relation: crops, not another capture.
        byte[] photo = Picture((x, y) => ((x / 64 + y / 64) % 3) switch
        {
            0 => UsualPhoto,
            1 => ((int)(UsualPhoto.R * 1.1), (int)(UsualPhoto.G * 1.1), (int)(UsualPhoto.B * 1.1)),
            _ => ((int)(UsualPhoto.R * 0.9), (int)(UsualPhoto.G * 0.9), (int)(UsualPhoto.B * 0.9)),
        });
        Assert.Null(Matcher().ForPhoto(Wroclaw, 1, size => (Resize(photo, Size, size), size)));
    }

    [Fact]
    public void One_odd_cell_is_not_corrected_but_a_capture_covering_several_is()
    {
        ColourGain usual = ColourMatch.DefaultTarget, grey = new(0.2f, 0f, 0.5f);
        ColourGain?[] relation = [.. Enumerable.Repeat<ColourGain?>(usual, 16)];
        relation[5] = grey; // one cell: a field, a cloud's shadow
        Assert.Null(TextureColour.Matching(relation, 4, usual));

        for (int i = 0; i < 16; i += 4) (relation[i + 2], relation[i + 3]) = (grey, grey); // the east half: another capture
        TextureColour colour = TextureColour.Matching(relation, 4, usual)!;
        Assert.Equal(ColourGain.None, colour.Gains[0]);
        Assert.True(colour.Gains[3].B < -0.3f, $"the grey capture loses its blue cast: {colour.Gains[3]}");
    }

    [Fact]
    public void A_correction_is_kept_for_its_picture_and_measured_again_for_a_new_one()
    {
        ColourMatcher matcher = Matcher();
        int samples = 0;
        (byte[], int)? Sample(int size)
        {
            samples++;
            return (Picture((_, _) => GreyPhoto, size), size);
        }

        TextureColour? first = matcher.ForPhoto(Wroclaw, 1, Sample);
        Assert.Same(first, matcher.ForPhoto(Wroclaw, 1, Sample));
        Assert.Equal(1, samples);
        matcher.ForPhoto(Wroclaw, 2, Sample); // a finer level arrived: perhaps another capture
        Assert.Equal(2, samples);
    }

    [Fact]
    public void Without_its_base_tile_a_photo_is_left_as_it_is_until_the_tile_is_here()
    {
        bool here = false;
        var matcher = new ColourMatcher(_ => here ? (Picture((_, _) => BaseColour), Size) : null, Resize, null);
        (byte[], int)? Sample(int size) => (Picture((_, _) => GreyPhoto, size), size);

        Assert.Null(matcher.ForPhoto(Wroclaw, 1, Sample));
        here = true;
        Assert.NotNull(matcher.ForPhoto(Wroclaw, 1, Sample));
    }

    [Fact]
    public void A_stand_in_from_the_base_gets_the_usual_relation_so_the_photo_does_not_jump_when_it_arrives()
    {
        TextureColour colour = Matcher().ForBase(Wroclaw)!;
        Assert.Equal(ColourMatch.DefaultTarget, Assert.Single(colour.Gains));
    }

    [Fact]
    public void An_area_learns_its_usual_colours_from_its_textures_and_keeps_them()
    {
        ColourMatcher matcher = Matcher("logs/colour-match.json");
        AreaName area = AreaName.Of(Wroclaw);
        // Photos here are a little brighter than the default relation says.
        byte[] bright = Picture((_, _) => (80, 100, 55));
        for (int i = 0; i < ColourMatcher.LearntFrom; i++) // northwards, within the same 1° area
            matcher.ForPhoto(Wroclaw with { Row = Wroclaw.Row - 16 * i }, 1, size => (Resize(bright, Size, size), size));
        Assert.Equal(ColourMatch.DefaultTarget, matcher.TargetOf(area)); // fixed for the session at its first use
        matcher.Save();

        ColourGain learnt = Matcher("logs/colour-match.json").TargetOf(area);
        Assert.InRange(learnt.R, -0.118, -0.098); // log(88 / 98): each channel + 8, as measured
        Assert.InRange(learnt.G, -0.099, -0.079); // log(108 / 118)
        Assert.InRange(learnt.B, -0.087, -0.067); // log(63 / 68)
    }

    [Fact]
    public void Partial_blocks_and_three_colour_blocks_are_left_as_they_are()
    {
        TextureColour colour = TextureColour.Uniform(new ColourGain(-0.4f, -0.4f, -0.4f))!;
        var layout = DdsLayout.For(64);
        byte[] file = [.. layout.Header, .. Dds.EncodeChain(Picture((_, _) => (200, 180, 160), 64), layout, 0, 1)];
        // A three-colour block (first colour not the larger) in the second block.
        (file[Dds.HeaderSize + 8], file[Dds.HeaderSize + 9], file[Dds.HeaderSize + 10], file[Dds.HeaderSize + 11]) = (0x10, 0, 0x20, 0);
        byte[] original = [.. file];

        colour.Apply(file.AsSpan(Dds.HeaderSize + 4, 28), Dds.HeaderSize + 4, 8, 64); // cuts the first block
        Assert.Equal(original.AsSpan(Dds.HeaderSize, 16).ToArray(), file.AsSpan(Dds.HeaderSize, 16).ToArray());
        Assert.NotEqual(original.AsSpan(Dds.HeaderSize + 16, 8).ToArray(), file.AsSpan(Dds.HeaderSize + 16, 8).ToArray());
    }

    [Fact]
    public void The_strength_makes_part_of_each_correction()
    {
        (byte[], int)? Grey(int size) => (Picture((_, _) => GreyPhoto, size), size);
        ColourGain full = Matcher().ForPhoto(Wroclaw, 1, Grey)!.Gains[0];
        ColourGain half = new ColourMatcher(_ => (Picture((_, _) => BaseColour), Size), Resize, null, strength: 0.5).ForPhoto(Wroclaw, 1, Grey)!.Gains[0];

        Assert.Equal(full.R / 2, half.R, 0.001);
        Assert.Equal(full.B / 2, half.B, 0.001);
        Assert.Null(new ColourMatcher(_ => (Picture((_, _) => BaseColour), Size), Resize, null, strength: 0).ForPhoto(Wroclaw, 1, Grey));
        Assert.Null(new ColourMatcher(_ => (Picture((_, _) => BaseColour), Size), Resize, null, strength: 0).ForBase(Wroclaw));
    }

    [Fact]
    public void Colour_matching_is_a_setting_off_by_default()
    {
        MountSettings Parse(params string[] extra)
        {
            Directory.CreateDirectory(_tree.PathOf("source"));
            return MountSettings.FromValues(TestSettings.Of(
                [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", "texture-provider = bing", .. extra]));
        }
        Assert.False(Parse().MatchColours);
        Assert.True(Parse("match-colours = on").MatchColours);
        Assert.Throws<SettingsException>(() => Parse("match-colours = maybe"));
        Assert.Equal(1.0, Parse().MatchStrength);
        Assert.Equal(0.6, Parse("match-strength = 60").MatchStrength, 0.0001);
        Assert.Throws<SettingsException>(() => Parse("match-strength = 120"));
    }
}
