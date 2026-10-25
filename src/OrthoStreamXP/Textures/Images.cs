using SkiaSharp;

namespace OrthoStreamXP.Textures;

/// <summary>Tightly packed 8-bit RGB images (3 bytes per pixel, row-major) and the operations on them.</summary>
internal static class Images
{
    /// <summary>
    /// Decodes a JPEG/PNG/WebP image. Alpha is premultiplied away, i.e. composited over black,
    /// which is what drawing onto the former 24-bit GDI+ bitmap did.
    /// </summary>
    /// <param name="scale">Below 1: decode at about this fraction of the size, which a JPEG does far faster (1/2, 1/4, 1/8).</param>
    public static (byte[] Rgb, int Width, int Height) Decode(byte[] encoded, float scale = 1)
    {
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec codec = SKCodec.Create(data) ?? throw new InvalidDataException("Not a decodable image");
        SKSizeI size = scale < 1 ? codec.GetScaledDimensions(scale) : codec.Info.Size;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        SKCodecResult result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"Image decode failed: {result}");

        int width = info.Width, height = info.Height, rowBytes = bitmap.RowBytes;
        ReadOnlySpan<byte> rgba = bitmap.GetPixelSpan();
        byte[] rgb = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = rgba.Slice(y * rowBytes, width * 4);
            int o = y * width * 3;
            for (int x = 0; x < width; x++, o += 3)
            {
                rgb[o] = row[x * 4];
                rgb[o + 1] = row[x * 4 + 1];
                rgb[o + 2] = row[x * 4 + 2];
            }
        }
        return (rgb, width, height);
    }

    /// <summary>Whether an image has any pixel that is not fully opaque (a JPEG never has; nothing is decoded for one).</summary>
    public static bool HasTransparentPixels(byte[] encoded)
    {
        using SKData data = SKData.CreateCopy(encoded);
        using SKCodec? codec = SKCodec.Create(data);
        if (codec is null || codec.Info.AlphaType == SKAlphaType.Opaque) return false;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) return false;
        ReadOnlySpan<byte> rgba = bitmap.GetPixelSpan();
        for (int y = 0; y < info.Height; y++)
        {
            ReadOnlySpan<byte> row = rgba.Slice(y * bitmap.RowBytes, info.Width * 4);
            for (int i = 3; i < row.Length; i += 4)
                if (row[i] != 255) return true;
        }
        return false;
    }

    /// <summary>Decodes a source tile and makes sure it is 256x256 (providers occasionally send other sizes).</summary>
    public static byte[] DecodeTile(byte[] encoded)
    {
        var (rgb, width, height) = Decode(encoded);
        return width == Tiles.TilePx && height == Tiles.TilePx ? rgb : Resize(rgb, width, height, Tiles.TilePx, Tiles.TilePx, 1);
    }

    /// <summary>Box-halving while at least 2x too large, then bicubic for whatever ratio remains.</summary>
    public static byte[] Resize(byte[] src, int width, int height, int outWidth, int outHeight, int workers)
    {
        while (width >= outWidth * 2 && height >= outHeight * 2)
        {
            src = BoxHalf(src, width, height, workers);
            width >>= 1;
            height >>= 1;
        }
        return width == outWidth && height == outHeight ? src : ResizeBicubic(src, width, height, outWidth, outHeight, workers);
    }

    /// <summary>2x2 average; odd edges repeat their last row/column.</summary>
    public static byte[] BoxHalf(byte[] src, int w, int h, int workers)
    {
        byte[] dst = new byte[Math.Max(1, w >> 1) * Math.Max(1, h >> 1) * 3];
        BoxHalfInto(src, w, h, dst, workers);
        return dst;
    }

    /// <summary><see cref="BoxHalf"/> into <paramref name="dst"/>, which may be longer than the half-size image (a pooled array).</summary>
    public static void BoxHalfInto(byte[] src, int w, int h, byte[] dst, int workers)
    {
        int nw = Math.Max(1, w >> 1), nh = Math.Max(1, h >> 1);
        if (dst.Length < nw * nh * 3) throw new ArgumentException("The destination is smaller than the half-size image", nameof(dst));
        void Row(int ny)
        {
            int y0 = Math.Min(h - 1, ny * 2), y1 = Math.Min(h - 1, ny * 2 + 1);
            for (int nx = 0; nx < nw; nx++)
            {
                int x0 = Math.Min(w - 1, nx * 2), x1 = Math.Min(w - 1, nx * 2 + 1);
                int a = (y0 * w + x0) * 3, b = (y0 * w + x1) * 3, c = (y1 * w + x0) * 3, d = (y1 * w + x1) * 3, o = (ny * nw + nx) * 3;
                for (int k = 0; k < 3; k++) dst[o + k] = (byte)((src[a + k] + src[b + k] + src[c + k] + src[d + k] + 2) >> 2);
            }
        }
        if (workers <= 1 || nh < 64)
            for (int ny = 0; ny < nh; ny++) Row(ny);
        else
            Parallel.For(0, nh, new ParallelOptions { MaxDegreeOfParallelism = workers }, Row);
    }

    /// <summary>Parallel separable Catmull-Rom resampling (edges clamped).</summary>
    public static byte[] ResizeBicubic(byte[] src, int sw, int sh, int dw, int dh, int workers) =>
        ResizeBicubicTop(src, sw, sh, dw, dh, dh, workers);

    /// <summary>
    /// The top <paramref name="rows"/> rows of <see cref="ResizeBicubic"/>'s result, pixel for pixel, made from only the
    /// source rows they need.
    /// </summary>
    public static byte[] ResizeBicubicTop(byte[] src, int sw, int sh, int dw, int dh, int rows, int workers)
    {
        rows = Math.Clamp(rows, 1, dh);
        BicubicWeights(sw, dw, out int[] xi, out float[] xw);
        BicubicWeights(sh, dh, out int[] yi, out float[] yw);
        int sourceRows = yi[rows * 4 - 1] + 1; // up to the last source row the top rows use: the indices only grow
        float[] tmp = new float[sourceRows * dw * 3];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, workers) };
        Parallel.For(0, sourceRows, options, row =>
        {
            int rowS = row * sw * 3, rowT = row * dw * 3;
            for (int x = 0; x < dw; x++)
            {
                int b = x * 4, p0 = rowS + xi[b] * 3, p1 = rowS + xi[b + 1] * 3, p2 = rowS + xi[b + 2] * 3, p3 = rowS + xi[b + 3] * 3, o = rowT + x * 3;
                float w0 = xw[b], w1 = xw[b + 1], w2 = xw[b + 2], w3 = xw[b + 3];
                for (int c = 0; c < 3; c++) tmp[o + c] = src[p0 + c] * w0 + src[p1 + c] * w1 + src[p2 + c] * w2 + src[p3 + c] * w3;
            }
        });
        byte[] dst = new byte[dw * rows * 3];
        int stride = dw * 3;
        Parallel.For(0, rows, options, outRow =>
        {
            int b = outRow * 4, r0 = yi[b] * stride, r1 = yi[b + 1] * stride, r2 = yi[b + 2] * stride, r3 = yi[b + 3] * stride, o = outRow * stride;
            float w0 = yw[b], w1 = yw[b + 1], w2 = yw[b + 2], w3 = yw[b + 3];
            for (int i = 0; i < stride; i++)
            {
                float v = tmp[r0 + i] * w0 + tmp[r1 + i] * w1 + tmp[r2 + i] * w2 + tmp[r3 + i] * w3;
                dst[o + i] = (byte)(v < 0f ? 0 : v > 255f ? 255 : (int)(v + 0.5f));
            }
        });
        return dst;
    }

    private static void BicubicWeights(int srcLen, int dstLen, out int[] idx, out float[] wt)
    {
        idx = new int[dstLen * 4];
        wt = new float[dstLen * 4];
        double scale = (double)srcLen / dstLen;
        for (int i = 0; i < dstLen; i++)
        {
            double sx = (i + 0.5) * scale - 0.5;
            int ix = (int)Math.Floor(sx);
            float t = (float)(sx - ix), t2 = t * t, t3 = t2 * t;
            wt[i * 4] = -0.5f * t3 + t2 - 0.5f * t;
            wt[i * 4 + 1] = 1.5f * t3 - 2.5f * t2 + 1f;
            wt[i * 4 + 2] = -1.5f * t3 + 2f * t2 + 0.5f * t;
            wt[i * 4 + 3] = 0.5f * t3 - 0.5f * t2;
            for (int k = 0; k < 4; k++) idx[i * 4 + k] = Math.Clamp(ix - 1 + k, 0, srcLen - 1);
        }
    }

    /// <summary>Deterministic offline test imagery: smooth colour field plus hash noise, seamless across tiles.</summary>
    public static byte[] SyntheticTile(int x, int y, int zoom)
    {
        const int size = Tiles.TilePx;
        var pixels = new byte[size * size * 3];
        double k = 1.0 / (Math.Pow(2, Math.Max(0, zoom - 8)) * 40.0);
        for (int py = 0; py < size; py++)
        for (int px = 0; px < size; px++)
        {
            double gx = (double)x * size + px, gy = (double)y * size + py;
            double n = Math.Sin(gx * 12.9898 + gy * 78.233) * 43758.5453, noise = (n - Math.Floor(n)) * 0.15 * 255;
            int i = (py * size + px) * 3;
            pixels[i] = Channel(0.5 + 0.5 * Math.Sin(gx * k * 0.9 + gy * k * 0.4), noise);
            pixels[i + 1] = Channel(0.5 + 0.5 * Math.Sin(gx * k * 0.3 - gy * k * 1.1 + 1), noise);
            pixels[i + 2] = Channel(0.5 + 0.5 * Math.Cos(gx * k * 0.7 + gy * k * 0.8), noise);
        }
        return pixels;

        static byte Channel(double value, double noise) => (byte)Math.Clamp((int)Math.Round(value * 0.85 * 255 + noise), 0, 255);
    }
}
