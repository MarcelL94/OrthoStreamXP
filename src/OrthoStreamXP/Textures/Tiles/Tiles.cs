using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OrthoStreamXP.Textures;

/// <summary>Web-Mercator (XYZ / slippy-map) tile math.</summary>
internal static class Tiles
{
    public const int TilePx = 256;
    /// <summary>A texture is 16x16 source tiles (4096 px at its file zoom), as in the .ter files.</summary>
    public const int TextureSpan = 16;
    public const double MaxLat = 85.05112878;

    /// <summary>The (x, y) of the XYZ tile containing lat/lon at <paramref name="zoom"/>.</summary>
    public static (int X, int Y) LatLonToTile(double lat, double lon, int zoom)
    {
        lat = Math.Clamp(lat, -MaxLat, MaxLat);
        double n = 1L << zoom;
        int x = (int)((lon + 180.0) / 360.0 * n);
        double latR = lat * Math.PI / 180.0;
        int y = (int)((1.0 - Math.Asinh(Math.Tan(latR)) / Math.PI) / 2.0 * n);
        int max = (int)n - 1;
        return (Math.Clamp(x, 0, max), Math.Clamp(y, 0, max));
    }

    /// <summary>(lat, lon) of the NW corner of tile (x, y); fractional coordinates give interior points.</summary>
    public static (double Lat, double Lon) TileToLatLon(double x, double y, int zoom)
    {
        double n = 1L << zoom;
        double lon = x / n * 360.0 - 180.0;
        double lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * y / n))) * 180.0 / Math.PI;
        return (lat, lon);
    }

    /// <summary>Bing Maps quadkey for tile (x, y) at <paramref name="zoom"/>.</summary>
    public static string QuadKey(int x, int y, int zoom)
    {
        var key = new StringBuilder(zoom);
        for (int i = zoom; i > 0; i--)
        {
            int mask = 1 << (i - 1);
            key.Append((char)('0' + ((x & mask) != 0 ? 1 : 0) + ((y & mask) != 0 ? 2 : 0)));
        }
        return key.ToString();
    }
}

/// <summary>
/// A tile texture name such as 21632_35680_BI16: the row and column of the top-left
/// source tile of a 16x16 block, the provider code (not used to pick imagery) and the zoom.
/// </summary>
internal readonly partial record struct TextureName(int Row, int Col, string Code, int Zoom)
{
    [GeneratedRegex(@"^(\d+)_(\d+)_([A-Za-z]+)(\d+)$")]
    private static partial Regex StemPattern();

    public string Stem => $"{Row}_{Col}_{Code}{Zoom}";

    /// <summary>The (lat, lon) of the texture's centre.</summary>
    public (double Lat, double Lon) Center => Tiles.TileToLatLon(Col + Tiles.TextureSpan / 2.0, Row + Tiles.TextureSpan / 2.0, Zoom);

    /// <summary>Its width on the ground in metres, as in the LOAD_CENTER of its terrain definition.</summary>
    public double GroundSizeM => 2.0 * Math.PI * 6_378_137.0 * Math.Cos(Center.Lat * Math.PI / 180.0) * Tiles.TextureSpan / (1L << Zoom);

    /// <summary>
    /// The code of the cache's own names (<see cref="TexturePlan.CacheStem"/>): their row, column and zoom are those of
    /// the tiles actually downloaded, so they are not package names and do not parse as one.
    /// </summary>
    public const string CacheCode = "PX";

    /// <summary>Parses a stem ("21632_35680_BI16") and checks it lies on the 16-tile grid.</summary>
    public static bool TryParseStem(string stem, out TextureName name)
    {
        name = default;
        Match m = StemPattern().Match(stem);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out int row)
            || !int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out int col)
            || !int.TryParse(m.Groups[4].Value, CultureInfo.InvariantCulture, out int zoom)
            || m.Groups[3].Value.Equals(CacheCode, StringComparison.OrdinalIgnoreCase))
            return false;
        return TryCreate(row, col, m.Groups[3].Value, zoom, out name);
    }

    /// <summary>A texture name, when its row and column lie on the 16-tile grid of a zoom from 4 to 22.</summary>
    public static bool TryCreate(int row, int col, string code, int zoom, out TextureName name)
    {
        name = default;
        if (zoom is < 4 or > 22 || row < 0 || col < 0 || row % Tiles.TextureSpan != 0 || col % Tiles.TextureSpan != 0) return false;
        long tileCount = 1L << zoom;
        if (row + Tiles.TextureSpan > tileCount || col + Tiles.TextureSpan > tileCount) return false;
        name = new TextureName(row, col, code, zoom);
        return true;
    }

    /// <summary>Parses a file name that must end in .dds.</summary>
    public static bool TryParseFile(string fileName, out TextureName name)
    {
        name = default;
        return Path.GetExtension(fileName).Equals(".dds", StringComparison.OrdinalIgnoreCase)
               && TryParseStem(Path.GetFileNameWithoutExtension(fileName), out name);
    }

    /// <summary>The texture containing a position, named for <paramref name="zoom"/> source tiles.</summary>
    public static TextureName At(double lat, double lon, string code, int zoom)
    {
        var (x, y) = Tiles.LatLonToTile(lat, lon, zoom);
        return new TextureName(y / Tiles.TextureSpan * Tiles.TextureSpan, x / Tiles.TextureSpan * Tiles.TextureSpan, code, zoom);
    }

    public static bool IsTextureFileName([NotNullWhen(true)] string? fileName) =>
        fileName is not null && TryParseFile(fileName, out _);
}
