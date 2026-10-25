namespace OrthoStreamXP.Setup;

/// <summary>
/// Moves what earlier versions left elsewhere to where OrthoStreamXP keeps it now (<see cref="AppPaths"/>), once, so
/// nothing is set up, learnt or downloaded again:
/// <list type="bullet">
/// <item>The settings file and its companions (.tuned.json, .connection.json) lived beside the program, first named
/// photo-xp: they move to the app's folder as orthostreamxp.*.</item>
/// <item>The cache's own folders were .photo-xp-logs, .photo-xp-base and .photo-xp-landcover: they take the
/// .orthostreamxp-* names.</item>
/// <item>The setup folders were named by level, such as level0_4096-4096: those of a preset take its name
/// (Ultra_4096-4096).</item>
/// <item>The cache's logs folder held the logs and the flights' history too: the logs move to the app's logs folder,
/// the history to its history folder. What describes the cache (area-usage.json, the map's pictures) stays.</item>
/// </list>
/// Nothing is moved over a file or folder that already exists, and what is in use stays for the next start.
/// </summary>
internal static class LegacyNames
{
    private const string OldPrefix = "photo-xp", NewPrefix = "orthostreamxp";
    private static readonly string[] SettingsCompanions = [".ini", ".tuned.json", ".connection.json"];
    private static readonly string[] CacheFolders = ["logs", "base", "landcover"];
    /// <summary>The files of the history that sessions left in the cache's logs folder.</summary>
    private static readonly string[] HistoryFiles = ["flight-report.json", "flight-review.json", "graphics-memory.json", "colour-match.json", "sim-date.txt"];

    /// <summary>Moves the settings files from <paramref name="fromFolder"/> (under either name) to <paramref name="settingsPath"/> and beside it.</summary>
    public static void MoveSettings(string fromFolder, string settingsPath)
    {
        string to = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;
        if (string.Equals(Path.GetFullPath(fromFolder).TrimEnd('\\'), to.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            foreach (string extension in SettingsCompanions)
                MoveFile(Path.Join(to, OldPrefix + extension), Path.Join(to, NewPrefix + extension));
            return;
        }
        foreach (string extension in SettingsCompanions)
            foreach (string prefix in new[] { NewPrefix, OldPrefix })
                MoveFile(Path.Join(fromFolder, prefix + extension), Path.Join(to, NewPrefix + extension));
    }

    /// <summary>
    /// Renames the cache's .photo-xp-* folders and its presets' level folders, then moves the logs and the history out of its logs folder to
    /// <paramref name="logs"/> and <paramref name="history"/>.
    /// </summary>
    public static void MoveCache(string? cache, string logs, string history)
    {
        if (string.IsNullOrWhiteSpace(cache) || !Directory.Exists(cache)) return;
        foreach (string name in CacheFolders)
            MoveFolder(Path.Join(cache, $".{OldPrefix}-{name}"), Path.Join(cache, $".{NewPrefix}-{name}"));
        foreach (string setupFolder in Directory.EnumerateDirectories(cache, "level*").ToArray())
        {
            string name = Path.GetFileName(setupFolder);
            if (CacheInventory.ParseSetupName(name) is { } setup && QualityPreset.ForLevel(setup.Level) is { } preset)
                MoveFolder(setupFolder, Path.Join(cache, preset.Name + name[name.IndexOf('_', StringComparison.Ordinal)..]));
        }

        string folder = Path.Join(cache, CacheInventory.LogsFolder);
        if (!Directory.Exists(folder)) return;
        // The log, under either name and with its rotated parts (orthostreamxp.log.1 ...), and the read and camera logs.
        foreach (string file in Directory.EnumerateFiles(folder, "*.log*").Concat(Directory.EnumerateFiles(folder, "reads-*.csv"))
                     .Concat(Directory.EnumerateFiles(folder, "camera-*.csv")).ToArray())
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith(OldPrefix, StringComparison.OrdinalIgnoreCase)) name = NewPrefix + name[OldPrefix.Length..];
            MoveFile(file, Path.Join(logs, name));
        }
        foreach (string name in HistoryFiles)
            MoveFile(Path.Join(folder, name), Path.Join(history, name));
        string flights = Path.Join(folder, "flights");
        if (Directory.Exists(flights))
            foreach (string file in Directory.EnumerateFiles(flights).ToArray())
                MoveFile(file, Path.Join(history, "flights", Path.GetFileName(file)));
    }

    private static void MoveFolder(string from, string to)
    {
        if (!Directory.Exists(from) || Directory.Exists(to)) return;
        try
        {
            Directory.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (another copy running): moved at the next start.
        }
    }

    private static void MoveFile(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use: moved at the next start.
        }
    }
}
