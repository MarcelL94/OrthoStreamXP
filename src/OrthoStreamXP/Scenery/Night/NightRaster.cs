using System.Numerics;
using System.Runtime.InteropServices;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Drawing on square single-channel float pictures (row by row, <c>side</c> x <c>side</c>): what the night lights need.
/// Most of a night texture is black, so rows without light are skipped wherever they can be.
/// </summary>
internal static class NightRaster
{
    /// <summary>
    /// A straight line <paramref name="width"/> pixels wide from (x0, y0) to (x1, y1) (pixel coordinates, the picture's
    /// corner at 0, 0): every pixel whose centre is within half the width of it, at least half a pixel, is raised to
    /// <paramref name="value"/>. Raising, not adding, keeps crossing streets and a chain's joints from doubling up.
    /// </summary>
    public static void Line(float[] picture, int side, double x0, double y0, double x1, double y1, double width, float value)
    {
        double radius = Math.Max(0.5, width / 2);
        int top = Math.Max(0, (int)Math.Floor(Math.Min(y0, y1) - radius)), bottom = Math.Min(side - 1, (int)Math.Ceiling(Math.Max(y0, y1) + radius));
        double dx = x1 - x0, dy = y1 - y0, length = Math.Sqrt(dx * dx + dy * dy);
        for (int y = top; y <= bottom; y++)
        {
            // The line is a capsule (a band with round ends): convex, so each row crosses it in one stretch, the
            // hull of where the row crosses the band and the two end circles.
            double cy = y + 0.5, from = double.PositiveInfinity, to = double.NegativeInfinity;
            Circle(x0, cy - y0);
            Circle(x1, cy - y1);
            if (length > 0)
            {
                // Along the line (0 to its length) and across it (within the radius), each linear in x.
                var (alongFrom, alongTo) = Solve(dx / length, (cy - y0) * dy / length - x0 * dx / length, 0, length);
                var (acrossFrom, acrossTo) = Solve(-dy / length, (cy - y0) * dx / length + x0 * dy / length, -radius, radius);
                double bandFrom = Math.Max(alongFrom, acrossFrom), bandTo = Math.Min(alongTo, acrossTo);
                if (bandFrom <= bandTo) (from, to) = (Math.Min(from, bandFrom), Math.Max(to, bandTo));
            }
            if (from > to) continue;
            int left = Math.Max(0, (int)Math.Ceiling(from - 0.5)), right = Math.Min(side - 1, (int)Math.Floor(to - 0.5));
            Span<float> row = picture.AsSpan(y * side, side);
            for (int x = left; x <= right; x++)
                if (row[x] < value) row[x] = value;

            void Circle(double centreX, double offsetY)
            {
                double squared = radius * radius - offsetY * offsetY;
                if (squared < 0) return;
                double half = Math.Sqrt(squared);
                (from, to) = (Math.Min(from, centreX - half), Math.Max(to, centreX + half));
            }
        }
    }

    /// <summary>The x where <paramref name="low"/> &lt;= a x + b &lt;= <paramref name="high"/>: an interval, every x, or none (from above to).</summary>
    private static (double From, double To) Solve(double a, double b, double low, double high)
    {
        if (Math.Abs(a) < 1e-12) return low <= b && b <= high ? (double.NegativeInfinity, double.PositiveInfinity) : (1, 0);
        double p = (low - b) / a, q = (high - b) / a;
        return p <= q ? (p, q) : (q, p);
    }

    /// <summary>
    /// A round, soft spot of light centred at (<paramref name="x"/>, <paramref name="y"/>) (pixel coordinates, anywhere
    /// within a pixel): <paramref name="value"/> at its centre, falling off as a Gaussian of <paramref name="sigma"/>
    /// pixels, three sigmas out. Each pixel is raised to it, not added to, as the lines are.
    /// </summary>
    public static void Spot(float[] picture, int side, double x, double y, double sigma, float value)
    {
        int radius = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        int cx = (int)Math.Floor(x), cy = (int)Math.Floor(y);
        // The falloff across and down, worked out once: the spot is their product.
        Span<float> across = stackalloc float[2 * radius + 1], down = stackalloc float[2 * radius + 1];
        double scale = -0.5 / (sigma * sigma);
        for (int i = -radius; i <= radius; i++)
        {
            double dx = cx + i + 0.5 - x, dy = cy + i + 0.5 - y;
            across[i + radius] = (float)Math.Exp(dx * dx * scale);
            down[i + radius] = (float)Math.Exp(dy * dy * scale) * value;
        }
        for (int j = -radius; j <= radius; j++)
        {
            int py = cy + j;
            if (py < 0 || py >= side) continue;
            int row = py * side;
            for (int i = -radius; i <= radius; i++)
            {
                int px = cx + i;
                if (px < 0 || px >= side) continue;
                float light = across[i + radius] * down[j + radius];
                if (picture[row + px] < light) picture[row + px] = light;
            }
        }
    }

    /// <summary>
    /// The picture blurred with a Gaussian of <paramref name="sigma"/> pixels (its kernel 4 sigmas each side, the edges
    /// mirrored), one direction then the other, into <paramref name="result"/> (<paramref name="across"/> holds the
    /// half-way picture). Rows that stay black are only cleared. <paramref name="result"/> may be the picture itself: it
    /// is read only into the half-way picture, before the result is written.
    /// </summary>
    public static void Blur(float[] picture, int side, double sigma, float[] across, float[] result)
    {
        int radius = Math.Min(side - 1, (int)Math.Ceiling(4 * sigma));
        float[] kernel = new float[radius + 1];
        double sum = 0;
        for (int i = 0; i <= radius; i++) sum += (i == 0 ? 1 : 2) * (kernel[i] = (float)Math.Exp(-0.5 * i * i / (sigma * sigma)));
        for (int i = 0; i <= radius; i++) kernel[i] = (float)(kernel[i] / sum);

        bool[] lit = new bool[side];
        float[] padded = new float[side + 2 * radius];
        for (int y = 0; y < side; y++)
        {
            Span<float> source = picture.AsSpan(y * side, side);
            if (source.IndexOfAnyExcept(0f) < 0)
            {
                across.AsSpan(y * side, side).Clear();
                continue;
            }
            lit[y] = true;
            source.CopyTo(padded.AsSpan(radius));
            for (int i = 1; i <= radius; i++)
            {
                padded[radius - i] = source[Math.Min(i - 1, side - 1)];
                padded[radius + side - 1 + i] = source[Math.Max(side - i, 0)];
            }
            Span<float> target = across.AsSpan(y * side, side);
            Scale(padded.AsSpan(radius, side), kernel[0], target);
            for (int i = 1; i <= radius; i++) AddPair(padded.AsSpan(radius - i, side), padded.AsSpan(radius + i, side), kernel[i], target);
        }
        for (int y = 0; y < side; y++)
        {
            // A row of the result is black unless a row within the radius has light.
            bool near = false;
            for (int i = -radius; i <= radius && !near; i++) near = lit[Mirror(y + i, side)];
            Span<float> target = result.AsSpan(y * side, side);
            if (!near)
            {
                target.Clear();
                continue;
            }
            Scale(across.AsSpan(y * side, side), kernel[0], target);
            for (int i = 1; i <= radius; i++)
            {
                int up = Mirror(y - i, side), down = Mirror(y + i, side);
                if (lit[up] || lit[down]) AddPair(across.AsSpan(up * side, side), across.AsSpan(down * side, side), kernel[i], target);
            }
        }
    }

    private static void Scale(ReadOnlySpan<float> source, float k, Span<float> into)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var vs = MemoryMarshal.Cast<float, Vector<float>>(source);
            var vi = MemoryMarshal.Cast<float, Vector<float>>(into);
            for (int v = 0; v < vi.Length; v++) vi[v] = vs[v] * k;
            i = vi.Length * Vector<float>.Count;
        }
        for (; i < into.Length; i++) into[i] = source[i] * k;
    }

    /// <summary><paramref name="into"/> += (a + b) * k, pixel by pixel.</summary>
    private static void AddPair(ReadOnlySpan<float> a, ReadOnlySpan<float> b, float k, Span<float> into)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            var vi = MemoryMarshal.Cast<float, Vector<float>>(into);
            for (int v = 0; v < vi.Length; v++) vi[v] += (va[v] + vb[v]) * k;
            i = vi.Length * Vector<float>.Count;
        }
        for (; i < into.Length; i++) into[i] += (a[i] + b[i]) * k;
    }

    /// <summary>A position past an edge reflected back in (-1 is 0, side is side - 1).</summary>
    private static int Mirror(int i, int side) => i < 0 ? Math.Min(-i - 1, side - 1) : i >= side ? Math.Max(2 * side - i - 1, 0) : i;
}
