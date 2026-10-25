namespace OrthoStreamXP.Tests;

/// <summary>The log: one file per session with the last ones kept, and the per-file lines only on demand.</summary>
public sealed class LogTests : IDisposable
{
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Only_the_newest_session_logs_are_kept()
    {
        for (int day = 1; day <= 12; day++) _tree.WriteText($"logs/orthostreamxp-202610{day:00}-090000.log", "x");
        _tree.WriteText("logs/orthostreamxp-20261001-090000.log.1", "the oldest one's first part");
        _tree.WriteText("logs/reads-20261001-090000.csv", "not a session log");
        string current = _tree.PathOf("logs/orthostreamxp-20261028-090000.log");

        Log.RemoveOldSessions(_tree.PathOf("logs"), current);

        string[] left = [.. Directory.EnumerateFiles(_tree.PathOf("logs"), "orthostreamxp-*").Select(Path.GetFileName).Order()!];
        Assert.Equal(Log.KeepSessions - 1, left.Length); // with the new session's own file: ten
        Assert.Equal("orthostreamxp-20261004-090000.log", left[0]);
        Assert.True(File.Exists(_tree.PathOf("logs/reads-20261001-090000.csv")));
    }

    [Fact]
    public void Per_file_lines_are_written_only_with_detailed_logging()
    {
        var lines = new List<string>();
        Action<string> sink = line => { lock (lines) lines.Add(line); };
        Log.Sink += sink;
        try
        {
            Log.Configure(LogOutput.None, null, detailed: false);
            Log.Detail("one texture: not wanted");
            Log.Configure(LogOutput.None, null, detailed: true);
            Log.Detail("one texture: wanted");
            Assert.True(Log.Flush(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Log.Sink -= sink;
            Log.Configure(LogOutput.Console, null);
        }
        lock (lines)
        {
            Assert.DoesNotContain(lines, l => l.Contains("not wanted", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("DETAIL one texture: wanted", StringComparison.Ordinal));
        }
    }
}
