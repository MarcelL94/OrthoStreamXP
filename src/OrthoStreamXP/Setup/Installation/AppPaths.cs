namespace OrthoStreamXP.Setup;

/// <summary>
/// Where OrthoStreamXP keeps its own files, apart from the program (an installer or an update replaces the program's
/// folder, and Program Files is not writable) and from the cache (it may be on another drive or be deleted):
/// %LOCALAPPDATA%\OrthoStreamXP with the settings, logs\ (the session logs) and history\ (flights, tuning, learnt colours).
/// The cache keeps only what describes it: when each area was flown, and the cache map's pictures.
/// </summary>
internal static class AppPaths
{
    public static string Root { get; } = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrthoStreamXP");

    /// <summary>The settings file (its tuning and connection test beside it).</summary>
    public static string Settings => Path.Join(Root, SettingsOptions.DefaultSettingsFile);

    /// <summary>The session logs, and the read and camera logs when they are on.</summary>
    public static string Logs => Path.Join(Root, "logs");

    /// <summary>What sessions leave for later ones: flight reports and their history, the flight review, the graphics memory, learnt colours, X-Plane's date.</summary>
    public static string History => Path.Join(Root, "history");
}
