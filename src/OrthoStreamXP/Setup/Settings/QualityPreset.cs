using System.Globalization;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>
/// A ready-made speed and sharpness choice: the level change and every setting the window does not show, as the test
/// flights tuned them for it (the same values as tests\OrthoStreamXP.Tests\examples\orthostreamxp-&lt;level&gt;.ini). The connection's own settings
/// (download-workers, timeout, attempts) and the paging model are not part of a preset, so choosing one keeps them.
/// The flight tuning adjusts some of these values for the PC and connection (<see cref="Tuning.TunedSettings"/>).
/// </summary>
internal sealed record QualityPreset(string Name, int Level, string Summary, string BestFor, string FirstVisit,
    IReadOnlyDictionary<string, string?> Settings)
{
    public static readonly QualityPreset Balanced = new("Balanced", -2,
        "1024 px textures from zoom 15-16 imagery: light on graphics memory and the connection, fine from higher up.",
        "High flying, a slower connection or little graphics memory", "about 3 min",
        Values(buildWorkers: 16, fileSystemThreads: 64, provisionalLod: 0, provisionalWait: null, radiusKm: 6, textures: 16, buildMemoryMb: 512, lookahead: 180));

    public static readonly QualityPreset Sharp = new("Sharp", -1,
        "2048 px textures from zoom 16-17 imagery: twice as sharp as Balanced.",
        "Everyday flying, approaches included", "about 3 min",
        Values(buildWorkers: 8, fileSystemThreads: 96, provisionalLod: 2, provisionalWait: 5, radiusKm: 5, textures: 8, buildMemoryMb: 512));

    public static readonly QualityPreset Ultra = new("Ultra", 0,
        "4096 px textures from zoom 17-18 imagery: twice as sharp as Sharp.",
        "Low flight and GA, with plenty of graphics memory", "about 3 min",
        Values(buildWorkers: 8, fileSystemThreads: 96, provisionalLod: 3, provisionalWait: 3, radiusKm: 4, textures: 6, buildMemoryMb: 1024));

    // Level +1: ZL18 airports at zoom 19, ZL16 areas at zoom 17 (as Ultra), as 4096 px textures. The finer, newer imagery
    // of zoom 19 often avoids the clouds and other artifacts of the coarser ones. A ZL18 texture downloads 1024 tiles,
    // four times Ultra's; a ZL16 texture the same 1024 as Ultra's.
    public static readonly QualityPreset Extra = new("Extra", 1,
        "4096 px textures like Ultra's, with the finest imagery (zoom 19) near airports, which often avoids clouds and other artifacts: four times Ultra's downloads there.",
        "Low and slow flight and screenshots, on a fast connection", "about 4 min, then sharp next to the aircraft",
        Values(buildWorkers: 8, fileSystemThreads: 96, provisionalLod: 3, provisionalWait: 3, radiusKm: 4, textures: 6, buildMemoryMb: 1024, lookahead: 90));

    public static IReadOnlyList<QualityPreset> All { get; } = [Balanced, Sharp, Ultra, Extra];

    /// <summary>The preset with this level change, if one has it.</summary>
    public static QualityPreset? ForLevel(int? level) => All.FirstOrDefault(p => p.Level == level);

    public string? Get(string name) => Settings.TryGetValue(name, out string? value) ? value : null;

    /// <summary>Zoom of the common ZL16 textures at this preset (the ones that cover most of the ground).</summary>
    public int Zl16Zoom => new TexturePlan(Level).FetchZoom(16);

    /// <summary>Zoom of the ZL18 textures at this preset (the ones the packages have near airports).</summary>
    public int Zl18Zoom => new TexturePlan(Level).FetchZoom(18);

    /// <summary>Texture size in the ZL16 areas (the size follows the level).</summary>
    public int Size => new TexturePlan(Level).SizeFor(16);

    /// <summary>Ground size of one texture pixel in ZL16 areas at mid latitudes (45°): a ZL16 texture is about 6.9 km across.</summary>
    public double MetresPerPixel => 2 * Math.PI * 6_378_137.0 * Math.Cos(Math.PI / 4) * Tiles.TextureSpan / (1 << 16) / Size;

    /// <summary>Writes the preset's values (and its level) into the settings.</summary>
    public void ApplyTo(SettingsDocument settings)
    {
        settings.Set("level-change", Level);
        foreach (var (name, value) in Settings) settings.Set(name, value);
    }

    private static Dictionary<string, string?> Values(int buildWorkers, int fileSystemThreads, int provisionalLod,
        double? provisionalWait, double radiusKm, int textures, int buildMemoryMb, double lookahead = MountSettings.DefaultLookaheadSeconds) => new()
    {
        ["dds-build-workers"] = Text(buildWorkers),
        ["encoder-workers"] = "4",
        ["filesystem-threads"] = Text(fileSystemThreads),
        ["provisional-lod"] = Text(provisionalLod),
        ["provisional-wait"] = provisionalWait is { } wait ? Text(wait) : null,
        ["prefetch-radius-km"] = Text(radiusKm),
        ["prefetch-lookahead-seconds"] = Text(lookahead),
        ["prefetch-textures"] = Text(textures),
        ["mip-streaming"] = "on",
        ["near-wait"] = Text(MountSettings.DefaultNearWait),
        ["max-wait"] = Text(MountSettings.DefaultMaxWait),
        ["build-memory"] = Text(buildMemoryMb),
        ["build-tiles"] = Text(Scenery.TextureStreamer.DefaultBuildTiles),
        ["prefetch-loaded-scenery"] = "on",
        ["track-aircraft"] = "true",
        ["lazy-terrain-listing"] = "true",
    };

    private static string Text(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
