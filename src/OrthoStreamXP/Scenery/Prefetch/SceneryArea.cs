using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// The scenery X-Plane loads around a place (a departure, an arrival, an alternate) or along a route: its 1° DSF tiles,
/// and the photo textures they use, read from the package's own DSF files. These are exactly the textures X-Plane will
/// ask for there, known before it opens anything.
/// </summary>
internal static class SceneryArea
{
    /// <summary>
    /// The DSF tiles X-Plane 12 loads around a place: three rows (the place's and one either side) by four columns,
    /// leaning towards the nearer half of the place's column (52.46°N 9.68°E: latitudes 51-53, longitudes 8-11).
    /// </summary>
    public static IReadOnlyList<(int Lat, int Lon)> TilesAround(double lat, double lon)
    {
        int row = (int)Math.Floor(lat), col = (int)Math.Floor(lon);
        int west = lon - col >= 0.5 ? col - 1 : col - 2;
        var tiles = new List<(int, int)>(12);
        for (int r = row - 1; r <= row + 1; r++)
            for (int c = west; c <= west + 3; c++)
                if (r is >= -90 and < 90) tiles.Add((r, ((c + 180) % 360 + 360) % 360 - 180));
        return tiles;
    }

    /// <summary>A DSF's path in a package: Earth nav data\+50+000\+52+009.dsf.</summary>
    public static string DsfPath(string packageSource, int lat, int lon)
    {
        static int Floor10(int v) => (int)Math.Floor(v / 10.0) * 10;
        static string Signed(int v, int digits) => (v < 0 ? "-" : "+") + Math.Abs(v).ToString(new string('0', digits), CultureInfo.InvariantCulture);
        return Path.Join(packageSource, "Earth nav data", Signed(Floor10(lat), 2) + Signed(Floor10(lon), 3), Signed(lat, 2) + Signed(lon, 3) + ".dsf");
    }

    /// <summary>
    /// The photo textures of the package's DSFs around the place, each once; <paramref name="tiles"/> is how many of
    /// its DSF tiles the package has and could read.
    /// </summary>
    public static IReadOnlyList<VirtualPath> Textures(string packageSource, double lat, double lon, out int tiles) =>
        TexturesIn(packageSource, TilesAround(lat, lon), out tiles);

    /// <summary>The photo textures of the package's DSFs among <paramref name="dsfTiles"/>, each once.</summary>
    public static IReadOnlyList<VirtualPath> TexturesIn(string packageSource, IEnumerable<(int Lat, int Lon)> dsfTiles, out int tiles)
    {
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textures = new List<VirtualPath>();
        tiles = 0;
        foreach (var (tileLat, tileLon) in dsfTiles.Distinct())
        {
            string dsf = DsfPath(packageSource, tileLat, tileLon);
            if (!File.Exists(dsf) || DsfTerrain.Read(dsf) is not { } names) continue;
            tiles++;
            foreach (string terrain in names)
                if (TerrainDefinitions.TextureOf(Path.GetFileName(terrain), out _, out _) is { } name && stems.Add(name.Stem))
                    textures.Add(VirtualPath.Of("textures", name.Stem + ".dds"));
        }
        return textures;
    }
}

/// <summary>Reads the terrain definitions a DSF uses (its TERT table), without reading the rest of the file.</summary>
internal static class DsfTerrain
{
    private static readonly byte[] Magic = "XPLNEDSF"u8.ToArray();
    private const int HeaderSize = 12, AtomHeader = 8;
    // Atom IDs are four characters stored as a little-endian integer, so they read backwards on disk.
    private const uint Definitions = 'D' << 24 | 'E' << 16 | 'F' << 8 | 'N', TerrainTable = 'T' << 24 | 'E' << 16 | 'R' << 8 | 'T';

    /// <summary>
    /// The TERT names ("terrain/21552_34576_BI16.ter", "terrain_Water", ...), or null when the file is not an
    /// uncompressed DSF (a 7-Zip DSF X-Plane unpacks itself) or cannot be read.
    /// </summary>
    public static IReadOnlyList<string>? Read(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Span<byte> header = stackalloc byte[HeaderSize];
            if (file.Read(header) < HeaderSize || !header[..Magic.Length].SequenceEqual(Magic)) return null;
            long end = file.Length - 16; // the MD5 footer
            for (long at = HeaderSize; at + AtomHeader <= end;)
            {
                var (id, length) = AtomAt(file, at);
                if (length < AtomHeader) return null;
                if (id == Definitions) return TerrainIn(file, at + AtomHeader, Math.Min(at + length, end));
                at += length;
            }
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? TerrainIn(FileStream file, long start, long end)
    {
        for (long at = start; at + AtomHeader <= end;)
        {
            var (id, length) = AtomAt(file, at);
            if (length < AtomHeader || at + length > end) return null;
            if (id == TerrainTable)
            {
                byte[] table = new byte[length - AtomHeader];
                file.Position = at + AtomHeader;
                file.ReadExactly(table);
                return [.. Encoding.UTF8.GetString(table).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
            }
            at += length;
        }
        return [];
    }

    private static (uint Id, long Length) AtomAt(FileStream file, long at)
    {
        Span<byte> atom = stackalloc byte[AtomHeader];
        file.Position = at;
        file.ReadExactly(atom);
        return (BinaryPrimitives.ReadUInt32LittleEndian(atom), BinaryPrimitives.ReadUInt32LittleEndian(atom[4..]));
    }
}
