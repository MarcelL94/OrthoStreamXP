using System.Windows;

namespace OrthoStreamXP.Tests;

/// <summary>The window's pages load with its theme: a XAML mistake shows here, not as an error box at start.</summary>
public sealed class ViewsTests
{
    [Fact]
    public void Every_page_loads_with_the_theme()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application();
#pragma warning disable WPF0001
                app.ThemeMode = ThemeMode.Light;
#pragma warning restore WPF0001
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/orthostreamxp;component/Gui/Theme.xaml") });
                foreach (Type view in new[] { typeof(OrthoStreamXP.Gui.HomeView), typeof(OrthoStreamXP.Gui.SettingsView), typeof(OrthoStreamXP.Gui.CacheView), typeof(OrthoStreamXP.Gui.LogView) })
                {
                    var page = (FrameworkElement)Activator.CreateInstance(view, nonPublic: true)!;
                    page.Measure(new Size(1200, 900));
                    page.Arrange(new Rect(0, 0, 1200, 900));
                }
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
