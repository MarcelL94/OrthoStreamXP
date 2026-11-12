using System.Globalization;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// X-Plane's roads by 1° tile, read from the overlay packages' DSFs (the roads X-Plane draws over photo scenery) and
/// kept for the last few tiles asked for. A tile no overlay package has, or has only compressed, has no roads.
/// </summary>
internal sealed class RoadNetworks
{
    /// <summary>Tiles kept read: the 3 x 3 around the aircraft.</summary>
    private const int Kept = 9;

    private readonly IReadOnlyList<string> _packages;
    private readonly Dictionary<(int South, int West), Lazy<RoadNetwork?>> _tiles = [];
    private readonly LinkedList<(int South, int West)> _recent = new();
    private readonly Lock _lock = new();
    private int _compressedWarned;

    /// <param name="overlayPackages">Package folders with an Earth nav data folder, searched in this order.</param>
    public RoadNetworks(IReadOnlyList<string> overlayPackages) => _packages = overlayPackages;

    public IReadOnlyList<string> Packages => _packages;

    /// <summary>
    /// The overlay packages to read roads from: the mounted packages whose name says overlays (their own folders,
    /// never the mount), then such packages in X-Plane's Custom Scenery that are not mounted by this program.
    /// </summary>
    public static IReadOnlyList<string> FindOverlayPackages(IReadOnlyList<PackagePlan> mounted, string? customScenery)
    {
        static bool IsOverlay(string name) => name.Contains("overlay", StringComparison.OrdinalIgnoreCase);
        var found = mounted.Where(p => IsOverlay(p.Name)).Select(p => p.Source).ToList();
        if (customScenery is not null && Directory.Exists(customScenery))
        {
            var mountedNames = mounted.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            try
            {
                found.AddRange(Directory.EnumerateDirectories(customScenery)
                    .Where(d => IsOverlay(Path.GetFileName(d)) && !mountedNames.Contains(Path.GetFileName(d)))
                    .Order(StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning($"Night lights: {customScenery} could not be listed for overlay packages: {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>"Earth nav data\+40+000\+43+007.dsf" for the tile at 43° N, 7° E.</summary>
    public static string TileFile(int south, int west)
    {
        static int Ten(int degrees) => (int)Math.Floor(degrees / 10.0) * 10;
        static string Name(int lat, int lon) => string.Create(CultureInfo.InvariantCulture, $"{lat:+00;-00}{lon:+000;-000}");
        return Path.Join("Earth nav data", Name(Ten(south), Ten(west)), Name(south, west) + ".dsf");
    }

    /// <summary>The roads of the tile whose south-west corner is at <paramref name="south"/>, <paramref name="west"/>; null when none are known.</summary>
    public RoadNetwork? For(int south, int west)
    {
        Lazy<RoadNetwork?> tile;
        lock (_lock)
        {
            var key = (south, west);
            if (!_tiles.TryGetValue(key, out tile!))
            {
                _tiles[key] = tile = new Lazy<RoadNetwork?>(() => Read(south, west));
                if (_recent.Count >= Kept)
                {
                    _tiles.Remove(_recent.Last!.Value);
                    _recent.RemoveLast();
                }
            }
            else
            {
                _recent.Remove(key);
            }
            _recent.AddFirst(key);
        }
        return tile.Value;
    }

    private RoadNetwork? Read(int south, int west)
    {
        string relative = TileFile(south, west);
        foreach (string package in _packages)
        {
            string file = Path.Join(package, relative);
            if (!File.Exists(file)) continue;
            try
            {
                byte[] data = File.ReadAllBytes(file);
                if (DsfNetworkReader.IsCompressed(data))
                {
                    if (Interlocked.Exchange(ref _compressedWarned, 1) == 0)
                        Log.Info($"Night lights: {file} is compressed, which is not read; such tiles get no night lights");
                    continue;
                }
                RoadNetwork roads = DsfNetworkReader.Read(data, south, west);
                Log.Detail($"Night lights: {roads.Count} roads ({roads.PointCount} points) read from {file}");
                return roads;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Log.Warning($"Night lights: the roads of {file} could not be read: {ex.Message}");
            }
        }
        Log.Detail($"Night lights: no overlay package has the roads of {relative}; its textures stay dark at night");
        return null;
    }
}
