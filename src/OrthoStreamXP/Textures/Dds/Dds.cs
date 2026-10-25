using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;

namespace OrthoStreamXP.Textures;

/// <summary>DXT1 (BC1) DDS files with a full mipmap chain, plus enough decoding to compare textures.</summary>
internal static class Dds
{
    public const int HeaderSize = 128;

    /// <summary>Encodes an RGB image and its 2x2-averaged mip chain into a complete DXT1 .dds file.</summary>
    public static byte[] Encode(byte[] rgb, int width, int height, int workers, out double mipmapSeconds, out double bc1Seconds)
    {
        int levels = 1;
        for (int w = width, h = height; w > 1 || h > 1; w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1)) levels++;

        long total = HeaderSize;
        for (int level = 0, w = width, h = height; level < levels; level++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            total += (long)Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * 8;
        byte[] output = new byte[total];
        WriteHeader(output, width, height, levels);

        mipmapSeconds = 0;
        bc1Seconds = 0;
        int offset = HeaderSize;
        byte[] current = rgb;
        for (int level = 0, w = width, h = height; level < levels; level++)
        {
            if (level > 0)
            {
                long mipStarted = Stopwatch.GetTimestamp();
                current = Images.BoxHalf(current, w, h, workers);
                w = Math.Max(1, w >> 1);
                h = Math.Max(1, h >> 1);
                mipmapSeconds += Stopwatch.GetElapsedTime(mipStarted).TotalSeconds;
            }
            long bc1Started = Stopwatch.GetTimestamp();
            offset += EncodeBC1(current, w, h, output, offset, workers);
            bc1Seconds += Stopwatch.GetElapsedTime(bc1Started).TotalSeconds;
        }
        return output;
    }

    /// <summary>
    /// The BC1 data of mip levels <paramref name="firstMip"/> to the last of a square texture, as they follow each other
    /// in the file: <paramref name="rgb"/> is level <paramref name="firstMip"/> itself, the smaller ones are 2x2 averages.
    /// </summary>
    public static byte[] EncodeChain(byte[] rgb, DdsLayout layout, int firstMip, int workers)
    {
        byte[] output = new byte[layout.ChainLength(firstMip)];
        int offset = 0;
        // The smaller levels take turns in two pooled pictures, the size of the first of them: a 4096 px chain made
        // 16 MB of new large objects per build otherwise.
        int half = layout.SizeOf(Math.Min(firstMip + 1, layout.Levels - 1)), halfBytes = half * half * 3;
        byte[]? even = null, odd = null;
        try
        {
            byte[] current = rgb;
            for (int mip = firstMip; mip < layout.Levels; mip++)
            {
                int size = layout.SizeOf(mip);
                if (mip > firstMip)
                {
                    byte[] next = (mip - firstMip) % 2 == 1
                        ? even ??= ArrayPool<byte>.Shared.Rent(halfBytes)
                        : odd ??= ArrayPool<byte>.Shared.Rent(halfBytes);
                    Images.BoxHalfInto(current, size * 2, size * 2, next, workers);
                    current = next;
                }
                offset += EncodeBC1(current, size, size, output, offset, workers);
            }
        }
        finally
        {
            if (even is not null) ArrayPool<byte>.Shared.Return(even);
            if (odd is not null) ArrayPool<byte>.Shared.Return(odd);
        }
        return output;
    }

    /// <summary>The BC1 data of one square level.</summary>
    public static byte[] EncodeLevel(byte[] rgb, int size, int workers)
    {
        byte[] output = new byte[LevelLength(size)];
        EncodeBC1(rgb, size, size, output, 0, workers);
        return output;
    }

    /// <summary>
    /// The BC1 data of the top <paramref name="rows"/> rows of a level <paramref name="width"/> pixels wide: the start
    /// of that level's data, block row by block row, the same bytes <see cref="EncodeLevel"/> gives there.
    /// </summary>
    public static byte[] EncodeRows(byte[] rgb, int width, int rows, int workers)
    {
        byte[] output = new byte[Math.Max(1, (width + 3) / 4) * Math.Max(1, (rows + 3) / 4) * 8];
        EncodeBC1(rgb, width, rows, output, 0, workers);
        return output;
    }

    /// <summary>One square level in a single colour, without encoding pixel by pixel.</summary>
    public static byte[] FlatLevel(int size, byte r, byte g, byte b) => FlatBlocks(LevelLength(size), r, g, b);

    /// <summary>At least <paramref name="length"/> bytes of BC1 blocks in a single colour: the start of a flat level.</summary>
    public static byte[] FlatBlocks(int length, byte r, byte g, byte b)
    {
        int c0 = Encode565(r, g, b), c1 = Math.Max(0, c0 - 1); // c0 > c1: the opaque four-colour mode
        byte[] output = new byte[(Math.Max(1, length) + 7) / 8 * 8];
        Span<byte> block = stackalloc byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)Math.Max(c0, 1));
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)c1);
        for (int i = 0; i < output.Length; i += 8) block.CopyTo(output.AsSpan(i));
        return output;
    }

    /// <summary>Bytes of one square BC1 level.</summary>
    public static int LevelLength(int size) => Math.Max(1, (size + 3) / 4) * Math.Max(1, (size + 3) / 4) * 8;

    /// <summary>Decodes one DXT1 level (its BC1 blocks) to RGB.</summary>
    public static byte[] DecodeLevel(ReadOnlySpan<byte> blocks, int width, int height) => DecodeBlocks(blocks, width, height, 8, 0);

    /// <summary>The (width, height) recorded in a DDS header, or null when the file is not a DDS.</summary>
    public static (int Width, int Height)? ReadDimensions(string path)
    {
        try
        {
            Span<byte> header = stackalloc byte[20];
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (RandomAccess.Read(file, header, 0) < 20 || !header[..4].SequenceEqual("DDS "u8)) return null;
            return (BinaryPrimitives.ReadInt32LittleEndian(header[16..]), BinaryPrimitives.ReadInt32LittleEndian(header[12..]));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void WriteHeader(Span<byte> h, int width, int height, int levels)
    {
        "DDS "u8.CopyTo(h);
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000; // CAPS | HEIGHT | WIDTH | PIXELFORMAT | LINEARSIZE
        if (levels > 1) flags |= 0x20000; // MIPMAPCOUNT
        Put(h, 4, 124);
        Put(h, 8, flags);
        Put(h, 12, (uint)height);
        Put(h, 16, (uint)width);
        Put(h, 20, (uint)(Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * 8));
        Put(h, 28, (uint)(levels > 1 ? levels : 0));
        Put(h, 76, 32); // pixel format size
        Put(h, 80, 0x4); // DDPF_FOURCC
        "DXT1"u8.CopyTo(h[84..]);
        Put(h, 108, levels > 1 ? 0x1000u | 0x8 | 0x400000 : 0x1000u); // TEXTURE [| COMPLEX | MIPMAP]

        static void Put(Span<byte> data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data[offset..], value);
    }

    /// <summary>
    /// Fast range-fit BC1: endpoints are the per-block min/max inset by 1/16 of the range,
    /// each pixel takes the nearest of the four palette colours. Returns the bytes written.
    /// </summary>
    private static int EncodeBC1(byte[] input, int width, int height, byte[] output, int offset, int workers)
    {
        int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
        void BlockRow(int by)
        {
            Span<byte> pix = stackalloc byte[48];
            for (int bx = 0; bx < blocksX; bx++)
            {
                int loR = 255, loG = 255, loB = 255, hiR = 0, hiG = 0, hiB = 0, p = 0;
                for (int py = 0; py < 4; py++)
                for (int px = 0; px < 4; px++)
                {
                    int x = Math.Min(width - 1, bx * 4 + px), y = Math.Min(height - 1, by * 4 + py), i = (y * width + x) * 3;
                    byte r = input[i], g = input[i + 1], b = input[i + 2];
                    pix[p++] = r; pix[p++] = g; pix[p++] = b;
                    loR = Math.Min(loR, r); loG = Math.Min(loG, g); loB = Math.Min(loB, b);
                    hiR = Math.Max(hiR, r); hiG = Math.Max(hiG, g); hiB = Math.Max(hiB, b);
                }
                int c0 = Encode565(hiR - (hiR - loR) / 16.0, hiG - (hiG - loG) / 16.0, hiB - (hiB - loB) / 16.0);
                int c1 = Encode565(loR + (hiR - loR) / 16.0, loG + (hiG - loG) / 16.0, loB + (hiB - loB) / 16.0);
                if (c0 < c1) (c0, c1) = (c1, c0);
                if (c0 == c1 && c0 > 0) c1--; // equal endpoints would select the 3-colour + transparent mode
                var (p0r, p0g, p0b) = Decode565(c0);
                var (p1r, p1g, p1b) = Decode565(c1);
                double p2r = (2 * p0r + p1r) / 3, p2g = (2 * p0g + p1g) / 3, p2b = (2 * p0b + p1b) / 3;
                double p3r = (p0r + 2 * p1r) / 3, p3g = (p0g + 2 * p1g) / 3, p3b = (p0b + 2 * p1b) / 3;
                uint indices = 0;
                for (int i = 0; i < 16; i++)
                {
                    double r = pix[i * 3], g = pix[i * 3 + 1], b = pix[i * 3 + 2];
                    double d0 = Dist(r, g, b, p0r, p0g, p0b), d1 = Dist(r, g, b, p1r, p1g, p1b);
                    double d2 = Dist(r, g, b, p2r, p2g, p2b), d3 = Dist(r, g, b, p3r, p3g, p3b);
                    int best = 0;
                    double bestD = d0;
                    if (d1 < bestD) { best = 1; bestD = d1; }
                    if (d2 < bestD) { best = 2; bestD = d2; }
                    if (d3 < bestD) best = 3;
                    indices |= (uint)best << (2 * i);
                }
                Span<byte> block = output.AsSpan(offset + (by * blocksX + bx) * 8, 8);
                BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)c0);
                BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)c1);
                BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
            }
        }
        if (workers <= 1 || blocksY < 16)
            for (int by = 0; by < blocksY; by++) BlockRow(by);
        else
            Parallel.For(0, blocksY, new ParallelOptions { MaxDegreeOfParallelism = workers }, BlockRow);
        return blocksX * blocksY * 8;
    }

    private static double Dist(double r, double g, double b, double pr, double pg, double pb)
    {
        double dr = r - pr, dg = g - pg, db = b - pb;
        return dr * dr + dg * dg + db * db;
    }

    private static int Encode565(double r, double g, double b) =>
        ((int)((r * 31 + 127) / 255) << 11) | ((int)((g * 63 + 127) / 255) << 5) | (int)((b * 31 + 127) / 255);

    private static (double R, double G, double B) Decode565(int c) =>
        (((c >> 11) & 31) * 255.0 / 31, ((c >> 5) & 63) * 255.0 / 63, (c & 31) * 255.0 / 31);

    /// <summary>Decodes the top mip level of a DXT1, DXT3 or DXT5 DDS to RGB (alpha ignored).</summary>
    public static (byte[] Rgb, int Width, int Height) DecodeTopLevel(byte[] file)
    {
        if (file.Length < HeaderSize || !file.AsSpan(0, 4).SequenceEqual("DDS "u8)) throw new InvalidDataException("Not a DDS file");
        int height = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(12)), width = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(16));
        string fourCC = Encoding.ASCII.GetString(file, 84, 4);
        var (blockBytes, colorOffset) = fourCC switch
        {
            "DXT1" => (8, 0),
            "DXT3" or "DXT5" => (16, 8),
            _ => throw new InvalidDataException($"Unsupported DDS format '{fourCC.TrimEnd('\0')}' (DXT1, DXT3 and DXT5 are supported)"),
        };
        int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
        if (file.Length < HeaderSize + (long)blocksX * blocksY * blockBytes) throw new InvalidDataException("Truncated DDS file");
        return (DecodeBlocks(file.AsSpan(HeaderSize), width, height, blockBytes, colorOffset), width, height);
    }

    private static byte[] DecodeBlocks(ReadOnlySpan<byte> blocks, int width, int height, int blockBytes, int colorOffset)
    {
        int blocksX = Math.Max(1, (width + 3) / 4), blocksY = Math.Max(1, (height + 3) / 4);
        byte[] rgb = new byte[width * height * 3];
        Span<byte> palette = stackalloc byte[12];
        for (int by = 0; by < blocksY; by++)
        for (int bx = 0; bx < blocksX; bx++)
        {
            ReadOnlySpan<byte> block = blocks.Slice((by * blocksX + bx) * blockBytes + colorOffset, 8);
            int c0 = BinaryPrimitives.ReadUInt16LittleEndian(block), c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            var (r0, g0, b0) = Decode565(c0);
            var (r1, g1, b1) = Decode565(c1);
            SetColor(palette, 0, r0, g0, b0);
            SetColor(palette, 1, r1, g1, b1);
            if (c0 > c1 || blockBytes == 16)
            {
                SetColor(palette, 2, (2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
                SetColor(palette, 3, (r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
            }
            else
            {
                SetColor(palette, 2, (r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
                SetColor(palette, 3, 0, 0, 0);
            }
            for (int i = 0; i < 16; i++)
            {
                int x = bx * 4 + (i & 3), y = by * 4 + (i >> 2);
                if (x >= width || y >= height) continue;
                int index = (int)((indices >> (2 * i)) & 3), o = (y * width + x) * 3;
                palette.Slice(index * 3, 3).CopyTo(rgb.AsSpan(o, 3));
            }
        }
        return rgb;

        static void SetColor(Span<byte> palette, int index, double r, double g, double b)
        {
            palette[index * 3] = (byte)Math.Round(r);
            palette[index * 3 + 1] = (byte)Math.Round(g);
            palette[index * 3 + 2] = (byte)Math.Round(b);
        }
    }
}

/// <summary>
/// Where each mip level of a square DXT1 texture with a full mip chain lies in its .dds file: X-Plane reads the
/// header, then only the byte ranges of the levels it wants, so a read's offset tells which levels it is after.
/// </summary>
internal sealed class DdsLayout
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, DdsLayout> Known = new();

    private readonly long[] _offsets;
    private readonly byte[] _header = new byte[Dds.HeaderSize];

    private DdsLayout(int size)
    {
        Size = size;
        Levels = 1;
        for (int s = size; s > 1; s >>= 1) Levels++;
        _offsets = new long[Levels + 1];
        _offsets[0] = Dds.HeaderSize;
        for (int mip = 0; mip < Levels; mip++) _offsets[mip + 1] = _offsets[mip] + Dds.LevelLength(SizeOf(mip));
        Dds.WriteHeader(_header, size, size, Levels);
    }

    /// <summary>The layout of a size x size texture (a power of two).</summary>
    public static DdsLayout For(int size) =>
        size >= 1 && (size & (size - 1)) == 0 ? Known.GetOrAdd(size, s => new DdsLayout(s)) : throw new ArgumentException("Texture size must be a power of two");

    public int Size { get; }
    public int Levels { get; }
    /// <summary>The whole file: header and every level.</summary>
    public long Length => _offsets[Levels];
    public ReadOnlySpan<byte> Header => _header;

    public int SizeOf(int mip) => Math.Max(1, Size >> mip);
    public long Offset(int mip) => _offsets[mip];
    public int LengthOf(int mip) => (int)(_offsets[mip + 1] - _offsets[mip]);
    /// <summary>Bytes from the start of <paramref name="firstMip"/> to the end of the file.</summary>
    public long ChainLength(int firstMip) => Length - _offsets[firstMip];

    /// <summary>The level a byte of the file belongs to (offsets in the header count as level 0).</summary>
    public int MipAt(long offset)
    {
        int mip = 0;
        while (mip < Levels - 1 && offset >= _offsets[mip + 1]) mip++;
        return mip;
    }
}
