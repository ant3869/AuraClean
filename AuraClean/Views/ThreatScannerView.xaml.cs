using System.ComponentModel;
using System.Windows.Controls;
using AuraClean.Models;
using AuraClean.ViewModels;

namespace AuraClean.Views;

public partial class ThreatScannerView : UserControl
{
    private ThreatScannerViewModel? _subscribedVm;

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
        if (args.PropertyName == nameof(ThreatScannerViewModel.SelectedScanMode) &&
            sender is ThreatScannerViewModel vm)
            UpdateCustomPathVisibility(vm.SelectedScanMode);
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
