using System.Globalization;
using System.Text.RegularExpressions;

namespace OrthoStreamXP.Textures;

/// <summary>
/// How each texture is built from the level-change setting. The zoom depends on the texture's own zoom: ZL18 textures (around
/// airports) use 18 + level, ZL16 textures one level finer up to level 0, 16 + (level + 1), so at the same setting the
/// wide ZL16 areas are not left much softer than the airports; above level 0 they use 16 + level (Extra +1: zoom 17,
/// as Ultra, while the airports get zoom 19). Other zooms use zoom + level.
/// <para>
/// The size follows the level too: 4096 px at level 0 and above, half per level below (Balanced -2: 1024, Sharp -1:
/// 2048, Ultra 0 and Extra +1: 4096), the same for ZL16 and ZL18. Below level 0 ZL16 textures download one zoom more
/// detail than that, and at level +1 ZL18 ones do, shrunk as it arrives: the finer imagery gives a cleaner picture than
/// the size alone would.
/// </para>
/// </summary>
/// <param name="Limit">A size limit instead of the level's own (tests of the streaming engine at full detail).</param>
internal sealed partial record TexturePlan(int LevelChange, int? Limit = null)
{
    /// <summary>Finer than the downloaded 4096 px detail at the file zoom; BuildAsync accepts up to +3.</summary>
    public const int MaxLevel = 3;

    /// <summary>The largest texture at this level.</summary>
    public int SizeLimit => Limit ?? TextureSizing.LimitFor(LevelChange);

    /// <summary>The level change applied to a texture of <paramref name="fileZoom"/>.</summary>
    public int LevelFor(int fileZoom) => Math.Clamp(LevelChange + (fileZoom == 16 && LevelChange < 1 ? 1 : 0), -4, MaxLevel);

    /// <summary>The zoom its tiles are downloaded at.</summary>
    public int FetchZoom(int fileZoom) => fileZoom + LevelFor(fileZoom);

    /// <summary>Its size in pixels: the downloaded detail, capped by the level's limit, never enlarged.</summary>
    public int SizeFor(int fileZoom) => Math.Min(TextureSizing.DownloadedDetail(LevelFor(fileZoom)), SizeLimit);

    /// <summary>
    /// A texture's name in the cache: the row and column of its top-left tile at the zoom it is downloaded at, and that
    /// zoom, such as 87520_143440_BI18 → 43760_71720_PX17 at level -1. The package's code is left out: it never chose
    /// the imagery. ZL17 keeps its package name: it is downloaded at the zoom of ZL16 (one level finer), where a ZL16
    /// texture twice as wide could start at the same tile.
    /// </summary>
    public string CacheStem(TextureName name)
    {
        if (name.Zoom == 17) return name.Stem;
        int level = LevelFor(name.Zoom);
        return FormattableString.Invariant($"{Shift(name.Row, level)}_{Shift(name.Col, level)}_{TextureName.CacheCode}{FetchZoom(name.Zoom)}");
    }

    /// <summary>The texture a cache name (<see cref="CacheStem"/>) stands for, with the cache code; null for other names.</summary>
    public TextureName? FromCacheStem(string stem)
    {
        Match m = CacheStemPattern().Match(stem);
        if (!m.Success
            || !int.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out int row)
            || !int.TryParse(m.Groups[2].Value, CultureInfo.InvariantCulture, out int col)
            || !int.TryParse(m.Groups[3].Value, CultureInfo.InvariantCulture, out int fetchZoom))
            return null;
        for (int zoom = 4; zoom <= 22; zoom++)
        {
            if (zoom == 17 || FetchZoom(zoom) != fetchZoom) continue;
            int level = LevelFor(zoom);
            if (TextureName.TryCreate(Shift(row, -level), Shift(col, -level), TextureName.CacheCode, zoom, out TextureName name)
                && CacheStem(name) == stem)
                return name;
        }
        return null;
    }

    private static int Shift(int tile, int levels) => levels >= 0 ? tile << levels : tile >> -levels;

    [GeneratedRegex(@"^(\d+)_(\d+)_PX(\d+)$")]
    private static partial Regex CacheStemPattern();

    /// <summary>The size for a texture file name such as 21904_35824_BI16.dds, or null when it is not a photo tile.</summary>
    public int? SizeFor(string textureFileName) => TextureName.TryParseFile(textureFileName, out TextureName name) ? SizeFor(name.Zoom) : null;

    /// <summary>
    /// The cache subfolder for textures of this plan, such as Balanced_1024-1024 (the quality preset, or level-3 for a
    /// level no preset has, then the ZL16 and ZL18 sizes): settings that build the same textures share it, any other
    /// setting gets its own.
    /// </summary>
    public string FolderName => FormattableString.Invariant($"{Setup.QualityPreset.ForLevel(LevelChange)?.Name ?? $"level{LevelChange:+0;-0;0}"}_{SizeFor(16)}-{SizeFor(18)}");

    private static readonly int[] PackageZooms = [16, 18];

    /// <summary>One line for the log, e.g. "ZL16 → zoom 15, 1024 px; ZL18 → zoom 16, 1024 px".</summary>
    public string Describe() => string.Join("; ", PackageZooms.Select(z => $"ZL{z} → zoom {FetchZoom(z)}, {SizeFor(z)} px"));
}

internal static class TextureSizing
{
    /// <summary>The largest texture built.</summary>
    public const int MaxSize = 4096;

    /// <summary>
    /// Pixels of detail level-change downloads for one texture: 4096 at the file's zoom (16 x 16 tiles of 256 px),
    /// half as much per level below. Above level 0 the detail is finer than 4096, so it is shrunk to 4096.
    /// </summary>
    public static int DownloadedDetail(int levelChange) => levelChange >= 0 ? MaxSize : MaxSize >> -levelChange;

    /// <summary>
    /// The largest texture at a level: 4096 px at level 0 and above, half per level below. That is the detail a ZL18
    /// texture downloads, so ZL16 textures, fetched one level finer, are shrunk to the same size.
    /// </summary>
    public static int LimitFor(int levelChange) => DownloadedDetail(levelChange);
}
