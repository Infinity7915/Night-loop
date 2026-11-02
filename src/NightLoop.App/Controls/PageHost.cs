using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace NightLoop.App.Controls;

/// <summary>
/// Shows the current page and cross-fades to the next one: the old page fades out and lifts a little while the
/// new one fades in and settles into place. Turned off when Windows animation effects are off.
/// </summary>
[TemplatePart(Name = OldPart, Type = typeof(Image))]
[TemplatePart(Name = NewPart, Type = typeof(ContentPresenter))]
public sealed class PageHost : ContentControl
{
    private const string OldPart = "PART_Old";
    private const string NewPart = "PART_New";
    private static readonly Duration OutDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration InDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly CubicEase Ease = new() { EasingMode = EasingMode.EaseOut };

    private Image? _old;
    private ContentPresenter? _new;

    static PageHost() => Ease.Freeze();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _old = GetTemplateChild(OldPart) as Image;
        _new = GetTemplateChild(NewPart) as ContentPresenter;
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        var snapshot = oldContent is null ? null : Snapshot();
        base.OnContentChanged(oldContent, newContent);
        if (_old is null || _new is null || !SystemParameters.ClientAreaAnimation) return;

        if (snapshot is not null)
        {
            _old.Source = snapshot;
            _old.Width = ActualWidth;
            _old.Height = ActualHeight;
            var fadeOut = new DoubleAnimation(1, 0, OutDuration) { EasingFunction = Ease };
            fadeOut.Completed += (_, _) => _old.Source = null;
            _old.RenderTransform = new TranslateTransform();
            _old.BeginAnimation(OpacityProperty, fadeOut);
            _old.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, -6, OutDuration) { EasingFunction = Ease });
        }

        var delay = TimeSpan.FromMilliseconds(snapshot is null ? 0 : 60);
        _new.Opacity = 0;
        _new.RenderTransform = new TranslateTransform(0, 14);
        _new.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, InDuration) { BeginTime = delay, EasingFunction = Ease });
        _new.RenderTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, InDuration) { BeginTime = delay, EasingFunction = Ease });
    }

    /// <summary>A picture of the page that is leaving, so it can fade out after its content is gone.</summary>
    private RenderTargetBitmap? Snapshot()
    {
        if (_new is null || ActualWidth < 1 || ActualHeight < 1 || !SystemParameters.ClientAreaAnimation) return null;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(_new);
        bitmap.Freeze();
        return bitmap;
    }
}
