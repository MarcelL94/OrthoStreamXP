namespace OrthoStreamXP.Scenery;

/// <summary>The kinds of road the night lights tell apart; railways, power lines and the rest are <see cref="None"/>.</summary>
internal enum RoadClass : byte
{
    None,
    Motorway,
    Ramp,
    Primary,
    Secondary,
    Local,
}

/// <summary>How a class of road is lit: its width, how bright its lamps and street are, and how far apart its lamps stand.</summary>
internal readonly record struct RoadLook(double WidthM, double Brightness, double LampSpacingM, bool LinedWithHouses);

/// <summary>
/// X-Plane's road subtypes (the network "road type" of lib/g10/roads.net as a DSF stores it) sorted into
/// <see cref="RoadClass"/>es. Seen in the overlays: 10-29 primary, 30-49 secondary, 50-69 local streets, 100-119
/// motorways, 120-129 ramps, 151-154 railways, 220 power lines; a type ending in 9 is the rural variant of its road.
/// </summary>
internal static class RoadClasses
{
    /// <summary>A rural road's lamps: this much as bright, this many times as far apart.</summary>
    public const double RuralBrightness = 0.45, RuralSpacing = 1.8;

    public static RoadClass Of(int subtype, out bool rural)
    {
        RoadClass road = subtype switch
        {
            >= 10 and < 30 => RoadClass.Primary,
            >= 30 and < 50 => RoadClass.Secondary,
            >= 50 and < 100 => RoadClass.Local,
            >= 100 and < 120 => RoadClass.Motorway,
            >= 120 and < 130 => RoadClass.Ramp,
            _ => RoadClass.None,
        };
        rural = road is RoadClass.Primary or RoadClass.Secondary or RoadClass.Local && subtype % 10 == 9;
        return road;
    }

    public static RoadLook Look(RoadClass road) => road switch
    {
        RoadClass.Motorway => new RoadLook(16, 1.00, 45, false),
        RoadClass.Ramp => new RoadLook(8, 0.90, 35, false),
        RoadClass.Primary => new RoadLook(12, 0.95, 35, true),
        RoadClass.Secondary => new RoadLook(10, 0.85, 32, true),
        RoadClass.Local => new RoadLook(6, 0.60, 28, true),
        _ => new RoadLook(0, 0, double.PositiveInfinity, false),
    };

    /// <summary>Whether a road counts towards how built up a place is (town streets, not motorways or country roads).</summary>
    public static bool IsStreet(RoadClass road, bool rural) => !rural && road is RoadClass.Primary or RoadClass.Secondary or RoadClass.Local;
}
