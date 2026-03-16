using System.IO;
using System.Windows;
using System.Windows.Controls;
using AuraClean.Services;
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
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _forceClose;

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
                ["ThreatScanner"] = ThreatScannerContent,
                ["SoftwareUpdater"] = SoftwareUpdaterContent,
                ["DiskOptimizer"] = DiskOptimizerContent,
                ["FileRecovery"] = FileRecoveryContent,
                ["EmptyFolders"] = EmptyFoldersContent,
            };

            // Show dashboard by default
            ShowView("Dashboard");

            // Auto-load installed programs when the app starts
            _ = _viewModel.Uninstaller.LoadProgramsCommand.ExecuteAsync(null);

            // Initialize system tray icon
            InitializeTrayIcon();
        }
        catch (Exception ex)
        {
            // Dispose tray icon if it was created before the crash
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }

            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "AuraClean_crash.log");
            File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] MainWindow Init Error:\n{ex}\n\n");
            throw; // rethrow so the app-level handler also catches it
        }
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "AuraClean",
            Visible = false,
        };

        // Extract icon from the running executable
        var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (exePath != null)
        {
            var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon != null)
                _trayIcon.Icon = icon;
        }
        _trayIcon.Icon ??= System.Drawing.SystemIcons.Application;

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open AuraClean", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _forceClose = true; Dispatcher.Invoke(Close); });
        _trayIcon.ContextMenuStrip = menu;

        // Register with NotificationService for app-wide tray notifications
        NotificationService.RegisterTrayIcon(_trayIcon);
    }

    private void RestoreFromTray()
    {
        Dispatcher.Invoke(() =>
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon != null) _trayIcon.Visible = false;
        });
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && IsTrayEnabled())
        {
            Hide();
            if (_trayIcon != null) _trayIcon.Visible = true;
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_forceClose && IsTrayEnabled())
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        // Cleanup tray icon on actual close
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private static bool IsTrayEnabled() => SettingsService.Load().MinimizeToTray;

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

        // Auto-refresh quarantine list when navigating to it
        if (viewName == "Quarantine")
            _viewModel.Quarantine.LoadEntriesCommand.Execute(null);
    }
}
