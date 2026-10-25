using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace OrthoStreamXP.Textures;

/// <summary>
/// When vegetation changes, by day of the year and latitude. Temperate phenology: leaf-out about 5 April to 10 May at
/// 50°, three days later per degree nearer the pole; leaves turning from mid-September, at their most colourful in
/// mid-October, bare in early November, two days earlier per degree nearer the pole. Grass and fields rest from about
/// two weeks after the leaves fall until a month before leaf-out. The southern hemisphere is half a year apart.
/// Towards the tropics the seasons fade out (nothing changes below 20°, everything above 42°).
/// </summary>
internal static class SeasonModel
{
    public static readonly IReadOnlyDictionary<string, SeasonChoice> Names = new Dictionary<string, SeasonChoice>(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = SeasonChoice.Auto,
        ["early-spring"] = SeasonChoice.EarlySpring,
        ["spring"] = SeasonChoice.Spring,
        ["summer"] = SeasonChoice.Summer,
        ["early-autumn"] = SeasonChoice.EarlyAutumn,
        ["autumn"] = SeasonChoice.Autumn,
        ["late-autumn"] = SeasonChoice.LateAutumn,
        ["winter"] = SeasonChoice.Winter,
    };

    /// <param name="dayOfYear">0 is 1 January, as X-Plane's sim/time/local_date_days.</param>
    public static SeasonState For(int dayOfYear, double lat)
    {
        double phi = Math.Abs(lat);
        double amplitude = Smooth(20, 42, phi);
        if (amplitude <= 0) return SeasonState.Summer;
        double d = lat >= 0 ? dayOfYear : (dayOfYear + 182) % 365;
        double p = Math.Clamp(phi, 35, 65) - 50;
        double upStart = 95 + 3 * p, upEnd = upStart + 35;
        double turnStart = 258 - 2 * p, turnPeak = turnStart + 30, leafFall = turnPeak + 22;

        double bare = d < upEnd ? 1 - Smooth(upStart, upEnd, d) : Smooth(turnPeak, leafFall, d);
        double spring = d < upEnd + 30 ? Smooth(upStart + 10, upEnd, d) * (1 - Smooth(upEnd, upEnd + 30, d)) : 0;
        double autumn = Smooth(turnStart, turnPeak, d) * (1 - Smooth(turnPeak + 5, leafFall + 8, d));
        double dormant = d < upEnd ? 1 - Smooth(upStart - 40, upStart - 10, d) : Smooth(leafFall, leafFall + 25, d);
        return new SeasonState(spring, autumn, bare, dormant).Scaled(amplitude);
    }

    /// <summary>A season chosen by hand, the same everywhere.</summary>
    public static SeasonState For(SeasonChoice choice) => choice switch
    {
        SeasonChoice.EarlySpring => new(0.3, 0, 0.8, 0),
        SeasonChoice.Spring => new(1, 0, 0.1, 0),
        SeasonChoice.EarlyAutumn => new(0, 0.5, 0, 0),
        SeasonChoice.Autumn => new(0, 1, 0.15, 0),
        SeasonChoice.LateAutumn => new(0, 0.4, 0.7, 0.4),
        SeasonChoice.Winter => new(0, 0, 1, 1),
        _ => SeasonState.Summer,
    };

    internal static double Smooth(double edge0, double edge1, double x)
    {
        double t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
