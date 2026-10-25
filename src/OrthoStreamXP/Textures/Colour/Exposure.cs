using System.Collections.Concurrent;

namespace OrthoStreamXP.Textures;

/// <summary>
/// The exposure of the photos: the light they let through, as a camera's exposure, from <see cref="Min"/> to 100 %.
/// Aerial photos are often brighter than X-Plane's own scenery around them; less exposure darkens them evenly. The
/// light is scaled in linear light and put back into sRGB, so 50 % is one stop darker without crushing the shadows
/// or greying the highlights. Like the seasons, it is one lookup table over the RGB565 colours, applied as X-Plane reads.
/// </summary>
internal static class Exposure
{
    /// <summary>The settings' default and most, in percent: the photos as they are.</summary>
    public const int Full = 100;

    /// <summary>The least the settings allow, in percent: one stop darker.</summary>
    public const int Min = 50;

    private static readonly ConcurrentDictionary<int, ushort[]> Tables = new();

    /// <summary>The lookup table of an exposure in percent (rounded to a whole one); null at 100 %, which changes nothing.</summary>
    public static ushort[]? TableFor(double percent)
    {
        int key = (int)Math.Round(Math.Clamp(percent, Min, Full));
        return key == Full ? null : Tables.GetOrAdd(key, k => Build(k / 100.0));
    }

    private static ushort[] Build(double factor)
    {
        int[] five = Channel(31, factor), six = Channel(63, factor);
        var table = new ushort[65536];
        for (int c = 0; c < table.Length; c++)
            table[c] = (ushort)((five[(c >> 11) & 31] << 11) | (six[(c >> 5) & 63] << 5) | five[c & 31]);
        return table;
    }

    /// <summary>Each value of a channel of <paramref name="max"/> + 1 steps, with its light scaled by <paramref name="factor"/>.</summary>
    private static int[] Channel(int max, double factor)
    {
        int[] values = new int[max + 1];
        for (int v = 0; v <= max; v++)
            values[v] = (int)Math.Round(ToSrgb(ToLinear((double)v / max) * factor) * max);
        return values;
    }

    internal static double ToLinear(double srgb) => srgb <= 0.04045 ? srgb / 12.92 : Math.Pow((srgb + 0.055) / 1.055, 2.4);

    internal static double ToSrgb(double linear) => linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
}
