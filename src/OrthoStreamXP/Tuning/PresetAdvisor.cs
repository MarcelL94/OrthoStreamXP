using System.Globalization;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tuning;

/// <summary>The preset recommended for this PC, and why in plain words.</summary>
internal sealed record Recommendation(QualityPreset Preset, string Reason);

/// <summary>
/// Which quality preset suits this PC: the sharpest one every known fact allows. The graphics card must hold the
/// preset's photo textures beside X-Plane's own needs; the connection must have reached the preset's download speed on
/// the fastest recent flight that downloaded enough to tell (Sharp about 20 Mbit/s, Ultra 50, Extra 100: a ZL16
/// texture is 4 times the tiles per step; a flight over cached scenery downloads little and says nothing); and a preset
/// that was often blurry on the last flight is not recommended again. Without such a flight the connection test says
/// how fast it is, when it was run; without either there is nothing to say about the connection: Sharp, or Balanced
/// on a small graphics card.
/// </summary>
internal static class PresetAdvisor
{
    /// <summary>The peak download speed (Mbit/s) a preset wants, by level change.</summary>
    public static double NeededMbits(QualityPreset preset) => preset.Level switch
    {
        <= -2 => 0,
        -1 => 20,
        0 => 50,
        _ => 100,
    };

    /// <param name="card">The graphics card's memory, when Windows says.</param>
    /// <param name="memory">The photo textures' graphics memory per texture size, as last measured.</param>
    /// <param name="flights">The flight reports kept, newest first (<see cref="FlightReport.History"/>).</param>
    /// <param name="test">The latest connection test, when there is one.</param>
    public static Recommendation Recommend(long? card, GraphicsMemory memory, IReadOnlyList<FlightReport> flights, ConnectionResult? test = null)
    {
        FlightReport? lastFlight = flights.Count > 0 ? flights[0] : null;
        bool Fits(QualityPreset p) => card is not { } c || memory.BytesAt(p.Size) + GraphicsMemory.XPlaneOwnBytes <= c;
        string cardText = card is { } bytes ? $"{CacheInventory.FormatBytes(bytes)} graphics card" : "";

        // Evidence of the connection: the fastest flight that downloaded enough to show its speed, else the connection test.
        FlightReport? fastest = flights.Where(f => f.Bytes > 200_000_000).MaxBy(f => f.PeakMbits);
        double? measured = fastest?.PeakMbits ?? (test is { Mbits: > 0 } t ? t.Mbits : null);
        if (measured is not { } peak)
        {
            QualityPreset start = Fits(QualityPreset.Sharp) ? QualityPreset.Sharp : QualityPreset.Balanced;
            return new(start, (start == QualityPreset.Sharp
                ? "A good start on most PCs"
                : $"Your {cardText} is small for sharper textures") + ". Test your connection or fly once, and the recommendation follows your PC and connection.");
        }

        int? flownLevel = lastFlight is null ? null : CacheInventory.ParseSetupName(lastFlight.Quality)?.Level;
        bool blurry = lastFlight is not null && ReportSummary.SharpnessRating(lastFlight) == Rating.Poor;
        foreach (QualityPreset preset in QualityPreset.All.Reverse())
        {
            if (preset == QualityPreset.Balanced) break;
            if (!Fits(preset) || peak < NeededMbits(preset)) continue;
            if (blurry && flownLevel is { } level && preset.Level >= level) continue;
            return new(preset, Because(peak, fastest is not null, cardText, blurry && flownLevel is not null));
        }
        return new(QualityPreset.Balanced, Because(peak, fastest is not null, cardText, blurry));
    }

    private static string Because(double peak, bool flown, string card, bool blurry)
    {
        string speed = peak.ToString("0", CultureInfo.CurrentCulture);
        var facts = new List<string> { flown ? $"downloads reached {speed} Mbit/s on your recent flights" : $"the connection test measured {speed} Mbit/s" };
        if (card.Length > 0) facts.Add($"your {card} holds its textures");
        if (blurry) facts.Add("a sharper quality was often blurry");
        return $"For your PC: {string.Join(", ", facts)}.";
    }
}
