namespace OrthoStreamXP;

/// <summary>The settings the program knows, and the reader that turns a settings file into <see cref="SettingValues"/>.</summary>
internal sealed class SettingsSchema
{
    private sealed record Spec(string Name, int Arity);

    private readonly Dictionary<string, Spec> _specs = new(StringComparer.Ordinal);

    /// <param name="name">The setting's name as written in the settings file.</param>
    /// <param name="help">What the setting does. Documentation only: it keeps each setting described where it is declared.</param>
    /// <param name="arity">0 for a flag, otherwise the number of values on its line (separated by spaces).</param>
    public SettingsSchema Add(string name, string help, int arity = 1)
    {
        _specs.Add(name, new Spec(name, arity));
        return this;
    }

    public SettingValues ReadFile(string path) => Read(File.ReadLines(path), path);

    /// <summary>
    /// Reads settings-file lines. Each is "name = value" with a setting's name; # and ; start comments, [sections]
    /// and blank values are ignored, and a name given twice takes its last value. Flags take true/false, and
    /// settings with several values take them separated by spaces. Paths need no quotes or escaping.
    /// <paramref name="path"/> names the file in error messages.
    /// </summary>
    public SettingValues Read(IEnumerable<string> lines, string path)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        int number = 0;
        foreach (string raw in lines)
        {
            number++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[') continue;
            int equals = line.IndexOf('=');
            if (equals <= 0) throw new SettingsException($"{path}, line {number}: expected 'name = value'");
            string name = line[..equals].Trim().TrimStart('-');
            string value = line[(equals + 1)..].Trim().Trim('"');
            if (!_specs.TryGetValue(name, out Spec? spec)) throw new SettingsException($"{path}, line {number}: unknown setting '{name}'");
            if (value.Length == 0) continue;
            if (spec.Arity == 0)
            {
                bool enabled = value.ToLowerInvariant() switch
                {
                    "true" or "yes" or "on" or "1" => true,
                    "false" or "no" or "off" or "0" => false,
                    _ => throw new SettingsException($"{path}, line {number}: '{name}' must be true or false"),
                };
                if (enabled) values[name] = ["true"];
                continue;
            }
            string[] parts = spec.Arity == 1 ? [value] : value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != spec.Arity)
                throw new SettingsException($"{path}, line {number}: '{name}' needs {spec.Arity} values separated by spaces");
            values[name] = [.. parts];
        }
        return new SettingValues(values);
    }
}
