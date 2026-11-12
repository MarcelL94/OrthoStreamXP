using System.Buffers.Binary;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>The night lights: X-Plane's roads read from overlay DSFs, painted into night textures and served to X-Plane.</summary>
public sealed class NightLightsTests : IDisposable
{
    private const int South = 43, West = 7;
    private readonly TempTree _tree = new();
    private readonly List<NightPainting> _paintings = [];

    public void Dispose()
    {
        foreach (NightPainting painting in _paintings) painting.Dispose();
        _tree.Dispose();
    }

    /// <summary>A ZL16 texture over Nice.</summary>
    private static readonly TextureName Nice = TextureName.At(43.70, 7.25, "BI", 16);

    private static (double North, double West, double South, double East) Bounds(TextureName name)
    {
        var (north, west) = Tiles.TileToLatLon(name.Col, name.Row, name.Zoom);
        var (south, east) = Tiles.TileToLatLon(name.Col + Tiles.TextureSpan, name.Row + Tiles.TextureSpan, name.Zoom);
        return (north, west, south, east);
    }

    /// <summary>An overlay package whose DSF has a grid of town streets every 0.001° over the west half of the Nice texture.</summary>
    private string OverlayWithTown()
    {
        var (north, west, south, east) = Bounds(Nice);
        double middle = (west + east) / 2;
        var dsf = new DsfBuilder(South, West);
        for (double lon = west + 0.0005; lon < middle; lon += 0.001) dsf.Road(50, (lon, south), (lon, north));
        for (double lat = south + 0.0005; lat < north; lat += 0.001) dsf.Road(50, (west, lat), (middle - 0.002, lat));
        _tree.WriteBytes("overlays/" + RoadNetworks.TileFile(South, West).Replace('\\', '/'), dsf.Build());
        return _tree.PathOf("overlays");
    }

    // --- Reading X-Plane's roads ------------------------------------------------------------------------------

    [Fact]
    public void A_DSF_s_road_chains_are_read_in_every_command_form_and_other_networks_are_left_out()
    {
        byte[] dsf = new DsfBuilder(South, West)
            .Road(50, (7.10, 43.20), (7.11, 43.21), (7.12, 43.20)) // local street, as a range
            .Road(59, DsfBuilder.ChainForm.List16, (7.30, 43.30), (7.31, 43.30)) // rural local road
            .Road(100, DsfBuilder.ChainForm.List32, (7.50, 43.50), (7.60, 43.60)) // motorway
            .Road(151, (7.70, 43.70), (7.80, 43.80)) // railway: not a road
            .Road(20, (7.90, 43.90)) // a single point lights nothing
            .Build();

        RoadNetwork roads = DsfNetworkReader.Read(dsf, South, West);

        Assert.Equal(3, roads.Count);
        Assert.Equal(new[] { RoadClass.Local, RoadClass.Local, RoadClass.Motorway }, new[] { roads.ClassOf(0), roads.ClassOf(1), roads.ClassOf(2) });
        Assert.Equal(new[] { false, true, false }, new[] { roads.IsRural(0), roads.IsRural(1), roads.IsRural(2) });
        var (lon, lat) = roads.Points(0);
        Assert.Equal(3, lon.Length);
        Assert.Equal(0.11, lon.Span[1], 5); // degrees from the tile's corner
        Assert.Equal(0.21, lat.Span[1], 5);
        Assert.Equal(0.60, roads.Points(2).Lon.Span[1], 5);
    }

    [Fact]
    public void A_compressed_or_damaged_DSF_is_refused_in_plain_words()
    {
        byte[] compressed = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4, .. new byte[40]];
        Assert.True(DsfNetworkReader.IsCompressed(compressed));
        Assert.Contains("compressed", Assert.Throws<InvalidDataException>(() => DsfNetworkReader.Read(compressed, South, West)).Message, StringComparison.Ordinal);

        byte[] dsf = new DsfBuilder(South, West).Road(50, (7.1, 43.1), (7.2, 43.2)).Build();
        Assert.Throws<InvalidDataException>(() => DsfNetworkReader.Read(dsf.AsSpan(0, dsf.Length - 40), South, West));
        Assert.Throws<InvalidDataException>(() => DsfNetworkReader.Read("XPLNEDSF"u8, South, West));
    }

    [Theory]
    [InlineData(10, (int)RoadClass.Primary, false)]
    [InlineData(29, (int)RoadClass.Primary, true)]
    [InlineData(34, (int)RoadClass.Secondary, false)]
    [InlineData(50, (int)RoadClass.Local, false)]
    [InlineData(69, (int)RoadClass.Local, true)]
    [InlineData(100, (int)RoadClass.Motorway, false)]
    [InlineData(110, (int)RoadClass.Motorway, false)]
    [InlineData(120, (int)RoadClass.Ramp, false)]
    [InlineData(151, (int)RoadClass.None, false)] // railway
    [InlineData(220, (int)RoadClass.None, false)] // power line
    public void Road_subtypes_are_sorted_into_classes(int subtype, int expected, bool rural)
    {
        Assert.Equal((RoadClass)expected, RoadClasses.Of(subtype, out bool isRural));
        Assert.Equal(rural, isRural);
    }

    [Fact]
    public void A_road_across_many_cells_is_found_once_and_a_far_one_not_at_all()
    {
        byte[] dsf = new DsfBuilder(South, West)
            .Road(30, (7.01, 43.5), (7.99, 43.5)) // across the whole tile
            .Road(50, (7.9, 43.9), (7.91, 43.91))
            .Build();
        RoadNetwork roads = DsfNetworkReader.Read(dsf, South, West);
        var found = new List<int>();
        roads.Near(43.4, 7.2, 43.6, 7.8, found);
        Assert.Equal([0], found);
        found.Clear();
        roads.Near(44.5, 7.2, 44.6, 7.8, found); // another tile
        Assert.Empty(found);
    }

    [Fact]
    public void A_place_is_as_built_up_as_the_streets_around_it()
    {
        RoadNetwork roads = new RoadNetworks([OverlayWithTown()]).For(South, West)!;
        var (north, west, south, _) = Bounds(Nice);
        Assert.True(roads.BuiltUp((north + south) / 2, west + 0.02) > 0.8);
        Assert.Equal(0, roads.BuiltUp(43.05, 7.95));
    }

    [Fact]
    public void Tile_files_are_named_as_X_Plane_names_them()
    {
        Assert.Equal(Path.Join("Earth nav data", "+40+000", "+43+007.dsf"), RoadNetworks.TileFile(43, 7));
        Assert.Equal(Path.Join("Earth nav data", "-40-060", "-34-059.dsf"), RoadNetworks.TileFile(-34, -59));
        Assert.Equal(Path.Join("Earth nav data", "+00-010", "+00-001.dsf"), RoadNetworks.TileFile(0, -1));
    }

    [Fact]
    public void Roads_come_from_overlay_packages_never_through_the_mount()
    {
        Directory.CreateDirectory(_tree.PathOf("Custom Scenery/yOrtho4XP_Overlays"));
        Directory.CreateDirectory(_tree.PathOf("Custom Scenery/OrthoStream_Overlays")); // our own mount point
        Directory.CreateDirectory(_tree.PathOf("Custom Scenery/KSEA Airport"));
        PackagePlan[] mounted =
        [
            new("OrthoStream_Eur", _tree.PathOf("packages/OrthoStream_Eur"), "", _tree.PathOf("Custom Scenery/OrthoStream_Eur")),
            new("OrthoStream_Overlays", _tree.PathOf("packages/OrthoStream_Overlays"), "", _tree.PathOf("Custom Scenery/OrthoStream_Overlays")),
        ];
        Assert.Equal(
            [_tree.PathOf("packages/OrthoStream_Overlays"), _tree.PathOf("Custom Scenery/yOrtho4XP_Overlays")],
            RoadNetworks.FindOverlayPackages(mounted, _tree.PathOf("Custom Scenery")));
    }

    // --- Painting -----------------------------------------------------------------------------------------------

    [Fact]
    public void The_town_is_lit_the_country_stays_black_and_the_same_texture_is_painted_the_same_way()
    {
        var painter = new NightPainter(new RoadNetworks([OverlayWithTown()]));
        const int Side = 1024;
        var workspaces = new NightWorkspaces();
        NightWorkspace workspace = workspaces.Rent(Side);
        Assert.True(painter.Paint(Nice, workspace));
        byte[] rgb = workspace.Rgb.ToArray();

        double town = 0;
        for (int y = 0; y < Side; y++)
            for (int x = 0; x < Side / 2 - 40; x++)
                town += rgb[(y * Side + x) * 3];
        Assert.True(town / (Side * (Side / 2 - 40)) > 5, $"the town's mean red is {town / (Side * (Side / 2 - 40)):F1}");
        for (int y = 0; y < Side; y++)
            for (int x = Side * 3 / 4; x < Side; x++)
                Assert.Equal(0, rgb[(y * Side + x) * 3]);
        // Every light is orange or white: never greener or bluer than red.
        for (int i = 0; i < rgb.Length; i += 3)
            Assert.True(rgb[i] >= rgb[i + 1] && rgb[i + 1] >= rgb[i + 2], $"pixel {i / 3} is {rgb[i]},{rgb[i + 1]},{rgb[i + 2]}");

        // The same texture painted again, in the same workspace after another one: the same picture.
        workspaces.Return(workspace);
        workspace = workspaces.Rent(Side);
        painter.Paint(TextureName.At(43.65, 7.15, "BI", 16), workspace);
        workspaces.Return(workspace);
        NightWorkspace again = workspaces.Rent(Side);
        Assert.Same(workspace, again);
        Assert.True(painter.Paint(Nice, again));
        Assert.Equal(rgb, again.Rgb);
    }

    [Fact]
    public void A_place_without_roads_has_no_lights()
    {
        Assert.False(new NightPainter(new RoadNetworks([])).Paint(Nice, new NightWorkspace(1024)));
        Assert.False(new NightPainter(new RoadNetworks([OverlayWithTown()])).Paint(TextureName.At(43.05, 7.95, "BI", 16), new NightWorkspace(1024)));
    }

    [Theory]
    [InlineData(16, 256, 1024)] // far: painted at the reference and made smaller
    [InlineData(16, 1024, 1024)]
    [InlineData(16, 2048, 2048)]
    [InlineData(16, 4096, 2048)] // beside the aircraft: painted twice as fine, then enlarged
    [InlineData(18, 4096, 512)] // a ZL18 tile covers a quarter of the ground: the same metres a pixel
    [InlineData(18, 64, 256)]
    [InlineData(22, 64, 64)]
    public void Night_textures_are_painted_at_the_same_metres_per_pixel_at_every_zoom(int zoom, int levelSide, int painted)
    {
        Assert.Equal(painted, NightTextures.PaintSide(zoom, levelSide));
    }

    [Theory]
    [InlineData(6.5)] // a far texture: the 2 x 2 dot it was
    [InlineData(3.2)] // beside the aircraft: twice as fine
    public void A_lamp_is_a_round_soft_spot_with_as_much_light_as_its_square_in_metres(double metresPerPixel)
    {
        const int Side = 64;
        float[] picture = new float[Side * Side];
        double sigma = NightPainter.LampSigma(metresPerPixel);
        NightRaster.Spot(picture, Side, 32.5, 32.5, sigma, 0.6f); // a pixel's centre

        Assert.Equal(0.6f, picture[32 * Side + 32], 3);
        double square = Math.Pow(13 / metresPerPixel, 2) * 0.6;
        Assert.InRange(picture.Sum(), square * 0.97, square * 1.03);
        // Round: as bright at the same distance whichever the direction.
        Assert.Equal(picture[32 * Side + 34], picture[34 * Side + 32], 5);
        Assert.Equal(picture[30 * Side + 32], picture[32 * Side + 30], 5);
        double diagonal = Math.Exp(-0.5 * 8 / (sigma * sigma)) * 0.6; // at (2, 2): 2.83 pixels out
        Assert.Equal(diagonal, picture[34 * Side + 34], 4);
    }

    [Fact]
    public void A_lamp_between_pixels_lights_them_alike()
    {
        float[] picture = new float[16 * 16];
        NightRaster.Spot(picture, 16, 8, 8, 0.8, 1f); // on the corner of four pixels
        Assert.Equal(picture[7 * 16 + 7], picture[8 * 16 + 8], 5);
        Assert.Equal(picture[7 * 16 + 8], picture[8 * 16 + 7], 5);
        Assert.Equal(picture[7 * 16 + 7], picture[7 * 16 + 8], 5);
    }

    // --- The DDS file -----------------------------------------------------------------------------------------------

    private static byte[] Level(byte[] dds, int side, int mip)
    {
        var layout = DdsLayout.For(side);
        int size = layout.SizeOf(mip);
        return Dds.DecodeLevel(dds.AsSpan((int)layout.Offset(mip), layout.LengthOf(mip)), size, size);
    }

    /// <summary>A workspace whose top level is <paramref name="rgb"/>, as if painted.</summary>
    private static NightWorkspace Painted(byte[] rgb, int side)
    {
        var workspace = new NightWorkspace(side);
        rgb.CopyTo(workspace.Rgb, 0);
        return workspace;
    }

    [Theory]
    [InlineData(64)] // as painted
    [InlineData(256)] // enlarged four times: a texture beside the aircraft finer than it was painted
    [InlineData(16)] // made smaller
    public void An_even_light_keeps_its_colour_at_every_level_and_never_turns_green(int nominal)
    {
        const int Side = 64;
        byte[] rgb = new byte[Side * Side * 3];
        for (int i = 0; i < rgb.Length; i += 3) (rgb[i], rgb[i + 1], rgb[i + 2]) = (200, 140, 72);
        byte[] dds = NightDds.Encode(Painted(rgb, Side), nominal);

        var layout = DdsLayout.For(nominal);
        Assert.Equal(layout.Length, dds.Length);
        Assert.True(dds.AsSpan(0, Dds.HeaderSize).SequenceEqual(layout.Header));
        for (int mip = 0; mip < layout.Levels; mip++)
        {
            byte[] level = Level(dds, nominal, mip);
            for (int i = 0; i < level.Length; i += 3)
            {
                Assert.InRange(level[i], 192, 208);
                Assert.InRange(level[i + 1], 132, 148);
                Assert.True(level[i + 1] <= level[i] && level[i + 2] <= level[i]);
            }
        }
    }

    [Fact]
    public void Smaller_levels_average_the_light_not_the_stored_values()
    {
        // A quarter of a 4 x 4 texture lit full white: its 1 x 1 level holds a quarter of the light, which is 136 stored
        // (gamma 2.2), not the 64 a plain average of the stored values gives: a town looks as bright from far as from near.
        byte[] rgb = new byte[4 * 4 * 3];
        foreach (int pixel in new[] { 0, 1, 4, 5 }) rgb.AsSpan(pixel * 3, 3).Fill(255);
        byte[] last = Level(NightDds.Encode(Painted(rgb, 4), 4), 4, 2);
        Assert.InRange(last[0], 128, 144);
    }

    [Fact]
    public void A_block_turned_green_by_BC1_is_warmed_and_its_texels_keep_their_colours()
    {
        // Two endpoints of the same red, the first greener: warmed, the second ends up above the first, so they swap
        // and every texel's index swaps with them.
        const ushort First = (2 << 11) | (40 << 5), Second = (2 << 11) | (4 << 5) | 31;
        byte[] block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, First);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), Second);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 0b11_10_01_00_11_10_01_00_11_10_01_00_11_10_01_00); // texel i: index i % 4

        NightDds.Warm(block);

        Assert.True(BinaryPrimitives.ReadUInt16LittleEndian(block) > BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(2)), "four-colour mode");
        byte[] texels = Dds.DecodeLevel(block, 4, 4);
        for (int i = 0; i < 16; i++)
        {
            byte r = texels[i * 3], g = texels[i * 3 + 1], b = texels[i * 3 + 2];
            Assert.True(g <= r + 1 && b <= r + 1, $"texel {i} is {r},{g},{b}");
        }
        // Texels that were the first colour are now the warmed first colour: red 2, green 4, blue 0.
        Assert.Equal([16, 16, 0], texels[..3]);
        // Those that were the second keep its warmed version: blue down to the red.
        Assert.Equal([16, 16, 16], texels[3..6]);
    }

    [Theory]
    [InlineData(1024, 2, 65408 + 43704)] // the header chunk's part of level 0, and the levels from 256 px
    [InlineData(1024, 1, 65408 + 174776)]
    [InlineData(1024, 0, 699064)] // the whole texture but its header
    [InlineData(512, 1, 65408 + 43704)]
    [InlineData(256, 2, 43704)] // every level starts within the header chunk: kept whole
    public void A_cache_file_keeps_the_header_chunk_and_the_levels_from_the_finest_wanted(int side, int from, long length)
    {
        var layout = DdsLayout.For(side);
        int stored = NightStore.Normalize(layout, from);
        Assert.Equal(length, NightStore.Length(layout, from));
        Assert.Equal(0, NightStore.Position(layout, from, Dds.HeaderSize));
        Assert.Equal(-1, NightStore.Position(layout, from, 0));
        Assert.Equal(length - 1, NightStore.Position(layout, from, layout.Length - 1));
        if (stored > 0) Assert.Equal(-1, NightStore.Position(layout, from, NightStore.HeadEnd(layout)));
    }

    [Fact]
    public void A_level_is_the_same_bytes_whichever_level_the_kept_part_starts_at()
    {
        // A texture the photos' size, painted at the reference side: X-Plane's far levels.
        const int Nominal = 4096;
        var painter = new NightPainter(new RoadNetworks([OverlayWithTown()]));
        NightWorkspace Paint()
        {
            var workspace = new NightWorkspace(1024);
            painter.Paint(Nice, workspace);
            return workspace;
        }
        byte[] whole = NightDds.Encode(Paint(), Nominal);
        var layout = DdsLayout.For(Nominal);
        foreach (int from in new[] { 1, 2, 4, 7 })
        {
            byte[] kept = NightDds.EncodeStored(Paint(), Nominal, from);
            Assert.True(kept.AsSpan(0, (int)NightStore.HeadEnd(layout) - Dds.HeaderSize).SequenceEqual(whole.AsSpan(Dds.HeaderSize, (int)NightStore.HeadEnd(layout) - Dds.HeaderSize)));
            int at = (int)NightStore.Position(layout, from, layout.Offset(from));
            Assert.True(kept.AsSpan(at).SequenceEqual(whole.AsSpan((int)layout.Offset(from))), $"from level {from}");
        }
    }

    // --- Serving -----------------------------------------------------------------------------------------------------

    /// <summary>The photos' side, as the Extra quality has them.</summary>
    private const int PhotoSide = 4096;

    private NightTextures Textures(params string[] overlays)
    {
        var painting = new NightPainting(new NightPainter(new RoadNetworks(overlays)), 2);
        _paintings.Add(painting);
        return new(_tree.PathOf("cache"), _ => PhotoSide, painting);
    }

    private static VirtualPath NightPath(TextureName name) => VirtualPath.Of(NightTextures.Folder, name.Stem + "_LIT.dds");

    /// <summary>One of X-Plane's reads on a handle: <paramref name="length"/> bytes from <paramref name="offset"/>.</summary>
    private static byte[] Read(NightTexture texture, ReadCursor cursor, long offset, int length)
    {
        byte[] buffer = new byte[length];
        Assert.Equal((int)Math.Min(length, texture.Layout.Length - offset), texture.Read(cursor, offset, buffer));
        return buffer;
    }

    /// <summary>The whole night texture of the photos' size, painted at <paramref name="paintSide"/>.</summary>
    private byte[] WholeNightTexture(TextureName name, int paintSide)
    {
        var workspace = new NightWorkspace(paintSide);
        new NightPainter(new RoadNetworks([OverlayWithTown()])).Paint(name, workspace);
        return NightDds.Encode(workspace, PhotoSide);
    }

    [Fact]
    public void Night_textures_have_the_photos_size_and_X_Planes_reads_are_painted_at_their_detail()
    {
        NightTextures night = Textures(OverlayWithTown());
        NightTexture texture = night.Open(NightPath(Nice))!.Value.Night!;
        Assert.Equal(PhotoSide, texture.Layout.Size);
        Assert.Equal(0, night.Counts.Painted);
        var layout = texture.Layout;
        byte[] far = WholeNightTexture(Nice, 1024), near = WholeNightTexture(Nice, 2048);
        string file = _tree.PathOf($"cache/night/{Nice.Stem}.lit4");

        // As X-Plane loads a far tile: the header chunk, then the levels from 256 px (the photo's 256 px level with them).
        var cursor = new ReadCursor();
        int level4 = (int)layout.Offset(4);
        Assert.Equal(far[..65536], Read(texture, cursor, 0, 65536));
        Assert.Equal(far[level4..], Read(texture, cursor, level4, far.Length - level4));
        Assert.Equal(1, night.Counts.Painted);
        // Kept compressed: less than half the 64 KB of level 0 and the 43 KB of the levels from 256 px, though this
        // town is lit edge to edge.
        Assert.True(new FileInfo(file).Length < NightStore.Length(layout, 4) / 2, $"{new FileInfo(file).Length} bytes");
        // Counted and removed with its area, and never taken for a photo (it is not a .dds).
        Assert.Equal(AreaName.Of(Nice), CacheAreas.AreaOf(Path.GetFileName(file)));

        // Beside the aircraft: the header chunk again (as kept), then level 0 read on from it, painted twice as fine.
        cursor = new ReadCursor();
        Assert.Equal(far[..65536], Read(texture, cursor, 0, 65536));
        Assert.Equal(near[65536..131072], Read(texture, cursor, 65536, 65536));
        Assert.Equal(2, night.Counts.Painted);
        Assert.Equal(near[131072..], Read(texture, cursor, 131072, near.Length - 131072));
        Assert.Equal(2, night.Counts.Painted);

        // The next session reads it from the cache without painting.
        NightTextures next = Textures(OverlayWithTown());
        Assert.Equal(near, Read(next.Open(NightPath(Nice))!.Value.Night!, new ReadCursor(), 0, near.Length));
        Assert.Equal(0, next.Counts.Painted);
    }

    [Fact]
    public void A_place_without_lights_reads_black_and_keeps_nothing()
    {
        NightTextures night = Textures();
        TextureName airport = TextureName.At(43.70, 7.25, "BI", 18);
        NightTexture texture = night.Open(NightPath(airport))!.Value.Night!;
        int level4 = (int)texture.Layout.Offset(4);

        var cursor = new ReadCursor();
        byte[] head = Read(texture, cursor, 0, 65536), levels = Read(texture, cursor, level4, (int)texture.Layout.Length - level4);
        Read(texture, new ReadCursor(), 0, 65536);

        Assert.True(head.AsSpan(0, Dds.HeaderSize).SequenceEqual(texture.Layout.Header));
        Assert.True(head.AsSpan(Dds.HeaderSize).IndexOfAnyExcept((byte)0) < 0);
        Assert.True(levels.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        Assert.False(Directory.Exists(_tree.PathOf("cache/night")));
        Assert.Equal(1, night.Counts.Dark);
    }

    [Fact]
    public void Night_textures_earlier_versions_kept_are_removed()
    {
        _tree.WriteText($"cache/night/{Nice.Stem}.lit1", "whole");
        _tree.WriteText($"cache/night/{Nice.Stem}.lit2", "four times coarser");
        _tree.WriteText($"cache/night/{Nice.Stem}.lit3", "square lamps");
        _tree.WriteText($"cache/night/{Nice.Stem}.lit4", "now");
        Assert.Equal(3, NightTextures.RemoveOldFiles(_tree.PathOf("cache")));
        Assert.Equal([_tree.PathOf($"cache/night/{Nice.Stem}.lit4")], Directory.GetFiles(_tree.PathOf("cache/night")));
    }

    [Fact]
    public void A_cache_file_gives_back_what_was_kept_across_its_chunks_and_black_ones_take_no_space()
    {
        byte[] kept = new byte[200_000]; // four chunks: two black, one with light, one partly
        new Random(5).NextBytes(kept.AsSpan(70_000, 50_000));
        string path = _tree.PathOf("cache/night/test.lit4");
        NightFile.Write(path, 3, kept);

        Assert.True(new FileInfo(path).Length < 60_000, "the black chunks take nothing");
        using (var handle = File.OpenHandle(path))
        {
            NightFile.Contents contents = NightFile.ReadContents(handle)!;
            Assert.Equal(3, contents.From);
            Assert.Equal(kept.Length, contents.Length);
            foreach (var (at, length) in new[] { (0, 10), (65_530, 100), (60_000, 140_000), (199_990, 10) })
            {
                byte[] read = new byte[length];
                NightFile.Read(handle, contents, at, read);
                Assert.Equal(kept.AsSpan(at, length).ToArray(), read);
            }
        }

        File.WriteAllBytes(path, [.. File.ReadAllBytes(path)[..^5]]);
        using var damaged = File.OpenHandle(path);
        Assert.Null(NightFile.ReadContents(damaged));
    }

    [Theory]
    [InlineData("textures", "21904_35824_BI16_LIT.dds")]
    [InlineData(NightTextures.Folder, "21904_35824_BI16.dds")]
    [InlineData(NightTextures.Folder, "not_a_tile_LIT.dds")]
    public void Other_paths_are_not_night_textures(string folder, string name) =>
        Assert.Null(Textures().Open(VirtualPath.Of(folder, name)));

    // --- The sun -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Day_twilight_and_night_are_told_once_each_even_with_the_sun_on_the_line()
    {
        var daylight = new Daylight();
        Assert.Equal(DaylightPhase.Day, (DaylightPhase)daylight.Offer(20)!);
        Assert.Null(daylight.Offer(5));
        Assert.Null(daylight.Offer(-0.2)); // on the line: not yet
        Assert.Null(daylight.Offer(0.1));
        Assert.Equal(DaylightPhase.Twilight, (DaylightPhase)daylight.Offer(-0.6)!);
        Assert.Null(daylight.Offer(0.2)); // back up a little: not yet day
        Assert.Equal(DaylightPhase.Night, (DaylightPhase)daylight.Offer(-30)!); // time moved in X-Plane's menu
        Assert.Equal(DaylightPhase.Day, (DaylightPhase)daylight.Offer(40)!);
        Assert.Null(daylight.Offer(double.NaN));
        Assert.Equal(DaylightPhase.Day, daylight.Phase);
    }

    [Fact]
    public void It_is_dark_for_the_photos_only_well_past_civil_twilight_and_a_sun_on_the_line_changes_nothing()
    {
        var daylight = new Daylight();
        Assert.False(daylight.Dark); // nothing from X-Plane yet
        daylight.Offer(-7);
        Assert.Equal((DaylightPhase.Night, false), (daylight.Phase!.Value, daylight.Dark)); // night for the lights, not dark for the photos
        daylight.Offer(-9.2);
        Assert.False(daylight.Dark); // on the line: not yet
        daylight.Offer(-9.6);
        Assert.True(daylight.Dark);
        daylight.Offer(-8.8);
        Assert.True(daylight.Dark); // dawn, but on the line
        daylight.Offer(-8.4);
        Assert.False(daylight.Dark);
        daylight.Offer(-30); // time moved in X-Plane's menu
        Assert.True(daylight.Dark);
        daylight.Offer(double.NaN);
        Assert.True(daylight.Dark);
    }

    [Fact]
    public void How_dark_it_must_be_for_the_photos_can_be_set()
    {
        var daylight = new Daylight(-12);
        daylight.Offer(-11);
        Assert.False(daylight.Dark);
        daylight.Offer(-12.6);
        Assert.True(daylight.Dark);
    }

    // --- The setting -----------------------------------------------------------------------------------------------

    [Fact]
    public void Night_lights_are_off_unless_turned_on()
    {
        Directory.CreateDirectory(_tree.PathOf("source"));
        MountSettings Parse(params string[] extra) => MountSettings.FromValues(TestSettings.Of(
            [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", .. extra]));
        Assert.False(Parse().NightLights);
        Assert.True(Parse("night-lights = on").NightLights);
        Assert.False(Parse("night-lights = off").NightLights);
        Assert.Throws<SettingsException>(() => Parse("night-lights = bright"));
    }

    [Fact]
    public void The_photos_stream_at_night_unless_skipped_whatever_the_night_lights()
    {
        Directory.CreateDirectory(_tree.PathOf("source"));
        MountSettings Parse(params string[] extra) => MountSettings.FromValues(TestSettings.Of(
            [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", .. extra]));
        Assert.False(Parse().SkipPhotosAtNight);
        Assert.True(Parse("skip-photos-at-night = on").SkipPhotosAtNight);
        Assert.Equal((true, false), (Parse("skip-photos-at-night = on", "night-lights = off") is var s ? (s.SkipPhotosAtNight, s.NightLights) : default));
        Assert.Throws<SettingsException>(() => Parse("skip-photos-at-night = sometimes"));
        Assert.Equal(9, Parse().SkipPhotosSunBelow);
        Assert.Equal(12, Parse("skip-photos-sun-below = 12").SkipPhotosSunBelow);
        Assert.Throws<SettingsException>(() => Parse("skip-photos-sun-below = 25"));
        Assert.Throws<SettingsException>(() => Parse("skip-photos-sun-below = -3"));
    }
}
