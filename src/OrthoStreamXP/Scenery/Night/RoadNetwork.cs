namespace OrthoStreamXP.Scenery;

/// <summary>
/// The roads of one 1° x 1° scenery tile, kept compact: every chain's points end to end as degrees from the tile's
/// south-west corner (float: a few millimetres), where each chain starts, its class and whether it is rural. A grid
/// over the tile finds the chains near a place, and a second one says how built up each place is
/// (<see cref="BuiltUp"/>), from the length of town streets around it.
/// </summary>
internal sealed class RoadNetwork
{
    /// <summary>Cells a side of the grid that finds chains: about 1.7 km.</summary>
    private const int IndexCells = 64;
    /// <summary>Cells a side of the built-up grid: about 900 m.</summary>
    private const int DensityCells = 120;
    /// <summary>Street km per km² that counts as fully built up (a city centre; a village has 4-6).</summary>
    public const double CityStreetKmPerKm2 = 20;

    private readonly float[] _lon, _lat;
    private readonly int[] _start;
    private readonly RoadClass[] _class;
    private readonly bool[] _rural;
    private readonly int[] _cellStart, _cellChains;
    private readonly byte[] _firstCellX, _firstCellY;
    private readonly float[] _builtUp;

    /// <param name="lon">Every chain's longitudes end to end, in degrees east of <paramref name="west"/>.</param>
    /// <param name="lat">Their latitudes, in degrees north of <paramref name="south"/>.</param>
    /// <param name="start">Where each chain starts in the points, and last where they end (chains + 1 entries).</param>
    public RoadNetwork(int south, int west, float[] lon, float[] lat, int[] start, RoadClass[] roadClass, bool[] rural)
    {
        if (lon.Length != lat.Length || start.Length != roadClass.Length + 1 || rural.Length != roadClass.Length || start[^1] != lon.Length)
            throw new ArgumentException("The road arrays do not match");
        South = south;
        West = west;
        _lon = lon;
        _lat = lat;
        _start = start;
        _class = roadClass;
        _rural = rural;
        (_cellStart, _cellChains, _firstCellX, _firstCellY) = BuildIndex();
        _builtUp = BuildDensity();
    }

    public int South { get; }
    public int West { get; }
    public int Count => _class.Length;
    public int PointCount => _lon.Length;

    public RoadClass ClassOf(int chain) => _class[chain];
    public bool IsRural(int chain) => _rural[chain];

    /// <summary>A chain's points, in degrees from the tile's corner.</summary>
    public (ReadOnlyMemory<float> Lon, ReadOnlyMemory<float> Lat) Points(int chain)
    {
        int from = _start[chain], count = _start[chain + 1] - from;
        return (_lon.AsMemory(from, count), _lat.AsMemory(from, count));
    }

    /// <summary>Every chain that may cross the box (degrees), each once; a chain may still lie just outside it.</summary>
    public void Near(double south, double west, double north, double east, List<int> chains)
    {
        if (east < West || west > West + 1 || north < South || south > South + 1) return;
        int x0 = Cell(west - West, IndexCells), x1 = Cell(east - West, IndexCells);
        int y0 = Cell(south - South, IndexCells), y1 = Cell(north - South, IndexCells);
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                int cell = y * IndexCells + x;
                for (int i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                {
                    // A chain is listed in every cell it covers: report it from the first of them inside the box only.
                    int chain = _cellChains[i];
                    if (x == Math.Max(_firstCellX[chain], x0) && y == Math.Max(_firstCellY[chain], y0)) chains.Add(chain);
                }
            }
    }

    /// <summary>
    /// How built up the place is, 0 (no streets) to 1 (<see cref="CityStreetKmPerKm2"/> and more), read between the
    /// cells' centres; 0 outside the tile.
    /// </summary>
    public double BuiltUp(double lat, double lon)
    {
        double fx = (lon - West) * DensityCells - 0.5, fy = (lat - South) * DensityCells - 0.5;
        if (fx < -1 || fy < -1 || fx > DensityCells || fy > DensityCells) return 0;
        int x0 = Math.Clamp((int)Math.Floor(fx), 0, DensityCells - 2), y0 = Math.Clamp((int)Math.Floor(fy), 0, DensityCells - 2);
        double tx = Math.Clamp(fx - x0, 0, 1), ty = Math.Clamp(fy - y0, 0, 1);
        float At(int x, int y) => _builtUp[y * DensityCells + x];
        return (At(x0, y0) * (1 - tx) + At(x0 + 1, y0) * tx) * (1 - ty) + (At(x0, y0 + 1) * (1 - tx) + At(x0 + 1, y0 + 1) * tx) * ty;
    }

    private static int Cell(double degrees, int cells) => Math.Clamp((int)Math.Floor(degrees * cells), 0, cells - 1);

    /// <summary>Each cell's chains (a chain in every cell its bounds cover), and each chain's first cell.</summary>
    private (int[] Start, int[] Chains, byte[] FirstX, byte[] FirstY) BuildIndex()
    {
        int chains = _class.Length;
        byte[] x0 = new byte[chains], y0 = new byte[chains], x1 = new byte[chains], y1 = new byte[chains];
        int[] count = new int[IndexCells * IndexCells + 1];
        for (int c = 0; c < chains; c++)
        {
            float minLon = float.MaxValue, minLat = float.MaxValue, maxLon = float.MinValue, maxLat = float.MinValue;
            for (int i = _start[c]; i < _start[c + 1]; i++)
            {
                minLon = Math.Min(minLon, _lon[i]);
                maxLon = Math.Max(maxLon, _lon[i]);
                minLat = Math.Min(minLat, _lat[i]);
                maxLat = Math.Max(maxLat, _lat[i]);
            }
            (x0[c], x1[c]) = ((byte)Cell(minLon, IndexCells), (byte)Cell(maxLon, IndexCells));
            (y0[c], y1[c]) = ((byte)Cell(minLat, IndexCells), (byte)Cell(maxLat, IndexCells));
            for (int y = y0[c]; y <= y1[c]; y++)
                for (int x = x0[c]; x <= x1[c]; x++)
                    count[y * IndexCells + x + 1]++;
        }
        for (int i = 1; i < count.Length; i++) count[i] += count[i - 1];
        int[] list = new int[count[^1]];
        int[] next = count[..^1];
        for (int c = 0; c < chains; c++)
            for (int y = y0[c]; y <= y1[c]; y++)
                for (int x = x0[c]; x <= x1[c]; x++)
                    list[next[y * IndexCells + x]++] = c;
        return (count, list, x0, y0);
    }

    /// <summary>Street km per km² in each cell, averaged with its neighbours and scaled to 0-1.</summary>
    private float[] BuildDensity()
    {
        const double MetresPerDegreeLat = 110_574, MetresPerDegreeLonAtEquator = 111_320;
        double cosLat = Math.Cos((South + 0.5) * Math.PI / 180);
        double cellKm2 = MetresPerDegreeLat / DensityCells * (MetresPerDegreeLonAtEquator * cosLat / DensityCells) / 1e6;
        double[] metres = new double[DensityCells * DensityCells];
        for (int c = 0; c < _class.Length; c++)
        {
            if (!RoadClasses.IsStreet(_class[c], _rural[c])) continue;
            for (int i = _start[c]; i < _start[c + 1] - 1; i++)
            {
                double dx = (_lon[i + 1] - _lon[i]) * MetresPerDegreeLonAtEquator * cosLat, dy = (_lat[i + 1] - _lat[i]) * MetresPerDegreeLat;
                double midLon = (_lon[i] + _lon[i + 1]) / 2.0, midLat = (_lat[i] + _lat[i + 1]) / 2.0;
                if (midLon < 0 || midLon >= 1 || midLat < 0 || midLat >= 1) continue;
                metres[Cell(midLat, DensityCells) * DensityCells + Cell(midLon, DensityCells)] += Math.Sqrt(dx * dx + dy * dy);
            }
        }
        float[] builtUp = new float[metres.Length];
        for (int y = 0; y < DensityCells; y++)
            for (int x = 0; x < DensityCells; x++)
            {
                double sum = 0;
                int cells = 0;
                for (int ny = Math.Max(0, y - 1); ny <= Math.Min(DensityCells - 1, y + 1); ny++)
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(DensityCells - 1, x + 1); nx++)
                    {
                        sum += metres[ny * DensityCells + nx];
                        cells++;
                    }
                builtUp[y * DensityCells + x] = (float)Math.Min(1, sum / cells / 1000 / cellKm2 / CityStreetKmPerKm2);
            }
        return builtUp;
    }
}
