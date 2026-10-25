using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OrthoStreamXP.Gui;

/// <summary>
/// Visible when the value is true, a non-empty string or any other non-null object; "Invert" as the
/// parameter turns it around.
/// </summary>
internal sealed class VisibleWhen : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool shown = value switch
        {
            bool flag => flag,
            string text => text.Length > 0,
            int number => number > 0,
            null => false,
            _ => true,
        };
        if (parameter is "Invert") shown = !shown;
        return shown ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound value equals the parameter (an enum name or a number), for radio buttons and triggers.</summary>
internal sealed class EqualsParameter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
