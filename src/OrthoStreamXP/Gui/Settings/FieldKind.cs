using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

internal enum FieldKind
{
    Number,
    Text,
    Toggle,
    Choice,
    Folder,
}
