using System.Windows;
using Microsoft.Win32;

namespace OrthoStreamXP.Gui;

/// <summary>The window's colours: the Windows theme (follows Windows), or always light or dark.</summary>
internal enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>The sidebar's theme button: what it shows for a theme, and the theme a click switches to.</summary>
internal sealed record ThemeChoice(string Label, string Glyph, AppTheme Theme)
{
    public static ThemeChoice Windows { get; } = new("Windows theme", "", AppTheme.System);
    public static ThemeChoice Light { get; } = new("Light theme", "", AppTheme.Light);
    public static ThemeChoice Dark { get; } = new("Dark theme", "", AppTheme.Dark);

    public static ThemeChoice Of(AppTheme theme) => theme switch
    {
        AppTheme.Light => Light,
        AppTheme.Dark => Dark,
        _ => Windows,
    };

    /// <summary>
    /// The Windows theme (follows Windows), then the other look, then Windows' look kept fixed, then the Windows theme again:
    /// the first click always changes the colours.
    /// </summary>
    public ThemeChoice Next(Func<bool> windowsUsesLight)
    {
        ThemeChoice same = windowsUsesLight() ? Light : Dark;
        ThemeChoice other = same == Light ? Dark : Light;
        if (this == Windows) return other;
        return this == other ? same : Windows;
    }
}

internal static class AppThemeExtensions
{
#pragma warning disable WPF0001 // WPF's Fluent theme is marked experimental.
    /// <summary>The WPF Fluent theme mode for the choice.</summary>
    public static ThemeMode ToThemeMode(this AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeMode.Light,
        AppTheme.Dark => ThemeMode.Dark,
        _ => ThemeMode.System,
    };

    /// <summary>Switches the running application to the theme; the window's DynamicResource brushes follow.</summary>
    public static void Apply(this AppTheme theme)
    {
        if (Application.Current is { } app) app.ThemeMode = theme.ToThemeMode();
    }
#pragma warning restore WPF0001
}

/// <summary>Whether Windows shows apps light or dark (Settings > Personalisation > Colours).</summary>
internal static class WindowsTheme
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>True for light apps, also when Windows does not say (older versions default to light).</summary>
    public static bool AppsUseLight()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Personalize);
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
