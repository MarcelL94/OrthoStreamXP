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
    /// Moves the entries of <paramref name="packageNames"/> (folders directly in Custom Scenery) to the end of
    /// scenery_packs.ini, adding the missing ones; a disabled entry stays disabled. The previous file is kept
    /// as scenery_packs.ini.bak. Returns whether the file changed.
    /// </summary>
    public static bool MoveToEnd(string customScenery, IReadOnlyList<string> packageNames)
    {
        string file = Path.Join(customScenery, FileName);
        string? text = File.Exists(file) ? File.ReadAllText(file) : null;
        string[] lines = text is null ? NewFileHeader : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        List<string> updated = Reorder(lines, packageNames, customScenery);
        if (lines.SequenceEqual(updated)) return false;
        string newline = text is not null && !text.Contains("\r\n", StringComparison.Ordinal) ? "\n" : "\r\n";
        if (text is not null) File.Copy(file, file + ".bak", overwrite: true);
        AtomicFile.Write(file, Encoding.UTF8.GetBytes(string.Join(newline, updated) + newline));
        return true;
    }

    /// <summary>The lines without the packages' entries, then one entry per package at the end.</summary>
    internal static List<string> Reorder(IReadOnlyList<string> lines, IReadOnlyList<string> packageNames, string customScenery)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>();
        foreach (string line in lines)
        {
            string? package = PackageOf(line, packageNames, customScenery, out bool isDisabled);
            if (package is null) kept.Add(line);
            else if (isDisabled) disabled.Add(package);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);
        foreach (string name in packageNames)
            kept.Add($"{(disabled.Contains(name) ? Disabled : Enabled)}Custom Scenery/{name}/");
        return kept;
    }

    /// <summary>Which of the packages a SCENERY_PACK line names (relative to the X-Plane folder, or as a full path).</summary>
    private static string? PackageOf(string line, IReadOnlyList<string> packageNames, string customScenery, out bool isDisabled)
    {
        string trimmed = line.Trim();
        isDisabled = trimmed.StartsWith(Disabled, StringComparison.Ordinal);
        if (!isDisabled && !trimmed.StartsWith(Enabled, StringComparison.Ordinal)) return null;
        string path = Normalize(trimmed[(isDisabled ? Disabled : Enabled).Length..]);
        return packageNames.FirstOrDefault(name =>
            path.Equals("Custom Scenery/" + name, StringComparison.OrdinalIgnoreCase)
            || path.Equals(Normalize(Path.Join(customScenery, name)), StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimEnd('/');
}
