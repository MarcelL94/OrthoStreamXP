using System.Globalization;
using System.Windows.Input;
using OrthoStreamXP.Setup;
using OrthoStreamXP.Textures;
using OrthoStreamXP.Tuning;

namespace OrthoStreamXP.Gui;

/// <summary>One setting of the Other section: a label, a plain explanation and a checked value.</summary>
internal sealed class SettingField : ObservableObject
{
    private readonly Action<string, string?> _save;
    private readonly double _min, _max;
    private readonly bool _decimals;
    private readonly string _on, _off;
    private string _value = "";
    private string? _error;
    private bool _loading;

    public SettingField(string key, string label, string help, FieldKind kind, Action<string, string?> save,
        double min = 0, double max = 0, bool decimals = false, IReadOnlyList<Choice>? choices = null,
        string on = "true", string off = "false", string? placeholder = null)
    {
        Key = key;
        Label = label;
        Help = help;
        Kind = kind;
        _save = save;
        _min = min;
        _max = max;
        _decimals = decimals;
        Choices = choices ?? [];
        _on = on;
        _off = off;
        Placeholder = placeholder;
        Browse = new RelayCommand(() =>
        {
            if (Shell.PickFolder(Label, Value) is { } folder) Value = folder;
        });
    }

    public string Key { get; }
    public string Label { get; }
    public string Help { get; }
    public FieldKind Kind { get; }
    public IReadOnlyList<Choice> Choices { get; }
    public string? Placeholder { get; }
    public ICommand Browse { get; }

    public string Value
    {
        get => _value;
        set
        {
            value = value?.Trim() ?? "";
            if (!Set(ref _value, value) || _loading) return;
            Error = Check(value);
            if (Error is null) _save(Key, value.Length == 0 ? null : value);
        }
    }

    public bool IsOn
    {
        get => Value == _on;
        set
        {
            Value = value ? _on : _off;
            Raise();
            Raise(nameof(OnOffText));
        }
    }

    public string OnOffText => IsOn ? "On" : "Off";

    public Choice? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value) ?? (Choices.Count > 0 ? Choices[0] : null);
        set
        {
            if (value is null) return;
            Value = value.Value;
            Raise();
        }
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>Shows a value from the file without saving it back.</summary>
    /// <param name="fallback">The program's default, shown when the file does not set it.</param>
    public void Load(string? value, string fallback)
    {
        _loading = true;
        Value = value ?? fallback;
        _loading = false;
        Error = null;
        Raise(nameof(IsOn));
        Raise(nameof(OnOffText));
        Raise(nameof(SelectedChoice));
    }

    private string? Check(string value)
    {
        switch (Kind)
        {
            case FieldKind.Number:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || (!_decimals && number != Math.Floor(number)))
                    return _decimals ? "Enter a number, such as 2.5" : "Enter a whole number";
                return number < _min || number > _max
                    ? string.Create(CultureInfo.InvariantCulture, $"Enter a number from {_min} to {_max}")
                    : null;
            case FieldKind.Folder:
                return value.Length > 0 && !Directory.Exists(value) ? "This folder does not exist" : null;
            case FieldKind.Text when Key == "xplane-address":
                return value.Length > 0 && !System.Net.IPEndPoint.TryParse(value, out _) ? "Enter an IP address and port, such as 127.0.0.1:49000" : null;
            default:
                return null;
        }
    }
}
