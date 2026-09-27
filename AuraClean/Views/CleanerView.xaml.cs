using System.Windows.Controls;
using AuraClean.Helpers;
using AuraClean.ViewModels;
using AuraClean.Views.Controls;

namespace AuraClean.Views;

public partial class CleanerView : UserControl
{
    private CleanerViewModel? _subscribedVm;

    public CleanerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => UnsubscribeVm();
        Loaded += (_, _) =>
        {
            // Re-attach after an unload/reload cycle (e.g. theme or template refresh).
            if (_subscribedVm == null && DataContext is CleanerViewModel vm)
                SubscribeVm(vm);
        };
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        UnsubscribeVm();
        if (e.NewValue is CleanerViewModel vm)
            SubscribeVm(vm);
    }

    private void SubscribeVm(CleanerViewModel vm)
    {
        _subscribedVm = vm;
        vm.CleanupCompleted += OnCleanupCompleted;
    }

    private void OnCleanupCompleted(object? sender, CleanupCompletedEventArgs e)
    {
        if (e.WasDryRun)
            return;

        if (e.ItemsCleaned > 0)
        {
            CleanerResultCard.Show(
                FormatHelper.FormatBytes(e.BytesFreed),
                $"{e.ItemsCleaned} items cleaned",
                ResultCard.Severity.Success);
        }
        else
        {
            CleanerResultCard.Show(
                "Nothing removed",
                "Selected items were in use, recent, or protected",
                ResultCard.Severity.Warning);
        }
    }

    private void UnsubscribeVm()
    {
        if (_subscribedVm is not null)
        {
            _subscribedVm.CleanupCompleted -= OnCleanupCompleted;
            _subscribedVm = null;
        }
    }
}
