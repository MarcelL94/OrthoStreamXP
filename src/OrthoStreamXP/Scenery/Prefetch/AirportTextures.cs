using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The photo textures a package has around a place, from the names of its terrain definitions (terrain\*.ter): the
/// same textures X-Plane will ask for when it loads the scenery there, at the zoom the package uses (ZL16, or ZL18
/// near airports), known before X-Plane opens anything.
/// </summary>
internal static class AirportTextures
{
    /// <summary>
    /// The textures whose centre is within <paramref name="radiusKm"/>, nearest first, at most <paramref name="max"/>.
    /// Empty when the package has no terrain folder (its definitions are generated as X-Plane opens them).
    /// </summary>
    public static IReadOnlyList<VirtualPath> Near(string packageSource, double lat, double lon, double radiusKm, int max)
    {
        string folder = Path.Join(packageSource, "terrain");
        if (!Directory.Exists(folder)) return [];
        double radiusM = radiusKm * 1000;
        var found = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(folder, "*.ter"))
        {
            // ..._water_overlay.ter and ..._sea_overlay.ter use the same texture as the plain one.
            if (TerrainDefinitions.TextureOf(Path.GetFileName(file), out _, out _) is not { } name || found.ContainsKey(name.Stem)) continue;
            var (centreLat, centreLon) = name.Center;
            double distance = Geo.DistanceM(lat, lon, centreLat, centreLon);
            if (distance <= radiusM) found[name.Stem] = distance;
        }
        return [.. found.OrderBy(f => f.Value).ThenBy(f => f.Key, StringComparer.Ordinal).Take(max).Select(f => VirtualPath.Of("textures", f.Key + ".dds"))];
    }
}
