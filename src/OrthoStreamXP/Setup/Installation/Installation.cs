using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OrthoStreamXP.Setup;

/// <summary>
/// The installed program (Velopack): a per-user install in %LOCALAPPDATA%\OrthoStreamXP.App, with a Start menu entry and
/// an uninstaller, no administrator needed. The app's own files are apart from it (<see cref="AppPaths"/>), so an
/// update or a reinstall never touches the settings, and an uninstall removes them only when asked.
/// </summary>
internal static partial class Installation
{
    /// <summary>The package id: the install folder's name. Not "OrthoStreamXP", which is the app's data folder.</summary>
    public const string PackId = "OrthoStreamXP.App";

    /// <summary>
    /// Asks whether the settings, logs and flight history, and the pictures downloaded to the cache, go too. The answer
    /// is "keep" unless chosen otherwise: a later install then carries on where this one ended. The deletion runs apart
    /// from the uninstaller, as a big cache takes longer than it may wait.
    /// </summary>
    /// <remarks>Velopack's uninstall hook, set up first thing in Program.cs (Velopack wants its call there).</remarks>
    public static void BeforeUninstall()
    {
        string? cache = SettingsDocument.Load(AppPaths.Settings).Get("cache");
        bool hasCache = cache is not null && Directory.Exists(cache);
        string question = "OrthoStreamXP is being removed.\n\nAlso delete your settings, logs and flight history" +
                          (hasCache ? $", and the pictures downloaded to {cache}" : "") +
                          "?\n\nChoose No to keep them for a later install.";
        if (MessageBox(IntPtr.Zero, question, "Remove OrthoStreamXP", YesNo | Question | TopMost) != Yes) return;

        var folders = new List<string> { AppPaths.Root };
        // Only what OrthoStreamXP made in the cache folder: the person chose that folder, and it may hold other things.
        if (hasCache) folders.AddRange(CacheInventory.OwnFolders(cache!));
        foreach (string folder in folders.Where(Directory.Exists))
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir /s /q \"{folder}\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Left for the person to delete.
            }
        }
    }

    private const uint YesNo = 0x4, Question = 0x20, TopMost = 0x40000;
    private const int Yes = 6;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr owner, string text, string caption, uint type);
}
