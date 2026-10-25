namespace OrthoStreamXP.Scenery;

/// <summary>
/// Which mip level X-Plane wants of a texture at a given distance. X-Plane does not document its paging, so this
/// is a model with one constant, k, which the flight tuning measures from X-Plane's reads in flight. The distance is
/// measured from the camera to the texture's near edge (its centre less half its width; height included), in units
/// of the texture's ground pixels: <c>x = edge distance * full_px / size_m</c> (size_m its width on the ground, its
/// LOAD_CENTER size; full_px its width in pixels). X-Plane then wants level n while <c>x &lt; 2^n k</c>: full detail
/// out to k, half size to 2k, quarter to 4k, and so on.
/// <para>
/// Measured in flight with a 3840 px screen at 62°: a 2048 px, 6.2 km ZL16 texture was read at full detail from
/// 25.8 km (to its centre), at level 1 from 48.4 km, level 2 from 93 km and level 3 from 180 km; to the near edge
/// that is 22.7, 45.3, 89.9 and 177 km, each about twice the one before (k about 7500). With the same screen and
/// 4096 px Extra textures the reads fit k = 8203. The screen alone, screen width / (2 tan(FOV / 2)), gives 3195 there:
/// k also depends on X-Plane's texture quality, so the guess from the screen is that times
/// <see cref="ScreenGuessFactor"/>, fitted to those two measurements.
/// </para>
/// </summary>
internal sealed class PagingModel
{
    /// <summary>k until X-Plane sends its screen: about what was measured with a 3840 px screen at 62°.</summary>
    public const double DefaultK = 8000;

    /// <summary>Measured k over screen width / (2 tan(FOV / 2)): 7500 / 3195 and 8203 / 3195.</summary>
    public const double ScreenGuessFactor = 2.5;

    private double _k;

    /// <param name="k">Set by the user (--paging-k); null takes a guess, refined from X-Plane's screen and field of view.</param>
    public PagingModel(double? k = null)
    {
        _k = k ?? DefaultK;
        Fixed = k is not null;
    }

    public double K => Volatile.Read(ref _k);

    /// <summary>Set by the user: X-Plane's screen and field of view do not change it.</summary>
    public bool Fixed { get; }

    public static double GuessK(double screenWidthPx, double fieldOfViewDeg) =>
        ScreenGuessFactor * screenWidthPx / (2 * Math.Tan(fieldOfViewDeg * Math.PI / 360));

    /// <summary>Takes the first guess from X-Plane's window width and horizontal field of view, unless k was set.</summary>
    public void UseScreen(double screenWidthPx, double fieldOfViewDeg)
    {
        if (Fixed || screenWidthPx < 100 || fieldOfViewDeg is < 5 or > 170) return;
        double guess = Math.Round(GuessK(screenWidthPx, fieldOfViewDeg));
        double old = Interlocked.Exchange(ref _k, guess);
        if (Math.Abs(old - guess) > 1)
            Log.Info($"Paging model: k = {guess:F0} from X-Plane's {screenWidthPx:F0} px window and {fieldOfViewDeg:F0}° field of view " +
                     "(a first guess; the flight tuning measures it in flight)");
    }

    /// <summary>The distance in the texture's ground pixels from the camera to its near edge, given the distance to its centre.</summary>
    public static double X(double centreDistanceM, double sizeM, int fullPx) => Math.Max(0, centreDistanceM - sizeM / 2) * fullPx / sizeM;

    /// <summary>The distance to the texture's centre out to which X-Plane wants level <paramref name="mip"/> (or a finer one).</summary>
    public double Threshold(int mip, double sizeM, int fullPx) => K * sizeM * Math.Pow(2, mip) / fullPx + sizeM / 2;

    /// <summary>The level X-Plane wants at <paramref name="centreDistanceM"/> from the texture's centre: 0 for full detail.</summary>
    public int MipFor(double centreDistanceM, double sizeM, int fullPx)
    {
        double x = X(centreDistanceM, sizeM, fullPx) / K;
        return x < 1 ? 0 : (int)Math.Floor(Math.Log2(x)) + 1;
    }

    /// <summary>The k that puts the end of level <paramref name="mip"/> (where X-Plane switches to the next coarser one) at <paramref name="centreDistanceM"/>.</summary>
    public static double Estimate(double centreDistanceM, int mip, double sizeM, int fullPx) => X(centreDistanceM, sizeM, fullPx) / Math.Pow(2, mip);
}
