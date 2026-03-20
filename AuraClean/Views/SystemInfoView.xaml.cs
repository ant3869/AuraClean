using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;

namespace AuraClean.Views;

public partial class SystemInfoView : UserControl
{
    private ViewModels.SystemInfoViewModel? _subscribedVm;

    public SystemInfoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        UnsubscribeVm();

        if (DataContext is ViewModels.SystemInfoViewModel vm)
        {
            _subscribedVm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            ApplyGrouping();
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ViewModels.SystemInfoViewModel.FilteredEntries))
            Dispatcher.BeginInvoke(new Action(ApplyGrouping));
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

    private void ApplyGrouping()
    {
        if (DataContext is not ViewModels.SystemInfoViewModel vm) return;

        var view = CollectionViewSource.GetDefaultView(vm.FilteredEntries);
        if (view != null)
        {
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(new PropertyGroupDescription("Category"));
        }
    }
}
