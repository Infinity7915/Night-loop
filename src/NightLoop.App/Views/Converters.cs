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

/// <summary>Visible when the bound text is not empty (used for error lines).</summary>
public sealed class TextToVisibleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
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

/// <summary>"#RRGGBB" to a brush (group colors, accent swatches).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value as string ?? "#808080"));
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
