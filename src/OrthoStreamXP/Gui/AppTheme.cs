using System.Windows;

namespace OrthoStreamXP.Gui;

/// <summary>The window's colours: the same as Windows, or always light or dark.</summary>
internal enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>The sidebar's theme button: what it shows for a theme, and which theme a click switches to.</summary>
internal sealed record ThemeChoice(string Label, string Glyph, AppTheme Theme)
{
    /// <summary>The themes in the order a click goes through them.</summary>
    public static IReadOnlyList<ThemeChoice> All { get; } =
    [
        new("Windows theme", "", AppTheme.System),
        new("Light theme", "", AppTheme.Light),
        new("Dark theme", "", AppTheme.Dark),
    ];

    public static ThemeChoice Of(AppTheme theme) => All.FirstOrDefault(c => c.Theme == theme) ?? All[0];

    /// <summary>The theme after this one, back to the first after the last.</summary>
    public ThemeChoice Next => All[(IndexOf(Theme) + 1) % All.Count];

    private static int IndexOf(AppTheme theme)
    {
        for (int i = 0; i < All.Count; i++)
            if (All[i].Theme == theme) return i;
        return 0;
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
