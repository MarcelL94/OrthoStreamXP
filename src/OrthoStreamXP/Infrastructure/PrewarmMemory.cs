using System.Diagnostics;

namespace OrthoStreamXP;

/// <summary>
/// Gives back the memory the ground pre-warm's builds left behind. In the EDDV to ENGM flight the working set stayed at
/// 4 GB (3.5 GB of it the large-object heap: build buffers) for the 24 minutes from the end of the pre-warm to the
/// takeoff, while the streamer held 0.9 GB of textures. One compacting collection once it is all built, while the
/// aircraft still stands at the gate, gives that back; it runs at most once a session, off the warmer's thread.
/// </summary>
internal sealed class PrewarmMemory
{
    private int _done;

    /// <summary>The ground pre-warm's work is all built (<see cref="Scenery.MipWarmer.GroundDone"/>).</summary>
    public void OnGroundDone()
    {
        if (Interlocked.Exchange(ref _done, 1) != 0) return;
        _ = Task.Run(Compact);
    }

    private static void Compact()
    {
        long before = Environment.WorkingSet;
        long started = Stopwatch.GetTimestamp();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        Log.Info($"Ground pre-warm: everything is built; unused build memory given back (working set {Gb(before)} -> {Gb(Environment.WorkingSet)}, " +
                 $"{Stopwatch.GetElapsedTime(started).TotalMilliseconds:0} ms)");
    }

    private static string Gb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
}
