using System.Buffers.Binary;

namespace OrthoStreamXP.Tests;

/// <summary>
/// Writes small uncompressed DSF files with road networks for the night lights' tests: one 32-bit point pool (longitude,
/// latitude and two more planes, each in a different encoding) and the chains as range, 16-bit and 32-bit commands,
/// with a few polygon commands among them that a reader must skip.
/// </summary>
internal sealed class DsfBuilder(int south, int west)
{
    // A point no road uses first: the chains' 16-bit indices are written from a junction offset of 1.
    private readonly List<(double Lon, double Lat)> _points = [(west, south)];
    private readonly List<(int Subtype, int First, int Count, ChainForm Form)> _chains = [];

    public enum ChainForm
    {
        Range,
        List16,
        List32,
    }

    /// <summary>A road of <paramref name="subtype"/> through the points (longitude, latitude in degrees).</summary>
    public DsfBuilder Road(int subtype, params (double Lon, double Lat)[] points) => Road(subtype, ChainForm.Range, points);

    public DsfBuilder Road(int subtype, ChainForm form, params (double Lon, double Lat)[] points)
    {
        _chains.Add((subtype, _points.Count, points.Length, form));
        _points.AddRange(points);
        return this;
    }

    public byte[] Build()
    {
        var file = new List<byte>();
        file.AddRange("XPLNEDSF"u8.ToArray());
        file.AddRange(Int(1));
        file.AddRange(Atom("HEAD", Atom("PROP", Strings("sim/overlay", "1", "sim/south", $"{south}", "sim/west", $"{west}"))));
        file.AddRange(Atom("DEFN", [.. Atom("TERT", []), .. Atom("POLY", Strings("lib/g8/beaches.bch")), .. Atom("NETW", Strings("lib/g10/roads.net"))]));
        file.AddRange(Atom("GEOD", [.. Atom("POOL", Pool16()), .. Atom("SCAL", [.. Float(1), .. Float(0)]), .. Atom("PO32", Pool32()), .. Atom("SC32", Scales())]));
        file.AddRange(Atom("CMDS", Commands()));
        file.AddRange(new byte[16]); // the MD5, not checked
        return [.. file];
    }

    private static uint Raw(double degrees, int corner) => (uint)Math.Round((degrees - corner) * 4294967295.0);

    /// <summary>The road points: longitude raw, latitude differenced and run-length coded, then two planes a reader skips.</summary>
    private byte[] Pool32()
    {
        var data = new List<byte>();
        data.AddRange(Int(_points.Count));
        data.Add(4);
        data.Add(0); // raw
        foreach (var (lon, _) in _points) data.AddRange(UInt(Raw(lon, west)));
        data.Add(3); // differenced and run-length coded: every value a literal run of one
        uint previous = 0;
        foreach (var (_, lat) in _points)
        {
            uint value = Raw(lat, south);
            data.Add(1);
            data.AddRange(UInt(unchecked(value - previous)));
            previous = value;
        }
        data.Add(2); // run-length: the whole plane one repeated value
        for (int left = _points.Count; left > 0; left -= Math.Min(left, 127))
        {
            data.Add((byte)(0x80 | Math.Min(left, 127)));
            data.AddRange(UInt(7));
        }
        data.Add(1); // differenced
        foreach (var _ in _points) data.AddRange(UInt(1));
        return [.. data];
    }

    private static byte[] Pool16() => [.. Int(1), 1, 0, .. BitConverter.GetBytes((ushort)5)];

    /// <summary>Each plane's scale and offset, as X-Plane writes them: longitude and latitude from the tile's corner.</summary>
    private byte[] Scales() => [.. Float(1), .. Float(west), .. Float(1), .. Float(south), .. Float(65535), .. Float(-32768), .. Float(0), .. Float(0)];

    private byte[] Commands()
    {
        var c = new List<byte>();
        // A few polygon commands first: a reader must step over them.
        c.AddRange([1, 0, 0]); // pool select 0
        c.AddRange([3, 0]); // definition 0
        c.AddRange([12, 0xFF, 0x00, 2, 0, 0, 0, 0]); // polygon: parameter, 2 indices
        c.AddRange([13, 0xFF, 0x00, 0, 0, 1, 0]); // polygon range
        c.AddRange([14, 0xFF, 0x00, 2, 1, 0, 0, 2, 0, 0, 0, 0]); // nested polygon: 2 windings of 1 and 2 indices
        c.AddRange([15, 0xFF, 0x00, 2, 0, 0, 1, 0, 2, 0]); // nested polygon range: 2 windings, 3 indices
        c.AddRange([32, 3, (byte)'a', (byte)'b', (byte)'c']); // comment
        // The roads, from a junction offset so 16-bit indices reach every point.
        const uint offset = 1;
        c.AddRange([2, .. UInt(offset)]);
        foreach (var (subtype, first, count, form) in _chains)
        {
            c.AddRange([6, (byte)subtype]);
            switch (form)
            {
                case ChainForm.Range:
                    c.Add(10);
                    c.AddRange(BitConverter.GetBytes((ushort)(first - offset)));
                    c.AddRange(BitConverter.GetBytes((ushort)(first + count - offset)));
                    break;
                case ChainForm.List16:
                    c.AddRange([9, (byte)count]);
                    for (int i = 0; i < count; i++) c.AddRange(BitConverter.GetBytes((ushort)(first + i - offset)));
                    break;
                case ChainForm.List32:
                    c.AddRange([11, (byte)count]);
                    for (int i = 0; i < count; i++) c.AddRange(UInt((uint)(first + i)));
                    break;
            }
        }
        return [.. c];
    }

    private static byte[] Atom(string id, byte[] body)
    {
        byte[] atom = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(atom, BinaryPrimitives.ReadUInt32BigEndian(System.Text.Encoding.ASCII.GetBytes(id)));
        BinaryPrimitives.WriteInt32LittleEndian(atom.AsSpan(4), atom.Length);
        body.CopyTo(atom, 8);
        return atom;
    }

    private static byte[] Strings(params string[] values) => [.. values.SelectMany(v => System.Text.Encoding.ASCII.GetBytes(v + "\0"))];

    private static byte[] Int(int value) => BitConverter.GetBytes(value);

    private static byte[] UInt(uint value) => BitConverter.GetBytes(value);

    private static byte[] Float(float value) => BitConverter.GetBytes(value);
}
