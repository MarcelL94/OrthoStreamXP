using System.Globalization;
using System.Text.RegularExpressions;

namespace OrthoStreamXP.Setup;

internal enum CacheEntryKind
{
    /// <summary>One texture plan's subfolder, such as Balanced_1024-1024.</summary>
    Setup,
    /// <summary>The low-zoom base (.orthostreamxp-base): coarse tiles far scenery is drawn from, shared by every setup.</summary>
    Base,
    /// <summary>The .orthostreamxp-logs folder.</summary>
    Logs,
    /// <summary>Anything else, such as a package folder from the layout before setups were separated.</summary>
    Other,
}

/// <summary>A folder in the cache and what it holds.</summary>
internal sealed record CacheEntry(string Name, string Path, CacheEntryKind Kind, long Bytes, int Files, DateTime? LastWriteUtc)
{
    /// <summary>The level and sizes of a setup folder, from its name.</summary>
    public SetupName? Setup => CacheInventory.ParseSetupName(Name);
}

/// <param name="Zl16Size">Texture size of the ZL16 areas, in pixels.</param>
/// <param name="Zl18Size">Texture size of the ZL18 (airport) areas, in pixels.</param>
internal sealed record SetupName(int Level, int Zl16Size, int Zl18Size);

/// <summary>What the cache folder holds: one entry per setup, the logs and anything else.</summary>
internal static partial class CacheInventory
{
    public const string LogsFolder = ".orthostreamxp-logs";
    /// <summary>The land cover earlier versions fetched for the seasons: no longer made, still known as OrthoStreamXP's own.</summary>
    public const string LandCoverFolder = ".orthostreamxp-landcover";

    [GeneratedRegex(@"^(?:level([+-]?\d+)|([A-Za-z]+))_(\d+)-(\d+)$")]
    private static partial Regex SetupPattern();

    /// <summary>
    /// The level and sizes of a setup folder such as Ultra_4096-4096, or level-3_512-512 for a level no preset has
    /// (and every level before setups took their preset's name); null for other names.
    /// </summary>
    public static SetupName? ParseSetupName(string name)
    {
        Match m = SetupPattern().Match(name);
        if (!m.Success) return null;
        int? level = m.Groups[1].Success
            ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : QualityPreset.All.FirstOrDefault(p => p.Name.Equals(m.Groups[2].Value, StringComparison.OrdinalIgnoreCase))?.Level;
        return level is { } l
            ? new SetupName(l, int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture))
            : null;
    }

    /// <summary>
    /// The folders OrthoStreamXP made in a cache folder: the setups (Ultra_..., level...), the low-zoom base, the land cover of earlier versions and the
    /// logs folder. Nothing else: the cache folder is the person's choice and may hold other things.
    /// </summary>
    public static IEnumerable<string> OwnFolders(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateDirectories(root).Where(folder => System.IO.Path.GetFileName(folder) is var name
                && (ParseSetupName(name) is not null || name is LogsFolder || name == Textures.TileStore.FolderName || name is LandCoverFolder))
            : [];

    /// <summary>Measures every folder in the cache (it reads every file's size, so call it off the UI thread).</summary>
    public static IReadOnlyList<CacheEntry> Scan(string root, CancellationToken cancel = default)
    {
        if (!Directory.Exists(root)) return [];
        var entries = new List<CacheEntry>();
        foreach (string folder in Directory.EnumerateDirectories(root))
        {
            cancel.ThrowIfCancellationRequested();
            string name = System.IO.Path.GetFileName(folder);
            CacheEntryKind kind = name == LogsFolder ? CacheEntryKind.Logs
                : name == Textures.TileStore.FolderName ? CacheEntryKind.Base
                : ParseSetupName(name) is not null ? CacheEntryKind.Setup
                : CacheEntryKind.Other;
            var (bytes, files, last) = Measure(folder, cancel);
            entries.Add(new CacheEntry(name, folder, kind, bytes, files, last));
        }
        return [.. entries.OrderBy(e => e.Kind).ThenBy(e => e.Setup?.Level ?? 0).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static (long Bytes, int Files, DateTime? LastWriteUtc) Measure(string folder, CancellationToken cancel)
    {
        long bytes = 0;
        int files = 0;
        DateTime? last = null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            if ((++files & 1023) == 0) cancel.ThrowIfCancellationRequested();
            bytes += file.Length;
            if (last is null || file.LastWriteTimeUtc > last) last = file.LastWriteTimeUtc;
        }
        return (bytes, files, last);
    }

    /// <summary>"7.1 GB", "830 MB", "12 KB".</summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} bytes",
    };
}
