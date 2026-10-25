using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace OrthoStreamXP.Scenery;

/// <summary>One read X-Plane made of a mounted file, as the read log records it.</summary>
/// <param name="FirstMip">For a streamed texture: the levels the read touched, its band and how it was answered.</param>
/// <param name="DistanceM">From the camera to the texture's centre at the time of the read, when the camera is known.</param>
internal sealed record ReadRecord(string Package, VirtualPath Path, long Offset, int Length,
    int? FirstMip = null, int? LastMip = null, string? Band = null, string? Served = null, double WaitMs = 0,
    double? DistanceM = null, double? SizeM = null, int? FullPx = null, int? WantedMip = null);

/// <summary>
/// The logs for calibrating the paging model: every open and read X-Plane makes through the mount
/// (reads-*.csv) and the camera position (camera-*.csv), with the same clock, one pair of files per session
/// in the log folder; the last 10 sessions are kept. Lines are written in the background.
/// </summary>
internal sealed class ReadLog : IDisposable
{
    private const int KeepSessions = 10;
    private const string ReadsHeader = "time_s,package,event,path,offset,length,first_mip,last_mip,band,served,wait_ms,distance_m,size_m,full_px,wanted_mip";
    private const string CameraHeader = "time_s,lat,lon,height_agl_m,elevation_m,ground_speed_kt,track_deg";

    private readonly Channel<(bool Camera, string Line)> _lines = Channel.CreateUnbounded<(bool, string)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly StreamWriter _reads, _camera;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Task _writer;

    private ReadLog(string folder)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        ReadsPath = Path.Join(folder, $"reads-{stamp}.csv");
        CameraPath = Path.Join(folder, $"camera-{stamp}.csv");
        _reads = Writer(ReadsPath, ReadsHeader);
        _camera = Writer(CameraPath, CameraHeader);
        _writer = Task.Run(WriteAsync);
    }

    public string ReadsPath { get; }
    public string CameraPath { get; }

    /// <summary>Starts this session's files in <paramref name="folder"/>, removing all but the last sessions' ones.</summary>
    public static ReadLog Start(string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (string pattern in new[] { "reads-*.csv", "camera-*.csv" })
            foreach (string old in Directory.EnumerateFiles(folder, pattern).Order(StringComparer.Ordinal).SkipLast(KeepSessions - 1))
                try
                {
                    File.Delete(old);
                }
                catch (IOException)
                {
                    // Open in a spreadsheet: removed next time.
                }
        var log = new ReadLog(folder);
        Log.Info($"Read log for calibrating the paging model: {log.ReadsPath} (camera: {Path.GetFileName(log.CameraPath)})");
        return log;
    }

    private static StreamWriter Writer(string path, string header)
    {
        var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false));
        writer.WriteLine(header);
        return writer;
    }

    private string Time => _clock.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture);

    public void Open(string package, VirtualPath path, string how) =>
        Add(false, $"{Time},{Csv(package)},open,{Csv(path.Display)},,,,,,{Csv(how)},,,,,");

    public void Read(ReadRecord r) =>
        Add(false, string.Create(CultureInfo.InvariantCulture,
            $"{Time},{Csv(r.Package)},read,{Csv(r.Path.Display)},{r.Offset},{r.Length},{r.FirstMip},{r.LastMip},{r.Band},{r.Served},{r.WaitMs:F0},{Number(r.DistanceM)},{Number(r.SizeM)},{r.FullPx},{r.WantedMip}"));

    public void Camera(CameraReport c) =>
        Add(true, string.Create(CultureInfo.InvariantCulture,
            $"{Time},{c.Lat:F6},{c.Lon:F6},{c.HeightAglM:F1},{(c.ElevationM is { } e ? e.ToString("F1", CultureInfo.InvariantCulture) : "")},{(c.Motion is { } m ? (m.SpeedMps * 1.943844).ToString("F0", CultureInfo.InvariantCulture) : "")},{(c.Motion is { } t ? t.TrackDeg.ToString("F0", CultureInfo.InvariantCulture) : "")}"));

    private static string Number(double? value) => value is { } v ? v.ToString("F0", CultureInfo.InvariantCulture) : "";

    private static string Csv(string value) =>
        value.AsSpan().IndexOfAny(",\"\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private void Add(bool camera, string line) => _lines.Writer.TryWrite((camera, line));

    private async Task WriteAsync()
    {
        try
        {
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var item)) (item.Camera ? _camera : _reads).WriteLine(item.Line);
                _reads.Flush();
                _camera.Flush();
            }
        }
        catch (IOException ex)
        {
            Log.Warning($"The read log stopped: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        _writer.Wait(TimeSpan.FromSeconds(2));
        _reads.Dispose();
        _camera.Dispose();
    }
}
