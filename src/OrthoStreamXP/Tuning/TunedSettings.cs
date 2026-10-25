using System.Globalization;
using System.Text.Json;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Tuning;

/// <summary>A setting the flight tuning may change: its name in plain words and how its value reads.</summary>
/// <param name="PerMode">Belongs to the quality preset (remembered for it); otherwise it is the connection's or the screen's, shared by all.</param>
internal sealed record TunableSetting(string Key, string Label, string Unit, bool PerMode)
{
    public string Show(string? value) => value switch
    {
        null or "" or "auto" => "automatic",
        _ when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) =>
            number.ToString(number >= 10_000 ? "N0" : "0.###", CultureInfo.CurrentCulture) + Unit,
        _ => value,
    };
}

/// <summary>
/// The settings the window no longer shows: each quality preset sets them, and the flight tuning adjusts some of
/// them for this PC and connection. What was tuned for a preset is remembered (orthostreamxp.tuned.json beside the
/// settings file), so switching presets and back keeps it; Reset puts the preset's own values back.
/// </summary>
internal static class TunedSettings
{
    public static readonly TunableSetting NearWait = new("near-wait", "Wait at a start for the scenery next to the aircraft", " s", PerMode: true);
    public static readonly TunableSetting MaxWait = new("max-wait", "Wait for other scenery before a stand-in", " s", PerMode: true);
    public static readonly TunableSetting BuildMemory = new("build-memory", "Memory for building textures", " MB", PerMode: true);
    public static readonly TunableSetting BuildTiles = new("build-tiles", "Tiles downloading at once for builds", " tiles", PerMode: true);
    public static readonly TunableSetting DownloadWorkers = new("download-workers", "Downloads at once", "", PerMode: false);
    public static readonly TunableSetting PagingK = new("paging-k", "Detail by distance (paging k)", "", PerMode: false);

    public static IReadOnlyList<TunableSetting> All { get; } = [NearWait, MaxWait, BuildMemory, BuildTiles, DownloadWorkers, PagingK];

    public static TunableSetting? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>The hidden settings' defaults at a preset: its own values, the automatic paging model and the download retries.</summary>
    public static IReadOnlyDictionary<string, string?> Defaults(QualityPreset preset)
    {
        var defaults = new Dictionary<string, string?>(preset.Settings)
        {
            ["paging-k"] = "auto",
            ["download-timeout"] = TextOf(Textures.TileSource.DefaultTimeoutSeconds),
            ["download-attempts"] = TextOf(Textures.TileSource.DefaultAttempts),
        };
        return defaults;
    }

    /// <summary>Chooses a preset: its values, then what was tuned for it before.</summary>
    public static void ChoosePreset(SettingsDocument settings, QualityPreset preset, TunedStore store)
    {
        preset.ApplyTo(settings);
        foreach (var (key, value) in store.For(preset.Level)) settings.Set(key, value);
    }

    /// <summary>Puts every hidden setting back to the preset's defaults and forgets what was tuned for it.</summary>
    public static void Reset(SettingsDocument settings, QualityPreset preset, TunedStore store)
    {
        settings.Set("level-change", preset.Level);
        foreach (var (key, value) in Defaults(preset)) settings.Set(key, value);
        store.Clear(preset.Level);
    }

    /// <summary>The tunable settings that differ from the preset's defaults now: (setting, value, default).</summary>
    public static IReadOnlyList<(TunableSetting Setting, string? Value, string? Default)> Changed(SettingsDocument settings, QualityPreset preset)
    {
        IReadOnlyDictionary<string, string?> defaults = Defaults(preset);
        var changed = new List<(TunableSetting, string?, string?)>();
        foreach (TunableSetting setting in All)
        {
            if (!defaults.TryGetValue(setting.Key, out string? fallback)) continue; // download-workers: shown in the window
            string? value = settings.Get(setting.Key);
            if (!Same(value ?? fallback, fallback)) changed.Add((setting, value, fallback));
        }
        return changed;
    }

    /// <summary>Whether any hidden setting differs from the preset's defaults (Reset would change something).</summary>
    public static bool DiffersFromDefaults(SettingsDocument settings, QualityPreset preset) =>
        settings.GetInt("level-change") != preset.Level
        || Defaults(preset).Any(d => d.Value is not null && !Same(settings.Get(d.Key), d.Value));

    /// <summary>Applies a review's changes for its preset; false when the settings are at another preset now.</summary>
    public static bool Apply(FlightReview review, SettingsDocument settings, TunedStore store)
    {
        if ((settings.GetInt("level-change") ?? 0) != review.Level) return false;
        foreach (Suggestion suggestion in review.Suggestions)
        {
            if (suggestion.Key is not { } key || Find(key) is not { } setting) continue;
            settings.Set(key, suggestion.To);
            if (setting.PerMode) store.Set(review.Level, key, suggestion.To);
        }
        return true;
    }

    private static bool Same(string? a, string? b) =>
        a == b || double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
               && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && x == y;

    internal static string TextOf(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>What the flight tuning set for each preset (by level change), kept beside the settings file.</summary>
internal sealed class TunedStore
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly Dictionary<int, Dictionary<string, string?>> _levels;

    private TunedStore(string path, Dictionary<int, Dictionary<string, string?>> levels)
    {
        Path = path;
        _levels = levels;
    }

    public string Path { get; }

    /// <summary>orthostreamxp.tuned.json beside orthostreamxp.ini.</summary>
    public static string PathFor(string settingsPath) => System.IO.Path.ChangeExtension(settingsPath, ".tuned.json");

    public static TunedStore Load(string settingsPath)
    {
        string path = PathFor(settingsPath);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<Dictionary<int, Dictionary<string, string?>>>(File.ReadAllText(path)) is { } levels)
                return new TunedStore(path, levels);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not read the tuned settings in {path}: {ex.Message}");
        }
        return new TunedStore(path, []);
    }

    public IReadOnlyDictionary<string, string?> For(int level) => _levels.TryGetValue(level, out var values) ? values : new Dictionary<string, string?>();

    public void Set(int level, string key, string? value)
    {
        if (!_levels.TryGetValue(level, out var values)) _levels[level] = values = [];
        values[key] = value;
    }

    public void Clear(int level) => _levels.Remove(level);

    public void Save()
    {
        if (_levels.Count == 0)
        {
            File.Delete(Path);
            return;
        }
        AtomicFile.Write(Path, JsonSerializer.SerializeToUtf8Bytes(_levels, Indented));
    }
}
