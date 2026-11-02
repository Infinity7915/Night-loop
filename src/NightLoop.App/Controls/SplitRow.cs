using System.Windows;
using System.Windows.Controls;

namespace NightLoop.App.Controls;

/// <summary>
/// A row with the first child on the left and the others on the right, like CSS "flex-wrap: wrap;
/// justify-content: space-between". When they don't fit side by side, every child gets its own line at full width
/// (left-aligned), so nothing overlaps or gets cut off when the window is narrow.
/// </summary>
public sealed class SplitRow : Panel
{
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(SplitRow), new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(SplitRow), new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Space between children on one line.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>Space between lines once the children are stacked.</summary>
    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    private bool _stacked;

    private IEnumerable<UIElement> Shown => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed);

    protected override Size MeasureOverride(Size available)
    {
        var children = Shown.ToList();
        foreach (var c in InternalChildren.Cast<UIElement>()) c.Measure(new Size(double.PositiveInfinity, available.Height));
        var natural = children.Sum(c => c.DesiredSize.Width) + Gap * Math.Max(0, children.Count - 1);

        _stacked = !double.IsInfinity(available.Width) && natural > available.Width;
        if (!_stacked) return new Size(natural, children.Count == 0 ? 0 : children.Max(c => c.DesiredSize.Height));

        // Stacked: measure again at the real width, so wrapping content wraps.
        double width = 0, height = 0;
        foreach (var c in children)
        {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            width = Math.Max(width, c.DesiredSize.Width);
            height += c.DesiredSize.Height;
        }
        height += RowGap * Math.Max(0, children.Count - 1);
        return new Size(Math.Min(width, available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var children = Shown.ToList();
        if (_stacked)
        {
            double y = 0;
            foreach (var c in children)
            {
                var h = c.DesiredSize.Height;
                c.Arrange(new Rect(0, y, final.Width, h));
                y += h + RowGap;
            }
            return final;
        }

        if (children.Count == 0) return final;
        var first = children[0];
        first.Arrange(new Rect(0, Center(first, final.Height), first.DesiredSize.Width, first.DesiredSize.Height));
        var x = final.Width;
        for (var i = children.Count - 1; i >= 1; i--)
        {
            var c = children[i];
            x -= c.DesiredSize.Width;
            c.Arrange(new Rect(x, Center(c, final.Height), c.DesiredSize.Width, c.DesiredSize.Height));
            x -= Gap;
        }
        return final;
    }

    /// <summary>Vertically centered on the line, or at the bottom when the child asks for it.</summary>
    private static double Center(UIElement c, double height)
    {
        var room = Math.Max(0, height - c.DesiredSize.Height);
        return c is FrameworkElement { VerticalAlignment: VerticalAlignment.Bottom } ? room : room / 2;
    }
}
