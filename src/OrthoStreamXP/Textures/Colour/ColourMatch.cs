using System.Buffers.Binary;

namespace OrthoStreamXP.Textures;

/// <summary>
/// Evens out the colours of photos from different captures. Imagery is a patchwork: next to each other, one capture can
/// be grey and hazy, another bright and green, and where they meet the scenery shows patches. The low-zoom base (one
/// satellite mosaic, already downloaded for stand-ins) is even across a whole region, so it is the reference: how a photo
/// relates to the base is measured place by place (cells about 1.5 km across, a zoom 14 tile, coarser than fields so
/// they keep their own colours), and each place is moved towards the relation usual in its area. That is brightness and
/// tint per channel only: a capture that recorded almost no colour does not get colour it never had.
/// </summary>
internal static class ColourMatch
{
    /// <summary>The usual relation of a photo to the base where nothing is known of its area yet (Bing around Wrocław, log per channel).</summary>
    public static readonly ColourGain DefaultTarget = new(-0.30f, -0.20f, -0.21f);

    /// <summary>The largest change, either way (log): about x0.6 to x1.65, so a real difference is never over-corrected.</summary>
    public const float MaxChange = 0.5f;

    /// <summary>
    /// How far a usual capture's cells differ from the base by what grows there on the two days (log, about 13 %):
    /// differences within it are left alone, and only the part beyond it is corrected.
    /// </summary>
    public const float Usual = 0.12f;

    /// <summary>Pixels per cell side in the pictures compared.</summary>
    public const int SamplePerCell = 32;

    /// <summary>Cells across a texture: one per zoom 14 tile (4 on a ZL16 texture, 1 on a ZL18), 1 to 16.</summary>
    public static int CellsAcross(TextureName name) =>
        Math.Clamp(name.Zoom <= 14 ? Tiles.TextureSpan << Math.Min(14 - name.Zoom, 4) : Tiles.TextureSpan >> Math.Min(name.Zoom - 14, 4), 1, 16);

    /// <summary>
    /// How the photo relates to the base in each cell (row by row): the difference of their mean log colours per channel,
    /// or null where too little of either is usable (cloud, deep shadow, glare). Both pictures are
    /// <paramref name="size"/> pixels square, RGB.
    /// </summary>
    public static ColourGain?[] Relation(byte[] photo, byte[] reference, int size, int cells)
    {
        var result = new ColourGain?[cells * cells];
        int cell = size / cells;
        for (int cy = 0; cy < cells; cy++)
            for (int cx = 0; cx < cells; cx++)
            {
                if (MeanLog(photo, size, cx * cell, cy * cell, cell) is not { } p || MeanLog(reference, size, cx * cell, cy * cell, cell) is not { } b) continue;
                result[cy * cells + cx] = new ColourGain(p.R - b.R, p.G - b.G, p.B - b.B);
            }
        return result;
    }

    /// <summary>The mean log colour of a square of a picture, over its pixels neither near black nor near white; null when under a quarter are.</summary>
    private static ColourGain? MeanLog(byte[] rgb, int size, int left, int top, int side)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int y = top; y < top + side; y++)
            for (int x = left; x < left + side; x++)
            {
                int i = (y * size + x) * 3;
                int luminance = (rgb[i] + rgb[i + 1] + rgb[i + 2]) / 3;
                if (luminance is < 16 or > 235) continue;
                r += Log[rgb[i]];
                g += Log[rgb[i + 1]];
                b += Log[rgb[i + 2]];
                n++;
            }
        return n * 4 < side * side ? null : new ColourGain((float)(r / n), (float)(g / n), (float)(b / n));
    }

    /// <summary>log(value + 8): the offset keeps dark pixels from weighing too much.</summary>
    private static readonly float[] Log = [.. Enumerable.Range(0, 256).Select(v => (float)Math.Log(v + 8))];
}

/// <summary>A change of colour per channel, as the log of a factor (0: none).</summary>
internal readonly record struct ColourGain(float R, float G, float B)
{
    public static readonly ColourGain None = new(0, 0, 0);

    public ColourGain Clamped(float limit) => new(Math.Clamp(R, -limit, limit), Math.Clamp(G, -limit, limit), Math.Clamp(B, -limit, limit));

    public static ColourGain operator -(ColourGain a, ColourGain b) => new(a.R - b.R, a.G - b.G, a.B - b.B);

    /// <summary>The change made stronger or weaker: 0.5 is half of it (the square root of the factor).</summary>
    public static ColourGain operator *(ColourGain a, float strength) => new(a.R * strength, a.G * strength, a.B * strength);

    public override string ToString() => FormattableString.Invariant($"x{Math.Exp(R):0.00} x{Math.Exp(G):0.00} x{Math.Exp(B):0.00}");
}

/// <summary>
/// One texture's colour correction: a factor per channel at the centre of each cell, changing smoothly between them
/// (bilinear, held at the texture's edges). It is applied to the BC1 blocks of the DDS as X-Plane reads it, like the
/// seasons and before them: each block's two colours are scaled, which scales the block. The cache keeps the imagery
/// as downloaded.
/// </summary>
internal sealed class TextureColour
{
    /// <summary>Places across and down that each get one factor: 128 px apart on a 4096 px texture.</summary>
    private const int Steps = 32;
    /// <summary>A factor is rounded to 1/64 of its log (1.6 %), from -<see cref="ColourMatch.MaxChange"/> to +.</summary>
    private const int Quanta = 64;
    private static readonly int Range = (int)Math.Ceiling(ColourMatch.MaxChange * Quanta);
    /// <summary>5-bit and 6-bit channel values scaled by each rounded factor.</summary>
    private static readonly byte[][] Scale5 = Scales(31), Scale6 = Scales(63);

    private readonly int _cells;
    private readonly ColourGain[] _gains;

    private TextureColour(int cells, ColourGain[] gains)
    {
        _cells = cells;
        _gains = gains;
    }

    /// <summary>The correction at each cell's centre (row by row), each within <see cref="ColourMatch.MaxChange"/>.</summary>
    public IReadOnlyList<ColourGain> Gains => _gains;

    /// <summary>The same change everywhere; null when it changes nothing.</summary>
    public static TextureColour? Uniform(ColourGain gain)
    {
        gain = gain.Clamped(ColourMatch.MaxChange);
        return Index(gain.R) == 0 && Index(gain.G) == 0 && Index(gain.B) == 0 ? null : new TextureColour(1, [gain]);
    }

    /// <summary>
    /// The correction that moves each cell's relation to the base (<see cref="ColourMatch.Relation"/>) to
    /// <paramref name="target"/>; null when it changes nothing or no cell has a relation.
    /// <para>
    /// The base was photographed on another day, so a single cell differs from it by its crops as well (a field green
    /// in one, brown in the other): each cell takes the median of the cells around it, which keeps a capture's edge
    /// sharp but drops an odd cell, and a difference within <see cref="ColourMatch.Usual"/> is left alone. So the
    /// countryside of a usual capture is not touched, and textures next to each other are not changed into patches.
    /// </para>
    /// </summary>
    /// <param name="strength">How much of the correction is made, 0 to 1.</param>
    public static TextureColour? Matching(ColourGain?[] relation, int cells, ColourGain target, float strength = 1)
    {
        if (!relation.Any(r => r is not null)) return null;
        var gains = new ColourGain[cells * cells];
        bool any = false;
        for (int cy = 0; cy < cells; cy++)
            for (int cx = 0; cx < cells; cx++)
            {
                List<ColourGain> around = [];
                for (int y = Math.Max(0, cy - 1); y <= Math.Min(cells - 1, cy + 1); y++)
                    for (int x = Math.Max(0, cx - 1); x <= Math.Min(cells - 1, cx + 1); x++)
                        if (relation[y * cells + x] is { } r) around.Add(r);
                if (around.Count == 0) continue;
                var median = new ColourGain(Median(around, g => g.R), Median(around, g => g.G), Median(around, g => g.B));
                ColourGain change = Beyond(target - median, ColourMatch.Usual).Clamped(ColourMatch.MaxChange) * strength;
                gains[cy * cells + cx] = change;
                any |= Index(change.R) != 0 || Index(change.G) != 0 || Index(change.B) != 0;
            }
        return any ? new TextureColour(cells, gains) : null;
    }

    /// <summary>The part of a change beyond <paramref name="usual"/> (its largest channel shrunk by it, the others in proportion).</summary>
    private static ColourGain Beyond(ColourGain change, float usual)
    {
        float largest = Math.Max(Math.Abs(change.R), Math.Max(Math.Abs(change.G), Math.Abs(change.B)));
        if (largest <= usual) return ColourGain.None;
        float keep = (largest - usual) / largest;
        return new ColourGain(change.R * keep, change.G * keep, change.B * keep);
    }

    private static float Median(List<ColourGain> values, Func<ColourGain, float> channel)
    {
        float[] sorted = [.. values.Select(channel).Order()];
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }

    /// <summary>
    /// Changes the whole colour blocks in <paramref name="data"/>, which holds the bytes of a square DDS of
    /// <paramref name="size"/> pixels from <paramref name="fileOffset"/>; blocks cut by either end stay as they are.
    /// </summary>
    public void Apply(Span<byte> data, long fileOffset, int blockBytes, int size)
    {
        // The factor of each place in a row of places, worked out once per row.
        Span<int> row = stackalloc int[Steps];
        Span<bool> done = stackalloc bool[Steps];
        var blocks = new DdsBlocks(fileOffset, data.Length, blockBytes, size, Steps);
        while (blocks.MoveNext())
        {
            if (blocks.NewRow) done.Clear();
            int cx = blocks.PlaceX;
            if (!done[cx])
            {
                ColourGain g = At((cx + 0.5) / Steps, (blocks.PlaceY + 0.5) / Steps);
                row[cx] = Pack(Index(g.R), Index(g.G), Index(g.B));
                done[cx] = true;
            }
            int packed = row[cx];
            if (packed == Pack(0, 0, 0)) continue;
            Span<byte> block = data.Slice(blocks.At, 8);
            ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block), c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
            if (blockBytes == 8 && c0 <= c1) continue; // three colours and transparent: left as made
            ColourTable.WriteColors(block, blockBytes, Scaled(c0, packed), Scaled(c1, packed));
        }
    }

    /// <summary>The correction at a place in the texture (0-1 across and down), between the four nearest cell centres.</summary>
    private ColourGain At(double u, double v)
    {
        if (_cells == 1) return _gains[0];
        double fx = Math.Clamp(u * _cells - 0.5, 0, _cells - 1), fy = Math.Clamp(v * _cells - 0.5, 0, _cells - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, _cells - 1), y1 = Math.Min(y0 + 1, _cells - 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        ColourGain a = _gains[y0 * _cells + x0], b = _gains[y0 * _cells + x1], c = _gains[y1 * _cells + x0], d = _gains[y1 * _cells + x1];
        float Mix(float pa, float pb, float pc, float pd) => (pa * (1 - tx) + pb * tx) * (1 - ty) + (pc * (1 - tx) + pd * tx) * ty;
        return new ColourGain(Mix(a.R, b.R, c.R, d.R), Mix(a.G, b.G, c.G, d.G), Mix(a.B, b.B, c.B, d.B));
    }

    private static int Index(float gain) => Math.Clamp((int)Math.Round(gain * Quanta), -Range, Range);

    private static int Pack(int r, int g, int b) => ((r + Range) << 16) | ((g + Range) << 8) | (b + Range);

    private static ushort Scaled(ushort c, int packed)
    {
        int r = Scale5[(packed >> 16) & 0xFF][(c >> 11) & 31], g = Scale6[(packed >> 8) & 0xFF][(c >> 5) & 63], b = Scale5[packed & 0xFF][c & 31];
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static byte[][] Scales(int max) =>
        [.. Enumerable.Range(-Range, 2 * Range + 1).Select(i => Enumerable.Range(0, max + 1)
            .Select(v => (byte)Math.Clamp((int)Math.Round(v * Math.Exp((double)i / Quanta)), 0, max)).ToArray())];
}
