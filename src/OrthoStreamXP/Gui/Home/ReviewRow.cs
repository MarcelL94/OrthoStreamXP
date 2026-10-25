using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>A suggestion of the flight tuning as Home shows it: the setting, its change ("4 s → 6 s") and why.</summary>
internal sealed record ReviewRow(string Setting, string Change, string Why);
