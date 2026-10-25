using System.Globalization;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>One area on the cache map: where it is drawn, its picture, and whether it is selected.</summary>
internal sealed class AreaTile(CacheArea area, double left, double top, double width, double height, Action<AreaTile> toggled) : ObservableObject
{
    private bool _selected;
    private ImageSource? _picture;

    public CacheArea Area { get; } = area;
    public double Left { get; } = left;
    public double Top { get; } = top;
    public double Width { get; } = width;
    public double Height { get; } = height;
    private ICommand? _toggle;

    public ICommand Toggle => _toggle ??= new RelayCommand(() =>
    {
        IsSelected = !IsSelected;
        toggled(this);
    });

    public ImageSource? Picture
    {
        get => _picture;
        set => Set(ref _picture, value);
    }

    public bool IsSelected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>Not flown for a month: drawn faded, the first the cache limit removes.</summary>
    public bool IsOld => DateTime.UtcNow - Area.LastUsedUtc > TimeSpan.FromDays(30);

    public string ToolTip
    {
        get
        {
            string qualities = string.Join(", ", Area.BytesBySetup.OrderByDescending(b => b.Value).Select(b => $"{QualityName(b.Key)} {CacheInventory.FormatBytes(b.Value)}"));
            return $"{Area.Name} · {CacheInventory.FormatBytes(Area.Bytes)}\n{qualities}\nLast flown {Area.LastUsedUtc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        }
    }

    internal static string QualityName(string setupFolder) =>
        CacheInventory.ParseSetupName(setupFolder) is { } setup && QualityPreset.ForLevel(setup.Level) is { } preset ? preset.Name : setupFolder;
}

/// <summary>A label at the edge of the cache map: a latitude or a longitude.</summary>
internal sealed record MapLabel(string Text, double Left, double Top);

/// <summary>
/// The cache map on the Cache page: every cached 1° area where it is on the globe (longitudes narrowed by the cosine of
/// the latitude, as on a map), as a small picture made from its own textures; areas are selected by clicking them and
/// deleted together.
/// </summary>
internal sealed class CacheMapViewModel : ObservableObject
{
    public const double MaxWidth = 820, MaxHeight = 440;
    private const int PicturePixels = 48;
    private readonly CacheViewModel _owner;
    private IReadOnlyList<AreaTile> _tiles = [];
    private IReadOnlyList<MapLabel> _labels = [];
    private IReadOnlyList<CityLabel> _cities = [];
    private double _width, _height;
    private bool _confirming, _deleting;
    private int _version;

    public CacheMapViewModel(CacheViewModel owner)
    {
        _owner = owner;
        AskDelete = new RelayCommand(() => Confirming = true, () => SelectedCount > 0 && !_owner.InUse && !_deleting);
        CancelDelete = new RelayCommand(() => Confirming = false);
        Delete = new RelayCommand(() => _ = DeleteAsync(), () => SelectedCount > 0 && !_owner.InUse && !_deleting);
        SelectOld = new RelayCommand(() =>
        {
            foreach (AreaTile tile in Tiles) tile.IsSelected = tile.IsOld;
            SelectionChanged();
        }, () => Tiles.Any(t => t.IsOld));
        ClearSelection = new RelayCommand(() =>
        {
            foreach (AreaTile tile in Tiles) tile.IsSelected = false;
            SelectionChanged();
        }, () => SelectedCount > 0);
    }

    public ICommand AskDelete { get; }
    public ICommand CancelDelete { get; }
    public ICommand Delete { get; }
    public ICommand SelectOld { get; }
    public ICommand ClearSelection { get; }

    public IReadOnlyList<AreaTile> Tiles
    {
        get => _tiles;
        private set
        {
            Set(ref _tiles, value);
            Raise(nameof(HasAreas));
        }
    }

    public IReadOnlyList<MapLabel> Labels
    {
        get => _labels;
        private set => Set(ref _labels, value);
    }

    /// <summary>City names over the map, largest first where they would crowd.</summary>
    public IReadOnlyList<CityLabel> CityLabels
    {
        get => _cities;
        private set => Set(ref _cities, value);
    }

    public bool HasAreas => Tiles.Count > 0;

    public double Width
    {
        get => _width;
        private set => Set(ref _width, value);
    }

    public double Height
    {
        get => _height;
        private set => Set(ref _height, value);
    }

    public int SelectedCount => Tiles.Count(t => t.IsSelected);

    public string SelectionText => SelectedCount == 0
        ? $"{Tiles.Count} area(s) of 1° x 1°. Click areas to select them."
        : $"{SelectedCount} area(s) selected · {CacheInventory.FormatBytes(Tiles.Where(t => t.IsSelected).Sum(t => t.Area.Bytes))}";

    public string DeleteQuestion => $"Delete the textures of {SelectedCount} area(s) ({CacheInventory.FormatBytes(Tiles.Where(t => t.IsSelected).Sum(t => t.Area.Bytes))}), in every quality? They are downloaded again when you next fly there.";

    public bool Confirming
    {
        get => _confirming;
        private set => Set(ref _confirming, value);
    }

    /// <summary>Lays the areas out and starts making their pictures (in the background).</summary>
    public void Show(IReadOnlyList<CacheArea> areas, string cache)
    {
        int version = ++_version;
        Confirming = false;
        if (areas.Count == 0)
        {
            (Tiles, Labels, CityLabels, Width, Height) = ([], [], [], 0, 0);
            SelectionChanged();
            return;
        }
        int south = areas.Min(a => a.Name.Lat), north = areas.Max(a => a.Name.Lat) + 1;
        int west = areas.Min(a => a.Name.Lon), east = areas.Max(a => a.Name.Lon) + 1;
        double squeeze = Math.Cos((south + north) / 2.0 * Math.PI / 180);
        double cell = Math.Clamp(Math.Min(MaxWidth / ((east - west) * squeeze), MaxHeight / (north - south)), 6, 72);
        double cellWidth = cell * squeeze;
        const double margin = 34;
        var tiles = areas.Select(a => new AreaTile(a, margin + (a.Name.Lon - west) * cellWidth, margin + (north - 1 - a.Name.Lat) * cell, cellWidth, cell, _ => SelectionChanged())).ToList();
        // A label every few degrees, so they never crowd.
        int step = Math.Max(1, (int)Math.Ceiling(28 / cell));
        var labels = new List<MapLabel>();
        for (int lat = south; lat < north; lat += step)
            labels.Add(new MapLabel(FormattableString.Invariant($"{Math.Abs(lat)}°{(lat >= 0 ? 'N' : 'S')}"), 0, margin + (north - 1 - lat) * cell + cell / 2 - 8));
        step = Math.Max(1, (int)Math.Ceiling(40 / cellWidth));
        for (int lon = west; lon < east; lon += step)
            labels.Add(new MapLabel(FormattableString.Invariant($"{Math.Abs(lon)}°{(lon >= 0 ? 'E' : 'W')}"), margin + (lon - west) * cellWidth, 8));
        (Tiles, Labels, Width, Height) = (tiles, labels, margin + (east - west) * cellWidth + 4, margin + (north - south) * cell + 4);
        // Names over the map: about 6.5 px a letter at 11 px, on a small tag.
        CityLabels = Cities.Place(new Setup.GeoBounds(south, west, north, east),
            (lat, lon) => new System.Windows.Point(margin + (lon - west) * cellWidth, margin + (north - lat) * cell),
            name => new System.Windows.Size(name.Length * 6.5 + 8, 16), Width, Height, max: 60);
        SelectionChanged();
        _ = PaintAsync(tiles, cache, version);
    }

    /// <summary>
    /// Pictures the areas, four at a time, each shown as soon as it is ready. A picture is kept in the log folder's map\
    /// (named with the area's size, so it is made again when the area changes) and in memory while the window is open.
    /// </summary>
    private async Task PaintAsync(IReadOnlyList<AreaTile> tiles, string cache, int version)
    {
        string folder = Path.Join(cache, CacheInventory.LogsFolder, "map");
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        await Parallel.ForEachAsync(tiles, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (tile, _) =>
        {
            if (version != _version) return;
            ImageSource? picture = await Task.Run(() => Picture(cache, folder, tile.Area)).ConfigureAwait(false);
            if (picture is null) return;
            if (dispatcher is null) tile.Picture = picture;
            else await dispatcher.InvokeAsync(() => tile.Picture = picture);
        }).ConfigureAwait(true);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> Pictures = new();

    private static ImageSource? Picture(string cache, string folder, CacheArea area)
    {
        string name = $"{area.Name}-{area.Bytes}-v2.png";
        if (Pictures.TryGetValue(name, out ImageSource? known)) return known;
        string file = Path.Join(folder, name);
        try
        {
            if (File.Exists(file))
            {
                var saved = new BitmapImage();
                saved.BeginInit();
                saved.CacheOption = BitmapCacheOption.OnLoad;
                saved.UriSource = new Uri(file);
                saved.EndInit();
                saved.Freeze();
                return Pictures[name] = saved;
            }
            if (CacheAreas.Picture(cache, area.Name, PicturePixels) is not { } bgra) return null;
            var bitmap = BitmapSource.Create(PicturePixels, PicturePixels, 96, 96, PixelFormats.Bgra32, null, bgra, PicturePixels * 4);
            bitmap.Freeze();
            Directory.CreateDirectory(folder);
            foreach (string older in Directory.EnumerateFiles(folder, $"{area.Name}-*.png")) File.Delete(older);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (FileStream stream = File.Create(file)) png.Save(stream);
            return Pictures[name] = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private void SelectionChanged()
    {
        Confirming = false;
        Raise(nameof(SelectedCount));
        Raise(nameof(SelectionText));
        Raise(nameof(DeleteQuestion));
        RelayCommand.Refresh();
    }

    private async Task DeleteAsync()
    {
        var areas = Tiles.Where(t => t.IsSelected).Select(t => t.Area.Name).ToHashSet();
        string cache = _owner.Root;
        _deleting = true;
        Confirming = false;
        RelayCommand.Refresh();
        try
        {
            long freed = await Task.Run(() =>
            {
                long bytes = CacheAreas.Delete(cache, areas);
                AreaUsage usage = AreaUsage.Load(cache);
                usage.Forget(areas);
                usage.Save();
                return bytes;
            }).ConfigureAwait(true);
            Log.Info($"Deleted {areas.Count} area(s) from the cache ({string.Join(", ", areas)}): {CacheInventory.FormatBytes(freed)} freed");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _owner.ShowError($"Some areas could not be deleted completely: {ex.Message}");
        }
        finally
        {
            _deleting = false;
        }
        await _owner.RescanAsync().ConfigureAwait(true);
    }
}
