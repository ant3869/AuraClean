using System.Windows.Controls;
using System.Windows.Data;

namespace AuraClean.Views;

public partial class SystemInfoView : UserControl
{
    public SystemInfoView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SystemInfoViewModel vm)
        {
            vm.PropertyChanged += (s, args) =>
            {
                if (args.PropertyName == nameof(vm.FilteredEntries))
                {
                    Dispatcher.BeginInvoke(new Action(ApplyGrouping));
                }
            };
            ApplyGrouping();
        }
    }

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
