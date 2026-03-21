using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AuraClean.Helpers;

namespace AuraClean.Views.Controls;

/// <summary>
/// A post-action result card that appears after an operation completes.
/// Shows value + description with a thin left-border accent stripe.
/// Auto-dismisses after 8 seconds, or user can click/press Escape to dismiss.
/// </summary>
public partial class ResultCard : UserControl
{
    private DispatcherTimer? _autoDismissTimer;

    /// <summary>Accent severity determines the left-border color.</summary>
    public enum Severity { Success, Warning, Danger }

    public ResultCard()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Shows the result card with entrance animation and auto-dismiss timer.
    /// </summary>                     
    /// <param name="value">Primary value text (e.g. "1.2 GB freed").</param>
    /// <param name="description">Secondary description text.</param>
    /// <param name="severity">Accent color: Success=mint, Warning=amber, Danger=coral.</param>
    public void Show(string value, string description, Severity severity = Severity.Success)
    {
        ValueText.Text = value;
        DescriptionText.Text = description;

        // Set accent stripe color
        var accentKey = severity switch
        {
            Severity.Warning => "AuraAmber",
            Severity.Danger => "AuraWarning",
            _ => "AuraSuccess"
        };

        if (TryFindResource(accentKey) is SolidColorBrush brush)
            AccentStripe.Background = brush;

        // Set automation name for screen readers
        AutomationProperties.SetName(CardBorder, $"Result: {value}. {description}");

        // Show with entrance animation
        Visibility = Visibility.Visible;
        Opacity = 0;
        AnimationHelper.PlayEntranceAnimation(this, 0);

        // Focus for keyboard accessibility
        Focus();

        // Start auto-dismiss timer (8 seconds)
        StopTimer();
        _autoDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _autoDismissTimer.Tick += (_, _) => Dismiss();
        _autoDismissTimer.Start();
    }

    /// <summary>Dismisses the card with exit animation.</summary>
    public void Dismiss()
    {
        StopTimer();
        AnimationHelper.PlayExitAnimation(this, () =>
        {
            Visibility = Visibility.Collapsed;
            Opacity = 1;
        });
    }

    private void OnCardClicked(object sender, MouseButtonEventArgs e) => Dismiss();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void StopTimer()
    {
        _autoDismissTimer?.Stop();
        _autoDismissTimer = null;
    }
}
