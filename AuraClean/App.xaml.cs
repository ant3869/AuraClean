using AuraClean.Helpers;
using AuraClean.Services;
using AuraClean.Views;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AuraClean;

public partial class App : Application
{
    private const string SingleInstanceMutexName = @"Local\AuraClean.SingleInstance";
    private const string ActivationEventName = @"Local\AuraClean.Activate";
    private const int MaxUnhandledErrorsPerWindow = 3;

    private static readonly string PendingCommandFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuraClean", "pending_command.txt");

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private readonly Queue<DateTime> _recentUnhandledErrors = new();

    public App()
    {
        // Register before InitializeComponent() so XAML resource-loading errors are captured.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var options = StartupOptions.Parse(e.Args);
        var settings = SettingsService.Load();

        if (options.AutoClean)
        {
            int exitCode = 0;
            try
            {
                await AutoCleanupRunner.RunAsync(settings);
            }
            catch (Exception ex)
            {
                exitCode = 1;
                DiagnosticLogger.Crash("AutoCleanup", ex);
            }

            DiagnosticLogger.Flush();
            Shutdown(exitCode);
            return;
        }

        if (!TryAcquireSingleInstance())
        {
            ForwardToRunningInstance(options);
            Shutdown();
            return;
        }

        try
        {
            ThemeService.Initialize(settings.Theme);
            DiagnosticLogger.PruneOldLogs(TimeSpan.FromDays(60));

            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            ListenForActivationRequests(window);

            if (options.StartMinimized)
                window.ShowMinimizedAtStartup();
            else
                window.Show();

            if (options.DeepUninstallTarget != null)
                window.OpenDeepUninstallFor(options.DeepUninstallTarget);
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Crash("Startup", ex);
            MessageBox.Show(
                $"AuraClean could not start:\n\n{ex.Message}\n\nDetails were written to:\n{DiagnosticLogger.CrashLogPath}",
                "AuraClean", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activationRegistration?.Unregister(null);
        _activationEvent?.Dispose();

        if (_instanceMutex != null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* Mutex was not owned by this thread — nothing to release. */ }
            _instanceMutex.Dispose();
        }

        DiagnosticLogger.Flush();
        base.OnExit(e);
    }

    // ══════════════════════════════════════════
    //  SINGLE INSTANCE
    // ══════════════════════════════════════════

    private bool TryAcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
            if (!createdNew)
            {
                _instanceMutex.Dispose();
                _instanceMutex = null;
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // Never block startup because the mutex could not be created.
            DiagnosticLogger.Warn("App", "Single-instance mutex unavailable", ex);
            return true;
        }
    }

    private static void ForwardToRunningInstance(StartupOptions options)
    {
        try
        {
            if (options.DeepUninstallTarget != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PendingCommandFile)!);
                File.WriteAllText(PendingCommandFile, options.DeepUninstallTarget);
            }

            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
            {
                using (handle)
                    handle.Set();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            DiagnosticLogger.Warn("App", "Could not signal the running instance", ex);
        }
    }

    private void ListenForActivationRequests(MainWindow window)
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
            _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
                _activationEvent,
                (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    window.RestoreFromTray();
                    var pending = ConsumePendingCommand();
                    if (pending != null)
                        window.OpenDeepUninstallFor(pending);
                }),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            DiagnosticLogger.Warn("App", "Activation listener unavailable", ex);
        }
    }

    private static string? ConsumePendingCommand()
    {
        try
        {
            if (!File.Exists(PendingCommandFile))
                return null;

            var target = File.ReadAllText(PendingCommandFile).Trim();
            File.Delete(PendingCommandFile);
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLogger.Warn("App", "Could not read forwarded command", ex);
            return null;
        }
    }

    // ══════════════════════════════════════════
    //  UNHANDLED EXCEPTIONS
    // ══════════════════════════════════════════

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLogger.Crash("DispatcherUnhandledException", e.Exception);
        e.Handled = true;

        // A fault that repeats on every render pass would otherwise trap the user in an endless
        // stream of dialogs. After several failures in a short window, shut down cleanly.
        var now = DateTime.UtcNow;
        _recentUnhandledErrors.Enqueue(now);
        while (_recentUnhandledErrors.Count > 0 && now - _recentUnhandledErrors.Peek() > TimeSpan.FromSeconds(15))
            _recentUnhandledErrors.Dequeue();

        if (_recentUnhandledErrors.Count > MaxUnhandledErrorsPerWindow)
        {
            MessageBox.Show(
                $"AuraClean hit repeated errors and will close to protect your system.\n\nDetails: {DiagnosticLogger.CrashLogPath}",
                "AuraClean", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\nThe operation was stopped. Details: {DiagnosticLogger.CrashLogPath}",
            "AuraClean", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            DiagnosticLogger.Crash(e.IsTerminating ? "UnhandledException (terminating)" : "UnhandledException", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        DiagnosticLogger.Crash("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }
}
