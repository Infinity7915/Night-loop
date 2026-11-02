using System.ComponentModel;
using System.Windows;
using NightLoop.App.ViewModels;

namespace NightLoop.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosing(e);
    }
}
