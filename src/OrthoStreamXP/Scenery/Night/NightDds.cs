using System.Buffers.Binary;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Night textures as DXT1 DDS files with every mip level. The smaller levels are averages in linear light (gamma
/// 2.2), so a town shines as bright from far as from near: averaging the stored values would dim it at a distance.
/// Light fainter than <see cref="NightPainter.Floor"/> is black at every level, and no block is left greener or bluer
/// than it is red (<see cref="Warm"/>): every light drawn is orange or white.
/// <para>
/// Only what X-Plane reads is encoded (<see cref="NightStore"/>): the start of level 0 its header chunk takes, and the
/// levels from the finest it wants. Every level is worked out from the whole painting, whichever level the stored part
/// starts at, so a level painted at the same side is the same bytes whether it was stored first or later (a finer
/// painting, for a level wanted later beside the aircraft, differs from it only in its finer detail).
/// </para>
/// </summary>
internal static class NightDds
{
    private const double Gamma = 2.2;
    private const int LinearSteps = 65535;
    private static readonly float[] ToLinear = [.. Enumerable.Range(0, 256).Select(v => (float)Math.Pow(v / 255.0, Gamma))];
    /// <summary>Linear light in 65536 steps back to the stored value: fine enough near black, where gamma is steepest.</summary>
    private static readonly byte[] ToStored = [.. Enumerable.Range(0, LinearSteps + 1).Select(i => (byte)Math.Clamp((int)(Math.Pow(i / (double)LinearSteps, 1 / Gamma) * 255 + 0.5), 0, 255))];

    /// <summary>The whole DDS file, <paramref name="nominal"/> pixels square, of the texture painted in <paramref name="painted"/>.</summary>
    public static byte[] Encode(NightWorkspace painted, int nominal)
    {
        var layout = DdsLayout.For(nominal);
        byte[] file = new byte[layout.Length];
        layout.Header.CopyTo(file);
        EncodeStored(painted, nominal, 0).CopyTo(file, Dds.HeaderSize);
        return file;
    }

    /// <summary>
    /// What is kept (<see cref="NightStore"/>) of a night texture <paramref name="nominal"/> pixels square, painted in
    /// <paramref name="painted"/> (whose RGB picture may be smaller or larger), when X-Plane wants it from level
    /// <paramref name="from"/>. A level as large as the painting or larger is the painting itself, enlarged in linear
    /// light where X-Plane wants more than was painted; a smaller one is the painting averaged down. Uses the
    /// workspace's pictures: the painting is gone after.
    /// </summary>
    public static byte[] EncodeStored(NightWorkspace painted, int nominal, int from)
    {
        int side = painted.Side;
        var layout = DdsLayout.For(nominal);
        from = NightStore.Normalize(layout, from);
        byte[] stored = new byte[NightStore.Length(layout, from)];

        // The painting in linear light, one picture per colour, kept for the levels as large as it or larger.
        float[] red = painted.Street, green = painted.Windows, blue = painted.Sodium;
        byte[] rgb = painted.Rgb;
        for (int i = 0, p = 0; i < side * side; i++, p += 3) (red[i], green[i], blue[i]) = (ToLinear[rgb[p]], ToLinear[rgb[p + 1]], ToLinear[rgb[p + 2]]);

        // Level 0 whole, or the block rows of the header chunk's part; then the levels from "from".
        int headBytes = (int)(NightStore.HeadEnd(layout) - Dds.HeaderSize);
        var wanted = new List<(int Mip, int BlockRows, int Bytes)>();
        int level0Rows = Math.Max(1, (nominal + 3) / 4), level0RowBytes = level0Rows * 8;
        wanted.Add((0, from == 0 ? level0Rows : Math.Min(level0Rows, (headBytes + level0RowBytes - 1) / level0RowBytes), from == 0 ? layout.LengthOf(0) : headBytes));
        for (int mip = Math.Max(1, from); mip < layout.Levels; mip++) wanted.Add((mip, Math.Max(1, (layout.SizeOf(mip) + 3) / 4), layout.LengthOf(mip)));

        // Smaller levels are averaged down from a copy, halved in place as the levels get smaller.
        float[] smallRed = painted.Led, smallGreen = painted.WarmGlow, smallBlue = painted.CoolGlow;
        int smallSize = 0;
        foreach (var (mip, blockRows, bytes) in wanted)
        {
            int size = layout.SizeOf(mip);
            byte[] blocks = new byte[blockRows * Math.Max(1, (size + 3) / 4) * 8];
            if (size == side)
            {
                EncodeLevel(rgb, size, blockRows, blocks);
            }
            else if (size > side)
            {
                EncodeEnlarged(red, green, blue, side, size, blockRows, blocks);
            }
            else
            {
                if (smallSize == 0)
                {
                    smallSize = side;
                    red.CopyTo(smallRed, 0);
                    green.CopyTo(smallGreen, 0);
                    blue.CopyTo(smallBlue, 0);
                }
                for (; smallSize > size; smallSize /= 2)
                {
                    Half(smallRed, smallSize);
                    Half(smallGreen, smallSize);
                    Half(smallBlue, smallSize);
                }
                ToBytes(smallRed, smallGreen, smallBlue, size, rgb); // the levels as large as the painting come first
                EncodeLevel(rgb, size, blockRows, blocks);
            }
            blocks.AsSpan(0, bytes).CopyTo(stored.AsSpan((int)NightStore.Position(layout, from, layout.Offset(mip))));
        }
        return stored;
    }

    /// <summary>
    /// The first <paramref name="blockRows"/> rows of BC1 blocks of a level <paramref name="size"/> pixels square, made
    /// from the painting (<paramref name="side"/> pixels, linear light) enlarged smoothly, four pixel rows at a time.
    /// </summary>
    private static void EncodeEnlarged(float[] red, float[] green, float[] blue, int side, int size, int blockRows, Span<byte> blocks)
    {
        // Each pixel of the level read between the four painted pixels around its centre.
        double scale = (double)side / size;
        int[] x0 = new int[size], x1 = new int[size];
        float[] fx = new float[size];
        for (int x = 0; x < size; x++) (x0[x], x1[x], fx[x]) = Between(x, scale, side);
        int rows = Math.Min(4, size), blocksAcross = Math.Max(1, (size + 3) / 4);
        byte[] band = new byte[size * rows * 3];
        for (int by = 0; by < blockRows; by++)
        {
            for (int r = 0; r < rows; r++)
            {
                var (y0, y1, fy) = Between(by * 4 + r, scale, side);
                for (int x = 0; x < size; x++)
                {
                    int a = y0 * side + x0[x], b = y0 * side + x1[x], c = y1 * side + x0[x], d = y1 * side + x1[x];
                    float tx = fx[x];
                    float Mix(float[] p) => (p[a] + (p[b] - p[a]) * tx) * (1 - fy) + (p[c] + (p[d] - p[c]) * tx) * fy;
                    int at = (r * size + x) * 3;
                    (band[at], band[at + 1], band[at + 2]) = Floored(Stored(Mix(red)), Stored(Mix(green)), Stored(Mix(blue)));
                }
            }
            EncodeBand(band, size, rows, blocks.Slice(by * blocksAcross * 8, blocksAcross * 8));
        }
    }

    /// <summary>The two painted pixels either side of pixel <paramref name="i"/>'s centre, and how far between them it is.</summary>
    private static (int Low, int High, float Fraction) Between(int i, double scale, int side)
    {
        double at = Math.Clamp((i + 0.5) * scale - 0.5, 0, side - 1);
        int low = (int)at;
        return (low, Math.Min(low + 1, side - 1), (float)(at - low));
    }

    /// <summary>
    /// The first <paramref name="blockRows"/> rows of BC1 blocks of a level <paramref name="size"/> pixels square into
    /// <paramref name="blocks"/>, four pixel rows at a time.
    /// </summary>
    private static void EncodeLevel(byte[] rgb, int size, int blockRows, Span<byte> blocks)
    {
        int rows = Math.Min(4, size), rowBytes = size * 3, blocksAcross = Math.Max(1, (size + 3) / 4);
        for (int by = 0; by < blockRows; by++)
            EncodeBand(rgb.AsSpan(by * 4 * rowBytes, rowBytes * rows), size, rows, blocks.Slice(by * blocksAcross * 8, blocksAcross * 8));
    }

    /// <summary>
    /// One row of BC1 blocks from <paramref name="rows"/> pixel rows <paramref name="size"/> wide. Only the runs of
    /// blocks with light in them are encoded: a black block (most of the country, and the gaps between a town's
    /// streets) stays zeros, which BC1 reads as black.
    /// </summary>
    private static void EncodeBand(ReadOnlySpan<byte> band, int size, int rows, Span<byte> blocks)
    {
        blocks.Clear();
        if (band.IndexOfAnyExcept((byte)0) < 0) return;
        int blocksAcross = Math.Max(1, (size + 3) / 4);
        byte[]? run = null;
        for (int bx = 0; bx < blocksAcross;)
        {
            if (!Lit(band, size, bx, rows))
            {
                bx++;
                continue;
            }
            int first = bx;
            while (bx < blocksAcross && Lit(band, size, bx, rows)) bx++;
            // The run's pixels side by side, encoded as a narrow picture of their own.
            int left = first * 4, width = Math.Min(size, bx * 4) - left;
            run ??= new byte[size * rows * 3];
            for (int r = 0; r < rows; r++) band.Slice((r * size + left) * 3, width * 3).CopyTo(run.AsSpan(r * width * 3));
            byte[] encoded = Dds.EncodeRows(run, width, rows, workers: 1);
            Warm(encoded);
            encoded.CopyTo(blocks[(first * 8)..]);
        }
    }

    /// <summary>Whether any pixel of the 4 x 4 block <paramref name="bx"/> of a band has light.</summary>
    private static bool Lit(ReadOnlySpan<byte> band, int size, int bx, int rows)
    {
        int left = bx * 4, width = Math.Min(4, size - left);
        for (int r = 0; r < rows; r++)
            if (band.Slice((r * size + left) * 3, width * 3).IndexOfAnyExcept((byte)0) >= 0) return true;
        return false;
    }

    /// <summary>The picture <paramref name="size"/> pixels square halved in place: each pixel of the half the mean of its four.</summary>
    private static void Half(float[] picture, int size)
    {
        // Each pixel is written at or before the first of its four, which no later pixel reads.
        int half = size / 2;
        for (int y = 0; y < half; y++)
            for (int x = 0; x < half; x++)
            {
                int top = 2 * y * size + 2 * x, bottom = top + size;
                picture[y * half + x] = (picture[top] + picture[top + 1] + picture[bottom] + picture[bottom + 1]) / 4;
            }
    }

    /// <summary>Linear light back to stored RGB, what is fainter than the floor black.</summary>
    private static void ToBytes(float[] red, float[] green, float[] blue, int size, byte[] rgb)
    {
        for (int i = 0, p = 0; i < size * size; i++, p += 3)
            (rgb[p], rgb[p + 1], rgb[p + 2]) = Floored(Stored(red[i]), Stored(green[i]), Stored(blue[i]));
    }

    private static byte Stored(float linear) => ToStored[Math.Clamp((int)(linear * LinearSteps + 0.5f), 0, LinearSteps)];

    /// <summary>A pixel fainter than the floor, black.</summary>
    private static (byte R, byte G, byte B) Floored(byte r, byte g, byte b) =>
        Math.Max(r, Math.Max(g, b)) < NightPainter.Floor ? ((byte)0, (byte)0, (byte)0) : (r, g, b);

    /// <summary>
    /// BC1 keeps green in 6 bits and red and blue in 5, so a dim block of orange light can come out a little green or
    /// purple. Each block's two colours get their green and blue brought down to their red at most; the block stays
    /// in four-colour mode (first colour above the second): two colours that swapped order swap back with their texels'
    /// indices, and two that became one keep it alone.
    /// </summary>
    internal static void Warm(Span<byte> blocks)
    {
        for (int at = 0; at + 8 <= blocks.Length; at += 8)
        {
            Span<byte> block = blocks.Slice(at, 8);
            int c0 = WarmColour(BinaryPrimitives.ReadUInt16LittleEndian(block)), c1 = WarmColour(BinaryPrimitives.ReadUInt16LittleEndian(block[2..]));
            uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
            if (c0 < c1)
            {
                (c0, c1) = (c1, c0);
                indices ^= 0x55555555; // each texel's 0 <-> 1 and 2 <-> 3
            }
            else if (c0 == c1)
            {
                indices = 0;
            }
            BinaryPrimitives.WriteUInt16LittleEndian(block, (ushort)c0);
            BinaryPrimitives.WriteUInt16LittleEndian(block[2..], (ushort)c1);
            BinaryPrimitives.WriteUInt32LittleEndian(block[4..], indices);
        }
    }

    private static int WarmColour(int c)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        return (r << 11) | (Math.Min(g, r * 63 / 31) << 5) | Math.Min(b, r);
    }
}

/// <summary>
/// Where the parts of a night texture's DDS file are kept in its cache file. X-Plane reads every texture's first 64 KB
/// (the header and the start of level 0), then only the levels from the finest it wants, which far away is a 256 px
/// one. So the cache file holds the start of level 0 that chunk covers, then the levels from the finest one X-Plane
/// has wanted ("from"); a small texture whose levels all start within that chunk is kept whole. The header itself is
/// the same for every texture of a size and is not kept. <see cref="NightFile"/> keeps it compressed.
/// </summary>
internal static class NightStore
{
    /// <summary>Where X-Plane's header chunk ends: the start of level 0 is kept up to here.</summary>
    public static long HeadEnd(DdsLayout layout) => Math.Min(ReadPattern.HeaderChunk, layout.Length);

    /// <summary>The level a stored part starts at: one that starts within the header chunk means the whole texture.</summary>
    public static int Normalize(DdsLayout layout, int from) =>
        from <= 0 || from >= layout.Levels || layout.Offset(from) <= HeadEnd(layout) ? 0 : from;

    /// <summary>The cache file's length when it holds the levels from <paramref name="from"/>.</summary>
    public static long Length(DdsLayout layout, int from)
    {
        from = Normalize(layout, from);
        return from == 0 ? layout.Length - Dds.HeaderSize : HeadEnd(layout) - Dds.HeaderSize + layout.ChainLength(from);
    }

    /// <summary>Where byte <paramref name="offset"/> of the DDS file is in the cache file, or -1 when it is not kept there.</summary>
    public static long Position(DdsLayout layout, int from, long offset)
    {
        from = Normalize(layout, from);
        if (offset < Dds.HeaderSize || offset >= layout.Length) return -1;
        if (from == 0 || offset < HeadEnd(layout)) return offset - Dds.HeaderSize;
        return offset >= layout.Offset(from) ? HeadEnd(layout) - Dds.HeaderSize + offset - layout.Offset(from) : -1;
    }
}
