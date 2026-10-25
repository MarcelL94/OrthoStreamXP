using System.Globalization;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tuning;

/// <summary>How a number of the flight report reads: good, fair, poor, or just information.</summary>
internal enum Rating
{
    Info,
    Good,
    Fair,
    Poor,
}

/// <summary>The report's verdict in one line, with what it means.</summary>
internal sealed record ReportVerdict(Rating Rating, string Title, string Text);

/// <summary>One figure of the report: its icon, name, value, and what it means in plain words.</summary>
internal sealed record ReportStat(string Glyph, string Title, string Value, string Text, Rating Rating);

/// <summary>
/// The flight report in plain words: whether the scenery was sharp (the share of X-Plane's reads that got the real
/// picture: 99% or more is smooth, 95% or more a few blurry moments, less is often blurry), and when the blurry moments
/// were, which says why.
/// </summary>
internal static class ReportSummary
{
    public const double Smooth = 0.99, Acceptable = 0.95;

    public static Rating SharpnessRating(FlightReport r) => r.RealShare switch
    {
        { } share when share >= Smooth => Rating.Good,
        { } share when share >= Acceptable => Rating.Fair,
        { } => Rating.Poor,
        null => r.StandIns == 0 ? Rating.Good : Rating.Fair,
    };

    public static ReportVerdict Verdict(FlightReport r)
    {
        Rating rating = SharpnessRating(r);
        string share = r.RealShare is { } s ? $"X-Plane got the real picture for {Percent(s)} of its texture reads." : "";
        return rating switch
        {
            Rating.Good when r.StandIns == 0 => new(rating, "Smooth flight", $"{share} The scenery was sharp all the way.".Trim()),
            Rating.Good => new(rating, "Smooth flight", $"{share} {When(r)}".Trim()),
            Rating.Fair => new(rating, "A few blurry moments", $"{share} {When(r)}".Trim()),
            _ => new(rating, "Often blurry", $"{share} {When(r)} If it happens again here, a lower quality or the flight tuning's suggestions may help.".Trim()),
        };
    }

    /// <summary>When the blurry moments were, which says why.</summary>
    private static string When(FlightReport r) => r.StandInsAtStartShare >= 0.6
        ? "Most blurry moments were at the start, while X-Plane loaded the area; the textures turn sharp as soon as their pictures arrive."
        : "Most blurry moments were in flight, where new scenery came into view faster than its pictures downloaded.";

    public static IReadOnlyList<ReportStat> Stats(FlightReport r)
    {
        string duration = r.Duration.TotalHours >= 1 ? $"{(int)r.Duration.TotalHours} h {r.Duration.Minutes:00} min" : $"{Math.Max(1, (int)r.Duration.TotalMinutes)} min";
        string sharpValue = r.RealShare is { } share ? Percent(share) : r.StandIns == 0 ? "All sharp" : $"{Count(r.StandIns)} blurry";
        string sharpText = r.StandIns == 0 ? "Every texture X-Plane read had its real picture."
            : r.Reads > 0 ? $"Of {Count(r.Reads)} texture reads by X-Plane, {Count(r.StandIns)} showed a blurry stand-in until the real picture arrived."
            : $"X-Plane showed a blurry stand-in {Count(r.StandIns)} times, each until the real picture arrived.";
        return
        [
            new("", "Flight", $"{r.DistanceKm:N0} km · {duration}",
                $"{r.MinutesInFlight:0} min with X-Plane sending the aircraft's position. A move to another place is not counted as distance.", Rating.Info),
            new("", "Sharp scenery", sharpValue, sharpText,
                SharpnessRating(r)),
            new("", "Downloaded", CacheInventory.FormatBytes(r.Bytes),
                r.Bytes == 0 ? "Nothing: everything came from the cache."
                    : $"Peak {r.PeakMbits:0} Mbit/s, about {r.MeanMbitsWhileDownloading:0} Mbit/s while downloading. Less means more came from the cache.",
                Rating.Info),
            new("", "Textures built", Count(r.Builds),
                r.Builds == 0 ? "None: every texture was in the cache already."
                    : "New textures downloaded and made for X-Plane. The next flight here takes them from the cache.",
                Rating.Info),
        ];
    }

    /// <summary>"100 %", "99.7 %", "93 %": rounded down, so a single blurry read never shows as 100 %.</summary>
    private static string Percent(double share) => share >= 1 ? "100 %"
        : share >= 0.99 ? (Math.Floor(share * 1000) / 10).ToString("0.0", CultureInfo.CurrentCulture) + " %"
        : Math.Floor(share * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    private static string Count(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
