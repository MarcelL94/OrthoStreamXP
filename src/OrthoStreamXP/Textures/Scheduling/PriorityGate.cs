namespace OrthoStreamXP.Textures;

/// <summary>
/// A semaphore whose waiters are let in by priority (lowest first, then in arrival order). A waiter's priority
/// is read when a slot frees up, so work raised while it waits moves up the queue.
/// </summary>
internal sealed class PriorityGate(int slots)
{
    private sealed class Waiter(Func<int> priority)
    {
        public Func<int> Priority { get; } = priority;
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiting = [];
    private int _free = slots;

    /// <summary>Waiters not yet admitted.</summary>
    public int Waiting
    {
        get { lock (_gate) return _waiting.Count; }
    }

    public Task WaitAsync(Func<int> priority)
    {
        lock (_gate)
        {
            if (_free > 0)
            {
                _free--;
                return Task.CompletedTask;
            }
            var waiter = new Waiter(priority);
            _waiting.Add(waiter);
            return waiter.Admitted.Task;
        }
    }

    public Task WaitAsync(FetchTicket ticket) => WaitAsync(() => ticket.Priority);

    public void Release()
    {
        Waiter next;
        lock (_gate)
        {
            if (_waiting.Count == 0)
            {
                _free++;
                return;
            }
            int best = 0, bestPriority = _waiting[0].Priority();
            for (int i = 1; i < _waiting.Count; i++)
            {
                int priority = _waiting[i].Priority();
                if (priority < bestPriority) (best, bestPriority) = (i, priority);
            }
            next = _waiting[best];
            _waiting.RemoveAt(best);
        }
        next.Admitted.SetResult(); // the slot passes straight to the next waiter
    }
}
