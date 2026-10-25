using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace OrthoStreamXP.Textures;

/// <summary>
/// Where vegetation is in its year at one place, each 0 to 1: <see cref="Spring"/> the fresh green of leaf-out,
/// <see cref="Autumn"/> the yellow and rust of turning leaves, <see cref="Bare"/> trees without leaves, and
/// <see cref="Dormant"/> grass and fields at rest (later in autumn and earlier in spring than the trees). All zero is
/// summer, which is what the imagery shows.
/// </summary>
internal readonly record struct SeasonState(double Spring, double Autumn, double Bare, double Dormant)
{
    public static readonly SeasonState Summer = new(0, 0, 0, 0);

    public bool IsSummer => Spring < 0.02 && Autumn < 0.02 && Bare < 0.02 && Dormant < 0.02;

    public SeasonState Scaled(double factor) => new(Spring * factor, Autumn * factor, Bare * factor, Dormant * factor);

    public override string ToString() =>
        FormattableString.Invariant($"spring {Spring * 100:0}%, autumn {Autumn * 100:0}%, bare trees {Bare * 100:0}%, dormant grass {Dormant * 100:0}%");
}
