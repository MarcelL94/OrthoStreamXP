using System.Globalization;
using System.Windows;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>A city or town for the maps' labels.</summary>
internal sealed record City(string Name, double Lat, double Lon, long Population);

/// <summary>A city's label on a map: its name, and where its dot is.</summary>
internal sealed record CityLabel(string Name, double X, double Y);

/// <summary>
/// The cities on the maps: Natural Earth's populated places (naturalearthdata.com, public domain), built into the
/// program, so the maps name places offline.
/// </summary>
internal static class Cities
{
    private static readonly Lazy<City[]> All = new(Load);

    /// <summary>The cities in a place, largest first.</summary>
    public static IEnumerable<City> In(GeoBounds bounds) =>
        All.Value.Where(c => c.Lat >= bounds.South && c.Lat <= bounds.North && c.Lon >= bounds.West && c.Lon <= bounds.East);

    /// <summary>
    /// Labels for a map, largest city first, each left out when its name would cover one already placed (or run off the
    /// map): a crowded region shows its big cities, an empty one its towns.
    /// </summary>
    /// <param name="at">Where a place is drawn, in the map's pixels.</param>
    /// <param name="measure">The size of a label's text.</param>
    public static IReadOnlyList<CityLabel> Place(GeoBounds bounds, Func<double, double, Point> at, Func<string, Size> measure,
        double width, double height, int max = 40)
    {
        var placed = new List<Rect>();
        var labels = new List<CityLabel>();
        foreach (City city in In(bounds))
        {
            Point p = at(city.Lat, city.Lon);
            Size size = measure(city.Name);
            // The dot, then the name to its right.
            var box = new Rect(p.X - 4, p.Y - size.Height / 2 - 2, size.Width + 12, size.Height + 4);
            if (box.Left < 0 || box.Top < 0 || box.Right > width || box.Bottom > height || placed.Any(r => r.IntersectsWith(box))) continue;
            placed.Add(box);
            labels.Add(new CityLabel(city.Name, p.X, p.Y));
            if (labels.Count >= max) break;
        }
        return labels;
    }

    private static City[] Load()
    {
        using Stream? stream = typeof(Cities).Assembly.GetManifestResourceStream("OrthoStreamXP.cities.csv");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        var cities = new List<City>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            string[] f = line.Split(',');
            if (f.Length == 4
                && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon)
                && long.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long population))
                cities.Add(new City(f[0], lat, lon, population));
        }
        return [.. cities];
    }
}
