using System.Text.Json;
using System.Windows;
using OrthoStreamXP.Gui;

namespace OrthoStreamXP.Tests;

/// <summary>The window's theme choice: what each entry switches WPF to, and how window.json keeps it.</summary>
public sealed class AppThemeTests
{
#pragma warning disable WPF0001
    [Fact]
    public void Each_choice_maps_to_its_wpf_theme_mode()
    {
        Assert.Equal(ThemeMode.System, AppTheme.System.ToThemeMode());
        Assert.Equal(ThemeMode.Light, AppTheme.Light.ToThemeMode());
        Assert.Equal(ThemeMode.Dark, AppTheme.Dark.ToThemeMode());
    }
#pragma warning restore WPF0001

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Clicks_go_windows_then_the_other_look_then_the_windows_look_then_windows(bool windowsUsesLight)
    {
        AppTheme same = windowsUsesLight ? AppTheme.Light : AppTheme.Dark;
        AppTheme other = windowsUsesLight ? AppTheme.Dark : AppTheme.Light;
        ThemeChoice choice = ThemeChoice.Of(AppTheme.System);
        var seen = new List<AppTheme>();
        for (int i = 0; i < 4; i++, choice = choice.Next(() => windowsUsesLight)) seen.Add(choice.Theme);

        Assert.Equal([AppTheme.System, other, same, AppTheme.System], seen);
    }

    [Fact]
    public void Each_saved_theme_shows_as_its_own_choice()
    {
        Assert.Same(ThemeChoice.Windows, ThemeChoice.Of(AppTheme.System));
        Assert.Same(ThemeChoice.Light, ThemeChoice.Of(AppTheme.Light));
        Assert.Same(ThemeChoice.Dark, ThemeChoice.Of(AppTheme.Dark));
    }

    [Fact]
    public void The_theme_is_kept_by_name_and_older_files_without_it_follow_windows()
    {
        string json = JsonSerializer.Serialize(new WindowPreferences { Theme = AppTheme.Dark });

        Assert.Contains("\"Theme\":\"Dark\"", json, StringComparison.Ordinal);
        Assert.Equal(AppTheme.Dark, JsonSerializer.Deserialize<WindowPreferences>(json)!.Theme);
        Assert.Equal(AppTheme.System, JsonSerializer.Deserialize<WindowPreferences>("{\"AutoStart\":true}")!.Theme);
    }
}
