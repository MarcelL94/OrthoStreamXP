using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>An entry of a drop-down: what the person reads, and what goes into the settings file.</summary>
internal sealed record Choice(string Label, string Value);
