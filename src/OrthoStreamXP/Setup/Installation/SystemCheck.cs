using System.Diagnostics;
using Microsoft.Win32;
using OrthoStreamXP.Scenery;

namespace OrthoStreamXP.Setup;

/// <summary>Finds what OrthoStreamXP works with on this PC (WinFsp, X-Plane) and checks the scenery packages folder the person chose.</summary>
internal static class SystemCheck
{
    public const string WinFspDownload = "https://winfsp.dev/rel/";

    /// <summary>Whether WinFsp (the driver that shows the scenery to X-Plane) is installed.</summary>
    public static bool WinFspInstalled()
    {
        string? folder = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\WinFsp", "InstallDir", null) as string
                         ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp");
        return File.Exists(Path.Join(folder, "bin", "winfsp-x64.dll"));
    }

    /// <summary>An X-Plane folder: it has the Custom Scenery folder packages are mounted in.</summary>
    public static bool IsXPlaneFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(Path.Join(folder, "Custom Scenery"));

    /// <summary>X-Plane 12 installs: the ones its installer recorded, then the usual places.</summary>
    public static IReadOnlyList<string> FindXPlaneFolders()
    {
        var candidates = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (string record in new[] { "x-plane_install_12.txt", "x-plane_install_11.txt" })
        {
            string file = Path.Join(localAppData, record);
            if (File.Exists(file)) candidates.AddRange(ReadLines(file).Select(l => l.Trim().TrimEnd('/', '\\')).Where(l => l.Length > 0));
        }
        foreach (DriveInfo drive in FixedDrives())
            candidates.Add(Path.Join(drive.RootDirectory.FullName, "X-Plane 12"));
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
            candidates.Add(Path.Join(steam.Replace('/', '\\'), "steamapps", "common", "X-Plane 12"));
        return Distinct(candidates.Where(IsXPlaneFolder));
    }

    /// <summary>The scenery packages (folders with Earth nav data) in a folder, by name; none when it cannot be read.</summary>
    public static IReadOnlyList<string> PackagesIn(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return [];
        try
        {
            return [.. SceneryPacks.FindPackages(folder, out _).Select(Path.GetFileName).OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Why a scenery packages folder in X-Plane's Custom Scenery is refused, and where the packages go instead.</summary>
    public const string InCustomSceneryExplanation =
        "Your scenery packages must not be in X-Plane's Custom Scenery folder. OrthoStreamXP puts each package into Custom Scenery " +
        "itself while it runs, so packages kept there would be in X-Plane twice." +
        "Move the package folders out of X-Plane, for example to D:\\OrthoStreamXP\\Base Packages, and choose that folder as your scenery packages folder.";

    /// <summary>Why the X-Plane folder is needed before the scenery packages folder.</summary>
    public const string XPlaneFirstExplanation =
        "Choose your X-Plane 12 folder first: OrthoStreamXP needs it to check that your scenery packages are not in its Custom Scenery folder.";

    /// <summary>
    /// A scenery packages folder that is the Custom Scenery folder of the X-Plane in <paramref name="xplane"/> or inside it:
    /// it cannot be used. Without an X-Plane folder there is nothing to check against (see <see cref="XPlaneFirstExplanation"/>).
    /// </summary>
    public static bool IsInCustomScenery(string? folder, string? xplane)
    {
        if (string.IsNullOrWhiteSpace(folder) || !IsXPlaneFolder(xplane)) return false;
        try
        {
            return SceneryPacks.IsInCustomScenery(folder, xplane!);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // not a usable path: the packages check reports it
        }
    }

    /// <summary>
    /// Package names that already have a folder in X-Plane's Custom Scenery. A mount cannot replace one: usually
    /// another program still shows the package there.
    /// </summary>
    public static IReadOnlyList<string> TakenInCustomScenery(string? xplane, IEnumerable<string> packages) =>
        IsXPlaneFolder(xplane) ? [.. packages.Where(p => Path.Exists(Path.Join(xplane, "Custom Scenery", p)))] : [];

    /// <summary>X-Plane's program in its folder (X-Plane.exe), or null when it is not there.</summary>
    public static string? XPlaneProgram(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string exe = Path.Join(folder, "X-Plane.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// Starts X-Plane from its folder, as a double-click on X-Plane.exe would: through the desktop's Explorer, so it does not
    /// inherit this program's environment (directly only when Explorer cannot).
    /// </summary>
    /// <param name="arguments">Added to its command line, such as --allow_reshade; null or empty for none.</param>
    public static void StartXPlane(string program, string? arguments = null)
    {
        string folder = Path.GetDirectoryName(program)!, options = arguments?.Trim() ?? "";
        if (DesktopShell.TryStart(program, options, folder)) return;
        Process.Start(new ProcessStartInfo(program, options) { WorkingDirectory = folder, UseShellExecute = true })?.Dispose();
    }

    public static bool IsXPlaneRunning()
    {
        Process[] running = Process.GetProcessesByName("X-Plane");
        foreach (Process process in running) process.Dispose();
        return running.Length > 0;
    }

    /// <summary>Free space on the drive of a folder (the folder itself need not exist yet).</summary>
    public static long? FreeBytes(string? folder)
    {
        try
        {
            return string.IsNullOrWhiteSpace(folder) || Path.GetPathRoot(Path.GetFullPath(folder)) is not { Length: > 0 } root
                ? null
                : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The cache the setup suggests. One folder holds both, by choice not by need:
    /// <c>OrthoStreamXP\Base Packages</c> and <c>OrthoStreamXP\Cache</c>. So the cache goes beside the base packages
    /// folder; before one is chosen (or for a drive's root) it is <c>OrthoStreamXP\Cache</c> in the user's home folder.
    /// </summary>
    public static string SuggestedCacheFolder(string? basePackages)
    {
        if (!string.IsNullOrWhiteSpace(basePackages) && Path.IsPathFullyQualified(basePackages)
            && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(basePackages)) is { Length: > 0 } parent)
            return Path.Join(parent, CacheFolderName);
        return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OrthoStreamXP", CacheFolderName);
    }

    /// <summary>The name of the cache folder the setup suggests, beside "Base Packages".</summary>
    public const string CacheFolderName = "Cache";

    /// <summary>
    /// Where the setup installs downloaded mesh regions for someone who has no scenery packages folder yet:
    /// <c>OrthoStreamXP\Base Packages</c> in the user's home folder, beside the cache it suggests.
    /// </summary>
    public static string SuggestedPackagesFolder() =>
        Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OrthoStreamXP", "Base Packages");

    private static IEnumerable<DriveInfo> FixedDrives()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (IOException)
            {
                ready = false;
            }
            if (ready) yield return drive;
        }
    }

    private static string[] ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<string> Distinct(IEnumerable<string> folders) =>
        [.. folders.Select(f => Path.TrimEndingDirectorySeparator(Path.GetFullPath(f))).Distinct(StringComparer.OrdinalIgnoreCase)];
}
