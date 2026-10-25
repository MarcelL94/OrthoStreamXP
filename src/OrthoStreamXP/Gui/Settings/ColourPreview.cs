using System.Buffers.Binary;
using System.Globalization;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Gui;

/// <summary>The colour settings a preview shows, as the Settings page has them.</summary>
/// <param name="MatchStrength">0 to 1.</param>
/// <param name="Exposure">In percent, <see cref="Textures.Exposure.Min"/> to 100.</param>
internal sealed record ColourAdjustments(bool MatchColours, double MatchStrength, SeasonOptions Seasons, double Exposure);

/// <summary>A photo texture of the cache, at its package name.</summary>
internal sealed record PreviewPhoto(string Path, TextureName Name);

/// <summary>One photo before and after the colour settings: RGB pictures <see cref="Size"/> pixels square, and what was done to it.</summary>
internal sealed record PreviewPicture(byte[] Before, byte[] After, int Size, string Place, IReadOnlyList<string> Notes);

/// <summary>
/// The colour settings' preview: a photo texture from the cache, adjusted the way X-Plane gets it (<see cref="PhotoColours"/>)
/// on the level nearest <see cref="PictureSize"/>. The season is that of the last flight's date at the photo's place
/// (or the one chosen), and the colour matching uses the low-zoom base and what was learnt of the area, as the next flight will.
/// </summary>
internal sealed class ColourPreview : IDisposable
{
    /// <summary>The size of the pictures shown, in pixels.</summary>
    public const int PictureSize = 512;

    /// <summary>The most recently written photos offered.</summary>
    private const int MaxPhotos = 40;

    private readonly string _cache;
    private readonly string _history;
    private readonly TileStore _store;
    private readonly TileSource _tiles;
    private readonly TextureBuilder _builder;

    /// <param name="cache">The cache folder.</param>
    /// <param name="history">The folder of the flights' history (the last X-Plane date, the areas' colours).</param>
    public ColourPreview(string cache, string history)
    {
        _cache = cache;
        _history = history;
        _store = new TileStore(Path.Join(cache, TileStore.FolderName));
        // Only the low-zoom base on disk is read: nothing is downloaded.
        _tiles = new TileSource(ImageryProvider.ParseList("bing", null), 1) { Store = _store };
        _builder = new TextureBuilder(_tiles, 1);
    }

    /// <summary>The photos to show, most recently made first; the finer ground zooms (ZL16 and up) before the coarse far scenery.</summary>
    public IReadOnlyList<PreviewPhoto> FindPhotos(CancellationToken cancel = default)
    {
        var found = new List<(PreviewPhoto Photo, DateTime Written)>();
        foreach (var (path, name) in CacheAreas.PhotoTextures(_cache))
        {
            cancel.ThrowIfCancellationRequested();
            found.Add((new PreviewPhoto(path, name), File.GetLastWriteTimeUtc(path)));
        }
        return [.. found.OrderByDescending(f => f.Photo.Name.Zoom >= 16).ThenByDescending(f => f.Written).Take(MaxPhotos).Select(f => f.Photo)];
    }

    /// <summary>The photo before and after <paramref name="settings"/>; null when it is not a whole DXT1 texture (or is being replaced).</summary>
    public PreviewPicture? Render(PreviewPhoto photo, ColourAdjustments settings)
    {
        using var file = File.OpenHandle(photo.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] header = new byte[Dds.HeaderSize];
        if (RandomAccess.Read(file, header, 0) < header.Length || !header.AsSpan(0, 4).SequenceEqual("DDS "u8)
            || ColourTable.BlockBytes(header.AsSpan(84, 4)) != 8) return null;
        int height = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)), size = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (size != height || size < 4 || (size & (size - 1)) != 0) return null;
        var layout = DdsLayout.For(size);
        if (RandomAccess.GetLength(file) < layout.Length) return null;
        int mip = Math.Clamp((int)Math.Round(Math.Log2((double)size / PictureSize)), 0, layout.Levels - 1), levelSize = layout.SizeOf(mip);
        byte[] blocks = new byte[layout.LengthOf(mip)];
        if (RandomAccess.Read(file, blocks, layout.Offset(mip)) < blocks.Length) return null;
        byte[] before = Dds.DecodeLevel(blocks, levelSize, levelSize);

        var notes = new List<string>();
        TextureColour? colour = settings.MatchColours ? MatchColours(photo, file, size, settings.MatchStrength, notes) : null;
        ushort[]? season = settings.Seasons.On ? Season(photo.Name, settings.Seasons, notes) : null;
        ushort[]? exposure = Exposure.TableFor(settings.Exposure);
        if (exposure is not null) notes.Add(FormattableString.Invariant($"Exposure {settings.Exposure:0} %"));
        if (notes.Count == 0) notes.Add("No colour change: the photos are shown as downloaded");

        PhotoColours.Apply(blocks, layout.Offset(mip), 8, size, colour, season, exposure);
        return new PreviewPicture(before, Dds.DecodeLevel(blocks, levelSize, levelSize), levelSize, Place(photo.Name), notes);
    }

    private TextureColour? MatchColours(PreviewPhoto photo, Microsoft.Win32.SafeHandles.SafeFileHandle file, int size, double strength, List<string> notes)
    {
        if (!_builder.StoreHasRegion(photo.Name, _store.MaxZoom))
        {
            notes.Add("Colour matching: the low-zoom base of this place is not in the cache");
            return null;
        }
        // A new matcher each time: the strength is its own, and nothing it learns is saved.
        var matcher = new ColourMatcher(name => _builder.TryStoredRegion(name, _store.MaxZoom), _builder.ResizeTo,
            Path.Join(_history, ColourMatcher.FileName), strength);
        TextureColour? colour = matcher.ForPhoto(photo.Name, photo.Path, wanted => SceneryFileSystem.FileSample(file, size, wanted));
        notes.Add(colour is null
            ? "Colour matching: this photo already fits its area"
            : FormattableString.Invariant($"Colour matching {strength * 100:0} %"));
        return colour;
    }

    private ushort[]? Season(TextureName name, SeasonOptions options, List<string> notes)
    {
        string dayFile = Path.Join(_history, SeasonClock.DayFileName);
        var clock = new SeasonClock(options, dayFile);
        string when = options.Choice == SeasonChoice.Auto
            ? $"Season of {SeasonClock.Describe(clock.Day)} ({(File.Exists(dayFile) ? "the last flight's date" : "today")}) here"
            : $"Season: {SeasonModel.Names.First(n => n.Value == options.Choice).Key.Replace('-', ' ')}";
        ushort[]? table = clock.TableFor(name);
        notes.Add(table is null ? $"{when}: the photo as it is" : FormattableString.Invariant($"{when}, {options.Strength * 100:0} %"));
        return table;
    }

    private static string Place(TextureName name)
    {
        var (lat, lon) = name.Center;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Math.Abs(lat):0.00}° {(lat >= 0 ? 'N' : 'S')}, {Math.Abs(lon):0.00}° {(lon >= 0 ? 'E' : 'W')} (ZL{name.Zoom})");
    }

    public void Dispose() => _tiles.Dispose();
}
