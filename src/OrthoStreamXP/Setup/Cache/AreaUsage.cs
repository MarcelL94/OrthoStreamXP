using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>
/// When each area was last flown: noted as X-Plane opens its textures, kept in &lt;cache&gt;\.orthostreamxp-logs\area-usage.json.
/// The cache limit removes the areas flown longest ago first.
/// </summary>
internal sealed class AreaUsage
{
    public const string FileName = "area-usage.json";
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private readonly string _path;
    private readonly ConcurrentDictionary<AreaName, DateTime> _used = new();
    private int _changed;

    private AreaUsage(string path) => _path = path;

    public static AreaUsage Load(string cache)
    {
        var usage = new AreaUsage(Path.Join(cache, CacheInventory.LogsFolder, FileName));
        try
        {
            if (File.Exists(usage._path) && JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(usage._path)) is { } saved)
                foreach (var (text, when) in saved)
                    if (AreaName.TryParse(text, out AreaName area)) usage._used[area] = when;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not read {usage._path}: {ex.Message}");
        }
        return usage;
    }

    /// <summary>X-Plane opened a texture of this area now.</summary>
    public void Note(AreaName area)
    {
        DateTime now = DateTime.UtcNow;
        // Once a minute per area is plenty: X-Plane opens hundreds of textures of an area at once.
        if (_used.TryGetValue(area, out DateTime last) && now - last < TimeSpan.FromMinutes(1)) return;
        _used[area] = now;
        Interlocked.Exchange(ref _changed, 1);
    }

    public DateTime? LastUsed(AreaName area) => _used.TryGetValue(area, out DateTime when) ? when : null;

    public void Forget(IEnumerable<AreaName> areas)
    {
        foreach (AreaName area in areas) _used.TryRemove(area, out _);
        Interlocked.Exchange(ref _changed, 1);
    }

    /// <summary>Saves what changed since the last save.</summary>
    public void Save()
    {
        if (Interlocked.Exchange(ref _changed, 0) == 0) return;
        try
        {
            var data = _used.OrderBy(u => u.Key.Lat).ThenBy(u => u.Key.Lon).ToDictionary(u => u.Key.ToString(), u => u.Value);
            AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(data, Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save {_path}: {ex.Message}");
        }
    }
}
