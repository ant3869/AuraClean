using System.Windows.Controls;
using System.Windows.Input;
using AuraClean.Models;
using AuraClean.ViewModels;

namespace AuraClean.Views;

public partial class ThreatScannerView : UserControl
{
    public ThreatScannerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is ThreatScannerViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(ThreatScannerViewModel.SelectedScanMode))
                    UpdateCustomPathVisibility(vm.SelectedScanMode);
            };
            UpdateCustomPathVisibility(vm.SelectedScanMode);
        }
    }

    private void UpdateCustomPathVisibility(ScanMode mode)
    {
        CustomPathPanel.Visibility = mode == ScanMode.Custom
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    private void QuickScan_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ThreatScannerViewModel vm)
            vm.SelectScanModeCommand.Execute("Quick");
    }

    private void FullScan_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ThreatScannerViewModel vm)
            vm.SelectScanModeCommand.Execute("Full");
    }

    private void CustomScan_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ThreatScannerViewModel vm)
            vm.SelectScanModeCommand.Execute("Custom");
    }

    private void BrowserScan_Click(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ThreatScannerViewModel vm)
            vm.SelectScanModeCommand.Execute("Browser");
    }
}
