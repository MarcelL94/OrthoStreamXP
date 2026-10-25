using System.Buffers;
using System.Diagnostics;

namespace OrthoStreamXP.Textures;

internal sealed record BuildResult(
    int Bytes, int Size, int Hits, int Misses, int Calls, int Fallbacks,
    double DownloadS, double FetchStitchS, double ResizeS, double MipmapS, double BC1EncodeS,
    double EncodeS, double WriteS, double TotalS);

/// <summary>Builds DDS textures from source tiles: fetch, stitch, resize, mipmaps, BC1, atomic write.</summary>
internal sealed class TextureBuilder(TileSource tiles, int encoderWorkers)
{
    public int EncoderWorkers { get; } = Math.Clamp(encoderWorkers, 1, 16);

    public TileSource Source => tiles;

    /// <summary>
    /// Builds <paramref name="textureName"/> from tiles at (file zoom + <paramref name="levelChange"/>).
    /// <paramref name="lodDrop"/> &gt; 0 lowers the fetch zoom further for a fast provisional image.
    /// </summary>
    public async Task<BuildResult> BuildAsync(string textureName, int size, string destination, int lodDrop = 0, int levelChange = 0)
    {
        if (size is < 4 or > TextureSizing.MaxSize || (size & (size - 1)) != 0) throw new InvalidDataException("Invalid texture size");
        if (!TextureName.TryParseFile(textureName, out TextureName name))
            throw new InvalidDataException($"Unsupported DDS texture name: {textureName}");

        int drop = Math.Clamp(lodDrop, 0, 3);
        int zoomShift = Math.Max(-4, Math.Clamp(levelChange, -4, TexturePlan.MaxLevel) - drop);
        int fetchZoom = name.Zoom + zoomShift;
        if (fetchZoom is < 1 or > 23) throw new InvalidDataException("Level change moves the fetch zoom outside 1..23");

        var total = Stopwatch.StartNew();
        var stats = new TileStats();
        using IDisposable buildScope = tiles.BeginBuild();
        var fetchStitch = Stopwatch.StartNew();
        var (stitched, stitchedSize) = await FetchRegionAsync(name, fetchZoom, size,
            new FetchTicket(drop > 0 ? FetchPriority.Read : FetchPriority.Warm), stats).ConfigureAwait(false);
        fetchStitch.Stop();

        double encodeStarted = total.Elapsed.TotalSeconds;
        var resize = Stopwatch.StartNew();
        byte[] baseImage = ResizeTo(stitched, stitchedSize, size);
        resize.Stop();
        byte[] dds = Dds.Encode(baseImage, size, size, EncoderWorkers, out double mipmapS, out double bc1S);
        double writeS = AtomicFile.Write(destination, dds);
        return new BuildResult(dds.Length, size, stats.Hits, stats.Misses, stats.Calls, stats.Fallbacks,
            stats.DownloadSeconds, fetchStitch.Elapsed.TotalSeconds, resize.Elapsed.TotalSeconds, mipmapS, bc1S,
            total.Elapsed.TotalSeconds - encodeStarted, writeS, total.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// The BC1 data of mip levels <paramref name="firstMip"/> to the last of a texture laid out as
    /// <paramref name="layout"/>, made from tiles at <paramref name="zoom"/> (the zoom that level needs).
    /// </summary>
    public async Task<byte[]> BuildChainAsync(TextureName name, DdsLayout layout, int firstMip, int zoom, FetchTicket ticket, TileStats stats)
    {
        int size = layout.SizeOf(firstMip);
        using IDisposable buildScope = tiles.BeginBuild();
        // The stitched picture lives only for this build: pooled, so builds reuse it instead of each leaving up to
        // 48 MB of large object behind.
        var (image, imageSize, pooled) = await FetchRegionAsync(name, zoom, size, ticket, stats, pool: true).ConfigureAwait(false);
        try
        {
            return Dds.EncodeChain(ResizeTo(image, imageSize, size), layout, firstMip, EncoderWorkers);
        }
        finally
        {
            if (pooled) ArrayPool<byte>.Shared.Return(image);
        }
    }

    /// <summary>
    /// The texture's area in tiles of <paramref name="zoom"/>, as one square RGB image. Its size is the detail those
    /// tiles have, except that detail beyond <paramref name="size"/> is shrunk away tile by tile as they arrive (exact
    /// box filter, no seams) instead of stitching a huge image. At a zoom so coarse that one tile holds the whole
    /// texture, the image is the texture's part of that tile.
    /// </summary>
    public async Task<(byte[] Rgb, int Size)> FetchRegionAsync(TextureName name, int zoom, int size, FetchTicket ticket, TileStats stats)
    {
        var (rgb, regionSize, _) = await FetchRegionAsync(name, zoom, size, ticket, stats, pool: false).ConfigureAwait(false);
        return (rgb, regionSize);
    }

    /// <param name="pool">
    /// Stitch into an array from <see cref="ArrayPool{T}.Shared"/> (it may be longer than the image); when the result
    /// says Pooled, the caller returns it there once done.
    /// </param>
    private async Task<(byte[] Rgb, int Size, bool Pooled)> FetchRegionAsync(TextureName name, int zoom, int size, FetchTicket ticket, TileStats stats, bool pool)
    {
        if (zoom is < 1 or > 23) throw new InvalidDataException("The fetch zoom is outside 1..23");
        int shift = zoom - name.Zoom;
        if (shift < -4)
        {
            // One tile covers 2^(-shift-4) textures across: crop this texture's square out of it.
            int coarser = -shift;
            byte[] tile = await tiles.GetTileAsync(name.Col >> coarser, name.Row >> coarser, zoom, ticket, stats).ConfigureAwait(false);
            int part = Math.Max(1, Tiles.TilePx * Tiles.TextureSpan >> coarser);
            int left = ((name.Col & ((1 << coarser) - 1)) * Tiles.TilePx) >> coarser, top = ((name.Row & ((1 << coarser) - 1)) * Tiles.TilePx) >> coarser;
            return (Crop(tile, Tiles.TilePx, left, top, part), part, false);
        }

        int span = Shift(Tiles.TextureSpan, shift);
        int originX = Shift(name.Col, shift), originY = Shift(name.Row, shift);
        int fullSize = Tiles.TilePx * span, tilePx = Tiles.TilePx, stitchedSize = fullSize;
        if (fullSize > size && fullSize % size == 0)
        {
            int factor = fullSize / size;
            if ((factor & (factor - 1)) == 0 && factor <= Tiles.TilePx)
            {
                tilePx = Tiles.TilePx / factor;
                stitchedSize = size;
            }
        }
        int length = stitchedSize * stitchedSize * 3;
        // Every pixel is written by a tile, so a pooled array needs no clearing.
        byte[] stitched = pool ? ArrayPool<byte>.Shared.Rent(length) : new byte[length];
        try
        {
            var jobs = new Task[span * span];
            for (int i = 0; i < jobs.Length; i++)
                jobs[i] = CopyTileAsync(i % span, i / span, originX, originY, zoom, ticket, stitched, stitchedSize, tilePx, stats);
            await Task.WhenAll(jobs).ConfigureAwait(false); // all of them finished, failed or not: nothing writes into it after
        }
        catch
        {
            if (pool) ArrayPool<byte>.Shared.Return(stitched);
            throw;
        }
        return (stitched, stitchedSize, pool);
    }

    /// <summary>
    /// The texture's area at <paramref name="zoom"/> from tiles already at hand (RAM or the low-zoom base), or null
    /// when one is missing. It never downloads, so it can answer X-Plane at once. At most 4 x 4 tiles.
    /// </summary>
    public (byte[] Rgb, int Size)? TryStoredRegion(TextureName name, int zoom)
    {
        int shift = zoom - name.Zoom;
        if (shift < -4)
        {
            int coarser = -shift;
            if (tiles.TryGetStored(name.Col >> coarser, name.Row >> coarser, zoom) is not { } tile) return null;
            int part = Math.Max(1, Tiles.TilePx * Tiles.TextureSpan >> coarser);
            int left = ((name.Col & ((1 << coarser) - 1)) * Tiles.TilePx) >> coarser, top = ((name.Row & ((1 << coarser) - 1)) * Tiles.TilePx) >> coarser;
            return (Crop(tile, Tiles.TilePx, left, top, part), part);
        }
        int span = Shift(Tiles.TextureSpan, shift);
        if (span > 4) return null;
        int originX = Shift(name.Col, shift), originY = Shift(name.Row, shift), size = span * Tiles.TilePx;
        byte[] image = new byte[size * size * 3];
        for (int row = 0; row < span; row++)
        for (int column = 0; column < span; column++)
        {
            if (tiles.TryGetStored(originX + column, originY + row, zoom) is not { } tile) return null;
            for (int y = 0; y < Tiles.TilePx; y++)
                Buffer.BlockCopy(tile, y * Tiles.TilePx * 3, image, ((row * Tiles.TilePx + y) * size + column * Tiles.TilePx) * 3, Tiles.TilePx * 3);
        }
        return (image, size);
    }

    /// <summary>Whether the low-zoom base holds every tile of the texture's area at <paramref name="zoom"/> (a cheap check, nothing is read).</summary>
    public bool StoreHasRegion(TextureName name, int zoom)
    {
        if (tiles.Store is not { } store || !store.Covers(zoom)) return false;
        int shift = zoom - name.Zoom;
        if (shift < -4) return store.Contains(name.Col >> -shift, name.Row >> -shift, zoom);
        int span = Shift(Tiles.TextureSpan, shift), originX = Shift(name.Col, shift), originY = Shift(name.Row, shift);
        if (span > 4) return false;
        for (int row = 0; row < span; row++)
        for (int column = 0; column < span; column++)
            if (!store.Contains(originX + column, originY + row, zoom)) return false;
        return true;
    }

    /// <summary>Stretches a square image to <paramref name="size"/>: bicubic up, box-halving and bicubic down.</summary>
    public byte[] ResizeTo(byte[] image, int imageSize, int size) =>
        imageSize == size ? image
            : imageSize < size ? Images.ResizeBicubic(image, imageSize, imageSize, size, size, EncoderWorkers)
            : Images.Resize(image, imageSize, imageSize, size, size, EncoderWorkers);

    /// <summary>
    /// The top <paramref name="rows"/> rows of <see cref="ResizeTo"/>'s result. Stretching up, only those rows are made
    /// (for X-Plane's header chunk, 32 of a 4096 px level's rows); shrinking is cheap, so the whole picture is made and its top kept.
    /// </summary>
    public byte[] ResizeTop(byte[] image, int imageSize, int size, int rows) =>
        imageSize < size
            ? Images.ResizeBicubicTop(image, imageSize, imageSize, size, size, rows, EncoderWorkers)
            : ResizeTo(image, imageSize, size)[..(size * Math.Clamp(rows, 1, size) * 3)];

    private static int Shift(int value, int shift) => shift >= 0 ? value << shift : value >> -shift;

    private static byte[] Crop(byte[] tile, int tileSize, int left, int top, int size)
    {
        byte[] part = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
            Buffer.BlockCopy(tile, ((top + y) * tileSize + left) * 3, part, y * size * 3, size * 3);
        return part;
    }

    private async Task CopyTileAsync(int column, int row, int originX, int originY, int zoom, FetchTicket ticket,
        byte[] destination, int width, int tilePx, TileStats stats)
    {
        byte[] pixels = await tiles.GetTileAsync(originX + column, originY + row, zoom, ticket, stats).ConfigureAwait(false);
        int px = Tiles.TilePx;
        while (px > tilePx)
        {
            pixels = Images.BoxHalf(pixels, px, px, 1);
            px >>= 1;
        }
        int rowBytes = px * 3, left = column * px, top = row * px;
        for (int y = 0; y < px; y++)
            Buffer.BlockCopy(pixels, y * rowBytes, destination, ((top + y) * width + left) * 3, rowBytes);
    }
}
