using System.IO;
using System.Windows;
using System.Windows.Controls;
using AuraClean.ViewModels;

namespace AuraClean.Views;

/// <summary>
/// Main application window — handles navigation between views via code-behind
/// since WPF doesn't have a built-in navigation frame with MaterialDesign.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Dictionary<string, FrameworkElement>? _viewMap;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            _viewModel = (MainViewModel)DataContext;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Build view lookup table after InitializeComponent so all named elements exist
            _viewMap = new Dictionary<string, FrameworkElement>
            {
                ["Dashboard"] = DashboardContent,
                ["Uninstaller"] = UninstallerContent,
                ["Cleaner"] = CleanerContent,
                ["Memory"] = MemoryContent,
                ["Browser"] = BrowserContent,
                ["StorageMap"] = StorageMapContent,
                ["Monitor"] = MonitorContent,
                ["Startup"] = StartupContent,
                ["Duplicates"] = DuplicatesContent,
                ["Shredder"] = ShredderContent,
                ["LargeFiles"] = LargeFilesContent,
                ["SystemInfo"] = SystemInfoContent,
                ["Settings"] = SettingsContent,
                ["History"] = HistoryContent,
                ["Quarantine"] = QuarantineContent,
            };

            // Show dashboard by default
            ShowView("Dashboard");

            // Auto-load installed programs when the app starts
            _ = _viewModel.Uninstaller.LoadProgramsCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "AuraClean_crash.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow Init Error:\n{ex}\n\n");
            throw; // rethrow so the app-level handler also catches it
        }
    }

    private void OnViewModelPropertyChanged(object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentViewName))
        {
            ShowView(_viewModel.CurrentViewName);
        }
    }

    private void ShowView(string viewName)
    {
        if (_viewMap == null) return;

        // Hide all views, then show only the selected one
        foreach (var element in _viewMap.Values)
            element.Visibility = Visibility.Collapsed;

        if (_viewMap.TryGetValue(viewName, out var target))
            target.Visibility = Visibility.Visible;
    }
}
