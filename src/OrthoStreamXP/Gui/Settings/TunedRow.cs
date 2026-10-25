using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>A setting the flight tuning changed: its value now and the preset's own.</summary>
internal sealed record TunedRow(string Label, string Value, string Default);
