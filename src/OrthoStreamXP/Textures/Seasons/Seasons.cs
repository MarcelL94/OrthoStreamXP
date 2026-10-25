using System.Collections.Concurrent;

namespace OrthoStreamXP.Textures;

/// <summary>
/// Seasonal colour for the imagery, applied to the BC1 blocks of a DDS as X-Plane reads it: each 4 x 4 block is two
/// colours and a blend between them per pixel, so recolouring the two colours recolours the block. A BC1 colour has
/// 65,536 values, so a season is one lookup table, and a 64 KB read takes microseconds. The cache keeps the imagery as
/// downloaded; a season, a strength or switching it off never costs a download.
/// <para>
/// The change is a smooth function of colour alone, so it has no mask edges: greens (vegetation) move with the season,
/// while reds, greys, blues, white and black (roofs, roads, water, rock, sand) stay. Darker greens count as tree crowns,
/// lighter ones as grass and fields, and the darkest, coolest as conifers. Snow is never painted: that is X-Plane's weather.
/// </para>
/// </summary>
internal static class Seasons
{
    private static readonly ConcurrentDictionary<int, ushort[]> Tables = new();

    /// <summary>The lookup table of a season, or null when it changes nothing, shared by every texture.</summary>
    public static ushort[]? TableFor(SeasonState state)
    {
        if (state.IsSummer) return null;
        int key = StateKey(state);
        if (Tables.TryGetValue(key, out ushort[]? table)) return table;
        if (Tables.Count > 1024) Tables.Clear();
        return Tables.GetOrAdd(key, _ => Build(Quantised(state)));
    }

    /// <summary>A season quantised to 1/32: neighbouring textures share a table, and a season is still a smooth change.</summary>
    private static int StateKey(SeasonState state) => (Q(state.Spring) << 18) | (Q(state.Autumn) << 12) | (Q(state.Bare) << 6) | Q(state.Dormant);

    private static SeasonState Quantised(SeasonState state) => new(Q(state.Spring) / 32.0, Q(state.Autumn) / 32.0, Q(state.Bare) / 32.0, Q(state.Dormant) / 32.0);

    private static int Q(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 32);

    private static ushort[] Build(SeasonState state)
    {
        var table = new ushort[65536];
        for (int c = 0; c < table.Length; c++)
        {
            var (r, g, b) = ColourTable.Decode565(c);
            var (r2, g2, b2) = Recolor(r, g, b, state);
            table[c] = (ushort)ColourTable.Encode565(r2, g2, b2);
        }
        return table;
    }

    /// <summary>One colour (0-255 per channel) in a season.</summary>
    internal static (double R, double G, double B) Recolor(double r, double g, double b, SeasonState s)
    {
        var (h, sat, v) = ToHsv(r / 255, g / 255, b / 255);
        // How much this colour is vegetation: green hues, not grey, not black shadow or white glare.
        double vegetation = SeasonModel.Smooth(48, 72, h) * (1 - SeasonModel.Smooth(160, 185, h))
                            * SeasonModel.Smooth(0.08, 0.2, sat) * SeasonModel.Smooth(0.05, 0.12, v) * (1 - SeasonModel.Smooth(0.88, 0.98, v));
        if (vegetation <= 0) return (r, g, b);
        // In summer imagery tree crowns are the darker greens, grass and fields the lighter ones; conifer forest is
        // the darkest, coolest green.
        double canopy = 1 - SeasonModel.Smooth(0.30, 0.46, v);
        double conifer = (1 - SeasonModel.Smooth(0.16, 0.30, v)) * SeasonModel.Smooth(85, 110, h);

        (double R, double G, double B) original = (r / 255, g / 255, b / 255);
        // Broadleaf crowns: fresh leaves, then yellow, orange and rust (yellower greens turn yellow, cooler ones rust), then bare wood.
        var broad = Lerp(original, FromHsv(h + (80 - h) * 0.5, sat * 0.9, Math.Min(1, v * 1.07)), s.Spring);
        broad = Lerp(broad, FromHsv(Math.Clamp(45 - (h - 75) * 0.4, 18, 48), Math.Min(1, sat * 1.3 + 0.1), Math.Min(1, v * 1.05)), s.Autumn * 0.9);
        broad = Lerp(broad, FromHsv(28 + 10 * v, Math.Min(sat * 0.5, 0.12 + 0.2 * v), v * 0.92), s.Bare);
        // Conifers: green all year, a little muted in winter.
        var needles = Lerp(original, FromHsv(h, sat * 0.8, v * 0.93), s.Bare * 0.7);
        // Grass and fields: brighter in spring, drying in autumn, then olive and straw at rest.
        var open = Lerp(original, FromHsv(h + (88 - h) * 0.3, Math.Min(1, sat * 1.08), Math.Min(1, v * 1.04)), s.Spring);
        open = Lerp(open, FromHsv(h + (55 - h) * 0.6, sat * 0.8, v * 0.98), s.Autumn * 0.55);
        open = Lerp(open, FromHsv(50 + (h - 50) * 0.2, sat * 0.5, v * 0.92), s.Dormant);

        var trees = Lerp(broad, needles, conifer);
        var result = Lerp(original, Lerp(open, trees, canopy), vegetation);
        return (result.R * 255, result.G * 255, result.B * 255);
    }


    private static (double R, double G, double B) Lerp((double R, double G, double B) a, (double R, double G, double B) b, double t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), delta = max - min;
        double h = delta == 0 ? 0 : max == r ? 60 * ((g - b) / delta % 6) : max == g ? 60 * ((b - r) / delta + 2) : 60 * ((r - g) / delta + 4);
        return (h < 0 ? h + 360 : h, max == 0 ? 0 : delta / max, max);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
