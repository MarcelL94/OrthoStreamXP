using System.Text.Json;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Tuning;

/// <summary>
/// One moment of a session, every few seconds: where the aircraft was (when X-Plane sent it), and the session's running
/// totals of downloads, textures built, X-Plane's reads of texture data and those that got a stand-in.
/// </summary>
internal sealed record ReportSample(double Seconds, double? Lat, double? Lon, double? GroundSpeedKt, long Tiles, long Bytes, long Builds, long StandIns,
    long Reads = 0);

/// <summary>
/// A session's report for the window: when, at which quality, and its samples, from which the summary and the charts
/// are worked out. The last one is kept in the log folder as flight-report.json, and earlier ones in flights\.
/// </summary>
internal sealed record FlightReport(DateTime StartedUtc, DateTime EndedUtc, string Quality, IReadOnlyList<ReportSample> Samples)
{
    public const string FileName = "flight-report.json";
    private const int KeepEarlier = 20;
    private static readonly JsonSerializerOptions Compact = new();

    /// <summary>The last report in a history folder (<see cref="Setup.AppPaths.History"/>).</summary>
    public static string PathFor(string history) => Path.Join(history, FileName);

    public TimeSpan Duration => EndedUtc - StartedUtc;

    /// <summary>The samples with a position, where the route is drawn.</summary>
    public IEnumerable<ReportSample> Positions => Samples.Where(s => s.Lat is not null && s.Lon is not null);

    /// <summary>Kilometres flown: the route from position to position (jumps of over 50 km, a move to another place, left out).</summary>
    public double DistanceKm
    {
        get
        {
            double metres = 0;
            ReportSample? last = null;
            foreach (ReportSample s in Positions)
            {
                if (last is not null)
                {
                    double step = Geo.DistanceM(last.Lat!.Value, last.Lon!.Value, s.Lat!.Value, s.Lon!.Value);
                    if (step < 50_000) metres += step;
                }
                last = s;
            }
            return metres / 1000;
        }
    }

    public long Bytes => Samples.Count == 0 ? 0 : Samples[^1].Bytes - Samples[0].Bytes;
    public long Tiles => Samples.Count == 0 ? 0 : Samples[^1].Tiles - Samples[0].Tiles;
    public long Builds => Samples.Count == 0 ? 0 : Samples[^1].Builds - Samples[0].Builds;
    public long StandIns => Samples.Count == 0 ? 0 : Samples[^1].StandIns - Samples[0].StandIns;

    /// <summary>X-Plane's reads of texture data (0 in reports from before they were counted).</summary>
    public long Reads => Samples.Count == 0 ? 0 : Samples[^1].Reads - Samples[0].Reads;

    /// <summary>The share of X-Plane's reads that got the real picture; null when the reads were not counted.</summary>
    public double? RealShare => Reads > 0 ? 1 - (double)StandIns / Reads : null;

    /// <summary>Minutes with X-Plane sending the aircraft position.</summary>
    public double MinutesInFlight => Samples.Count(s => s.Lat is not null) * FlightRecorder.Interval.TotalMinutes;

    /// <summary>The share of the stand-ins in the first 5 minutes after X-Plane's first position: while it loaded the area.</summary>
    public double StandInsAtStartShare
    {
        get
        {
            if (StandIns == 0 || Positions.FirstOrDefault() is not { } first) return 0;
            ReportSample? atFive = Samples.LastOrDefault(s => s.Seconds <= first.Seconds + 300);
            long early = (atFive?.StandIns ?? Samples[0].StandIns) - Samples[0].StandIns;
            return (double)early / StandIns;
        }
    }

    /// <summary>The average download speed while downloading (samples above 1 Mbit/s), in Mbit/s.</summary>
    public double MeanMbitsWhileDownloading
    {
        get
        {
            double[] busy = [.. Speed().Select(s => s.Mbits).Where(m => m > 1)];
            return busy.Length == 0 ? 0 : busy.Average();
        }
    }

    /// <summary>Download speed between samples, in Mbit/s, with the time it was at.</summary>
    public IEnumerable<(double Seconds, double Mbits)> Speed() =>
        Samples.Zip(Samples.Skip(1), (a, b) => (b.Seconds, b.Seconds > a.Seconds ? (b.Bytes - a.Bytes) * 8 / (b.Seconds - a.Seconds) / 1e6 : 0));

    /// <summary>The highest download speed, averaged over half a minute so a burst of one sample does not count.</summary>
    public double PeakMbits
    {
        get
        {
            double best = 0;
            // For each sample, the latest one at least 30 s before it.
            for (int i = 0, j = 0; i < Samples.Count; i++)
            {
                while (j + 1 < i && Samples[i].Seconds - Samples[j + 1].Seconds >= 30) j++;
                if (Samples[i].Seconds - Samples[j].Seconds >= 30)
                    best = Math.Max(best, (Samples[i].Bytes - Samples[j].Bytes) * 8 / (Samples[i].Seconds - Samples[j].Seconds) / 1e6);
            }
            return best;
        }
    }

    /// <summary>Saved as the last report; the one it replaces moves to flights\ (the newest 20 are kept).</summary>
    public void Save(string path)
    {
        try
        {
            string folder = Path.Join(Path.GetDirectoryName(path)!, "flights");
            if (File.Exists(path))
            {
                Directory.CreateDirectory(folder);
                File.Move(path, Path.Join(folder, $"flight-{File.GetLastWriteTime(path):yyyyMMdd-HHmmss}.json"), overwrite: true);
                foreach (string old in Directory.EnumerateFiles(folder, "flight-*.json").OrderDescending().Skip(KeepEarlier)) File.Delete(old);
            }
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Compact));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save the flight report to {path}: {ex.Message}");
        }
    }

    /// <summary>The last report and the ones kept before it (flights\), newest first.</summary>
    public static IEnumerable<FlightReport> History(string cache)
    {
        string last = PathFor(cache);
        string folder = Path.Join(Path.GetDirectoryName(last)!, "flights");
        IEnumerable<string> earlier = Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "flight-*.json").OrderDescending() : [];
        foreach (string path in earlier.Prepend(last))
            if (Load(path) is { } report) yield return report;
    }

    public static FlightReport? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FlightReport>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
