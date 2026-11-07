using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace NightLoop.App.Views;

public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class ZeroToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the bound text is not empty (used for error lines); ConverterParameter=invert flips it.</summary>
public sealed class TextToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is string { Length: > 0 }) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Chip colors per block category, matching the palette in the design mockups.</summary>
public sealed class CategoryBrushConverter : IValueConverter
{
    private static readonly Dictionary<string, Brush> Brushes = new()
    {
        ["input"] = Make("#8B7CF6"),
        ["camera"] = Make("#F472B6"),
        ["timing"] = Make("#9CA3AF"),
        ["detect"] = Make("#F59E0B"),
        ["logic"] = Make("#60A5FA"),
        ["window"] = Make("#4CC9F0"),
        ["utility"] = Make("#D1D5DB"),
    };

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s && Brushes.TryGetValue(s, out var b) ? b : Brushes["utility"];

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Make(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

/// <summary>True when the bound update stage matches the parameter, e.g. ConverterParameter=Checking.</summary>
public sealed class IsStageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// For radio buttons bound to one string: checked when the value equals the parameter, and checking it writes the
/// parameter back. Example: IsChecked="{Binding Mode, Converter={StaticResource EqualsParam}, ConverterParameter=dark}".
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter as string, StringComparison.OrdinalIgnoreCase);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>
/// "#RRGGBB" to a brush (group colors, accent swatches). ConverterParameter is an optional alpha in hex, e.g. "1A" for a
/// faint tint; then a missing color gives a transparent brush instead of gray.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var alpha = parameter is string a && byte.TryParse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed) ? parsed : (byte?)null;
        if (value is not string { Length: > 0 } hex) return alpha is null ? Brushes.Gray : Brushes.Transparent;
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            if (alpha is { } al) color.A = al;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Gray;
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is not null; ConverterParameter=invert flips it.</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is not null) ^ (parameter as string == "invert") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when all bound values are the same text, ignoring case (e.g. which group color is picked).</summary>
public sealed class StringsEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 1 && values.All(v => string.Equals(v?.ToString(), values[0]?.ToString(), StringComparison.OrdinalIgnoreCase));
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A share of a size, minus half of a marker's size (the parameter), to place a marker's middle on a spot: the
/// click spot dot on a block's picture preview.
/// </summary>
public sealed class ShareToOffsetConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var size = values.Length > 0 && values[0] is double s && double.IsFinite(s) ? s : 0;
        var share = values.Length > 1 && values[1] is double f && double.IsFinite(f) ? f : 0.5;
        var half = double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 0;
        return size * share - half;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
