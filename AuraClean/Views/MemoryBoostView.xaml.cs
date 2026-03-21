using System.ComponentModel;
using System.Windows.Controls;
using AuraClean.Helpers;
using AuraClean.ViewModels;
using AuraClean.Views.Controls;

namespace AuraClean.Views;

public partial class MemoryBoostView : UserControl
{
    private MemoryViewModel? _subscribedVm;

    public MemoryBoostView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => UnsubscribeVm();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeVm();
        if (e.NewValue is MemoryViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MemoryViewModel.HasBoostResult) &&
            sender is MemoryViewModel vm && vm.HasBoostResult)
        {
            MemoryResultCard.Show(
                FormatHelper.FormatBytes(vm.LastFreedBytes),
                $"Trimmed {vm.LastTrimmedCount} processes",
                ResultCard.Severity.Success);
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
