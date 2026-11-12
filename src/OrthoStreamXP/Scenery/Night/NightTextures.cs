using System.Collections.Concurrent;
using System.Diagnostics;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The night textures of a package's photo tiles. A photo tile's terrain definition names its night texture
/// (<see cref="TerrainLine"/>, a LIT_TEX_NOWRAP line), which X-Plane adds to the photo at night, fading it in at dusk
/// by itself. X-Plane loads it with the scenery, by day too, from the file under <see cref="Folder"/>, which exists
/// only in the mount (<see cref="NightTexture"/>).
/// <para>
/// X-Plane reads a night texture at the same level number as the photo (the 1024 px level of a 4096 px photo goes
/// with level 2 of the night texture). So a night texture has the photo's size, or a town's lights would be four times
/// coarser than the ground under them. It is painted at the detail X-Plane reads, from <see cref="ReferenceSide"/> (about
/// 6.5 m a pixel) to twice as fine; a finer level is the painting enlarged smoothly. What X-Plane reads is kept in the
/// package's cache (night\21904_35824_BI16.lit4, compressed, counted and removed with its area); a tile without
/// lights keeps nothing and reads as black.
/// </para>
/// </summary>
/// <param name="cache">The package's cache folder.</param>
/// <param name="photoSide">The photo texture's side at a zoom level: the night texture's.</param>
internal sealed class NightTextures(string cache, Func<int, int> photoSide, NightPainting painting)
{
    /// <summary>The package folder the night textures are served under (it exists only in the mount).</summary>
    public const string Folder = "orthostreamxp_night";
    private const string Suffix = "_LIT.dds";
    /// <summary>The cache file's extension; a new way of keeping them gets a new one, and the old files are removed.</summary>
    private const string CacheExtension = ".lit4";
    private static readonly string[] OldExtensions = [".lit1", ".lit2", ".lit3"];
    /// <summary>A ZL16 texture painted this wide is about 6.5 m a pixel in Europe: lamps 28-45 m apart stay apart.</summary>
    private const int Reference16 = 1024;
    /// <summary>The level side X-Plane reads of most night textures, from far: the first painting keeps that much.</summary>
    private const int FarSide = 256;

    private readonly ConcurrentDictionary<string, NightTexture> _textures = new(StringComparer.OrdinalIgnoreCase);
    private long _painted, _dark, _paintTicks, _keptBytes;

    /// <summary>The terrain definition line that gives a photo tile its night texture.</summary>
    public static string TerrainLine(TextureName name) => $"LIT_TEX_NOWRAP ../{Folder}/{name.Stem}{Suffix}";

    /// <summary>
    /// The coarsest a night texture is painted, in pixels: the same metres per pixel at every zoom level (a ZL18
    /// texture covers a quarter of the ground of a ZL16 one, so a quarter of the side), never below 64.
    /// </summary>
    public static int ReferenceSide(int zoom) => zoom <= 16 ? Reference16 : Math.Max(64, Reference16 >> (zoom - 16));

    /// <summary>The side a level of <paramref name="levelSide"/> pixels is painted at: the reference side to twice it.</summary>
    public static int PaintSide(int zoom, int levelSide) => Math.Clamp(levelSide, ReferenceSide(zoom), 2 * ReferenceSide(zoom));

    /// <summary>The level of a night texture X-Plane reads from far away (<see cref="FarSide"/>).</summary>
    public static int FarLevel(DdsLayout layout)
    {
        int level = 0;
        while (level < layout.Levels - 1 && layout.SizeOf(level) > FarSide) level++;
        return level;
    }

    /// <summary>Paintings with lights and without any this session, the time they took, and the bytes they kept (compressed).</summary>
    public (long Painted, long Dark, TimeSpan Time, long KeptBytes) Counts =>
        (Interlocked.Read(ref _painted), Interlocked.Read(ref _dark), Stopwatch.GetElapsedTime(0, Interlocked.Read(ref _paintTicks)), Interlocked.Read(ref _keptBytes));

    /// <summary>
    /// Removes the night textures earlier versions kept (whole, or four times coarser than the photos): they are
    /// painted again as X-Plane reads them. Returns how many were removed.
    /// </summary>
    public static int RemoveOldFiles(string cache)
    {
        string folder = Path.Join(cache, "night");
        if (!Directory.Exists(folder)) return 0;
        int removed = 0;
        foreach (string extension in OldExtensions)
            foreach (string file in Directory.EnumerateFiles(folder, "*" + extension))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // left for the next start
                }
            }
        return removed;
    }

    /// <summary>What X-Plane gets for a path under <see cref="Folder"/>; null for any other path. Nothing is painted yet.</summary>
    public OverlayEntry? Open(VirtualPath path)
    {
        if (path.Parts.Length != 2 || !path.IsUnder(Folder) || !path.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
            || !TextureName.TryParseStem(path.Name[..^Suffix.Length], out TextureName name)) return null;
        NightTexture texture = _textures.GetOrAdd(name.Stem,
            _ => new NightTexture(this, name, photoSide(name.Zoom), Path.Join(cache, "night", name.Stem + CacheExtension)));
        return new OverlayEntry(null, null, Night: texture);
    }

    /// <summary>
    /// Paints <paramref name="texture"/> and keeps it from level <paramref name="from"/>: true when kept, false when it
    /// has no light at all (nothing kept), null when it could not be kept.
    /// </summary>
    internal bool? Paint(NightTexture texture, int from)
    {
        int side = PaintSide(texture.Name.Zoom, texture.Layout.SizeOf(from));
        TimeSpan took = TimeSpan.Zero;
        try
        {
            byte[]? kept = painting.Paint(texture.Name, side, painted => NightDds.EncodeStored(painted, texture.Layout.Size, from), out took);
            if (kept is null)
            {
                Interlocked.Increment(ref _dark);
                return false;
            }
            NightFile.Write(texture.File, from, kept);
            Interlocked.Increment(ref _painted);
            long written = new FileInfo(texture.File).Length;
            Interlocked.Add(ref _keptBytes, written);
            Log.Detail($"Night texture of {texture.Name.Stem} painted at {side} px, kept from {texture.Layout.SizeOf(from)} px ({written / 1024} KB) in {took.TotalMilliseconds:F0} ms");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"The night texture of {texture.Name.Stem} could not be kept ({ex.Message}); it is black until X-Plane reads it again");
            return null;
        }
        finally
        {
            Interlocked.Add(ref _paintTicks, (long)(took.TotalSeconds * Stopwatch.Frequency));
        }
    }
}
