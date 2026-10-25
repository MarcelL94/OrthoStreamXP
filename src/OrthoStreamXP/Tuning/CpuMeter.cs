using System.Diagnostics;
using System.Runtime.InteropServices;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tuning;

/// <summary>Windows' CPU time of every processor together, in 100 ns units: idle, and all of it (kernel time includes idle).</summary>
internal static partial class SystemCpu
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>(idle, total) so far, or null if Windows does not say.</summary>
    public static (long Idle, long Total)? Now() => GetSystemTimes(out long idle, out long kernel, out long user) ? (idle, kernel + user) : null;
}

/// <summary>
/// The CPU this process and all of Windows used, sampled every few seconds, and the memory the process holds. A
/// scenery load lasts about a minute, so the summary gives the busiest sample next to the average: the average of
/// two minutes hides it.
/// </summary>
internal sealed class CpuMeter
{
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Lock _gate = new();
    private (long Idle, long Total)? _system;
    private TimeSpan _processCpu;
    private long _sampledAt, _periodStarted;
    private int _fullCollections;
    private double _processSum, _systemSum, _processPeak, _systemPeak;
    private int _processSamples, _systemSamples;

    public CpuMeter()
    {
        _system = SystemCpu.Now();
        _processCpu = _process.TotalProcessorTime;
        _sampledAt = _periodStarted = Stopwatch.GetTimestamp();
        _fullCollections = GC.CollectionCount(2);
    }

    /// <summary>The share of every processor all of Windows used in the latest sample (0 to 1), null before one.</summary>
    public double? SystemBusy { get; private set; }

    /// <summary>Notes the CPU used since the previous sample.</summary>
    public void Sample()
    {
        _process.Refresh();
        TimeSpan cpu = _process.TotalProcessorTime;
        (long Idle, long Total)? system = SystemCpu.Now();
        long now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            double? process = Share(cpu - _processCpu, Stopwatch.GetElapsedTime(_sampledAt, now), Environment.ProcessorCount);
            double? windows = _system is { } before && system is { } after ? Busy(before, after) : null;
            (_processCpu, _system, _sampledAt) = (cpu, system, now);
            if (process is { } p)
            {
                _processSum += p;
                _processSamples++;
                _processPeak = Math.Max(_processPeak, p);
            }
            if (windows is { } w)
            {
                _systemSum += w;
                _systemSamples++;
                _systemPeak = Math.Max(_systemPeak, w);
                SystemBusy = w;
            }
        }
    }

    /// <summary>One log line about the time since the previous one (or the start), which begins the next.</summary>
    public string Summary()
    {
        string process, windows;
        TimeSpan period;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            period = Stopwatch.GetElapsedTime(_periodStarted, now);
            process = Describe(_processSum, _processSamples, _processPeak);
            windows = Describe(_systemSum, _systemSamples, _systemPeak);
            (_processSum, _processSamples, _processPeak, _systemSum, _systemSamples, _systemPeak) = (0, 0, 0, 0, 0, 0);
            _periodStarted = now;
        }
        int fullCollections = GC.CollectionCount(2), since = fullCollections - Interlocked.Exchange(ref _fullCollections, fullCollections);
        return $"Load over the last {period.TotalMinutes:0.#} min (share of all {Environment.ProcessorCount} logical processors): OrthoStreamXP CPU {process}, " +
               $"all of Windows {windows}; memory: {Memory(GC.GetGCMemoryInfo(), Environment.WorkingSet)}, " +
               $"{since} full garbage collection{(since == 1 ? "" : "s")}";
    }

    /// <summary>
    /// The process's memory in RAM (working set) and the .NET heap as the latest collection left it: how much of it is
    /// free gaps between live objects, and the large objects (85 KB and up: tiles, pictures, chains), which are not
    /// moved to close their gaps unless the collector decides to compact them.
    /// </summary>
    internal static string Memory(GCMemoryInfo memory, long workingSet)
    {
        ReadOnlySpan<GCGenerationInfo> generations = memory.GenerationInfo;
        const int largeObjects = 3; // gen0, gen1, gen2, then the large object heap
        string large = generations.Length > largeObjects
            ? $", large objects {Bytes(generations[largeObjects].SizeAfterBytes)} ({Bytes(generations[largeObjects].FragmentationAfterBytes)} free)"
            : "";
        return $"working set {Bytes(workingSet)}; .NET heap {Bytes(memory.HeapSizeBytes)} after the last collection, " +
               $"{Bytes(memory.FragmentedBytes)} of it free gaps{large}, {Bytes(memory.TotalCommittedBytes)} committed";

        static string Bytes(long n) => CacheInventory.FormatBytes(n);
    }

    private static string Describe(double sum, int samples, double peak) =>
        samples == 0 ? "not measured" : $"{sum / samples:P0} average, {peak:P0} at the busiest {FlightRecorder.Interval.TotalSeconds:0} s";

    /// <summary>The share of the time between two readings of <see cref="SystemCpu"/> the processors were not idle.</summary>
    internal static double? Busy((long Idle, long Total) before, (long Idle, long Total) after)
    {
        long total = after.Total - before.Total;
        return total <= 0 ? null : Math.Clamp(1 - (double)(after.Idle - before.Idle) / total, 0, 1);
    }

    /// <summary>The share of <paramref name="processors"/> that <paramref name="cpu"/> of CPU time in <paramref name="elapsed"/> is.</summary>
    internal static double? Share(TimeSpan cpu, TimeSpan elapsed, int processors) =>
        elapsed <= TimeSpan.Zero || processors < 1 ? null : Math.Clamp(cpu / (elapsed * processors), 0, 1);
}
