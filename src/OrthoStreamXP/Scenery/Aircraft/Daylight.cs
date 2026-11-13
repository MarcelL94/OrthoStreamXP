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
/// set beside the read log, it shows what X-Plane reads of the night textures by day and when night falls. The
/// position listener offers it the sun; the photo streaming reads <see cref="Dark"/> from the file-system threads.
/// </summary>
/// <param name="darkBelow">The sun's height below which it is <see cref="Dark"/> (negative: below the horizon).</param>
internal sealed class Daylight(double darkBelow = Daylight.DefaultDarkBelow)
{
    /// <summary>Below this the sky is dark (civil twilight's end), where X-Plane's towns are fully lit.</summary>
    public const double NightBelow = -6;
    /// <summary>
    /// Below this the photos may be skipped by default: past civil twilight, where the sky still lights the ground
    /// enough to see the photos, so their detail is skipped only once it is really dark (and comes back earlier at dawn).
    /// </summary>
    public const double DefaultDarkBelow = -9;
    /// <summary>A change counts once the sun is this far past the line, so a sun sitting on it does not fill the log.</summary>
    private const double Margin = 0.5;

    private volatile bool _dark;

    /// <summary>
    /// The sun is lower than <see cref="DarkBelow"/>: false until X-Plane sends it, and changing only
    /// once the sun is <see cref="Margin"/> past the line.
    /// </summary>
    public bool Dark => _dark;

    /// <summary>The sun's height below which it is <see cref="Dark"/>.</summary>
    public double DarkBelow => darkBelow;

    /// <summary>The phase as an int, -1 before X-Plane sent the sun: written by the listener, read by any thread.</summary>
    private int _phase = -1;

    /// <summary>The latest phase, or null until X-Plane sends the sun.</summary>
    public DaylightPhase? Phase => Volatile.Read(ref _phase) is var phase and >= 0 ? (DaylightPhase)phase : null;

    /// <summary>The phase of a sun at <paramref name="degrees"/> above the horizon.</summary>
    public static DaylightPhase PhaseOf(double degrees) => degrees >= 0 ? DaylightPhase.Day : degrees >= NightBelow ? DaylightPhase.Twilight : DaylightPhase.Night;

    /// <summary>The sun's height from X-Plane; returns the new phase when it changed (logged), otherwise null.</summary>
    public DaylightPhase? Offer(double degrees)
    {
        if (!double.IsFinite(degrees) || degrees is < -90 or > 90) return null;
        if (_dark ? degrees > DarkBelow + Margin : degrees < DarkBelow - Margin) _dark = !_dark;
        DaylightPhase phase = PhaseOf(degrees);
        DaylightPhase? current = Phase;
        if (current == phase) return null;
        // Past the line by the margin, the way it is crossing: a sun wavering on it is not a change.
        if (current is { } previous && PhaseOf(degrees + (phase > previous ? Margin : -Margin)) != phase) return null;
        Volatile.Write(ref _phase, (int)phase);
        Log.Step($"Sun {Math.Abs(degrees):F1}° {(degrees >= 0 ? "above" : "below")} the horizon in X-Plane: {phase switch
        {
            DaylightPhase.Day => "day",
            DaylightPhase.Twilight => "twilight, X-Plane fades its lights",
            _ => "night",
        }}");
        return phase;
    }
}
