using Wavelit.App.Dashboard.ViewModels;
using Wpf.Ui.Controls;

namespace Wavelit.App.Dashboard;

public partial class WelcomeWindow : FluentWindow
{
    public WelcomeWindow(WelcomeViewModel vm)
    {
        DataContext = vm;
        InitializeComponent();
        vm.Finished += (_, _) => Close();
        Closed += (_, _) => vm.Dispose();
    }
}
