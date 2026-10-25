using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace OrthoStreamXP.Gui;

/// <summary>Base for view models: raises PropertyChanged for the bindings.</summary>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A button's action; it is re-checked whenever WPF re-checks commands (or <see cref="Refresh"/> asks it to).</summary>
internal sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => execute();

    /// <summary>Asks every button to re-check whether it is enabled (after a change that came from a timer or a task).</summary>
    public static void Refresh() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>A command with the clicked item (or other value) as its parameter.</summary>
internal sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => parameter is T value ? canExecute?.Invoke(value) ?? true : canExecute is null;

    public void Execute(object? parameter)
    {
        if (parameter is T value) execute(value);
    }
}

/// <summary>Opens folders, files and web pages the way Explorer would.</summary>
internal static class Shell
{
    public static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Warning($"Could not open {target}: {ex.Message}");
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void Reveal(string file)
    {
        if (File.Exists(file)) Open("explorer.exe", $"/select,\"{file}\"");
        else if (Path.GetDirectoryName(file) is { } folder && Directory.Exists(folder)) Open(folder);
    }

    private static void Open(string program, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program, arguments) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Log.Warning($"Could not open {arguments}: {ex.Message}");
        }
    }

    /// <summary>Lets the user pick a folder; null when they cancel.</summary>
    public static string? PickFolder(string title, string? start)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dialog.InitialDirectory = start;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}
