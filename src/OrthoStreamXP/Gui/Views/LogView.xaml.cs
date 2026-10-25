using System.Windows;
using System.Windows.Input;

namespace OrthoStreamXP.Gui;

internal sealed partial class LogView
{
    public LogView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is LogViewModel old) old.LinesAdded -= ScrollToNewest;
            if (e.NewValue is LogViewModel model)
            {
                model.LinesAdded += ScrollToNewest;
                ScrollToNewest(model, EventArgs.Empty);
            }
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is LogViewModel model) model.LinesAdded -= ScrollToNewest;
        };
        // Ctrl+C copies the selected lines.
        Lines.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) =>
        {
            string text = string.Join(Environment.NewLine, Lines.SelectedItems.Cast<LogLine>().Select(l => $"{l.Time} {l.Level} {l.Text}"));
            if (text.Length > 0) Clipboard.SetText(text);
        }));
    }

    private void ScrollToNewest(object? sender, EventArgs e)
    {
        if (Lines.Items.Count > 0) Lines.ScrollIntoView(Lines.Items[^1]);
    }
}
