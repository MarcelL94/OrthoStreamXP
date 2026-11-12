using System.ComponentModel;
using System.Windows;

namespace OrthoStreamXP.Gui;

internal sealed partial class MainWindow
{
    private readonly MainViewModel _main;
    private bool _closing;

    public MainWindow(MainViewModel main)
    {
        _main = main;
        DataContext = main;
        InitializeComponent();
        // The Home page's Update button: the release notes first, installed only when confirmed.
        _main.Home.UpdateOffered += (_, offer) =>
        {
            if (new UpdateDialog(offer) { Owner = this }.ShowDialog() == true) _main.Home.InstallConfirmedUpdate();
        };
    }

    /// <summary>Closing while the scenery is mounted asks first, then unmounts before the window goes.</summary>
    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closing || _main.Mount.IsStopped) return;
        e.Cancel = true;
        if (_main.Mount.IsRunning && MessageBox.Show(this,
                "OrthoStreamXP is running. If you close it, X-Plane loses the photo scenery.\n\nStop OrthoStreamXP and close?",
                "OrthoStreamXP", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        _closing = true;
        while (_main.Mount.State is MountState.Starting or MountState.Stopping) await Task.Delay(200).ConfigureAwait(true);
        await _main.Mount.StopAsync().ConfigureAwait(true);
        Close();
    }
}
