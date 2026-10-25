using System.Globalization;
using System.Text;

namespace OrthoStreamXP.Setup;

/// <summary>
/// The settings file (orthostreamxp.ini) as the window edits it: values are changed in their own lines, so the
/// comments and the order a person wrote stay as they are. A setting that is not in the file yet is added
/// at its end.
/// </summary>
internal sealed class SettingsDocument
{
    private readonly List<string> _lines;

    private SettingsDocument(string path, List<string> lines)
    {
        Path = path;
        _lines = lines;
    }

    public string Path { get; }

    /// <summary>The settings file in the app's own folder (<see cref="AppPaths"/>).</summary>
    public static string DefaultPath => AppPaths.Settings;

    public IReadOnlyList<string> Lines => _lines;

    /// <summary>Reads the file; a missing file is an empty document (saved on the first change).</summary>
    public static SettingsDocument Load(string path) =>
        new(path, File.Exists(path) ? [.. File.ReadAllLines(path)] : []);

    public static SettingsDocument FromText(string path, string text) =>
        new(path, [.. text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n')]);

    /// <summary>The value of a setting, or null when it is missing or empty (not set).</summary>
    public string? Get(string name)
    {
        string? value = null;
        foreach (string line in _lines)
            if (Split(line) is { } setting && setting.Name == name) value = setting.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Every setting the file sets, with the value that counts (the last line's); empty values left out.</summary>
    public IReadOnlyDictionary<string, string> Values()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in _lines)
            if (Split(line) is { } setting)
            {
                if (setting.Value.Length == 0) values.Remove(setting.Name);
                else values[setting.Name] = setting.Value;
            }
        return values;
    }

    public int? GetInt(string name) =>
        int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    public double? GetDouble(string name) =>
        double.TryParse(Get(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : null;

    /// <summary>A flag or an on/off setting; null when it is not set.</summary>
    public bool? GetBool(string name) => Get(name) is { } value ? MountSettings.ParseOnOff(value) ?? value == "1" : null;

    /// <summary>
    /// Sets a value in the line that has it (the last one, which is the one that counts), keeping the line's
    /// layout; null leaves the setting empty, which means "not set". A new setting goes at the end.
    /// </summary>
    public void Set(string name, string? value)
    {
        for (int i = _lines.Count - 1; i >= 0; i--)
        {
            if (Split(_lines[i]) is not { } setting || setting.Name != name) continue;
            string prefix = _lines[i][..(_lines[i].IndexOf('=', StringComparison.Ordinal) + 1)];
            _lines[i] = value is null ? prefix : $"{prefix} {value}";
            return;
        }
        if (value is null) return;
        if (_lines.Count > 0 && _lines[^1].Length > 0 && !_lines.Any(l => l.StartsWith(AddedHeader, StringComparison.Ordinal)))
            _lines.Add("");
        if (!_lines.Any(l => l.StartsWith(AddedHeader, StringComparison.Ordinal))) _lines.Add(AddedHeader);
        _lines.Add($"{name,-18} = {value}");
    }

    public void Set(string name, int value) => Set(name, value.ToString(CultureInfo.InvariantCulture));

    public void Set(string name, double value) => Set(name, value.ToString("0.###", CultureInfo.InvariantCulture));

    private const string AddedHeader = "# --- Set in the OrthoStreamXP window";

    /// <summary>Writes the file whole, so a reader never sees half of it.</summary>
    public void Save() => AtomicFile.Write(Path, new UTF8Encoding(false).GetBytes(string.Join(Environment.NewLine, _lines) + Environment.NewLine));

    /// <summary>The mount options in the file, checked as a mount checks them.</summary>
    public SettingValues Parse() => SettingsOptions.Schema.Read(_lines, Path);

    private static (string Name, string Value)? Split(string raw)
    {
        string line = raw.Trim();
        if (line.Length == 0 || line[0] is '#' or ';' or '[') return null;
        int equals = line.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0) return null;
        return (line[..equals].Trim().TrimStart('-'), line[(equals + 1)..].Trim().Trim('"'));
    }

    /// <summary>A new settings file, laid out and commented like the examples.</summary>
    public static SettingsDocument CreateNew(string path, string xplane, string basePackages, string cache, QualityPreset preset)
    {
        var text = new StringBuilder();
        text.AppendLine("# OrthoStreamXP settings, made by the OrthoStreamXP window. The window changes these values in place;")
            .AppendLine("# you can also edit them here, with OrthoStreamXP stopped.")
            .AppendLine("# The quality preset sets most of them, and the window tunes some after each flight with your OK.")
            .AppendLine()
            .AppendLine("# --- Where things live -------------------------------------------------------------")
            .AppendLine("# the folder of base packages: every subfolder with Earth nav data is mounted as Custom Scenery\\<its name>")
            .AppendLine(CultureInfo.InvariantCulture, $"base-packages      = {basePackages}")
            .AppendLine("# the X-Plane folder; the mounted packages are moved to the end of Custom Scenery\\scenery_packs.ini (on or off)")
            .AppendLine(CultureInfo.InvariantCulture, $"xplane             = {xplane}")
            .AppendLine("scenery-packs      = on")
            .AppendLine("# one folder for every setup: each level and texture size keeps its textures in its own subfolder")
            .AppendLine(CultureInfo.InvariantCulture, $"cache              = {cache}")
            .AppendLine("# the most the textures in the cache may take, in GB (empty: no limit); the areas flown longest ago go first")
            .AppendLine("cache-limit        =")
            .AppendLine("lazy-texture-store =")
            .AppendLine("# none, console, log-file or both")
            .AppendLine("log-output         = both")
            .AppendLine()
            .AppendLine("# --- Pictures ---------------------------------------------------------------------")
            .AppendLine("# bing, esri, or several tried in order for each tile: a tile the first lacks comes from the next")
            .AppendLine("texture-provider   = bing,esri")
            .AppendLine(CultureInfo.InvariantCulture, $"# quality preset: {preset.Name}; the level also sets the texture size ({preset.Size} px)")
            .AppendLine(CultureInfo.InvariantCulture, $"level-change       = {preset.Level.ToString(CultureInfo.InvariantCulture)}")
            .AppendLine()
            .AppendLine("# --- Speed ------------------------------------------------------------------------")
            .AppendLine(CultureInfo.InvariantCulture, $"dds-build-workers  = {preset.Get("dds-build-workers")}")
            .AppendLine("download-workers   = 128")
            .AppendLine(CultureInfo.InvariantCulture, $"encoder-workers    = {preset.Get("encoder-workers")}")
            .AppendLine("download-timeout   = 5")
            .AppendLine("download-attempts  = 4")
            .AppendLine(CultureInfo.InvariantCulture, $"filesystem-threads = {preset.Get("filesystem-threads")}")
            .AppendLine()
            .AppendLine("# --- Cold start -------------------------------------------------------------------")
            .AppendLine("# serve textures level by level at the detail X-Plane needs (on or off); the waits are in seconds")
            .AppendLine("mip-streaming        = on")
            .AppendLine(CultureInfo.InvariantCulture, $"near-wait            = {preset.Get("near-wait")}")
            .AppendLine(CultureInfo.InvariantCulture, $"max-wait             = {preset.Get("max-wait")}")
            .AppendLine("# what the builds may hold and download at once (MB, tiles)")
            .AppendLine(CultureInfo.InvariantCulture, $"build-memory         = {preset.Get("build-memory")}")
            .AppendLine(CultureInfo.InvariantCulture, $"build-tiles          = {preset.Get("build-tiles")}")
            .AppendLine("# when mip-streaming is off: a quick blurrier stand-in while a texture builds")
            .AppendLine(CultureInfo.InvariantCulture, $"provisional-lod      = {preset.Get("provisional-lod")}")
            .AppendLine(CultureInfo.InvariantCulture, $"provisional-wait     = {preset.Get("provisional-wait")}")
            .AppendLine("lazy-terrain-listing = true")
            .AppendLine()
            .AppendLine("# --- Building ahead of X-Plane ----------------------------------------------------")
            .AppendLine("prefetch-loaded-scenery   = on")
            .AppendLine()
            .AppendLine("# --- Following the aircraft -------------------------------------------------------")
            .AppendLine("track-aircraft            = true")
            .AppendLine(CultureInfo.InvariantCulture, $"prefetch-radius-km         = {preset.Get("prefetch-radius-km")}")
            .AppendLine(CultureInfo.InvariantCulture, $"prefetch-lookahead-seconds = {preset.Get("prefetch-lookahead-seconds")}")
            .AppendLine(CultureInfo.InvariantCulture, $"prefetch-textures         = {preset.Get("prefetch-textures")}")
            .AppendLine()
            .AppendLine("# --- Paging model (mip streaming) -------------------------------------------------")
            .AppendLine("# which mip level X-Plane wants at which distance: auto guesses from the screen and the flight tuning measures it")
            .AppendLine("paging-k                  = auto")
            .AppendLine("read-log                  = on")
            .AppendLine()
            .AppendLine("# --- Seasons ----------------------------------------------------------------------")
            .AppendLine("# colour the photos for the season of X-Plane's date and the place (on or off); the cache keeps them as downloaded")
            .AppendLine("seasons                   = off")
            .AppendLine("# auto, or early-spring, spring, summer, early-autumn, autumn, late-autumn or winter everywhere")
            .AppendLine("season                    = auto")
            .AppendLine("# 0-100: how far the colours move towards the season")
            .AppendLine("season-strength           = 80")
            .AppendLine()
            .AppendLine("# --- Exposure ---------------------------------------------------------------------")
            .AppendLine("# 50-100: how much light the photos let through; less darkens them evenly")
            .AppendLine("exposure                  = 100");
        SettingsDocument settings = FromText(path, text.ToString());
        preset.ApplyTo(settings); // every value of the preset, whatever the layout above lists
        return settings;
    }
}
