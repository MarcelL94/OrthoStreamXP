namespace OrthoStreamXP.Gui;

/// <summary>
/// One install or delete of a mesh region at a time in the whole window: the Packages page and the setup's list of regions
/// each have their own rows, and both write into the same packages folder and the one shared overlays package.
/// </summary>
internal sealed class InstallSlot
{
    private int _taken;

    /// <summary>An install or a delete is running.</summary>
    public bool Busy => Volatile.Read(ref _taken) == 1;

    /// <summary>Takes the slot; false when another install or delete has it.</summary>
    public bool TryTake() => Interlocked.CompareExchange(ref _taken, 1, 0) == 0;

    public void Release() => Volatile.Write(ref _taken, 0);
}
