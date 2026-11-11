namespace OrthoStreamXP.Scenery;

/// <summary>
/// One streaming session's SimBrief side: the flight plan (fetched once as streaming starts, and again only when the
/// pilot presses "Reload SimBrief flight plan" on Home: no polling), the departure prefetch, and the ground pre-warm.
/// A reload with a newer plan drops the old plan's requests still waiting, prefetches the new departure when it moved,
/// and watches for the new flight's gate from the start. Failures are only logged: SimBrief never fails streaming.
/// </summary>
internal sealed class SimBriefSession
{
    private readonly Func<CancellationToken, Task<SimBriefAnswer>> _fetch;
    private readonly IReadOnlyList<DepartureTarget> _departureTargets;
    private readonly IReadOnlyList<GroundTarget> _groundTargets;
    private readonly Func<(double Lat, double Lon)?> _aircraft;
    private readonly double _radiusKm;
    private readonly CancellationToken _stop;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly List<Task> _work = [];
    private FlightPlan? _current;
    private Task<string>? _reload;

    /// <param name="fetch">Asks SimBrief for the latest flight plan (<see cref="SimBriefClient.FetchAsync"/>).</param>
    /// <param name="groundTargets">The packages the ground pre-warm queues in; empty: no ground pre-warm.</param>
    /// <param name="aircraft">Where the aircraft is now, when X-Plane has sent a position.</param>
    /// <param name="run">Runs the ground pre-warm's queuing (tests run it inline).</param>
    public SimBriefSession(Func<CancellationToken, Task<SimBriefAnswer>> fetch, IReadOnlyList<DepartureTarget> departureTargets,
        IReadOnlyList<GroundTarget> groundTargets, Func<(double Lat, double Lon)?> aircraft, double radiusKm,
        TimeProvider? clock = null, Func<Action, Task>? run = null, CancellationToken stop = default)
    {
        _fetch = fetch;
        _departureTargets = departureTargets;
        _groundTargets = groundTargets;
        _aircraft = aircraft;
        _radiusKm = radiusKm;
        _clock = clock ?? TimeProvider.System;
        _stop = stop;
        FirstPlan = FetchFirstAsync();
        if (groundTargets.Count > 0) Ground = new GroundPrewarm(FirstPlan, groundTargets, run, stop);
    }

    /// <summary>The plan fetched as streaming started (null when there was none).</summary>
    public Task<FlightPlan?> FirstPlan { get; }

    public GroundPrewarm? Ground { get; }

    /// <summary>The plan in use now: the first, or the latest reloaded.</summary>
    public FlightPlan? Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    /// <summary>Starts the departure prefetch for the first plan, in the background.</summary>
    public void Start() => Track(PrefetchDepartureAsync(FirstPlan.WaitAsync));

    /// <summary>Every position X-Plane sends (from its UDP thread).</summary>
    public void OnCamera(CameraReport camera) => Ground?.OnCamera(camera);

    /// <summary>
    /// The Home page's "Reload SimBrief flight plan": asks SimBrief again and says what came of it, in the window's
    /// words. A press while a reload runs gets that reload's answer (one request).
    /// </summary>
    public Task<string> ReloadAsync()
    {
        lock (_gate)
        {
            if (_reload is { IsCompleted: false } running) return running;
            _reload = ReloadOnceAsync();
            _work.Add(_reload);
            return _reload;
        }
    }

    /// <summary>Everything still running in the background; the session awaits it before the packages go away.</summary>
    public Task WhenDone()
    {
        lock (_gate) return Task.WhenAll([.. _work, Ground?.Running ?? Task.CompletedTask]);
    }

    private void Track(Task task)
    {
        lock (_gate) _work.Add(task);
    }

    private async Task<FlightPlan?> FetchFirstAsync()
    {
        SimBriefAnswer answer = await _fetch(_stop).ConfigureAwait(false);
        if (answer.Problem is { } problem) Log.Warning($"SimBrief: {problem}");
        lock (_gate) _current ??= answer.Plan;
        return answer.Plan;
    }

    private async Task<string> ReloadOnceAsync()
    {
        try
        {
            await FirstPlan.ConfigureAwait(false); // its answer first: the reload compares against it
            SimBriefAnswer answer = await _fetch(_stop).ConfigureAwait(false);
            if (answer.Plan is not { } plan)
            {
                Log.Info($"SimBrief: reloading the flight plan: {answer.Problem}");
                return $"No flight plan: {answer.Problem?.TrimEnd('.')}.";
            }
            FlightPlan? old;
            lock (_gate)
            {
                old = _current;
                _current = plan;
            }
            string flight = Describe(plan);
            if (plan.SameOfp(old))
            {
                Log.Info($"SimBrief: reloaded the flight plan; unchanged ({flight})");
                return $"Unchanged: {flight} is still your latest flight plan{Age(plan)}.";
            }
            bool moved = old is null || !FlightPlan.SamePlace(old.Origin, plan.Origin);
            int ground = _groundTargets.Sum(t => t.Cancel());
            int departure = moved ? _groundTargets.Sum(t => t.CancelDeparture()) : 0;
            Ground?.UsePlan(plan);
            Log.Info($"SimBrief: new flight plan {flight}{Age(plan)}; the old one's textures still waiting were dropped " +
                     $"({ground} of the ground pre-warm{(moved ? $", {departure} of the departure" : "")})");
            if (moved) await PrefetchDepartureAsync(_ => Task.FromResult<FlightPlan?>(plan)).ConfigureAwait(false);
            return $"Loaded {flight}{Age(plan)}." + (moved ? " Its departure is being built." : "");
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return "Streaming stopped.";
        }
    }

    /// <summary>The departure prefetch for a plan; a failure is only logged.</summary>
    private async Task PrefetchDepartureAsync(Func<CancellationToken, Task<FlightPlan?>> plan)
    {
        try
        {
            await new DeparturePrefetch(plan, _aircraft, _departureTargets, _radiusKm, _clock).RunAsync(_stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            // Stopped before the flight plan arrived.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"SimBrief: departure prefetch failed ({ex.Message})");
        }
    }

    /// <summary>"EDDH → EGPH" (or the departure alone).</summary>
    internal static string Describe(FlightPlan plan)
    {
        string from = plan.Origin.Icao.Length > 0 ? plan.Origin.Icao : "your departure";
        return plan.Destination is { Icao.Length: > 0 } to ? $"{from} → {to.Icao}" : from;
    }

    /// <summary>", generated 12 min ago" when the plan says when.</summary>
    private string Age(FlightPlan plan) => plan.AgeAt(_clock.GetUtcNow()) is not { } age ? ""
        : age < TimeSpan.FromHours(1) ? $", generated {Math.Max(0, age.TotalMinutes):0} min ago"
        : $", generated {age.TotalHours:0} h ago";
}
