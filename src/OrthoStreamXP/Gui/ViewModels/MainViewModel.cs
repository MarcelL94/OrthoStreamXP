using System.Text.Json;
using System.Windows.Input;
using OrthoStreamXP.Setup;

namespace OrthoStreamXP.Gui;

/// <summary>A page that wants to refresh when it is shown.</summary>
internal interface IPage
{
    void OnShown();
}

/// <summary>A sidebar entry.</summary>
internal sealed record NavItem(string Title, string Glyph, IPage Page);

/// <summary>The window's own choices, kept in %LOCALAPPDATA%\OrthoStreamXP\window.json (not in the mount settings).</summary>
internal sealed class WindowPreferences
{
    private static readonly string FilePath = Path.Join(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrthoStreamXP", "window.json");

    /// <summary>Start the mount as soon as the window opens.</summary>
    public bool AutoStart { get; set; }

    public static WindowPreferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<WindowPreferences>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"Could not save the window preferences: {ex.Message}");
        }
    }
}

/// <summary>The window: the sidebar pages, the first-run setup, and the actions problems offer.</summary>
internal sealed class MainViewModel : ObservableObject
{
    private NavItem _selected;
    private SetupViewModel? _setup;

    public MainViewModel()
    {
        Preferences = WindowPreferences.Load();
        LogPage = new LogViewModel();
        Home = new HomeViewModel(this);
        Settings = new SettingsViewModel(this);
        Cache = new CacheViewModel(this);
        About = new AboutViewModel();
        Navigation =
        [
            new NavItem("Home", "", Home),
            new NavItem("Settings", "", Settings),
            new NavItem("Cache", "", Cache),
            new NavItem("Log", "", LogPage),
            new NavItem("About", "", About),
        ];
        _selected = Navigation[0];
        HandleProblem = new RelayCommand<ProblemAction>(Handle);
        Donate = new RelayCommand(() => Shell.Open(AboutViewModel.DonationPage));
        Mount.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MountController.State)) Raise(nameof(SidebarStatus));
        };
    }

    public MountController Mount { get; } = new();
    public WindowPreferences Preferences { get; }
    public HomeViewModel Home { get; }
    public SettingsViewModel Settings { get; }
    public CacheViewModel Cache { get; }
    public LogViewModel LogPage { get; }
    public AboutViewModel About { get; }
    public IReadOnlyList<NavItem> Navigation { get; }

    public NavItem SelectedNav
    {
        get => _selected;
        set
        {
            if (value is null || !Set(ref _selected, value)) return;
            Raise(nameof(CurrentPage));
            value.Page.OnShown();
        }
    }

    public IPage CurrentPage => _selected.Page;

    /// <summary>The first-run setup, shown instead of the pages while it is open.</summary>
    public SetupViewModel? Setup
    {
        get => _setup;
        private set
        {
            Set(ref _setup, value);
            Raise(nameof(InSetup));
        }
    }

    public bool InSetup => Setup is not null;

    /// <summary>"Running" or "Stopped", under the sidebar.</summary>
    public string SidebarStatus => Mount.State switch
    {
        MountState.Running => "Running",
        MountState.Starting => "Starting…",
        MountState.Stopping => "Stopping…",
        _ => "Stopped",
    };

    public ICommand HandleProblem { get; }

    /// <summary>Opens the donation page, from the sidebar.</summary>
    public ICommand Donate { get; }

    public void Show(IPage page) => SelectedNav = Navigation.First(n => n.Page == page);

    /// <summary>Called when the window has opened: setup on the first run, otherwise the auto-start.</summary>
    public async Task OnOpenedAsync()
    {
        if (NeedsSetup()) RunSetup();
        else if (Preferences.AutoStart) await Mount.StartAsync().ConfigureAwait(true);
    }

    /// <summary>No settings file yet, or one without the folders a mount needs.</summary>
    public static bool NeedsSetup()
    {
        SettingsDocument settings = SettingsDocument.Load(SettingsDocument.DefaultPath);
        return settings.Get("cache") is null
               || (settings.Get("base-packages") ?? settings.Get("source")) is null
               || (settings.Get("xplane") ?? settings.Get("mountpoint")) is null;
    }

    public void RunSetup() => Setup = new SetupViewModel(this);

    public void CloseSetup() => Setup = null;

    /// <summary>The setup saved the settings: show them, and start when asked to.</summary>
    public async Task FinishSetupAsync(bool startNow)
    {
        Setup = null;
        Settings.Reload();
        SelectedNav = Navigation[0];
        Home.OnShown();
        if (startNow) await Mount.StartAsync().ConfigureAwait(true);
    }

    private void Handle(ProblemAction action)
    {
        switch (action)
        {
            case ProblemAction.OpenSettings:
                Show(Settings);
                break;
            case ProblemAction.RunSetup:
                RunSetup();
                break;
            case ProblemAction.DownloadWinFsp:
                Shell.Open(SystemCheck.WinFspDownload);
                break;
            case ProblemAction.OpenCustomScenery:
                if (SettingsDocument.Load(SettingsDocument.DefaultPath).Get("xplane") is { } xplane)
                    Shell.Open(Path.Join(xplane, "Custom Scenery"));
                break;
        }
    }
}
