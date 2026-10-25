using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// One photo texture served level by level: what real data it has (the "mip index") and what is held in RAM.
/// Real data always covers a level and every smaller one: imagery fetched at the zoom of level k gives levels k
/// to the last. Levels finer than that are stand-ins, stretched from the best data at hand when X-Plane reads them.
/// </summary>
internal sealed class StreamedTexture
{
    public const int Unknown = -1;

    private readonly int _exactZoom0;

    public StreamedTexture(VirtualPath path, TextureName name, TexturePlan plan, int baseZoom)
    {
        Path = path;
        Name = name;
        Layout = DdsLayout.For(plan.SizeFor(name.Zoom));
        FetchZoom = plan.FetchZoom(name.Zoom);
        // The zoom whose tiles have exactly the texture's pixels across: 4096 px is 16 tiles at the file's own zoom.
        _exactZoom0 = name.Zoom + (int)Math.Log2(Layout.Size) - 12;
        (Lat, Lon) = name.Center;
        SizeM = name.GroundSizeM;
        BaseMip = Layout.Levels - 1;
        for (int mip = 0; mip < Layout.Levels; mip++)
            if (ZoomFor(mip) <= baseZoom)
            {
                BaseMip = mip;
                break;
            }
    }

    public VirtualPath Path { get; }
    public TextureName Name { get; }
    public DdsLayout Layout { get; }
    /// <summary>The zoom level 0 is built from, as the texture plan says (the user's chosen detail).</summary>
    public int FetchZoom { get; }
    public double Lat { get; }
    public double Lon { get; }
    public double SizeM { get; }
    /// <summary>The first level the low-zoom base covers: one zoom 12 tile for a ZL16 texture.</summary>
    public int BaseMip { get; }

    /// <summary>
    /// The zoom level <paramref name="mip"/> is fetched at: the plan's zoom for level 0, and for smaller levels the
    /// zoom whose tiles have just their pixels (level n of a 4096 px ZL16 texture is ZL16-n).
    /// </summary>
    public int ZoomFor(int mip) => mip == 0 ? FetchZoom : Math.Max(1, Math.Min(FetchZoom - mip, _exactZoom0 - mip));

    // Everything below is guarded by Gate.
    public Lock Gate { get; } = new();
    /// <summary>The first level with real data (Layout.Levels for none), or Unknown before the cache is looked at.</summary>
    public int RealFrom = Unknown;
    /// <summary>The real data is the low-zoom base's: made again from it, never saved as a chain.</summary>
    public bool RealFromBase;
    /// <summary>The real data in RAM: the BC1 bytes from level ChainFrom to the end of the file.</summary>
    public byte[]? Chain;
    public int ChainFrom;
    /// <summary>Stand-in levels made from the data of level StandInSource (Layout.Levels: no real data then).</summary>
    public readonly Dictionary<int, (byte[] Bytes, Served Kind)> StandIns = [];
    public int StandInSource = Unknown;
    public readonly Dictionary<int, (Task<bool> Task, FetchTicket Ticket)> Builds = [];
    /// <summary>When its latest build failed (0: it did not): reads get stand-ins for a while instead of new attempts.</summary>
    public long FailedAt;
    public long RamBytes;
    public long LastUsed;
    public DateTime Version = DateTime.UtcNow;
    /// <summary>The finest level of X-Plane's latest read and when it was, to measure the paging model.</summary>
    public int LastWanted = Unknown;
    public long LastReadAt;
    public bool LastPlaced;
    /// <summary>The finest level X-Plane has wanted of it, and when it last wanted any: the graphics memory it takes.</summary>
    public int FinestWanted = Unknown;
    public long WantedAt;
}
