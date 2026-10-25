using System.Reflection;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>The About page: version, where things are, and the imagery credits.</summary>
internal sealed class AboutViewModel : ObservableObject, IPage
{
    public const string ProjectPage = Setup.AppUpdates.Repository;
    public const string DonationPage = "https://www.paypal.com/donate/?hosted_button_id=KHUJKNM8PXR7E";
    private bool _winFsp = SystemCheck.WinFspInstalled();

    public AboutViewModel()
    {
        OpenProject = new RelayCommand(() => Shell.Open(ProjectPage));
        OpenSettingsFile = new RelayCommand(() => Shell.Reveal(SettingsDocument.DefaultPath));
        GetWinFsp = new RelayCommand(() => Shell.Open(SystemCheck.WinFspDownload));
    }

    public ICommand OpenProject { get; }
    public ICommand OpenSettingsFile { get; }
    public ICommand GetWinFsp { get; }

    public string Version { get; } =
        typeof(AboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    public string WinFspStatus => _winFsp ? "WinFsp is installed." : "WinFsp is not installed. OrthoStreamXP needs it to show the scenery to X-Plane.";

    public bool WinFspMissing => !_winFsp;

    public string SettingsFile { get; } = SettingsDocument.DefaultPath;

    public void OnShown()
    {
        _winFsp = SystemCheck.WinFspInstalled();
        Raise(nameof(WinFspStatus));
        Raise(nameof(WinFspMissing));
    }
}
