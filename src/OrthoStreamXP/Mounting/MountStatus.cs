using Fsp;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP;

/// <summary>A snapshot of a running mount, for the window's live status. Counts are for this session.</summary>
internal sealed record MountStatus
{
    public required IReadOnlyList<PackagePlan> Packages { get; init; }
    /// <summary>Textures X-Plane asked for, by how they were answered.</summary>
    public long Requested { get; init; }
    public long ReadyByPrefetch { get; init; }
    public long ReadyInCache { get; init; }
    public long WaitedForPrefetch { get; init; }
    public long BuiltOnRequest { get; init; }
    public long Provisional { get; init; }
    public long Unavailable { get; init; }
    /// <summary>Streaming: X-Plane's reads of texture data, and how many of them got real imagery at every level read.</summary>
    public long StreamedReads { get; init; }
    public long RealReads { get; init; }
    /// <summary>Background builds queued or running.</summary>
    public int Building { get; init; }
    public long TilesDownloaded { get; init; }
    public long BytesDownloaded { get; init; }
    public long FailedTiles { get; init; }
    /// <summary>The latest aircraft position, when X-Plane has sent one.</summary>
    public AircraftReport? Aircraft { get; init; }
}
