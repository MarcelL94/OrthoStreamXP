using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Setup;

/// <summary>One file of a region's release: its download address, size, and the address of its SHA-256 when one is published.</summary>
internal sealed record MeshAsset(string Name, string Url, long Size, string? Sha256Url = null);

/// <summary>
/// A region of AutoOrtho's scenery mesh (github.com/kubilus1/autoortho-scenery): the zips of its mesh, each holding one
/// top folder with Earth nav data, terrain and textures, and the parts of one overlays zip (X-Plane's roads, forests and
/// buildings for its squares). Installed as the package <see cref="PackageName"/>, its overlays into
/// <see cref="OverlaysPackageName"/>.
/// </summary>
internal sealed record MeshRegion(string Id, string Release, IReadOnlyList<MeshAsset> Mesh, IReadOnlyList<MeshAsset> Overlays)
{
    /// <summary>The package every region's overlays go into.</summary>
    public const string OverlaysPackageName = SceneryPacks.DownloadedPrefix + "Overlays";

    /// <summary>The regions in the order they are listed, with their names and their packages' names.</summary>
    public static readonly IReadOnlyList<(string Id, string Name, string Package)> Known =
    [
        ("eur", "Europe", SceneryPacks.DownloadedPrefix + "Eur"),
        ("na", "North America", SceneryPacks.DownloadedPrefix + "NA"),
        ("sa", "South America", SceneryPacks.DownloadedPrefix + "SA"),
        ("afr", "Africa", SceneryPacks.DownloadedPrefix + "Afr"),
        ("asi", "Asia", SceneryPacks.DownloadedPrefix + "Asi"),
        ("aus_pac", "Australia and the Pacific", SceneryPacks.DownloadedPrefix + "AusPac"),
    ];

    public static bool IsKnown(string id) => Known.Any(k => k.Id == id);

    public static string NameOf(string id) => Known.FirstOrDefault(k => k.Id == id).Name ?? id;

    public static string PackageOf(string id) => Known.FirstOrDefault(k => k.Id == id).Package ?? SceneryPacks.DownloadedPrefix + id;

    public string Name => NameOf(Id);

    public string PackageName => PackageOf(Id);

    public long MeshSize => Mesh.Sum(a => a.Size);

    public long OverlaysSize => Overlays.Sum(a => a.Size);

    /// <summary>What an install downloads, with or without the overlays.</summary>
    public long DownloadSize(bool withOverlays) => MeshSize + (withOverlays ? OverlaysSize : 0);
}
