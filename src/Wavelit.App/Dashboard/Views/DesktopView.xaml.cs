using System.Windows;
using System.Windows.Controls;
using Wavelit.App.Dashboard.ViewModels;

namespace Wavelit.App.Dashboard.Views;

public partial class DesktopView : UserControl
{
    private DesktopViewModel? _vm;

    public DesktopView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(IsLoaded ? DataContext as DesktopViewModel : null);
        // The view model outlives this view (pages are re-created on navigation): attach only while loaded.
        Loaded += (_, _) =>
        {
            Attach(DataContext as DesktopViewModel);
            if (IsVisible) _vm?.Activate();
        };
        Unloaded += (_, _) => { _vm?.Deactivate(); Attach(null); };
        Preview.Selected += (_, id) => _vm?.OnEditorSelected(id);
        Preview.Moved += (_, m) => _vm?.OnEditorMoved(m.Id, m.X, m.Y);
        Preview.Resized += (_, r) => _vm?.OnEditorResized(r.Id, r.Size);
        Preview.DeleteRequested += (_, id) => _vm?.OnEditorDelete(id);
    }

    private void Attach(DesktopViewModel? vm)
    {
        if (_vm is not null) _vm.SceneChanged -= OnScene;
        _vm = vm;
        if (vm is null) return;
        Preview.Paths = vm.Paths;
        vm.SceneChanged += OnScene;
    }

    private void OnScene(object? sender, object scene) => Preview.ShowScene(scene);

    // The wallpaper and displays may have changed while the page was hidden; the preview's browser is released
    // when the page is left (DesktopPreview.Unloaded).
    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is null) return;
        if (e.NewValue is true) _vm.Activate();
        else _vm.Deactivate();
    }
}
