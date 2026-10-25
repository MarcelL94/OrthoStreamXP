using System.Text;
using System.Threading.Channels;

namespace OrthoStreamXP;

/// <summary>Where log lines go (--log-output).</summary>
[Flags]
internal enum LogOutput
{
    None = 0,
    Console = 1,
    File = 2,
    Both = Console | File,
}

/// <summary>
/// Console plus file log ("2026-10-25 09:00:00,123 INFO message"), one file per session with the last few kept. Callers
/// only queue the line; a background thread does the slow console and disk writes, so file-system callbacks never wait
/// on them. Lines about each file X-Plane asks for and each texture built are <see cref="Detail"/>: written only when
/// detailed logging is on, so a usual log holds the steps, summaries and problems.
/// </summary>
internal static class Log
{
    private const long MaxFileBytes = 20L * 1024 * 1024;
    private const int Backups = 1;
    /// <summary>Session logs kept in their folder (orthostreamxp-*.log).</summary>
    public const int KeepSessions = 10;

    // Log lines (string) and flush requests (TaskCompletionSource, completed once everything before it is written).
    private static readonly Channel<object> Queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private static readonly Thread Writer = StartWriter();
    private static volatile LogOutput _output = LogOutput.Console;
    private static string? _path;
    private static StreamWriter? _file;
    private static string? _filePath;
    private static volatile bool _detailed;

    public static bool ConsoleEnabled => _output.HasFlag(LogOutput.Console);

    /// <summary>
    /// Also receives every line, whatever <see cref="LogOutput"/> says (the window's log page). It is called
    /// on the background writer thread, so it must only hand the line over, never wait.
    /// </summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>
    /// Chooses where lines go. With <see cref="LogOutput.File"/>, they are written to <paramref name="path"/> (at most
    /// 20 MB, then one previous part is kept), and of the session logs beside it only the newest
    /// <see cref="KeepSessions"/> are kept.
    /// </summary>
    /// <param name="detailed">Also write the <see cref="Detail"/> lines.</param>
    public static void Configure(LogOutput output, string? path, bool detailed = false)
    {
        if (output.HasFlag(LogOutput.File))
        {
            ArgumentNullException.ThrowIfNull(path);
            string folder = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(folder);
            RemoveOldSessions(folder, path);
            // Only the writer thread touches _file; it picks the path up before the next line.
            Volatile.Write(ref _path, path);
        }
        _detailed = detailed;
        _output = output;
    }

    /// <summary>Whether <see cref="Detail"/> lines are written.</summary>
    public static bool Detailed => _detailed;

    /// <summary>Deletes all but the newest session logs (orthostreamxp-*.log and their parts) in <paramref name="folder"/>.</summary>
    internal static void RemoveOldSessions(string folder, string current)
    {
        try
        {
            string[] sessions = [.. Directory.EnumerateFiles(folder, "orthostreamxp-*.log").Append(current)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            foreach (string old in sessions.SkipLast(KeepSessions))
            {
                File.Delete(old);
                File.Delete(old + ".1");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use or read-only: removed next time.
        }
    }

    /// <summary>Parses a --log-output value: none, console, log-file or both.</summary>
    public static LogOutput? ParseOutput(string value) => value.ToLowerInvariant() switch
    {
        "none" => LogOutput.None,
        "console" => LogOutput.Console,
        "log-file" or "file" => LogOutput.File,
        "both" => LogOutput.Both,
        _ => null,
    };

    /// <summary>
    /// Waits (up to <paramref name="timeout"/>) until every line logged so far is on the console and in the file.
    /// Used before anything that may end the process abruptly, such as closing the console window.
    /// </summary>
    public static bool Flush(TimeSpan timeout)
    {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Queue.Writer.TryWrite(written) && written.Task.Wait(timeout);
    }

    /// <summary>Writes out everything queued so far and stops the background writer.</summary>
    public static void Close()
    {
        Queue.Writer.TryComplete();
        Writer.Join(TimeSpan.FromSeconds(5));
    }

    public static void Info(string message) => Write("INFO", message);

    /// <summary>A line about one file or one texture (thousands a flight): written only with detailed logging on.</summary>
    public static void Detail(string message)
    {
        if (_detailed) Write("DETAIL", message);
    }

    /// <summary>
    /// Also receives every major step (<see cref="Step"/>): the window's Log page shows those and the problems. Called
    /// on the logging thread, so it must only hand the message over.
    /// </summary>
    public static Action<string>? StepSink { get; set; }

    /// <summary>A major step (started, running, stopped, X-Plane connected, the flight's report): logged as INFO, and shown on the Log page.</summary>
    public static void Step(string message)
    {
        Write("INFO", message);
        StepSink?.Invoke(message);
    }
    public static void Warning(string message) => Write("WARNING", message);
    public static void Error(string message) => Write("ERROR", message);
    public static void Error(string message, Exception ex) => Write("ERROR", $"{message}: {Describe(ex)}");

    /// <summary>Expected I/O and network failures get one line; anything else keeps its stack trace.</summary>
    private static string Describe(Exception ex)
    {
        Exception root = ex is AggregateException aggregate ? aggregate.Flatten().InnerExceptions[0] : ex;
        return root is IOException or HttpRequestException or InvalidDataException or TaskCanceledException
            ? root.Message
            : root.ToString();
    }

    private static void Write(string level, string message)
    {
        if (_output == LogOutput.None && Sink is null) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {message}";
        if (!Queue.Writer.TryWrite(line) && ConsoleEnabled) Console.Out.WriteLine(line); // after Close
    }

    private static Thread StartWriter()
    {
        var thread = new Thread(Drain) { IsBackground = true, Name = "log-writer" };
        thread.Start();
        return thread;
    }

    private static void Drain()
    {
        ChannelReader<object> reader = Queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out object? item))
            {
                if (item is TaskCompletionSource flushed)
                {
                    _file?.Flush();
                    flushed.TrySetResult();
                    continue;
                }
                string line = (string)item;
                LogOutput output = _output;
                if (output.HasFlag(LogOutput.Console)) Console.Out.WriteLine(line);
                if (output.HasFlag(LogOutput.File)) WriteToFile(line);
                Sink?.Invoke(line);
            }
            _file?.Flush();
        }
        _file?.Dispose();
    }

    private static void WriteToFile(string line)
    {
        string? path = Volatile.Read(ref _path);
        if (path is null) return;
        try
        {
            if (_file is not null && !string.Equals(_filePath, path, StringComparison.OrdinalIgnoreCase))
            {
                _file.Dispose(); // a new session's file
                _file = null;
            }
            if (_file is null)
            {
                _file = OpenWriter(path);
                _filePath = path;
            }
            _file.WriteLine(line);
            if (_file.BaseStream.Length >= MaxFileBytes) Rotate(path);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"Log file {path} cannot be written: {ex.Message}");
            Volatile.Write(ref _path, null);
        }
    }

    private static StreamWriter OpenWriter(string path) =>
        new(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));

    private static void Rotate(string path)
    {
        _file!.Dispose();
        try
        {
            for (int i = Backups - 1; i >= 1; i--)
            {
                string from = $"{path}.{i}";
                if (File.Exists(from)) File.Move(from, $"{path}.{i + 1}", overwrite: true);
            }
            File.Move(path, $"{path}.1", overwrite: true);
        }
        catch (IOException)
        {
            // Another process holds an old log open; keep appending to the current file.
        }
        _file = OpenWriter(path);
    }
}
