namespace OrthoStreamXP.Textures;

/// <summary>The urgency of one piece of work; it can be raised while the work waits (never lowered).</summary>
internal sealed class FetchTicket(int priority)
{
    private int _priority = priority;

    public int Priority => Volatile.Read(ref _priority);

    public void Raise(int priority)
    {
        int current;
        do
        {
            current = Volatile.Read(ref _priority);
            if (priority >= current) return;
        } while (Interlocked.CompareExchange(ref _priority, priority, current) != current);
    }
}
