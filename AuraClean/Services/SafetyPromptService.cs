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

    private static bool ShowConfirmation(string message, string title)
    {
        var owner = Application.Current?.MainWindow;
        var result = owner == null
            ? MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning);

        return result == MessageBoxResult.Yes;
    }
}
