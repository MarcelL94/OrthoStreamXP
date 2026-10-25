using System.Globalization;

namespace OrthoStreamXP;

/// <summary>
/// The settings a settings file sets, by name (as written in the file, without dashes), with typed readers.
/// A setting that is not set is absent; its default is the reader's business.
/// </summary>
internal sealed class SettingValues(Dictionary<string, List<string>> values)
{
    public bool Has(string name) => values.ContainsKey(name);

    /// <summary>A setting that takes no value: set (true) or not.</summary>
    public bool Flag(string name) => values.ContainsKey(name);

    public string? String(string name) => values.TryGetValue(name, out var v) ? v[0] : null;

    public string Required(string name) => String(name) ?? throw new SettingsException($"Setting required: {name}");

    public int Int(string name, int fallback) => values.TryGetValue(name, out var v) ? ParseInt(name, v[0]) : fallback;

    public int? OptionalInt(string name) => values.TryGetValue(name, out var v) ? ParseInt(name, v[0]) : null;

    public double Double(string name, double fallback) => values.TryGetValue(name, out var v) ? ParseDouble(name, v[0]) : fallback;

    public (double, double)? Pair(string name) =>
        values.TryGetValue(name, out var v) ? (ParseDouble(name, v[0]), ParseDouble(name, v[1])) : null;

    private static int ParseInt(string name, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new SettingsException($"{name}: invalid whole number: '{value}'");

    private static double ParseDouble(string name, string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : throw new SettingsException($"{name}: invalid number: '{value}'");
}
