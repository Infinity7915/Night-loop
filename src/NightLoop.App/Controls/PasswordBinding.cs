using System.Windows;
using System.Windows.Controls;

namespace NightLoop.App.Controls;

/// <summary>
/// PasswordBox.Password can't be bound, so this attached property mirrors it:
/// &lt;PasswordBox controls:PasswordBinding.Value="{Binding CurrentPassword, Mode=TwoWay}" /&gt;.
/// Setting the bound value to "" clears the box.
/// </summary>
public static class PasswordBinding
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(string), typeof(PasswordBinding),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(PasswordBinding));

    public static string GetValue(DependencyObject d) => (string)d.GetValue(ValueProperty);
    public static void SetValue(DependencyObject d, string value) => d.SetValue(ValueProperty, value);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        if (!(bool)box.GetValue(HookedProperty))
        {
            box.SetValue(HookedProperty, true);
            box.PasswordChanged += (_, _) =>
            {
                if (GetValue(box) != box.Password) SetValue(box, box.Password);
            };
        }
        var text = e.NewValue as string ?? "";
        if (box.Password != text) box.Password = text;
    }
}
