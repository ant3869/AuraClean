using AuraClean.Helpers;
using AuraClean.Models;
using AuraClean.Services;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AuraClean;

public partial class App : Application
{
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "AuraClean_crash.log");

    public App()
    {
        // Register BEFORE InitializeComponent() is called by the auto-generated Main.
        // This ensures we catch errors during XAML resource loading.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        this.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        LogInfo("OnStartup reached — creating main window...");
        base.OnStartup(e);

        // Apply persisted theme preference
        var settings = SettingsService.Load();
        ThemeService.ApplyTheme(settings.IsLightTheme);

        // Handle scheduled auto-cleanup mode
        if (e.Args.Length > 0 && e.Args[0].Equals("/autoclean", StringComparison.OrdinalIgnoreCase))
        {
            LogInfo("Auto-cleanup mode triggered by scheduled task.");
            try
            {
                await RunAutoCleanupAsync(settings);
            }
            catch (Exception ex)
            {
                LogError("AutoCleanup", ex);
            }
            Shutdown();
            return;
        }

        LogInfo("OnStartup completed successfully.");
    }

    private static async Task RunAutoCleanupAsync(AppSettings settings)
    {
        DiagnosticLogger.Info("AutoCleanup", "Starting scheduled cleanup...");

        var items = await FileCleanerService.AnalyzeSystemJunkAsync();
        if (items.Count == 0)
        {
            DiagnosticLogger.Info("AutoCleanup", "No junk found.");
            return;
        }

        // Select items matching user's cleanup preferences
        foreach (var item in items)
        {
            item.IsSelected = item.Type switch
            {
                JunkType.TempFile => settings.CleanTempFiles,
                JunkType.WindowsUpdateCache => settings.CleanWindowsUpdate,
                JunkType.Prefetch => settings.CleanPrefetch,
                JunkType.CrashDump => settings.CleanCrashDumps,
                JunkType.RecycleBin => settings.CleanRecycleBin,
                JunkType.BrowserCache => settings.CleanBrowserCache,
                JunkType.ThumbnailCache => settings.CleanThumbnailCache,
                JunkType.LogFile => settings.CleanWindowsLogs,
                _ => true
            };
        }

        var (deleted, _, bytesFreed, _) = await FileCleanerService.CleanItemsAsync(items);
        DiagnosticLogger.Info("AutoCleanup",
            $"Cleaned {deleted} items, freed {bytesFreed} bytes.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLogger.Flush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"An error occurred:\n\n{e.Exception.Message}\n\nSee crash log on Desktop.",
            "AuraClean Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            LogError("UnhandledException", ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    private static void LogError(string source, Exception ex)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch (Exception logEx)
        {
            System.Diagnostics.Debug.WriteLine($"[AuraClean] Failed to write crash log: {logEx.Message}");
        }
    }

    private static void LogInfo(string message)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}\n");
        }
        catch (Exception logEx)
        {
            System.Diagnostics.Debug.WriteLine($"[AuraClean] Failed to write info log: {logEx.Message}");
        }
    }
}

