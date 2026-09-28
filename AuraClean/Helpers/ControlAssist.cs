using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AuraClean.Helpers;

/// <summary>
/// Attached properties the shared control templates read, so one template serves every button
/// variant: corner radius plus the hover layer's fill and border.
/// </summary>
public static class ControlAssist
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(ControlAssist), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject element) => (CornerRadius)element.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject element, CornerRadius value) => element.SetValue(CornerRadiusProperty, value);

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(ControlAssist), new FrameworkPropertyMetadata(null));

    public static Brush? GetHoverBackground(DependencyObject element) => (Brush?)element.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject element, Brush? value) => element.SetValue(HoverBackgroundProperty, value);

    public static readonly DependencyProperty HoverBorderBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBorderBrush", typeof(Brush), typeof(ControlAssist), new FrameworkPropertyMetadata(null));

    public static Brush? GetHoverBorderBrush(DependencyObject element) => (Brush?)element.GetValue(HoverBorderBrushProperty);
    public static void SetHoverBorderBrush(DependencyObject element, Brush? value) => element.SetValue(HoverBorderBrushProperty, value);
}

/// <summary>
/// CSS <c>cubic-bezier(x1, y1, x2, y2)</c> easing, so WPF motion matches the design system's
/// curves exactly (<c>cubic-bezier(.2, 0, 0, 1)</c> for controls). Implemented as a plain
/// <see cref="IEasingFunction"/> so it can be shared by frozen storyboards in templates.
/// </summary>
public sealed class CubicBezierEase : IEasingFunction
{
    public double X1 { get; set; } = 0.2;
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; } = 1;

    public double Ease(double normalizedTime) => CubicBezier.Evaluate(normalizedTime, X1, Y1, X2, Y2);
}
