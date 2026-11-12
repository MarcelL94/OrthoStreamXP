namespace OrthoStreamXP.Scenery;

/// <summary>Where the sun is in X-Plane, as the night lights see it.</summary>
internal enum DaylightPhase
{
    Day,
    /// <summary>The sun just below the horizon: X-Plane fades its lights in or out.</summary>
    Twilight,
    Night,
}

/// <summary>
/// Follows the sun's height in X-Plane and logs each change of day, twilight and night, with the time it happened:
/// set beside the read log, it shows what X-Plane reads of the night textures by day and when night falls.
/// </summary>
internal sealed class Daylight
{
    /// <summary>Below this the sky is dark (civil twilight's end), where X-Plane's towns are fully lit.</summary>
    public const double NightBelow = -6;
    /// <summary>A change counts once the sun is this far past the line, so a sun sitting on it does not fill the log.</summary>
    private const double Margin = 0.5;

    private DaylightPhase? _phase;

    public DaylightPhase? Phase => _phase;

    /// <summary>The phase of a sun at <paramref name="degrees"/> above the horizon.</summary>
    public static DaylightPhase PhaseOf(double degrees) => degrees >= 0 ? DaylightPhase.Day : degrees >= NightBelow ? DaylightPhase.Twilight : DaylightPhase.Night;

    /// <summary>The sun's height from X-Plane; returns the new phase when it changed (logged), otherwise null.</summary>
    public DaylightPhase? Offer(double degrees)
    {
        if (!double.IsFinite(degrees) || degrees is < -90 or > 90) return null;
        DaylightPhase phase = PhaseOf(degrees);
        if (_phase == phase) return null;
        // Past the line by the margin, the way it is crossing: a sun wavering on it is not a change.
        if (_phase is { } previous && PhaseOf(degrees + (phase > previous ? Margin : -Margin)) != phase) return null;
        _phase = phase;
        Log.Step($"Sun {Math.Abs(degrees):F1}° {(degrees >= 0 ? "above" : "below")} the horizon in X-Plane: {phase switch
        {
            DaylightPhase.Day => "day",
            DaylightPhase.Twilight => "twilight, X-Plane fades its lights",
            _ => "night",
        }}");
        return phase;
    }
}
