using System.Buffers.Binary;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Reads the road networks of an uncompressed DSF (X-Plane's scenery tile format): the coordinates of its 32-bit
/// point pools (PO32, scaled by SC32) and the chains its command stream (CMDS) draws from them. Everything else in
/// the file (terrain, objects, polygons) is skipped. X-Plane's own Global Scenery is 7-Zip compressed and not read
/// (<see cref="IsCompressed"/>); the overlay packages the region installer places are plain DSFs.
/// </summary>
internal static class DsfNetworkReader
{
    private const int HeaderSize = 12, FooterSize = 16; // "XPLNEDSF", version; the MD5 at the end

    // Atom ids are four characters stored as a little-endian integer.
    private const uint Geodesy = 0x47454F44, Commands = 0x434D4453, Pool32 = 0x504F3332, Scale32 = 0x53433332; // GEOD, CMDS, PO32, SC32

    /// <summary>Whether the file is a 7-Zip archive (how X-Plane ships its Global Scenery DSFs).</summary>
    public static bool IsCompressed(ReadOnlySpan<byte> file) => file.Length >= 6 && file[..6].SequenceEqual((ReadOnlySpan<byte>)[0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]);

    /// <summary>The roads of the DSF of the tile at <paramref name="south"/>, <paramref name="west"/>.</summary>
    /// <exception cref="InvalidDataException">The file is not an uncompressed DSF, or is damaged.</exception>
    public static RoadNetwork Read(ReadOnlySpan<byte> file, int south, int west)
    {
        if (file.Length < HeaderSize + FooterSize || !file[..8].SequenceEqual("XPLNEDSF"u8))
            throw new InvalidDataException(IsCompressed(file) ? "the DSF is compressed (7-Zip)" : "not a DSF file");
        try
        {
            ReadOnlySpan<byte> atoms = file[HeaderSize..^FooterSize];
            ReadOnlySpan<byte> geodesy = default, commands = default;
            foreach (var (id, body) in Atoms(atoms))
            {
                if (id == Geodesy) geodesy = atoms[body];
                else if (id == Commands) commands = atoms[body];
            }
            var pools = new List<(uint[] Lon, uint[] Lat)>();
            var scales = new List<(double LonScale, double LonOffset, double LatScale, double LatOffset)>();
            foreach (var (id, range) in Atoms(geodesy))
            {
                ReadOnlySpan<byte> body = geodesy[range];
                if (id == Pool32) pools.Add(DecodePool(body));
                else if (id == Scale32) scales.Add(body.Length >= 16
                    ? (ReadFloat(body, 0), ReadFloat(body, 4), ReadFloat(body, 8), ReadFloat(body, 12))
                    : (0, 0, 0, 0));
            }
            return new ChainBuilder(south, west, pools, scales).Run(commands);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException("the DSF is cut short or damaged", ex);
        }
    }

    private static double ReadFloat(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadSingleLittleEndian(data[at..]);

    /// <summary>The atoms one after another in a piece of the file (or of a parent atom): id and where its contents are.</summary>
    private static List<(uint Id, Range Body)> Atoms(ReadOnlySpan<byte> data)
    {
        var atoms = new List<(uint, Range)>();
        int at = 0;
        while (at + 8 <= data.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            if (length < 8 || length > data.Length - at) throw new InvalidDataException("a DSF atom runs past its parent");
            atoms.Add((id, (at + 8)..(at + length)));
            at += length;
        }
        return atoms;
    }

    /// <summary>The first two planes (longitude, latitude) of a 32-bit point pool, as stored (not yet scaled).</summary>
    private static (uint[] Lon, uint[] Lat) DecodePool(ReadOnlySpan<byte> pool)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(pool);
        int planes = pool[4];
        if (count < 0) throw new InvalidDataException("a DSF point pool has a negative size");
        if (planes < 2) return ([], []);
        int at = 5;
        uint[] lon = new uint[count], lat = new uint[count];
        uint[]? skipped = null;
        for (int plane = 0; plane < planes; plane++)
        {
            uint[] target = plane == 0 ? lon : plane == 1 ? lat : skipped ??= new uint[count];
            at = DecodePlane(pool, at, target);
        }
        return (lon, lat);
    }

    /// <summary>
    /// One plane of 32-bit values: raw (0), differenced (1), run-length (2) or both (3). Returns where the next plane starts.
    /// </summary>
    private static int DecodePlane(ReadOnlySpan<byte> pool, int at, uint[] values)
    {
        byte encoding = pool[at++];
        if (encoding > 3) throw new InvalidDataException($"unknown DSF pool encoding {encoding}");
        if (encoding is 0 or 1)
        {
            for (int i = 0; i < values.Length; i++, at += 4) values[i] = BinaryPrimitives.ReadUInt32LittleEndian(pool[at..]);
        }
        else
        {
            int i = 0;
            while (i < values.Length)
            {
                byte code = pool[at++];
                int run = code & 0x7F;
                if (i + run > values.Length) throw new InvalidDataException("a DSF pool run is longer than its pool");
                if ((code & 0x80) != 0)
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(pool[at..]);
                    at += 4;
                    values.AsSpan(i, run).Fill(value);
                    i += run;
                }
                else
                {
                    for (int end = i + run; i < end; i++, at += 4) values[i] = BinaryPrimitives.ReadUInt32LittleEndian(pool[at..]);
                }
            }
        }
        if (encoding is 1 or 3)
        {
            uint sum = 0;
            for (int i = 0; i < values.Length; i++) values[i] = sum = unchecked(sum + values[i]);
        }
        return at;
    }

    /// <summary>Walks the command stream, collecting each road chain's points as degrees from the tile's corner.</summary>
    private sealed class ChainBuilder(int south, int west, List<(uint[] Lon, uint[] Lat)> pools,
        List<(double LonScale, double LonOffset, double LatScale, double LatOffset)> scales)
    {
        private readonly List<float> _lon = [], _lat = [];
        private readonly List<int> _start = [0];
        private readonly List<RoadClass> _class = [];
        private readonly List<bool> _rural = [];
        private int _pool, _subtype;
        private uint _junctionOffset;

        public RoadNetwork Run(ReadOnlySpan<byte> commands)
        {
            int at = 0;
            while (at < commands.Length)
            {
                byte command = commands[at++];
                switch (command)
                {
                    case 1: _pool = BinaryPrimitives.ReadUInt16LittleEndian(commands[at..]); at += 2; break; // pool select
                    case 2: _junctionOffset = BinaryPrimitives.ReadUInt32LittleEndian(commands[at..]); at += 4; break;
                    case 3: at += 1; break; // definition select (8, 16, 32 bit): one road network per DSF
                    case 4: at += 2; break;
                    case 5: at += 4; break;
                    case 6: _subtype = commands[at++]; break; // road subtype
                    case 7: at += 2; break; // object
                    case 8: at += 4; break; // object range
                    case 9: // network chain, 16-bit indices from the junction offset
                    {
                        int count = commands[at++];
                        BeginChain();
                        for (int i = 0; i < count; i++, at += 2) Add(BinaryPrimitives.ReadUInt16LittleEndian(commands[at..]) + (long)_junctionOffset);
                        EndChain();
                        break;
                    }
                    case 10: // network chain range [first, last) from the junction offset
                    {
                        long first = BinaryPrimitives.ReadUInt16LittleEndian(commands[at..]) + (long)_junctionOffset;
                        long last = BinaryPrimitives.ReadUInt16LittleEndian(commands[(at + 2)..]) + (long)_junctionOffset;
                        at += 4;
                        BeginChain();
                        for (long i = first; i < last; i++) Add(i);
                        EndChain();
                        break;
                    }
                    case 11: // network chain, 32-bit indices
                    {
                        int count = commands[at++];
                        BeginChain();
                        for (int i = 0; i < count; i++, at += 4) Add(BinaryPrimitives.ReadUInt32LittleEndian(commands[at..]));
                        EndChain();
                        break;
                    }
                    case 12: at += 2; at += 1 + 2 * commands[at]; break; // polygon: parameter, count, indices
                    case 13: at += 6; break; // polygon range
                    case 14: // nested polygon: parameter, windings, each a count and its indices
                    {
                        at += 2;
                        int windings = commands[at++];
                        for (int w = 0; w < windings; w++) at += 1 + 2 * commands[at];
                        break;
                    }
                    case 15: at += 2; at += 1 + 2 * (commands[at] + 1); break; // nested polygon range: parameter, windings, windings + 1 indices
                    case 16: break; // terrain patch
                    case 17: at += 1; break; // patch with flags
                    case 18: at += 9; break; // patch with flags and LOD
                    case 23 or 26 or 29: at += 1 + 2 * commands[at]; break; // triangles, strip, fan
                    case 24 or 27 or 30: at += 1 + 4 * commands[at]; break; // the same across pools
                    case 25 or 28 or 31: at += 4; break; // the same as ranges
                    case 32: at += 1 + commands[at]; break; // comments
                    case 33: at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(commands[at..]); break;
                    case 34: at += 4 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(commands[at..])); break;
                    default: throw new InvalidDataException($"unknown DSF command {command}");
                }
            }
            if (at > commands.Length) throw new InvalidDataException("the DSF commands are cut short");
            return new RoadNetwork(south, west, [.. _lon], [.. _lat], [.. _start], [.. _class], [.. _rural]);
        }

        private RoadClass _current;
        private bool _currentRural, _skip;

        private void BeginChain()
        {
            _current = RoadClasses.Of(_subtype, out _currentRural);
            _skip = _current == RoadClass.None || _pool >= pools.Count || _pool >= scales.Count || pools[_pool].Lon.Length == 0;
        }

        private void Add(long index)
        {
            if (_skip) return;
            var (lon, lat) = pools[_pool];
            if (index < 0 || index >= lon.Length) throw new InvalidDataException("a DSF road chain points outside its pool");
            var s = scales[_pool];
            _lon.Add((float)(lon[index] / 4294967295.0 * s.LonScale + s.LonOffset - west));
            _lat.Add((float)(lat[index] / 4294967295.0 * s.LatScale + s.LatOffset - south));
        }

        private void EndChain()
        {
            if (_skip) return;
            int points = _lon.Count - _start[^1];
            if (points < 2)
            {
                // A single point lights nothing: dropped.
                _lon.RemoveRange(_start[^1], points);
                _lat.RemoveRange(_start[^1], points);
                return;
            }
            _start.Add(_lon.Count);
            _class.Add(_current);
            _rural.Add(_currentRural);
        }
    }
}
