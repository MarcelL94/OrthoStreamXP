namespace OrthoStreamXP.Scenery;

/// <summary>
/// One streaming session's SimBrief side: the flight plan (fetched once as streaming starts: no polling) and the
/// departure prefetch. Failures are only logged: SimBrief never fails streaming.
/// </summary>
internal sealed class SimBriefSession
{
    private readonly Func<CancellationToken, Task<SimBriefAnswer>> _fetch;
    private readonly DeparturePrefetch _departure;
    private readonly CancellationToken _stop;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly List<Task> _work = [];
    private FlightPlan? _current;
    private string _firstNoPlan = "";

    /// <param name="fetch">Asks SimBrief for the latest flight plan (<see cref="SimBriefClient.FetchAsync"/>).</param>
    /// <param name="aircraft">Where the aircraft is now, when X-Plane has sent a position.</param>
    public SimBriefSession(Func<CancellationToken, Task<SimBriefAnswer>> fetch, IReadOnlyList<DepartureTarget> departureTargets,
        Func<(double Lat, double Lon)?> aircraft, double radiusKm, TimeProvider? clock = null, CancellationToken stop = default)
    {
        _fetch = fetch;
        _clock = clock ?? TimeProvider.System;
        _departure = new DeparturePrefetch(aircraft, departureTargets, radiusKm, _clock);
        _stop = stop;
        FirstPlan = FetchFirstAsync();
    }

    /// <summary>The plan fetched as streaming started (null when there was none).</summary>
    public Task<FlightPlan?> FirstPlan { get; }

    /// <summary>The plan in use now.</summary>
    public FlightPlan? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>Starts the departure prefetch for the first plan, in the background.</summary>
    public void Start()
    {
        lock (_gate) _work.Add(PrefetchDepartureAsync(FirstPlan));
    }

    /// <summary>Everything still running in the background; the session awaits it before the packages go away.</summary>
    public Task WhenDone()
    {
        lock (_gate) return Task.WhenAll([.. _work]);
    }

    private async Task<FlightPlan?> FetchFirstAsync()
    {
        SimBriefAnswer answer = await FetchAsync().ConfigureAwait(false);
        if (answer.Problem is { } problem) Log.Warning($"SimBrief: {problem}");
        lock (_gate)
        {
            _current ??= answer.Plan;
            _firstNoPlan = answer.NoPlan;
        }
        return answer.Plan;
    }

    /// <summary>
    /// The plan fetched as streaming started, in the window's words ("Flight plan: EDDV → ENGM, generated 12 min ago."),
    /// or why there is none: Home shows it while the SimBrief switch is on.
    /// </summary>
    public async Task<string> DescribeFirstAsync()
    {
        try
        {
            if (await FirstPlan.ConfigureAwait(false) is not { } plan)
                lock (_gate) return _firstNoPlan;
            return $"Flight plan: {plan.Describe()}{Age(plan)}.";
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return "Streaming stopped.";
        }
    }

    /// <summary>
    /// Asks SimBrief. The client answers its failures as a problem; anything it throws besides the stop (a bug) becomes
    /// one too, so the plan never ends faulted (the stop awaits it).
    /// </summary>
    private async Task<SimBriefAnswer> FetchAsync()
    {
        try
        {
            return await _fetch(_stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The flight plan is a hint: whatever went wrong is logged as "no plan", never a reason for streaming to fail.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return SimBriefAnswer.Failed($"the flight plan could not be read ({ex.GetType().Name})");
        }
    }

    /// <summary>The departure prefetch for a plan once it is there; a failure is only logged.</summary>
    private async Task PrefetchDepartureAsync(Task<FlightPlan?> plan)
    {
        try
        {
            FlightPlan? flight = await plan.WaitAsync(_stop).ConfigureAwait(false);
            await Task.Run(() => _departure.Run(flight, _stop), _stop).ConfigureAwait(false); // reads DSFs: off the caller's thread
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped before the flight plan arrived.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"SimBrief: departure prefetch failed ({ex.Message})");
        }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested)
        {
            // Streaming stopped while a package was queuing.
        }
#pragma warning disable CA1031 // Anything else is a bug, but on a background task nobody would see it: log it, don't lose it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Log.Warning($"SimBrief: departure prefetch failed unexpectedly ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>", generated 12 min ago" when the plan says when.</summary>
    private string Age(FlightPlan plan) => plan.AgeAt(_clock.GetUtcNow()) is not { } age ? ""
        : age < TimeSpan.FromHours(1) ? $", generated {Math.Max(0, age.TotalMinutes):0} min ago"
        : $", generated {age.TotalHours:0} h ago";
}
