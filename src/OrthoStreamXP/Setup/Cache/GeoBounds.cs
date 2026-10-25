using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>A rectangle of the globe in degrees.</summary>
internal readonly record struct GeoBounds(double South, double West, double North, double East);
