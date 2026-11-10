using System.Windows;
using System.Windows.Threading;

namespace OrthoStreamXP.Gui;

internal sealed partial class SetupView
{
    public SetupView() => InitializeComponent();

    /// <summary>The list of regions opened (perhaps from the button under the red message): scrolled into sight once laid out.</summary>
    private void OnDownloadsShown(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is FrameworkElement list)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => list.BringIntoView());
    }
}
