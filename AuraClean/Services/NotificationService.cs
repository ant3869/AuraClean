using System.Windows;

namespace AuraClean.Services;

/// <summary>
/// Provides balloon-tip notifications via the system tray icon.
/// Uses the WinForms NotifyIcon already present in MainWindow.
/// </summary>
public static class NotificationService
{
    private static System.Windows.Forms.NotifyIcon? _trayIcon;

    /// <summary>
    /// Registers the existing tray icon so notifications can be sent from anywhere.
    /// Called once from MainWindow code-behind after tray icon initialization.
    /// </summary>
    public static void RegisterTrayIcon(System.Windows.Forms.NotifyIcon icon)
    {
        _trayIcon = icon;
    }

    /// <summary>
    /// Shows a balloon notification in the system tray.
    /// </summary>
    public static void Show(string title, string message,
        System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info,
        int timeoutMs = 3000)
    {
        if (_trayIcon == null || !_trayIcon.Visible)
        {
            // If tray icon isn't visible, make it briefly visible for the notification
            if (_trayIcon != null)
            {
                _trayIcon.Visible = true;
            }
            else
            {
                return; // No tray icon available
            }
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _trayIcon?.ShowBalloonTip(timeoutMs, title, message, icon);
        });
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
