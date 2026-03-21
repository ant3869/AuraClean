using System.ComponentModel;
using System.Windows.Controls;
using AuraClean.Helpers;
using AuraClean.ViewModels;
using AuraClean.Views.Controls;

namespace AuraClean.Views;

public partial class BrowserCleanerView : UserControl
{
    private BrowserCleanerViewModel? _subscribedVm;
    private bool _wasCleanInProgress;

    public BrowserCleanerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => UnsubscribeVm();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeVm();
        if (e.NewValue is BrowserCleanerViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not BrowserCleanerViewModel vm) return;

        if (e.PropertyName == nameof(BrowserCleanerViewModel.IsBusy))
        {
            if (vm.IsBusy) _wasCleanInProgress = true;
            else if (_wasCleanInProgress && vm.TotalSavingsBytes > 0)
            {
                _wasCleanInProgress = false;
                BrowserResultCard.Show(
                    FormatHelper.FormatBytes(vm.TotalSavingsBytes),
                    $"{vm.TotalItemCount} browser items cleaned",
                    ResultCard.Severity.Success);
            }
            else _wasCleanInProgress = false;
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
}
