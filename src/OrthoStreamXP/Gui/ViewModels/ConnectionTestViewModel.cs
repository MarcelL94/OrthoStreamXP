using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Gui;

/// <summary>
/// The connection test as the Settings page and the setup show it: a button, a line while it runs, then what it measured
/// and the downloads at once it suggests (<see cref="ConnectionTest"/>). The result is kept beside the settings file.
/// </summary>
internal sealed class ConnectionTestViewModel : ObservableObject
{
    private readonly Func<string> _providers;
    private readonly Func<bool> _canRun;
    private readonly Action<ConnectionResult> _measured;
    private readonly Action<int>? _use;
    private readonly Func<int>? _current;
    private bool _isRunning;
    private string? _status;
    private ConnectionResult? _result;

    /// <param name="providers">The texture-provider setting now ("bing,esri"): the first one is tested.</param>
    /// <param name="canRun">Whether the test may run (not while streaming: it would share the connection).</param>
    /// <param name="measured">Called with each result, after it is saved.</param>
    /// <param name="use">Sets downloads at once to the suggestion (null: the page sets it itself).</param>
    /// <param name="current">Downloads at once now, so the suggestion is offered only when it differs.</param>
    public ConnectionTestViewModel(Func<string> providers, Func<bool> canRun, Action<ConnectionResult> measured, Action<int>? use = null, Func<int>? current = null)
    {
        _providers = providers;
        _canRun = canRun;
        _measured = measured;
        _use = use;
        _current = current;
        _result = ConnectionResult.Load(ConnectionResult.PathFor(SettingsDocument.DefaultPath));
        _status = _result is null ? null : Describe(_result);
        Run = new RelayCommand(() => _ = RunAsync(), () => !IsRunning && _canRun());
        Use = new RelayCommand(() =>
        {
            if (_result is { Suggested: > 0 } r) _use?.Invoke(r.Suggested);
            Decide();
        }, () => CanUse);
    }

    public ICommand Run { get; }
    public ICommand Use { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!Set(ref _isRunning, value)) return;
            Raise(nameof(RunText));
            RelayCommand.Refresh();
        }
    }

    public string RunText => IsRunning ? "Testing…" : _result is null ? "Test my connection" : "Test again";

    /// <summary>What the test does now, or what it measured last.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>Whether the suggestion differs from the setting now, so "Use" has something to do.</summary>
    public bool CanUse => !IsRunning && _use is not null && _result is { Suggested: > 0, Decided: false } r && r.Suggested != (_current?.Invoke() ?? 0);

    /// <summary>
    /// Downloads at once were set, by "Use" or by hand: the person has decided, so the suggestion is not offered again
    /// until the next test (kept with the result, so it holds after a restart too).
    /// </summary>
    public void Decide()
    {
        if (_result is not { Decided: false } result) return;
        _result = result with { Decided = true };
        _result.Save(ConnectionResult.PathFor(SettingsDocument.DefaultPath));
        Raise(nameof(CanUse));
        ShowResult();
        RelayCommand.Refresh();
    }

    public string UseText => _result is { Suggested: > 0 } r ? $"Use {r.Suggested} at once" : "";

    public void Refresh()
    {
        Raise(nameof(CanUse));
        ShowResult();
        RelayCommand.Refresh();
    }

    /// <summary>The last result's line again (the setting may have changed since), unless a test is running.</summary>
    private void ShowResult()
    {
        if (!IsRunning && _result is not null) Status = Describe(_result);
    }

    private async Task RunAsync()
    {
        IsRunning = true;
        Status = "Opening the connection…";
        try
        {
            ImageryProvider provider = ImageryProvider.ParseList(_providers(), null)[0];
            ConnectionResult result = await ConnectionTest.RunAsync(provider, new Progress<string>(line => Status = line)).ConfigureAwait(true);
            result.Save(ConnectionResult.PathFor(SettingsDocument.DefaultPath));
            _result = result;
            Status = Describe(result);
            _measured(result);
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or IOException or TaskCanceledException)
        {
            Status = $"The test could not run: {ex.Message}";
            Log.Warning($"Connection test failed: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
            Raise(nameof(UseText));
            Raise(nameof(CanUse));
        }
    }

    /// <summary>What the test measured, and, when the setting already is its suggestion, that there is nothing to change.</summary>
    private string Describe(ConnectionResult result)
    {
        string line = $"{result.Summary} (tested {result.WhenUtc.ToLocalTime():d MMM, HH:mm})";
        return result.Suggested > 0 && _current?.Invoke() == result.Suggested ? $"{line} Your setting is already {result.Suggested}." : line;
    }
}
