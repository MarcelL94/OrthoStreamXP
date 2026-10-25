using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>A line of the Log page: time, kind (STEP, WARNING or ERROR) and text.</summary>
internal sealed record LogLine(string Time, string Level, string Text)
{
    public bool IsProblem => Level is "WARNING" or "ERROR";

    public static LogLine Parse(string line)
    {
        // "2026-10-25 21:05:58,742 INFO message"
        string[] parts = line.Split(' ', 4);
        return parts.Length == 4 && parts[1].Length >= 8
            ? new LogLine(parts[1][..8], parts[2], parts[3])
            : new LogLine("", "INFO", line);
    }

    public static LogLine Step(string message) => new(DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), "STEP", message);
}

/// <summary>
/// The Log page: the major steps (OrthoStreamXP started, running, stopped, X-Plane connected, the flight's report) and every
/// warning and error, newest in view. Everything else is only in the log file, in the log folder.
/// </summary>
internal sealed class LogViewModel : ObservableObject, IPage
{
    private const int MaxLines = 2000;
    private readonly ConcurrentQueue<LogLine> _incoming = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private string _search = "";

    public LogViewModel()
    {
        Lines = [];
        View = CollectionViewSource.GetDefaultView(Lines);
        View.Filter = item => item is LogLine line && (_search.Length == 0 || line.Text.Contains(_search, StringComparison.OrdinalIgnoreCase));
        Log.StepSink = message => _incoming.Enqueue(LogLine.Step(message));
        Log.Sink = text =>
        {
            LogLine line = LogLine.Parse(text);
            if (line.IsProblem) _incoming.Enqueue(line);
        };
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
        OpenFolder = new RelayCommand(() =>
        {
            Directory.CreateDirectory(AppPaths.Logs);
            Shell.Open(AppPaths.Logs);
        });
    }

    public ObservableCollection<LogLine> Lines { get; }
    public ICollectionView View { get; }
    public ICommand OpenFolder { get; }

    /// <summary>Raised after new lines arrive, so the view can scroll to the newest one.</summary>
    public event EventHandler? LinesAdded;

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value ?? "")) View.Refresh();
        }
    }

    public void OnShown()
    {
    }

    private void Drain()
    {
        if (_incoming.IsEmpty) return;
        while (_incoming.TryDequeue(out LogLine? line)) Lines.Add(line);
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        LinesAdded?.Invoke(this, EventArgs.Empty);
    }
}
