using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace OrthoStreamXP.Textures;

/// <summary>A season chosen by hand instead of X-Plane's date.</summary>
internal enum SeasonChoice
{
    Auto,
    EarlySpring,
    Spring,
    Summer,
    EarlyAutumn,
    Autumn,
    LateAutumn,
    Winter,
}
