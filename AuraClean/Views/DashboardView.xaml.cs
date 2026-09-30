using System.Windows.Controls;

namespace AuraClean.Views;

public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            // D1: first show primes the dashboard (Uninstaller load + Cleaner analyze
            // if never scanned). The DataContext is the shared MainViewModel.
            if (DataContext is ViewModels.MainViewModel vm)
                vm.PrimeDashboardCommand.Execute(null);
        };
    }
}
