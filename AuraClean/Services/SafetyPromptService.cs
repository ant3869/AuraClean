using System.Windows;

namespace AuraClean.Services;

/// <summary>
/// Centralizes user-facing safety gates for destructive operations.
/// </summary>
public static class SafetyPromptService
{
    public static bool IsDryRunEnabled() => SettingsService.Load().DryRunMode;

    public static bool ShouldCreateRestorePoint() =>
        SettingsService.Load().CreateRestorePointBeforeClean;

    public static bool ConfirmDestructiveAction(string message, string title = "Confirm destructive action")
    {
        var settings = SettingsService.Load();
        if (!settings.ShowConfirmationDialogs)
            return true;

        if (Application.Current?.Dispatcher.CheckAccess() == false)
        {
            return Application.Current.Dispatcher.Invoke(() =>
                ShowConfirmation(message, title));
        }

        return ShowConfirmation(message, title);
    }

    /// <summary>
    /// Asks the user regardless of the "show confirmation dialogs" setting. Used for security
    /// decisions (e.g. running an unsigned installer) that must never be skipped silently.
    /// </summary>
    public static bool ConfirmSecurityDecision(string message, string title)
    {
        if (Application.Current == null)
            return false;

        if (Application.Current.Dispatcher.CheckAccess() == false)
            return Application.Current.Dispatcher.Invoke(() => ShowConfirmation(message, title));

        return ShowConfirmation(message, title);
    }

    private static bool ShowConfirmation(string message, string title)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner is { IsVisible: true }
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        return result == MessageBoxResult.Yes;
    }
}
