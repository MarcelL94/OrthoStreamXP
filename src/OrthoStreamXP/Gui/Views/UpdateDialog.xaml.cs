using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace OrthoStreamXP.Gui;

/// <summary>Asks before installing a downloaded version, with its release notes: true when the person chose to update.</summary>
internal sealed partial class UpdateDialog
{
    private readonly UpdateOffer _offer;

    public UpdateDialog(UpdateOffer offer)
    {
        _offer = offer;
        InitializeComponent();
        Heading.Text = $"OrthoStreamXP {offer.Version} is ready to install";
        Intro.Text = (offer.CurrentVersion is null ? "" : $"You have {offer.CurrentVersion}. ")
                     + "OrthoStreamXP closes, installs the update and starts again. Your settings, cache and flight history stay.";
        ReleasePageLink.ToolTip = offer.ReleasePage;
        if (offer.Notes.Count == 0) NoNotes.Visibility = Visibility.Visible;
        for (int i = 0; i < offer.Notes.Count; i++) Notes.Children.Add(Show(offer.Notes[i], first: i == 0));
    }

    private static FrameworkElement Show(NoteBlock block, bool first)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        foreach (NoteSpan span in block.Spans)
            text.Inlines.Add(new Run(span.Text) { FontWeight = span.Bold ? FontWeights.SemiBold : FontWeights.Normal });

        switch (block.Kind)
        {
            case NoteKind.Heading:
                text.FontSize = 16;
                text.FontWeight = FontWeights.SemiBold;
                text.Margin = new Thickness(0, first ? 0 : 16, 0, 2);
                return text;
            case NoteKind.Bullet:
                var row = new DockPanel { Margin = new Thickness(4, first ? 0 : 6, 0, 0) };
                var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 10, 0) };
                DockPanel.SetDock(dot, Dock.Left);
                row.Children.Add(dot);
                row.Children.Add(text);
                return row;
            default:
                text.Margin = new Thickness(0, first ? 0 : 8, 0, 0);
                return text;
        }
    }

    private void Confirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OpenReleasePage(object sender, RoutedEventArgs e) => Shell.Open(_offer.ReleasePage);
}
