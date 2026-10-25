namespace OrthoStreamXP.Textures;

/// <summary>
/// Like <see cref="PriorityGate"/>, but each entry takes its own share of two budgets (the memory a build holds and
/// the tiles it downloads) as well as one of at most <paramref name="maxEntries"/> places, and must fit all of them.
/// Waiters are let in by priority (lowest first, then in arrival order) and never past the first one that does not
/// fit: small work of the same or lower priority cannot keep a big waiting build out for ever, while more urgent work
/// still goes first. An entry bigger than a whole budget is let in when nothing else runs.
/// </summary>
internal sealed class WeightedPriorityGate(long budget, int maxEntries, long secondBudget = long.MaxValue)
{
    private sealed class Waiter(long weight, long secondWeight, Func<int> priority, long order)
    {
        public long Weight { get; } = weight;
        public long SecondWeight { get; } = secondWeight;
        public Func<int> Priority { get; } = priority;
        public long Order { get; } = order;
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiting = [];
    private long _used, _secondUsed, _arrivals;
    private int _entries;

    public long Budget { get; } = Math.Max(1, budget);
    public long SecondBudget { get; } = Math.Max(1, secondBudget);
    public int MaxEntries { get; } = Math.Max(1, maxEntries);

    /// <summary>Waiters not yet let in.</summary>
    public int Waiting
    {
        get { lock (_gate) return _waiting.Count; }
    }

    /// <summary>The first budget in use now.</summary>
    public long Used
    {
        get { lock (_gate) return _used; }
    }

    /// <summary>The second budget in use now.</summary>
    public long SecondUsed
    {
        get { lock (_gate) return _secondUsed; }
    }

    /// <summary>What keeps the most urgent waiter out now: <see cref="GateLimit.None"/> when nobody waits.</summary>
    public GateLimit Limit
    {
        get
        {
            lock (_gate)
            {
                if (_waiting.Count == 0) return GateLimit.None;
                Waiter next = _waiting.MinBy(w => (w.Priority(), w.Order))!;
                if (_entries >= MaxEntries) return GateLimit.Entries;
                if (_used + next.Weight > Budget) return GateLimit.First;
                return _secondUsed + next.SecondWeight > SecondBudget ? GateLimit.Second : GateLimit.None;
            }
        }
    }

    /// <summary>
    /// Waits until <paramref name="weight"/> of the first budget, <paramref name="secondWeight"/> of the second and a
    /// place are free; give them back with <see cref="Release"/>.
    /// </summary>
    public Task WaitAsync(long weight, long secondWeight, Func<int> priority)
    {
        List<Waiter> admitted;
        Waiter waiter;
        lock (_gate)
        {
            waiter = new Waiter(Math.Max(0, weight), Math.Max(0, secondWeight), priority, _arrivals++);
            _waiting.Add(waiter);
            admitted = Admit();
        }
        foreach (Waiter next in admitted) next.Admitted.SetResult();
        return waiter.Admitted.Task;
    }

    public Task WaitAsync(long weight, Func<int> priority) => WaitAsync(weight, 0, priority);

    public Task WaitAsync(long weight, long secondWeight, FetchTicket ticket) => WaitAsync(weight, secondWeight, () => ticket.Priority);

    public Task WaitAsync(long weight, FetchTicket ticket) => WaitAsync(weight, 0, () => ticket.Priority);

    public void Release(long weight, long secondWeight = 0)
    {
        List<Waiter> admitted;
        lock (_gate)
        {
            _used -= Math.Max(0, weight);
            _secondUsed -= Math.Max(0, secondWeight);
            _entries--;
            admitted = Admit();
        }
        foreach (Waiter next in admitted) next.Admitted.SetResult();
    }

    /// <summary>Lets waiters in, most urgent first, until the next one does not fit. Called under the lock.</summary>
    private List<Waiter> Admit()
    {
        var admitted = new List<Waiter>();
        if (_waiting.Count == 0) return admitted;
        // Priorities can rise while work waits, so they are read now.
        var queue = _waiting.Select(w => (Waiter: w, Priority: w.Priority())).OrderBy(w => w.Priority).ThenBy(w => w.Waiter.Order).ToList();
        foreach (var (next, _) in queue)
        {
            bool fits = _entries == 0
                        || (_entries < MaxEntries && _used + next.Weight <= Budget && _secondUsed + next.SecondWeight <= SecondBudget);
            if (!fits) break;
            _used += next.Weight;
            _secondUsed += next.SecondWeight;
            _entries++;
            _waiting.Remove(next);
            admitted.Add(next);
        }
        return admitted;
    }
}
