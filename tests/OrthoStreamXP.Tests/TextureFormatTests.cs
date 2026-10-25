using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

public sealed class TextureFormatTests
{
    [Fact]
    public void Dds_round_trips_a_colour_that_565_can_hold()
    {
        const int size = 64;
        byte[] rgb = new byte[size * size * 3];
        for (int i = 0; i < rgb.Length; i += 3) (rgb[i], rgb[i + 1], rgb[i + 2]) = (255, 0, 255);

        byte[] dds = Dds.Encode(rgb, size, size, workers: 2, out _, out _);
        var (decoded, width, height) = Dds.DecodeTopLevel(dds);

        Assert.Equal((size, size), (width, height));
        Assert.Equal(rgb, decoded);
        // header + 7 mip levels (64..1) of 8-byte blocks
        Assert.Equal(128 + 8 * (256 + 64 + 16 + 4 + 1 + 1 + 1), dds.Length);
    }

    [Fact]
    public void Read_dimensions_comes_from_the_header()
    {
        using var tree = new TempTree();
        byte[] dds = Dds.Encode(new byte[8 * 4 * 3], 8, 4, workers: 1, out _, out _);
        Assert.Equal((8, 4), Dds.ReadDimensions(tree.WriteBytes("t.dds", dds)));
        Assert.Null(Dds.ReadDimensions(tree.WriteText("t.png", "not a dds file at all")));
    }

    [Fact]
    public void Quadkey_matches_bing() => Assert.Equal("12021132131033", Tiles.QuadKey(9147, 5331, 14));

    [Theory]
    [InlineData("21904_35824_BI16.dds", true)]
    [InlineData("20624_36576_ZL16.dds", true)] // parses: the overlay, not the name, keeps masks from being built
    [InlineData("21905_35824_BI16.dds", false)] // not on the 16-tile grid
    [InlineData("21904_35824_BI16.png", false)]
    [InlineData("21904_35824_16.dds", false)]
    public void Texture_names(string fileName, bool valid) => Assert.Equal(valid, TextureName.TryParseFile(fileName, out _));

    [Fact]
    public void Texture_at_a_position_is_the_16_tile_block_containing_it() =>
        Assert.Equal("21904_35824_BI16", TextureName.At(51.10872, 16.86611, "BI", 16).Stem);

    [Fact]
    public void Position_packet_row_20_gives_latitude_and_longitude()
    {
        byte[] packet = new byte[5 + 36 * 2];
        "DATA"u8.CopyTo(packet);
        BitConverter.TryWriteBytes(packet.AsSpan(5), 3);
        BitConverter.TryWriteBytes(packet.AsSpan(41), 20);
        BitConverter.TryWriteBytes(packet.AsSpan(45), 54.58f);
        BitConverter.TryWriteBytes(packet.AsSpan(49), 18.52f);

        var position = XPlaneUdp.ReadDataRow20(packet);
        Assert.NotNull(position);
        Assert.Equal(54.58, position.Value.Lat, 3);
        Assert.Equal(18.52, position.Value.Lon, 3);
        Assert.Null(XPlaneUdp.ReadDataRow20("DATA*"u8.ToArray().Concat(new byte[10]).ToArray()));
    }
}
