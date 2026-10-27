using System.Windows;
using System.Windows.Controls;
using Wavelit.App.Dashboard.ViewModels;

namespace Wavelit.App.Dashboard.Views;

public partial class StudioView : UserControl
{
    public StudioView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Preview.PreviewFailed += (_, message) => { if (DataContext is StudioViewModel vm) vm.ShowPreviewProblem(message); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not StudioViewModel vm) return;
        Preview.Paths = vm.Paths;
        vm.Attach(Preview);
        vm.Activate();
        _ = StartPreviewAsync(vm);
    }

    private async Task StartPreviewAsync(StudioViewModel vm)
    {
        try
        {
            await Preview.EnsureReadyAsync();
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException
                                       or System.Runtime.InteropServices.COMException or Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            Serilog.Log.Warning(ex, "Studio preview failed to start");
            vm.ShowPreviewProblem("The Studio preview couldn't start, so shaders can't be checked. Restart Wavelit and try again.");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is StudioViewModel vm) vm.Attach(null);
    }
}
