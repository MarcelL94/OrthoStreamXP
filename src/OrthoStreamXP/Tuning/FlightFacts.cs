using System.Runtime.InteropServices;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tuning;

/// <summary>
/// What one session measured, for <see cref="Tuner"/>: the streamers' answers and waits, the downloads, and samples
/// taken every few seconds of how busy the downloads and builds were and how much memory Windows had left.
/// Shares are of the samples taken while X-Plane was sending the aircraft position (in flight).
/// </summary>
internal sealed record FlightFacts
{
    public double MinutesRunning { get; init; }
    public double MinutesInFlight { get; init; }
    public StreamingFacts? Streaming { get; init; }
    public long Tiles { get; init; }
    public long FailedTiles { get; init; }
    public long Attempts { get; init; }
    public long TroubledAttempts { get; init; }
    public double MeanTileSeconds { get; init; }
    /// <summary>Share of the flight with every download worker busy and more downloads waiting.</summary>
    public double DownloadsQueuedShare { get; init; }
    /// <summary>Share of the flight with X-Plane's builds held back by the build memory.</summary>
    public double BuildsWaitedForMemoryShare { get; init; }
    /// <summary>Share of the flight with X-Plane's builds held back by the build tiles.</summary>
    public double BuildsWaitedForTilesShare { get; init; }
    public long TotalMemoryBytes { get; init; }
    /// <summary>The least physical memory Windows had free during the session.</summary>
    public long MinAvailableMemoryBytes { get; init; }
    /// <summary>The physical memory OrthoStreamXP itself held (its working set) at that moment; 0 when not measured.</summary>
    public long OwnMemoryAtMinBytes { get; init; }
}

/// <summary>Samples, every few seconds while a mount runs, what <see cref="FlightFacts"/> needs over time.</summary>
internal sealed class FlightRecorder
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly Lock _gate = new();
    private readonly DateTime _started = DateTime.UtcNow;
    private int _inFlight, _queued, _waitedForMemory, _waitedForTiles;
    private long _totalMemory, _minAvailable = long.MaxValue, _ownAtMin;

    /// <param name="inFlight">X-Plane is sending the aircraft position.</param>
    /// <param name="downloadsQueued">Every download worker is busy and more downloads wait.</param>
    /// <param name="buildLimit">What holds X-Plane's builds back now.</param>
    /// <param name="memory">Windows' physical memory (total, available), when known.</param>
    /// <param name="ownMemory">The physical memory this process holds (its working set).</param>
    public void Add(bool inFlight, bool downloadsQueued, GateLimit buildLimit, (long Total, long Available)? memory, long ownMemory = 0)
    {
        lock (_gate)
        {
            if (memory is { } m)
            {
                _totalMemory = m.Total;
                if (m.Available < _minAvailable) (_minAvailable, _ownAtMin) = (m.Available, ownMemory);
            }
            if (!inFlight) return;
            _inFlight++;
            if (downloadsQueued) _queued++;
            if (buildLimit == GateLimit.First) _waitedForMemory++;
            if (buildLimit == GateLimit.Second) _waitedForTiles++;
        }
    }

    public FlightFacts Facts(StreamingFacts? streaming, TileSource? tiles)
    {
        lock (_gate)
        {
            double Share(int n) => _inFlight == 0 ? 0 : (double)n / _inFlight;
            return new FlightFacts
            {
                MinutesRunning = (DateTime.UtcNow - _started).TotalMinutes,
                MinutesInFlight = _inFlight * Interval.TotalMinutes,
                Streaming = streaming,
                Tiles = tiles?.TilesDownloaded ?? 0,
                FailedTiles = tiles?.FailedTiles ?? 0,
                Attempts = tiles?.AttemptsMade ?? 0,
                TroubledAttempts = tiles?.TroubledAttempts ?? 0,
                MeanTileSeconds = tiles?.MeanTileSeconds ?? 0,
                DownloadsQueuedShare = Share(_queued),
                BuildsWaitedForMemoryShare = Share(_waitedForMemory),
                BuildsWaitedForTilesShare = Share(_waitedForTiles),
                TotalMemoryBytes = _totalMemory,
                MinAvailableMemoryBytes = _minAvailable == long.MaxValue ? 0 : _minAvailable,
                OwnMemoryAtMinBytes = _ownAtMin,
            };
        }
    }
}

/// <summary>Windows' physical memory.</summary>
internal static partial class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    /// <summary>(total, available) bytes, or null if Windows does not say.</summary>
    public static (long Total, long Available)? Now()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : null;
    }
}
