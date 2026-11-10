using System.Text;

namespace OrthoStreamXP.Scenery;

/// <summary>
/// Finds the scenery packages in a folder of base packages, and keeps the mounted ones at the end of
/// X-Plane's Custom Scenery\scenery_packs.ini: the end has the lowest priority, so airports, overlays and
/// other scenery listed above them stay on top of the photo base.
/// </summary>
internal static class SceneryPacks
{
    public const string FileName = "scenery_packs.ini";

    /// <summary>The names of the packages OrthoStreamXP downloads itself (OrthoStream_Eur, OrthoStream_Overlays...).</summary>
    public const string DownloadedPrefix = "OrthoStream_";
    private const string Enabled = "SCENERY_PACK ", Disabled = "SCENERY_PACK_DISABLED ";
    private static readonly string[] NewFileHeader = ["I", "1000 Version", "SCENERY", ""];

    /// <summary>The subfolders of <paramref name="folder"/> that are scenery packages (they have Earth nav data), by name.</summary>
    public static IReadOnlyList<string> FindPackages(string folder, out IReadOnlyList<string> skipped)
    {
        var packages = new List<string>();
        var others = new List<string>();
        foreach (string directory in Directory.EnumerateDirectories(folder).Order(StringComparer.OrdinalIgnoreCase))
            (Directory.Exists(Path.Join(directory, "Earth nav data")) ? packages : others).Add(directory);
        skipped = others;
        return packages;
    }

    /// <summary>
    /// Whether <paramref name="folder"/> is the Custom Scenery folder of the X-Plane in <paramref name="xplane"/> or inside it
    /// (such as Custom Scenery\z_autoortho\scenery). The packages are mounted into that Custom Scenery, so ones kept there would
    /// be in it twice. A folder of the person's own that is only named Custom Scenery does not count.
    /// </summary>
    public static bool IsInCustomScenery(string folder, string xplane)
    {
        string customScenery = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Join(xplane, "Custom Scenery")));
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return full.Equals(customScenery, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(customScenery + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Moves the entries of <paramref name="packageNames"/> (folders directly in Custom Scenery) to the end of
    /// scenery_packs.ini, adding the missing ones; a disabled entry stays disabled. The previous file is kept
    /// as scenery_packs.ini.bak. Returns whether the file changed.
    /// </summary>
    public static bool MoveToEnd(string customScenery, IReadOnlyList<string> packageNames)
    {
        string file = Path.Join(customScenery, FileName);
        string? text = File.Exists(file) ? File.ReadAllText(file) : null;
        string[] lines = text is null ? NewFileHeader : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        List<string> updated = Reorder(lines, packageNames);
        if (lines.SequenceEqual(updated)) return false;
        string newline = text is not null && !text.Contains("\r\n", StringComparison.Ordinal) ? "\n" : "\r\n";
        if (text is not null) File.Copy(file, file + ".bak", overwrite: true);
        AtomicFile.Write(file, Encoding.UTF8.GetBytes(string.Join(newline, updated) + newline));
        return true;
    }

    /// <summary>
    /// Renames the entries of packages that took a new name (<paramref name="names"/>: old and new) in scenery_packs.ini,
    /// in place: their position and whether they are disabled stay. The previous file is kept as scenery_packs.ini.bak.
    /// Returns whether the file changed.
    /// </summary>
    public static bool RenameEntries(string customScenery, IReadOnlyList<(string Old, string New)> names)
    {
        string file = Path.Join(customScenery, FileName);
        if (!File.Exists(file)) return false;
        string text = File.ReadAllText(file);
        string[] lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        List<string> updated = Rename(lines, names);
        if (lines.SequenceEqual(updated)) return false;
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        File.Copy(file, file + ".bak", overwrite: true);
        AtomicFile.Write(file, Encoding.UTF8.GetBytes(string.Join(newline, updated) + newline));
        return true;
    }

    /// <summary>The lines with each old name's entry naming the new one instead; one already listed under its new name keeps only that entry.</summary>
    internal static List<string> Rename(IReadOnlyList<string> lines, IReadOnlyList<(string Old, string New)> names)
    {
        string[] olds = [.. names.Select(n => n.Old)], news = [.. names.Select(n => n.New)];
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
            if (PackageOf(line, news, out _) is { } name) listed.Add(name);
        var updated = new List<string>(lines.Count);
        foreach (string line in lines)
        {
            if (PackageOf(line, olds, out bool isDisabled) is not { } old)
            {
                updated.Add(line);
                continue;
            }
            string renamed = names.First(n => n.Old.Equals(old, StringComparison.OrdinalIgnoreCase)).New;
            if (listed.Add(renamed)) updated.Add($"{(isDisabled ? Disabled : Enabled)}Custom Scenery/{renamed}/");
        }
        return updated;
    }

    /// <summary>
    /// The lines without the packages' entries, then one entry per package at the end. Overlay packages
    /// (such as XPME_Overlays) come first among them, so their overlays draw on top of the other base packages;
    /// the meshes OrthoStreamXP downloads (a whole region each) come last, so the person's own packages cover them.
    /// </summary>
    internal static List<string> Reorder(IReadOnlyList<string> lines, IReadOnlyList<string> packageNames)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (string line in lines)
        {
            string? package = PackageOf(line, packageNames, out bool isDisabled);
            if (package is null) kept.Add(line);
            else if (isDisabled) disabled.Add(package);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        foreach (string name in packageNames.OrderBy(name => IsOverlays(name) ? 0 : IsDownloadedMesh(name) ? 2 : 1))
            kept.Add($"{(disabled.Contains(name) ? Disabled : Enabled)}Custom Scenery/{name}/");
        return kept;
    }

    /// <summary>
    /// Which of the packages a SCENERY_PACK line names, by its folder name wherever the path points: an entry
    /// for a package of the same name elsewhere (such as the base packages folder itself) would load it twice.
    /// </summary>
    private static string? PackageOf(string line, IReadOnlyList<string> packageNames, out bool isDisabled)
    {
        string trimmed = line.Trim();
        isDisabled = trimmed.StartsWith(Disabled, StringComparison.Ordinal);
        if (!isDisabled && !trimmed.StartsWith(Enabled, StringComparison.Ordinal)) return null;
        string path = Normalize(trimmed[(isDisabled ? Disabled : Enabled).Length..]);
        string folderName = path[(path.LastIndexOf('/') + 1)..];
        return packageNames.FirstOrDefault(name => folderName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsOverlays(string packageName) => packageName.Contains("Overlays", StringComparison.OrdinalIgnoreCase);

    private static bool IsDownloadedMesh(string packageName) => packageName.StartsWith(DownloadedPrefix, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
