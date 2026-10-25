using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

internal enum Severity
{
    Info,
    Success,
    Warning,
    Error,
}
