using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Setup;

/// <summary>A 1° x 1° area of the photo scenery, named as X-Plane names its scenery tiles ("+51+017": south-west corner).</summary>
internal readonly record struct AreaName(int Lat, int Lon)
{
    public override string ToString() => FormattableString.Invariant($"{Lat:+00;-00}{Lon:+000;-000}");

    public static AreaName Of(TextureName name)
    {
        var (lat, lon) = name.Center;
        return new AreaName((int)Math.Floor(lat), (int)Math.Floor(lon));
    }

    public static bool TryParse(string text, out AreaName area)
    {
        area = default;
        if (text.Length != 7
            || !int.TryParse(text[..3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lat)
            || !int.TryParse(text[3..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int lon))
            return false;
        area = new AreaName(lat, lon);
        return true;
    }
}
