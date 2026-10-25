using Velopack;
using Velopack.Sources;

namespace OrthoStreamXP.Setup;

/// <summary>
/// New versions from the project's GitHub Releases (Velopack): looked for a little after the window opens and then every
/// few hours, downloaded in the background when there is one, and installed only when the person chooses to (with
/// OrthoStreamXP restarting), never while the scenery streams. A program that was not installed (a build run from the
/// source) does not look.
/// </summary>
internal sealed class AppUpdates
{
    /// <summary>Where the releases are: the project's GitHub page (betas included, as the first releases are).</summary>
    public const string Repository = "https://github.com/flysimdev/OrthoStreamXP";

    private readonly UpdateManager? _manager;
    private bool _checking;

    public AppUpdates()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, accessToken: null, prerelease: true));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            Log.Info($"Updates are not looked for: {ex.Message}");
        }
    }

    /// <summary>The version running, when installed.</summary>
    public string? CurrentVersion => _manager?.CurrentVersion?.ToString();

    /// <summary>A newer version, downloaded and ready to install; null when there is none (yet).</summary>
    public UpdateInfo? Ready { get; private set; }

    public string? ReadyVersion => Ready?.TargetFullRelease.Version.ToString();

    /// <summary>Looks for a newer version and downloads it; true when one became ready. Problems (offline, GitHub busy) are only logged.</summary>
    public async Task<bool> CheckAsync()
    {
        if (_manager is null || _checking || Ready is not null) return false;
        _checking = true;
        try
        {
            if (await _manager.CheckForUpdatesAsync().ConfigureAwait(true) is not { } update) return false;
            Log.Info($"OrthoStreamXP {update.TargetFullRelease.Version} is available (this is {CurrentVersion}); downloading it");
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(true);
            Ready = update;
            Log.Step($"OrthoStreamXP {ReadyVersion} is downloaded and ready to install");
            return true;
        }
        catch (Exception ex)
        {
            Log.Info($"Could not look for a new version: {ex.Message}");
            return false;
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Installs the downloaded version and starts OrthoStreamXP again (this process ends).</summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || Ready is null) return;
        Log.Step($"Installing OrthoStreamXP {ReadyVersion} and restarting");
        Log.Flush(TimeSpan.FromSeconds(1));
        _manager.ApplyUpdatesAndRestart(Ready);
    }
}
