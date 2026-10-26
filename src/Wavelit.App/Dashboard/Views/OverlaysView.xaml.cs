using System.Windows;
using System.Windows.Controls;
using Wavelit.App.Dashboard.ViewModels;

namespace Wavelit.App.Dashboard.Views;

public partial class OverlaysView : UserControl
{
    public OverlaysView() => InitializeComponent();

    // The current wallpaper may have changed while the page was hidden.
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && DataContext is OverlaysViewModel vm) vm.Sync();
    }
}
