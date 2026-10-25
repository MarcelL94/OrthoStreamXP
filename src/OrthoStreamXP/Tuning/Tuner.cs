using System.Globalization;
using System.Text;
using System.Text.Json;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tuning;

/// <summary>
/// One change the flight tuning suggests (From and To as the settings file has them), or, without a key, a finding
/// nothing in the settings can fix.
/// </summary>
internal sealed record Suggestion(string? Key, string Setting, string? From, string? To, string Why);

/// <summary>
/// What the flight tuning found after a session at one quality preset (by level change). Saved beside the log until
/// it is applied or dismissed, so it waits for the window if the flight ends with the window closed.
/// </summary>
internal sealed record FlightReview(int Level, DateTime When, double MinutesInFlight, IReadOnlyList<Suggestion> Suggestions, bool TooShort = false)
{
    public const string FileName = "flight-review.json";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The review of the last session, in the cache's log folder.</summary>
    /// <summary>The review in a history folder (<see cref="AppPaths.History"/>).</summary>
    public static string PathFor(string history) => Path.Join(history, FileName);

    public bool HasChanges => Suggestions.Any(s => s.Key is not null);

    public static FlightReview? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<FlightReview>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Saved when it has something to say. A flight judged with nothing to change removes an older review (it is the
    /// newer word); a session too short to judge leaves it alone, so a quick start and stop never loses suggestions.
    /// </summary>
    public void Save(string path)
    {
        try
        {
            if (TooShort) return;
            if (Suggestions.Count == 0) File.Delete(path);
            else AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(this, Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save the flight review to {path}: {ex.Message}");
        }
    }

    public static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not remove the flight review {path}: {ex.Message}");
        }
    }

    /// <summary>The review as log lines.</summary>
    public string Describe()
    {
        string preset = QualityPreset.ForLevel(Level)?.Name ?? $"level {Level}";
        if (TooShort) return $"Flight tuning: {MinutesInFlight:0} min in flight; it takes {Tuner.MinMinutesInFlight} min to suggest settings for {preset}";
        if (Suggestions.Count == 0) return $"Flight tuning: the {preset} settings suited this flight ({MinutesInFlight:0} min in flight)";
        var text = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Flight tuning for {preset} after {MinutesInFlight:0} min in flight (apply them in the window):");
        foreach (Suggestion s in Suggestions)
            text.Append(CultureInfo.InvariantCulture, $"{Environment.NewLine}  {(s.Key is null ? s.Setting : $"{s.Key}: {s.From} -> {s.To}")}: {s.Why}");
        return text.ToString();
    }
}

/// <summary>The values a session ran with that the tuning may change.</summary>
internal sealed record CurrentSettings(int Level, bool Streaming, double NearWait, double MaxWait, int BuildMemoryMb, int BuildTiles,
    int DownloadWorkers, double? PagingK)
{
    public static CurrentSettings From(MountSettings s) => new(s.Textures.LevelChange, s.MipStreaming && s.Providers is not null,
        s.NearWait, s.MaxWait, s.BuildMemoryMb, s.BuildTiles, s.DownloadWorkers, s.PagingK);
}

/// <summary>
/// Turns what a session measured into setting changes, each with its reason in plain words. Every rule needs enough
/// of a flight to go on and changes one thing in one direction by a step, so flight after flight the settings settle
/// on what this PC and connection do best at the preset. The rules follow what the test flights showed:
/// <list type="bullet">
/// <item>Windows short of memory: builds hold less (build-memory), unless OrthoStreamXP held far more than its builds
/// may (then a finding, as no setting fixes it).</item>
/// <item>Downloads timing out or refused: fewer at once. All busy while tiles still arrive fast: more at once, as many
/// as the connection test found best when there is one (a flight runs at one number, so it cannot tell how many more
/// would help; the test tried several). A flight that disagrees with the test asks for the test again.</item>
/// <item>Builds held back by their memory or tiles while the connection had room: a bigger budget.</item>
/// <item>X-Plane's reads in flight getting stand-ins whose picture came soon after: a longer max-wait.</item>
/// <item>Blurry textures next to the aircraft at the end of a start: a longer near-wait.</item>
/// <item>X-Plane's detail by distance unlike the paging model's: the measured k.</item>
/// </list>
/// </summary>
internal static class Tuner
{
    public const double MinMinutesInFlight = 10;
    private const long Mb = 1 << 20;

    /// <param name="test">The latest connection test, when there is one.</param>
    /// <param name="peakMbits">The flight's peak download speed (<see cref="FlightReport.PeakMbits"/>), when known.</param>
    public static FlightReview Review(FlightFacts f, CurrentSettings s, DateTime when, ConnectionResult? test = null, double? peakMbits = null)
    {
        if (f.MinutesInFlight < MinMinutesInFlight) return new FlightReview(s.Level, when, f.MinutesInFlight, [], TooShort: true);
        var list = new List<Suggestion>();
        bool memoryLow = MemoryLow(f);

        // Memory first: X-Plane needs it more than the builds do, unless the builds were not what held it.
        if (memoryLow && OwnMemoryBeyondBuilds(f, s))
            list.Add(new Suggestion(null, "OrthoStreamXP held more memory than its builds", null, null,
                $"Windows had only {Gb(f.MinAvailableMemoryBytes)} of memory free at the lowest, while OrthoStreamXP held {Gb(f.OwnMemoryAtMinBytes)}: " +
                $"far more than its builds may use ({s.BuildMemoryMb} MB), so a smaller build memory would free little. Closing other programs leaves more to X-Plane."));
        else if (memoryLow && s.BuildMemoryMb > 256)
            list.Add(Change(TunedSettings.BuildMemory, s.BuildMemoryMb, Math.Max(256, RoundTo(s.BuildMemoryMb / 2.0, 128)),
                $"Windows had only {Gb(f.MinAvailableMemoryBytes)} of memory free at the lowest. Builds that hold less leave more to X-Plane."));

        // The connection.
        if (Downloads(f, s, test, peakMbits) is { } downloads) list.Add(downloads);

        if (f.Streaming is { } st && s.Streaming)
        {
            bool blurry = st.FlightTimedOut > 0 || (st.Reads > 0 && (double)(st.StandInReads + st.FlatReads) / st.Reads > 0.05);
            bool lineFree = f.DownloadsQueuedShare < 0.3;

            // Builds held back by their own budget while the connection had room to spare.
            if (!memoryLow && blurry && lineFree && f.BuildsWaitedForMemoryShare >= 0.3)
            {
                int cap = (int)Math.Clamp(RoundTo(f.TotalMemoryBytes / 8.0 / Mb, 128), 512, 4096);
                int more = Math.Min(cap, RoundTo(s.BuildMemoryMb * 1.5, 128));
                if (more > s.BuildMemoryMb)
                    list.Add(Change(TunedSettings.BuildMemory, s.BuildMemoryMb, more,
                        $"X-Plane's textures waited for building memory {f.BuildsWaitedForMemoryShare:P0} of the flight while the connection had room to spare, and your PC has the memory for more."));
            }
            if (blurry && lineFree && f.BuildsWaitedForTilesShare >= 0.3)
            {
                int more = Math.Min(131_072, RoundTo(s.BuildTiles * 1.5, 4096));
                if (more > s.BuildTiles)
                    list.Add(Change(TunedSettings.BuildTiles, s.BuildTiles, more,
                        $"X-Plane's textures waited for their turn to download {f.BuildsWaitedForTilesShare:P0} of the flight while the connection had room to spare."));
            }

            // Reads in flight that got a stand-in: wait as long as most of those pictures took, within reason.
            if (st.FlightWaits >= 50 && st.FlightTimedOut >= 20 && (double)st.FlightTimedOut / st.FlightWaits > 0.2 && st.FlightNeeded.Count >= 20)
            {
                double p80 = Percentile(st.FlightNeeded, 0.8);
                if (p80 <= 12)
                {
                    // Only when most of those pictures took longer than the wait now: otherwise it is a tail no wait fixes.
                    double longer = Math.Min(Math.Ceiling(p80 * 2) / 2, 8);
                    if (p80 > s.MaxWait && longer > s.MaxWait)
                        list.Add(Change(TunedSettings.MaxWait, s.MaxWait, longer,
                            $"{st.FlightTimedOut:N0} of X-Plane's {st.FlightWaits:N0} reads in flight that had to wait got a blurry stand-in after {Seconds(s.MaxWait)}; most of those pictures were there within {Seconds(p80)}."));
                }
                else
                {
                    string preset = QualityPreset.ForLevel(s.Level)?.Name ?? "this quality";
                    list.Add(new Suggestion(null, "The connection could not keep up", null, null,
                        $"Most pictures X-Plane waited for in flight took over {Seconds(p80)}, so waiting longer would not help. If the scenery often stays blurry, a lower quality than {preset} loads faster."));
                }
            }

            // A start that ended with blurry textures next to the aircraft.
            if (st.StartTimedOut >= 5 && (double)st.StartTimedOut / Math.Max(1, st.StartWaits) >= 0.1 && st.StartNeeded.Count >= 5)
            {
                double p90 = Percentile(st.StartNeeded, 0.9);
                double longer = Math.Min(Math.Ceiling(p90 / 5) * 5, 120);
                if (p90 > s.NearWait && longer > s.NearWait)
                    list.Add(Change(TunedSettings.NearWait, s.NearWait, longer,
                        $"At the start, {st.StartTimedOut:N0} textures around the aircraft were still blurry after {Seconds(s.NearWait)}; they arrived within {Seconds(p90)}. X-Plane's loading screen may last up to that long, with sharp scenery at the end."));
            }

            // The paging model: what X-Plane showed against what OrthoStreamXP expected.
            if (st.MeasuredK is { Count: >= 100 } measured && Math.Abs(measured.Median - st.ModelK) / st.ModelK > 0.15)
            {
                double k = Math.Clamp(RoundTo(measured.Median, 50), 100, 100_000);
                list.Add(new Suggestion(TunedSettings.PagingK.Key, TunedSettings.PagingK.Label,
                    s.PagingK is null ? "auto" : TunedSettings.TextOf(st.ModelK),
                    TunedSettings.TextOf(k),
                    $"X-Plane changed the detail it wanted at {(measured.Median > st.ModelK ? "farther" : "nearer")} distances than OrthoStreamXP expected ({measured.Count:N0} changes measured). With the measured value, the right detail is built ahead."));
            }
        }
        return new FlightReview(s.Level, when, f.MinutesInFlight, list);
    }

    /// <summary>
    /// Downloads at once from the flight, with the connection test (<paramref name="test"/>) to size the change: the
    /// flight says whether more or fewer are needed, the test how many. Null when the flight gives no reason to change.
    /// </summary>
    private static Suggestion? Downloads(FlightFacts f, CurrentSettings s, ConnectionResult? test, double? peakMbits)
    {
        if (f.Attempts < 2000) return null;
        double trouble = (double)f.TroubledAttempts / f.Attempts;
        string? tested = test is { Suggested: > 0 } t
            ? string.Create(CultureInfo.CurrentCulture, $"your connection test on {t.WhenUtc.ToLocalTime():d MMM} reached {t.Mbits:0} Mbit/s with {t.Suggested} at once")
            : null;

        if (trouble > 0.03 && s.DownloadWorkers > 32)
        {
            string why = $"{trouble:P0} of the downloads timed out or were refused and were tried again: the picture servers or your connection had more than they could take. Fewer at once arrive sooner.";
            if (tested is not null && s.DownloadWorkers >= test!.Suggested)
                why += $" ({char.ToUpperInvariant(tested[0])}{tested[1..]}: the servers were busier during this flight than at the test.)";
            return Change(TunedSettings.DownloadWorkers, s.DownloadWorkers, Math.Max(32, RoundTo(s.DownloadWorkers * 0.75, 16)), why);
        }

        bool queued = trouble < 0.005 && f.DownloadsQueuedShare >= 0.5 && f.MeanTileSeconds is > 0 and < 0.6;
        if (!queued || s.DownloadWorkers >= 256) return null;
        string waited = $"Downloads waited for a free place {f.DownloadsQueuedShare:P0} of the flight, while each picture tile still arrived in {f.MeanTileSeconds:0.00} s";

        // The test found more at once faster: go there at once, rather than a step per flight.
        if (test is { Suggested: > 0 } more && more.Suggested > s.DownloadWorkers)
            return Change(TunedSettings.DownloadWorkers, s.DownloadWorkers, more.Suggested, $"{waited}, and {tested}.");

        // The test found this many enough, yet the flight queued: one of them is out of date.
        if (test is { Suggested: > 0 } enough)
        {
            string slower = peakMbits is { } peak && enough.Mbits > 0 && peak < enough.Mbits * 0.5
                ? string.Create(CultureInfo.CurrentCulture, $" The flight's downloads peaked at {peak:0} Mbit/s, under half the test's {enough.Mbits:0}.")
                : "";
            return new Suggestion(null, "Test the connection again", null, null,
                $"{waited}, although {tested}.{slower} Your connection or the picture servers may have changed: run Test my connection again in Settings (Pictures and download), and the suggestion follows it.");
        }

        return Change(TunedSettings.DownloadWorkers, s.DownloadWorkers, Math.Min(256, RoundTo(s.DownloadWorkers * 1.25, 16)),
            $"{waited}: your connection has room for more at once.");
    }

    /// <summary>Less than 1.5 GB or 8% of the memory free at the lowest.</summary>
    private static bool MemoryLow(FlightFacts f) =>
        f.TotalMemoryBytes > 0 && f.MinAvailableMemoryBytes > 0 && f.MinAvailableMemoryBytes < Math.Max(1536 * Mb, f.TotalMemoryBytes * 0.08);

    /// <summary>
    /// What OrthoStreamXP holds besides its builds: the textures kept for X-Plane's reads (1 GB at most), the tiles
    /// kept for the next builds (256 MB), the window and .NET itself.
    /// </summary>
    internal const long BesidesBuildsBytes = 3L << 30;

    /// <summary>
    /// OrthoStreamXP itself held far more than its builds may, when Windows was lowest: then the builds are not what
    /// to cut, and halving them would only make the scenery sharpen more slowly.
    /// </summary>
    private static bool OwnMemoryBeyondBuilds(FlightFacts f, CurrentSettings s) =>
        f.OwnMemoryAtMinBytes > s.BuildMemoryMb * Mb + BesidesBuildsBytes;

    private static Suggestion Change(TunableSetting setting, double from, double to, string why) =>
        new(setting.Key, setting.Label, TunedSettings.TextOf(from), TunedSettings.TextOf(to), why);

    internal static double Percentile(IReadOnlyList<double> values, double share)
    {
        double[] sorted = [.. values.Order()];
        return sorted[Math.Clamp((int)Math.Ceiling(share * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static int RoundTo(double value, int step) => (int)Math.Round(value / step, MidpointRounding.AwayFromZero) * step;

    private static string Seconds(double s) => s.ToString(s < 10 ? "0.#" : "0", CultureInfo.CurrentCulture) + " s";

    private static string Gb(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
}
