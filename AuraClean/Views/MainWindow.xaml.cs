using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AuraClean.Helpers;
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
    private readonly Dictionary<string, FrameworkElement> _viewMap;
    private readonly Dictionary<string, RadioButton> _navMap;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _forceClose;
    private bool _startupMaintenanceDone;
    private string? _currentViewKey;

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            _viewModel = (MainViewModel)DataContext;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

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
                ["AppInstaller"] = AppInstallerContent,
            };

            _navMap = new Dictionary<string, RadioButton>
            {
                ["Dashboard"] = NavDashboard,
                ["ThreatScanner"] = NavThreatScanner,
                ["Cleaner"] = NavCleaner,
                ["Browser"] = NavBrowser,
                ["Uninstaller"] = NavUninstaller,
                ["StorageMap"] = NavStorage,
                ["Duplicates"] = NavDuplicates,
                ["Memory"] = NavMemory,
                ["DiskOptimizer"] = NavDiskOptimizer,
                ["Startup"] = NavStartup,
                ["LargeFiles"] = NavLargeFiles,
                ["Shredder"] = NavShredder,
                ["SystemInfo"] = NavSystemInfo,
                ["Quarantine"] = NavQuarantine,
                ["SoftwareUpdater"] = NavSoftwareUpdater,
                ["FileRecovery"] = NavFileRecovery,
                ["EmptyFolders"] = NavEmptyFolders,
                ["AppInstaller"] = NavAppInstaller,
                ["Monitor"] = NavMonitor,
                ["History"] = NavHistory,
                ["Settings"] = NavSettings,
            };

            ShowView("Dashboard");

            if (_viewModel.Onboarding.ShouldShowOnboarding())
            {
                _viewModel.Onboarding.OnboardingCompleted += OnOnboardingCompleted;
                _viewModel.Onboarding.Show();
                OnboardingOverlay.Visibility = Visibility.Visible;
            }

            _ = _viewModel.Uninstaller.LoadProgramsCommand.ExecuteAsync(null);

            InitializeTrayIcon();
            ContentRendered += OnFirstContentRendered;

            // Never intercept a Windows logoff/shutdown with the "minimize to tray" behavior.
            Application.Current.SessionEnding += (_, _) => _forceClose = true;
        }
        catch (Exception ex)
        {
            DisposeTrayIcon();
            DiagnosticLogger.Crash("MainWindow initialization", ex);
            throw; // rethrow so the app-level handler also reports it
        }
    }

    // ══════════════════════════════════════════
    //  STARTUP / ACTIVATION (called by App)
    // ══════════════════════════════════════════

    /// <summary>
    /// Used for "Launch at startup": starts in the tray when tray mode is enabled,
    /// otherwise minimized on the taskbar.
    /// </summary>
    public void ShowMinimizedAtStartup()
    {
        if (IsTrayEnabled() && _trayIcon != null)
        {
            _trayIcon.Visible = true;
            RunStartupMaintenanceOnce();
            return;
        }

        WindowState = WindowState.Minimized;
        Show();
    }

    /// <summary>Brings the window back from the tray (or from minimized) and focuses it.</summary>
    public void RestoreFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RestoreFromTray);
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;   // Reliable foreground activation across processes...
        Topmost = false;  // ...without staying pinned on top.
        Focus();

        if (_trayIcon != null)
            _trayIcon.Visible = false;
    }

    /// <summary>
    /// Handles "Deep Uninstall with AuraClean" from Explorer: opens the Uninstaller focused on
    /// the program that owns <paramref name="path"/>.
    /// </summary>
    public void OpenDeepUninstallFor(string path)
    {
        _viewModel.NavigateToCommand.Execute("Uninstaller");
        _ = _viewModel.Uninstaller.FocusProgramForPathAsync(path);
    }

    private void OnFirstContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstContentRendered;
        RunStartupMaintenanceOnce();
    }

    private void RunStartupMaintenanceOnce()
    {
        if (_startupMaintenanceDone)
            return;
        _startupMaintenanceDone = true;
        _ = _viewModel.RunStartupMaintenanceAsync();
    }

    // ══════════════════════════════════════════
    //  TRAY
    // ══════════════════════════════════════════

    private void InitializeTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "AuraClean",
            Visible = false,
        };

        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
                _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Warn("MainWindow", "Could not extract tray icon", ex);
        }
        _trayIcon.Icon ??= System.Drawing.SystemIcons.Application;

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open AuraClean", null, (_, _) => RestoreFromTray());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        _trayIcon.ContextMenuStrip = menu;

        NotificationService.RegisterTrayIcon(_trayIcon);
    }

    private void ExitFromTray()
    {
        if (!ConfirmExitWhileBusy())
            return;

        _forceClose = true;

        // A window that started hidden in the tray was never shown; closing it would not end
        // the application, so shut down explicitly in that case.
        if (!IsLoaded)
        {
            DisposeTrayIcon();
            Application.Current.Shutdown();
            return;
        }

        Close();
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null)
            return;

        NotificationService.UnregisterTrayIcon(_trayIcon);
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized && IsTrayEnabled() && _trayIcon != null)
        {
            Hide();
            _trayIcon.Visible = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (!_forceClose && IsTrayEnabled() && _trayIcon != null)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            return;
        }

        if (!_forceClose && !ConfirmExitWhileBusy())
        {
            e.Cancel = true;
            return;
        }

        DisposeTrayIcon();
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Warns before exiting while a scan, cleanup, or install is still running: closing would
    /// abort it midway (for example leaving Windows Update services stopped).
    /// </summary>
    private bool ConfirmExitWhileBusy()
    {
        if (!_viewModel.IsAnyOperationRunning)
            return true;

        const string message = "AuraClean is still working. Closing now may leave the current operation unfinished.\n\nExit anyway?";
        var result = IsVisible
            ? MessageBox.Show(this, message, "Operation in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, "Operation in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    private static bool IsTrayEnabled() => SettingsService.Load().MinimizeToTray;

    // ══════════════════════════════════════════
    //  NAVIGATION
    // ══════════════════════════════════════════

    private void OnOnboardingCompleted()
    {
        Dispatcher.BeginInvoke(() => OnboardingOverlay.Visibility = Visibility.Collapsed);
        _viewModel.Onboarding.OnboardingCompleted -= OnOnboardingCompleted;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentViewName))
            ShowView(_viewModel.CurrentViewName);
    }

    private void ShowView(string viewName)
    {
        if (!_viewMap.TryGetValue(viewName, out var target))
            return;

        FrameworkElement? outgoing = null;
        if (_currentViewKey != null && _currentViewKey != viewName &&
            _viewMap.TryGetValue(_currentViewKey, out var prev) && prev.Visibility == Visibility.Visible)
        {
            outgoing = prev;
        }

        _currentViewKey = viewName;

        foreach (var kvp in _viewMap)
        {
            if (kvp.Value != outgoing && kvp.Value != target)
                kvp.Value.Visibility = Visibility.Collapsed;
        }

        AnimationHelper.TransitionViews(outgoing, target);
        SyncNavigationSelection(viewName);

        // Refresh data that other features may have changed while the page was hidden.
        switch (viewName)
        {
            case "Quarantine":
                _viewModel.Quarantine.LoadEntriesCommand.Execute(null);
                break;
            case "History":
                _viewModel.CleanupHistory.LoadHistoryCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// Keeps the sidebar highlight in sync when navigation happens through keyboard shortcuts
    /// or dashboard buttons rather than the sidebar itself, expanding the owning section.
    /// </summary>
    private void SyncNavigationSelection(string viewName)
    {
        if (!_navMap.TryGetValue(viewName, out var radio))
            return;

        switch (viewName)
        {
            case "Cleaner" or "Browser" or "Uninstaller":
                _viewModel.IsCleanupExpanded = true;
                break;
            case "StorageMap" or "Duplicates":
                _viewModel.IsAnalyzeExpanded = true;
                break;
            case "Memory" or "DiskOptimizer" or "Startup":
                _viewModel.IsOptimizeExpanded = true;
                break;
            case "LargeFiles" or "Shredder" or "SystemInfo" or "Quarantine" or "SoftwareUpdater"
                or "FileRecovery" or "EmptyFolders" or "AppInstaller" or "Monitor":
                _viewModel.IsUtilitiesExpanded = true;
                break;
        }

        if (radio.IsChecked != true)
            radio.IsChecked = true;
    }
}
