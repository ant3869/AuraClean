using System.ComponentModel;
using System.Windows.Controls;
using AuraClean.Helpers;
using AuraClean.ViewModels;
using AuraClean.Views.Controls;

namespace AuraClean.Views;

public partial class CleanerView : UserControl
{
    private CleanerViewModel? _subscribedVm;
    private bool _wasCleanInProgress;

    public CleanerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => UnsubscribeVm();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeVm();
        if (e.NewValue is CleanerViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CleanerViewModel vm) return;

        if (e.PropertyName == nameof(CleanerViewModel.IsBusy))
        {
            if (vm.IsBusy) _wasCleanInProgress = true;
            else if (_wasCleanInProgress && vm.LastCleanedCount > 0)
            {
                _wasCleanInProgress = false;
                CleanerResultCard.Show(
                    FormatHelper.FormatBytes(vm.LastCleanedBytes),
                    $"{vm.LastCleanedCount} items cleaned",
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
