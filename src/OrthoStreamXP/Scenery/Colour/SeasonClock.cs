using System.Globalization;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Scenery;

/// <summary>The seasons settings: on or off, how strong (0-1), and a season chosen by hand.</summary>
internal sealed record SeasonOptions(bool On, double Strength, SeasonChoice Choice)
{
    public const double DefaultStrength = 80;

    public static readonly SeasonOptions Off = new(false, DefaultStrength / 100, SeasonChoice.Auto);
}

/// <summary>
/// Which season the photo textures show, and each texture's lookup table for it. The date is X-Plane's
/// (sim/time/local_date_days), taken when a flight starts, so every texture of a flight is coloured for the same day:
/// a date changed in X-Plane shows from the next flight or move to another place. Until X-Plane sends a date, the date
/// of the last flight is used (kept in the history folder), else today's.
/// </summary>
internal sealed class SeasonClock
{
    /// <summary>The file in the history folder that keeps the last X-Plane date.</summary>
    public const string DayFileName = "sim-date.txt";

    private readonly SeasonOptions _options;
    private readonly string? _dayFile;
    private int _day;
    private bool _fromXPlane;

    /// <param name="dayFile">Where the latest X-Plane date is kept between runs.</param>
    public SeasonClock(SeasonOptions options, string? dayFile)
    {
        _options = options;
        _dayFile = dayFile;
        _day = Remembered(dayFile) ?? DateTime.Now.DayOfYear - 1;
    }

    /// <summary>The day of the year in use (0 is 1 January).</summary>
    public int Day => Volatile.Read(ref _day);

    public SeasonOptions Options => _options;

    /// <summary>A date from X-Plane: used if it is the first, or a flight is starting (a new flight, or a move to another place).</summary>
    public void OfferDate(int dayOfYear, bool newFlight)
    {
        if (dayOfYear is < 0 or > 365 || (_fromXPlane && !newFlight)) return;
        bool first = !_fromXPlane;
        _fromXPlane = true;
        if (dayOfYear == Day && !first) return;
        if (first || dayOfYear != Day)
            Log.Step($"Seasons: X-Plane's date is {Describe(dayOfYear)}" +
                     (_options.Choice == SeasonChoice.Auto ? "" : $" (the season is set by hand: {_options.Choice})"));
        if (dayOfYear == Day) return;
        Volatile.Write(ref _day, dayOfYear);
        try
        {
            if (_dayFile is not null) File.WriteAllText(_dayFile, dayOfYear.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a convenience for the next start.
        }
    }

    /// <summary>The season at a place in the settings' strength (summer when it changes nothing).</summary>
    public SeasonState StateAt(double lat) =>
        (_options.Choice == SeasonChoice.Auto ? SeasonModel.For(Day, lat) : SeasonModel.For(_options.Choice)).Scaled(_options.Strength);

    /// <summary>A texture's lookup table for the season at its centre, or null when nothing in it changes.</summary>
    public ushort[]? TableFor(TextureName name) => Seasons.TableFor(StateAt(name.Center.Lat));

    /// <summary>"27 October" for day 299.</summary>
    public static string Describe(int dayOfYear) =>
        new DateTime(2025, 1, 1).AddDays(Math.Clamp(dayOfYear, 0, 364)).ToString("d MMMM", CultureInfo.InvariantCulture);

    private static int? Remembered(string? file)
    {
        try
        {
            return file is not null && File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)
                   && day is >= 0 and <= 365 ? day : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
