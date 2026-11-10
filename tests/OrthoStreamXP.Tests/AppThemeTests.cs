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

    [Fact]
    public void A_click_switches_between_light_and_dark()
    {
        Assert.Equal(AppTheme.Dark, ThemeChoice.Light.Other.Theme);
        Assert.Equal(AppTheme.Light, ThemeChoice.Dark.Other.Theme);
    }

    [Fact]
    public void Until_a_click_the_button_shows_the_look_windows_gives()
    {
        Assert.Same(ThemeChoice.Light, ThemeChoice.Of(AppTheme.System, () => true));
        Assert.Same(ThemeChoice.Dark, ThemeChoice.Of(AppTheme.System, () => false));
        Assert.Same(ThemeChoice.Dark, ThemeChoice.Of(AppTheme.Dark, () => true));
        Assert.Same(ThemeChoice.Light, ThemeChoice.Of(AppTheme.Light, () => false));
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
