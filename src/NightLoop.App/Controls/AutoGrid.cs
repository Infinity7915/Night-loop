using System.Windows;
using System.Windows.Controls;

namespace NightLoop.App.Controls;

/// <summary>
/// Lays children out in as many equal columns as fit (each at least <see cref="MinItemWidth"/> wide), like CSS
/// "grid-template-columns: repeat(auto-fill, minmax(190px, 1fr))". Items in a row share the tallest one's height.
/// </summary>
public sealed class AutoGrid : Panel
{
    public static readonly DependencyProperty MinItemWidthProperty = DependencyProperty.Register(
        nameof(MinItemWidth), typeof(double), typeof(AutoGrid), new FrameworkPropertyMetadata(190.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(AutoGrid), new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinItemWidth
    {
        get => (double)GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>
    /// Like CSS auto-fit instead of auto-fill: with fewer items than columns, the items stretch to share the width
    /// (forms) instead of leaving empty columns (the block palette).
    /// </summary>
    public bool Fit
    {
        get => (bool)GetValue(FitProperty);
        set => SetValue(FitProperty, value);
    }

    public static readonly DependencyProperty FitProperty = DependencyProperty.Register(
        nameof(Fit), typeof(bool), typeof(AutoGrid), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Children that take a cell: collapsed ones are skipped.</summary>
    private List<UIElement> Shown() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToList();

    private (int Columns, double ItemWidth) Layout(double width, int count)
    {
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0) width = MinItemWidth * 4 + Gap * 3;
        var columns = Math.Max(1, (int)((width + Gap) / (MinItemWidth + Gap)));
        if (Fit && count > 0) columns = Math.Min(columns, count);
        return (columns, Math.Max(0, (width - Gap * (columns - 1)) / columns));
    }

    private static List<double> RowHeights(List<UIElement> items, int columns)
    {
        var rows = new List<double>();
        for (var i = 0; i < items.Count; i++)
        {
            if (i % columns == 0) rows.Add(0);
            rows[^1] = Math.Max(rows[^1], items[i].DesiredSize.Height);
        }
        return rows;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Shown();
        var (columns, itemWidth) = Layout(availableSize.Width, items.Count);
        foreach (UIElement child in InternalChildren) child.Measure(new Size(itemWidth, double.PositiveInfinity));
        var rows = RowHeights(items, columns);
        var height = rows.Sum() + Gap * Math.Max(0, rows.Count - 1);
        var width = double.IsInfinity(availableSize.Width) ? columns * itemWidth + Gap * (columns - 1) : availableSize.Width;
        return new Size(items.Count == 0 ? 0 : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = Shown();
        var (columns, itemWidth) = Layout(finalSize.Width, items.Count);
        var rows = RowHeights(items, columns);
        var y = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            var row = i / columns;
            if (i > 0 && i % columns == 0) y += rows[row - 1] + Gap;
            var x = (i % columns) * (itemWidth + Gap);
            items[i].Arrange(new Rect(x, y, itemWidth, rows[row]));
        }
        return finalSize;
    }
}
