using AuraClean.Helpers;
using System.Windows;

namespace AuraClean.Services;

/// <summary>
/// Provides balloon-tip notifications via the system tray icon.
/// Uses the WinForms NotifyIcon owned by MainWindow.
/// </summary>
public static class NotificationService
{
    private static System.Windows.Forms.NotifyIcon? _trayIcon;
    private static bool _hideAfterBalloon;

    /// <summary>
    /// Registers the tray icon so notifications can be sent from anywhere.
    /// Called once from MainWindow after tray icon initialization.
    /// </summary>
    public static void RegisterTrayIcon(System.Windows.Forms.NotifyIcon icon)
    {
        _trayIcon = icon;
        icon.BalloonTipClosed += OnBalloonFinished;
        icon.BalloonTipClicked += OnBalloonFinished;
    }

    /// <summary>Detaches the tray icon before it is disposed.</summary>
    public static void UnregisterTrayIcon(System.Windows.Forms.NotifyIcon icon)
    {
        icon.BalloonTipClosed -= OnBalloonFinished;
        icon.BalloonTipClicked -= OnBalloonFinished;
        if (ReferenceEquals(_trayIcon, icon))
            _trayIcon = null;
    }

    /// <summary>
    /// Shows a balloon notification in the system tray. Safe to call from any thread.
    /// When the icon is normally hidden (window visible), it is shown only for the balloon.
    /// </summary>
    public static void Show(string title, string message,
        System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info,
        int timeoutMs = 3000)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted)
            return;

        dispatcher.BeginInvoke(() =>
        {
            var tray = _trayIcon;
            if (tray == null)
                return;

            try
            {
                if (!tray.Visible)
                {
                    _hideAfterBalloon = true;
                    tray.Visible = true;
                }

                tray.ShowBalloonTip(timeoutMs, title, message, icon);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                DiagnosticLogger.Warn("NotificationService", "Tray notification failed", ex);
            }
        });
    }

    private static void OnBalloonFinished(object? sender, EventArgs e)
    {
        if (!_hideAfterBalloon || _trayIcon == null)
            return;

        _hideAfterBalloon = false;

        // Keep the icon if the window has since been hidden to the tray.
        var mainWindow = Application.Current?.MainWindow;
        if (mainWindow != null && mainWindow.IsVisible)
            _trayIcon.Visible = false;
    }

    /// <summary>
    /// Shows a success notification.
    /// </summary>
    public static void ShowSuccess(string title, string message)
        => Show(title, message, System.Windows.Forms.ToolTipIcon.Info);

    /// <summary>
    /// Shows a warning notification.
    /// </summary>
    public static void ShowWarning(string title, string message)
        => Show(title, message, System.Windows.Forms.ToolTipIcon.Warning);

    /// <summary>
    /// Shows an error notification.
    /// </summary>
    public static void ShowError(string title, string message)
        => Show(title, message, System.Windows.Forms.ToolTipIcon.Error);
}
