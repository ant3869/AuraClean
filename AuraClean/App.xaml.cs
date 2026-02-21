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

    protected override void OnStartup(StartupEventArgs e)
    {
        LogInfo("OnStartup reached — creating main window...");
        base.OnStartup(e);
        LogInfo("OnStartup completed successfully.");
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
        catch { }
    }

    private static void LogInfo(string message)
    {
        try
        {
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] INFO: {message}\n");
        }
        catch { }
    }
}

