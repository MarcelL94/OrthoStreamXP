using System.Diagnostics;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP;

/// <summary>
/// Gives back the memory the ground pre-warm's builds left behind. In the EDDV to ENGM flight the working set stayed at
/// 4 GB (3.5 GB of it the large-object heap: build buffers) for the 24 minutes from the end of the pre-warm to the
/// takeoff, while the streamer held 0.9 GB of textures. One compacting collection once every package's pre-warm is
/// built, while the aircraft still stands at the gate, gives that back; at most once a session, off the warmer's thread.
/// </summary>
internal sealed class PrewarmMemory
{
    private readonly IReadOnlyList<MipWarmer> _warmers;
    private int _done;

    private PrewarmMemory(IReadOnlyList<MipWarmer> warmers) => _warmers = warmers;

    /// <summary>Compacts once every one of <paramref name="warmers"/> has built its ground pre-warm.</summary>
    public static void Watch(IReadOnlyList<MipWarmer> warmers)
    {
        var memory = new PrewarmMemory(warmers);
        foreach (MipWarmer warmer in warmers) warmer.GroundDone += memory.OnGroundDone;
    }

    private void OnGroundDone()
    {
        if (_warmers.Any(w => w.GroundPending)) return; // another package still builds
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _ = Task.Run(() =>
        {
            long before = Environment.WorkingSet, started = Stopwatch.GetTimestamp();
            GiveBack();
            Log.Info($"Ground pre-warm: everything is built; unused build memory given back (working set {CacheInventory.FormatBytes(before)} -> " +
                     $"{CacheInventory.FormatBytes(Environment.WorkingSet)}, {Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms)");
        });
    }

    /// <summary>
    /// The large build and texture arrays: an ordinary collection frees them but keeps the memory for reuse; an aggressive
    /// one gives it back to Windows (also used when streaming stops).
    /// </summary>
    public static void GiveBack() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
}
