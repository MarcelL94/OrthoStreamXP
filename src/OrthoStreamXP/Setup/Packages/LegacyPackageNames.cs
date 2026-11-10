using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Setup;

/// <summary>
/// OrthoStreamXP 1.1.1 installed its regions as OSXP_Eur, OSXP_Overlays..., names OrthoStudio XP's own packages
/// (z_osxp_eur, y_osxp_overlays) are easily mistaken for. They take the <see cref="SceneryPacks.DownloadedPrefix"/> names:
/// <list type="bullet">
/// <item>the packages, in the packages folder, with the hidden download folder of an install that was stopped;</item>
/// <item>their textures in the cache (each setup's folder holds one folder per package);</item>
/// <item>their entries in X-Plane's scenery_packs.ini, in place, so their order and whether they are disabled stay.</item>
/// </list>
/// Only folders OrthoStreamXP installed are renamed (they hold its marker or its overlays record), nothing is renamed over a
/// folder that already exists, and what is in use is renamed at the next start: every step can run again.
/// </summary>
internal static class LegacyPackageNames
{
    public const string OldPrefix = "OSXP_";
    private const string OldWorkFolderName = ".osxp-download";

    /// <summary>Each downloaded package's old name and its new one: the regions' and the overlays'.</summary>
    public static IReadOnlyList<(string Old, string New)> Names { get; } =
        [.. MeshRegion.Known.Select(k => k.Package).Append(MeshRegion.OverlaysPackageName)
            .Select(name => (OldPrefix + name[SceneryPacks.DownloadedPrefix.Length..], name))];

    /// <summary>Renames the packages in <paramref name="packagesFolder"/>, their textures in <paramref name="cache"/> and their entries in the scenery_packs.ini of the X-Plane in <paramref name="xplane"/>.</summary>
    public static void Move(string? packagesFolder, string? cache, string? xplane)
    {
        if (string.IsNullOrWhiteSpace(packagesFolder) || !Directory.Exists(packagesFolder)) return;
        MovePackages(packagesFolder);
        (string Old, string New)[] moved = [.. Names.Where(n => !Directory.Exists(Path.Join(packagesFolder, n.Old))
                                                              && Directory.Exists(Path.Join(packagesFolder, n.New)))];
        if (moved.Length == 0) return;

        if (!string.IsNullOrWhiteSpace(cache) && Directory.Exists(cache))
            foreach (string setup in Directory.EnumerateDirectories(cache).Where(f => CacheInventory.ParseSetupName(Path.GetFileName(f)) is not null))
                foreach (var (old, renamed) in moved)
                    TryMove(Path.Join(setup, old), Path.Join(setup, renamed));

        string? customScenery = string.IsNullOrWhiteSpace(xplane) ? null : Path.Join(xplane, "Custom Scenery");
        if (customScenery is null || !Directory.Exists(customScenery)) return;
        try
        {
            SceneryPacks.RenameEntries(customScenery, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // In use (X-Plane writing it): renamed at the next start.
        }
    }

    /// <summary>Renames the packages OrthoStreamXP installed in <paramref name="packagesFolder"/>, and its download folder; returns the old names left (in use, or a folder of the new name already there).</summary>
    public static IReadOnlyList<string> MovePackages(string packagesFolder)
    {
        var left = new List<string>();
        foreach (var (old, renamed) in Names)
        {
            string from = Path.Join(packagesFolder, old);
            if (IsInstalledByUs(from) && !TryMove(from, Path.Join(packagesFolder, renamed))) left.Add(old);
        }
        string work = Path.Join(packagesFolder, OldWorkFolderName);
        if (!TryMove(work, Path.Join(packagesFolder, RegionInstaller.WorkFolderName))) left.Add(OldWorkFolderName);
        return left;
    }

    /// <summary>The folder of <paramref name="package"/> under its old name, when it is still there.</summary>
    public static string? OldFolderOf(string packagesFolder, string package)
    {
        string folder = Path.Join(packagesFolder, OldPrefix + package[SceneryPacks.DownloadedPrefix.Length..]);
        return IsInstalledByUs(folder) ? folder : null;
    }

    private static bool IsInstalledByUs(string folder) =>
        File.Exists(Path.Join(folder, RegionInstaller.MarkerName)) || File.Exists(Path.Join(folder, OverlayPackage.RecordName));

    /// <summary>Whether <paramref name="from"/> is gone: moved now, or there was none.</summary>
    private static bool TryMove(string from, string to)
    {
        if (!Directory.Exists(from)) return true;
        if (Directory.Exists(to)) return false;
        try
        {
            Directory.Move(from, to);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // in use: renamed at the next start
        }
    }
}
