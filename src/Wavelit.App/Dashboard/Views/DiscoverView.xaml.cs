using System.Windows.Controls;
using System.Windows.Navigation;
using Wavelit.App.Dashboard.ViewModels;

namespace Wavelit.App.Dashboard.Views;

public partial class DiscoverView : UserControl
{
    public DiscoverView() => InitializeComponent();

    // Infinite scroll: fetch the next page when the user nears the bottom.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DiscoverViewModel vm || e.ExtentHeight <= 0) return;
        if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 400 && vm.LoadMoreCommand.CanExecute(null))
            vm.LoadMoreCommand.Execute(null);
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Links.Open(e.Uri);
        e.Handled = true;
    }
}

internal static class Links
{
    public static void Open(Uri uri)
    {
        if (uri.Scheme == Uri.UriSchemeHttps)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
