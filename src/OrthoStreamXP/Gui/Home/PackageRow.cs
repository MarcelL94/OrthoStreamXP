using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Threading;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>A mounted (or to-be-mounted) package: its name and where X-Plane sees it.</summary>
internal sealed record PackageRow(string Name, string Where);
