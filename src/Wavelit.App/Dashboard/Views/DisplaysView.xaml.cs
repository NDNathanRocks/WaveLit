using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wavelit.App.Dashboard.ViewModels;

namespace Wavelit.App.Dashboard.Views;

public partial class DisplaysView : UserControl
{
    private Point? _dragFrom;

    public DisplaysView() => InitializeComponent();

    private FramingEditorViewModel? Editor => (DataContext as DisplaysViewModel)?.Framing;

    private void OnFramingDown(object sender, MouseButtonEventArgs e)
    {
        FramingMap.Focus();
        _dragFrom = e.GetPosition(FramingMap);
        FramingMap.CaptureMouse();
        e.Handled = true;
    }

    private void OnFramingMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from) return;
        var now = e.GetPosition(FramingMap);
        Editor?.PanBy(now.X - from.X, now.Y - from.Y);
        _dragFrom = now;
    }

    private void OnFramingUp(object sender, MouseButtonEventArgs e) => FramingMap.ReleaseMouseCapture();

    private void OnFramingLostCapture(object sender, MouseEventArgs e) => _dragFrom = null;

    private void OnFramingWheel(object sender, MouseWheelEventArgs e)
    {
        Editor?.ZoomBy(e.Delta);
        e.Handled = true;
    }

    // Keyboard access: arrows nudge, +/- zoom.
    private void OnFramingKey(object sender, KeyEventArgs e)
    {
        if (Editor is not { } editor) return;
        const double step = 4;
        switch (e.Key)
        {
            case Key.Left: editor.PanBy(-step, 0); break;
            case Key.Right: editor.PanBy(step, 0); break;
            case Key.Up: editor.PanBy(0, -step); break;
            case Key.Down: editor.PanBy(0, step); break;
            case Key.OemPlus or Key.Add: editor.ZoomBy(120); break;
            case Key.OemMinus or Key.Subtract: editor.ZoomBy(-120); break;
            default: return;
        }
        e.Handled = true;
    }
}
