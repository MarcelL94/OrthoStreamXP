using System.Windows;
using System.Windows.Threading;

namespace OrthoStreamXP.Gui;

internal sealed partial class App
{
    private MainViewModel? _main;
    private RegisteredWaitHandle? _activation;

    /// <summary>A second start of the program sets this event: show this window instead.</summary>
    internal void WatchActivation(EventWaitHandle requests) =>
        _activation = ThreadPool.RegisterWaitForSingleObject(requests, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (MainWindow is not { } window) return;
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }), null, Timeout.Infinite, executeOnlyOnce: false);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Until a mount configures it, the log goes only to the Log page.
        Log.Configure(LogOutput.None, null);
        DispatcherUnhandledException += OnUnhandledException;
        _main = new MainViewModel();
        var window = new MainWindow(_main);
        MainWindow = window;
        window.Show();
        window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _ = _main.OnOpenedAsync());
    }

    /// <summary>Logoff or shutdown: unmount quickly, Windows does not wait long.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (_main?.Mount.IsRunning == true) _main.Mount.StopAsync(hurry: true).Wait(TimeSpan.FromSeconds(4));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activation?.Unregister(null);
        Log.Sink = null;
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unexpected error in the window", e.Exception);
        MessageBox.Show($"Something went wrong: {e.Exception.GetBaseException().Message}\n\nThe details are in the log.", "OrthoStreamXP",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
