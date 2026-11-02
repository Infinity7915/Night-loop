using System.Windows.Controls;

namespace NightLoop.App.Views;

/// <summary>Shown at start: checks the server and the saved sign-in. Offers Retry when offline.</summary>
public partial class LoadingView : UserControl
{
    public LoadingView()
    {
        InitializeComponent();
    }
}
