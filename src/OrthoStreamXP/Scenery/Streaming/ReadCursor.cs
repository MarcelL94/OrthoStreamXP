namespace OrthoStreamXP.Scenery;

/// <summary>
/// The levels one open handle has been given, so a level X-Plane reads in several pieces is all from the same
/// version even if better data arrives in between (a BC1 block split between two versions would be garbage).
/// </summary>
internal sealed class ReadCursor
{
    private readonly Dictionary<int, (ReadOnlyMemory<byte> Bytes, Served Kind)> _levels = [];

    /// <summary>Where X-Plane's first chunk of the file (the header and the start of level 0) ended on this handle.</summary>
    public long? HeaderChunkEnd { get; set; }

    /// <summary>Where the latest read on this handle ended: a read starting there is the next piece of the same read.</summary>
    public long LastEnd { get; set; } = -1;

    /// <summary>
    /// The latest level X-Plane wanted on this handle was kept to the low-zoom base at night: the next pieces of the
    /// same read are counted with it, as night reads, not as stand-ins.
    /// </summary>
    public bool KeptToBase { get; set; }

    public bool TryGet(int mip, out ReadOnlyMemory<byte> bytes, out Served kind)
    {
        lock (_levels)
        {
            bool found = _levels.TryGetValue(mip, out var level);
            (bytes, kind) = level;
            return found;
        }
    }

    public bool Has(int mip)
    {
        lock (_levels) return _levels.ContainsKey(mip);
    }

    public void Set(int mip, ReadOnlyMemory<byte> bytes, Served kind)
    {
        lock (_levels) _levels[mip] = (bytes, kind);
    }
}
