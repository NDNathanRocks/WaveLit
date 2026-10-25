using System.Windows;
using Wavelit.App.Dashboard.ViewModels;
using Wpf.Ui.Controls;

namespace Wavelit.App.Dashboard;

public partial class DashboardWindow : FluentWindow
{
    private readonly MainViewModel _vm;

    public DashboardWindow(MainViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        InitializeComponent();
        Drop += OnDrop;
        DragOver += OnDragOver;
        Closed += (_, _) => _vm.Dispose();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        _vm.Library.AddPaths(files);
        _vm.SelectedNav = _vm.NavItems.First(n => n.Key == "Library");
    }
}
