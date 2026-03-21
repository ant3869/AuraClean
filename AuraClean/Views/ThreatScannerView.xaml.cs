using System.ComponentModel;
using System.Windows.Controls;
using AuraClean.Models;
using AuraClean.ViewModels;
using AuraClean.Views.Controls;

namespace AuraClean.Views;

public partial class ThreatScannerView : UserControl
{
    private ThreatScannerViewModel? _subscribedVm;
    private bool _wasScanInProgress;

    public ThreatScannerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeVm();

        if (e.NewValue is ThreatScannerViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            UpdateCustomPathVisibility(vm.SelectedScanMode);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (sender is not ThreatScannerViewModel vm) return;

        if (args.PropertyName == nameof(ThreatScannerViewModel.SelectedScanMode))
            UpdateCustomPathVisibility(vm.SelectedScanMode);

        if (args.PropertyName == nameof(ThreatScannerViewModel.IsScanning))
        {
            if (vm.IsScanning) _wasScanInProgress = true;
            else if (_wasScanInProgress)
            {
                _wasScanInProgress = false;
                if (vm.IsClean)
                {
                    ScanResultCard.Show(
                        "No threats found",
                        $"{vm.FilesScanned} files scanned in {vm.ScanDuration}",
                        ResultCard.Severity.Success);
                }
                else
                {
                    var severity = vm.CriticalCount > 0
                        ? ResultCard.Severity.Danger
                        : ResultCard.Severity.Warning;
                    ScanResultCard.Show(
                        $"{vm.TotalThreats} threats detected",
                        $"{vm.CriticalCount} critical, {vm.HighCount} high — {vm.ScanDuration}",
                        severity);
                }
            }
        }
    }

    private void UnsubscribeVm()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            _subscribedVm = null;
        }
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e) => UnsubscribeVm();

    private void UpdateCustomPathVisibility(ScanMode mode)
    {
        CustomPathPanel.Visibility = mode == ScanMode.Custom
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }
}
