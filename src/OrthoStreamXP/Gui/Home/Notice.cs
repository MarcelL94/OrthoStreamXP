using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>A message on the Home page, with an optional button.</summary>
internal sealed record Notice(Severity Severity, string Title, string Message, string? ActionLabel = null, ICommand? Action = null, object? ActionParameter = null)
{
    public string Glyph => Severity switch
    {
        Severity.Error => "",
        Severity.Warning => "",
        Severity.Success => "",
        _ => "",
    };
}
