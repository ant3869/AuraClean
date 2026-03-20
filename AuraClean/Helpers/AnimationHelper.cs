using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AuraClean.Helpers;

/// <summary>
/// Attached behaviors for purposeful motion throughout AuraClean.
/// All animations use Transform + Opacity only (GPU-accelerated).
/// Respects SystemParameters.ClientAreaAnimation (reduced motion).
/// </summary>
public static class AnimationHelper
{
    // ── Duration constants ──
    private static readonly Duration FastDuration = new(TimeSpan.FromMilliseconds(150));
    private static readonly Duration NormalDuration = new(TimeSpan.FromMilliseconds(250));
    private static readonly Duration EntranceDuration = new(TimeSpan.FromMilliseconds(400));

    // ── Easing (QuarticEase = ease-out-quart equivalent) ──
    private static readonly IEasingFunction EaseOut = new QuarticEase { EasingMode = EasingMode.EaseOut };
    private static readonly IEasingFunction EaseInOut = new QuarticEase { EasingMode = EasingMode.EaseInOut };

    private static bool IsAnimationEnabled => SystemParameters.ClientAreaAnimation;

    // ═══════════════════════════════════════
    //  ATTACHED PROPERTY: AnimateOnVisible
    //  Fade+slide when element becomes Visible
    // ═══════════════════════════════════════

    public static readonly DependencyProperty AnimateOnVisibleProperty =
        DependencyProperty.RegisterAttached(
            "AnimateOnVisible",
            typeof(bool),
            typeof(AnimationHelper),
            new PropertyMetadata(false, OnAnimateOnVisibleChanged));

    public static bool GetAnimateOnVisible(DependencyObject obj) =>
        (bool)obj.GetValue(AnimateOnVisibleProperty);

    public static void SetAnimateOnVisible(DependencyObject obj, bool value) =>
        obj.SetValue(AnimateOnVisibleProperty, value);

    private static void OnAnimateOnVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        if ((bool)e.NewValue)
            element.IsVisibleChanged += OnElementVisibilityChanged;
        else
            element.IsVisibleChanged -= OnElementVisibilityChanged;
    }

    private static void OnElementVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not FrameworkElement element) return;
        if ((bool)e.NewValue)
            PlayEntranceAnimation(element);
    }

    // ═══════════════════════════════════════
    //  ATTACHED PROPERTY: StaggerIndex
    //  Staggers entrance delay per element
    // ═══════════════════════════════════════

    public static readonly DependencyProperty StaggerIndexProperty =
        DependencyProperty.RegisterAttached(
            "StaggerIndex",
            typeof(int),
            typeof(AnimationHelper),
            new PropertyMetadata(0));

    public static int GetStaggerIndex(DependencyObject obj) =>
        (int)obj.GetValue(StaggerIndexProperty);

    public static void SetStaggerIndex(DependencyObject obj, int value) =>
        obj.SetValue(StaggerIndexProperty, value);

    // ═══════════════════════════════════════
    //  ATTACHED PROPERTY: SlideDistance
    //  Customize slide-up distance (default: 16px)
    // ═══════════════════════════════════════

    public static readonly DependencyProperty SlideDistanceProperty =
        DependencyProperty.RegisterAttached(
            "SlideDistance",
            typeof(double),
            typeof(AnimationHelper),
            new PropertyMetadata(16.0));

    public static double GetSlideDistance(DependencyObject obj) =>
        (double)obj.GetValue(SlideDistanceProperty);

    public static void SetSlideDistance(DependencyObject obj, double value) =>
        obj.SetValue(SlideDistanceProperty, value);

    // ═══════════════════════════════════════
    //  PUBLIC ANIMATION METHODS
    // ═══════════════════════════════════════

    /// <summary>
    /// Plays a fade+slide entrance on the element.
    /// Used by the AnimateOnVisible behavior and called from code-behind for view transitions.
    /// </summary>
    public static void PlayEntranceAnimation(FrameworkElement element, int staggerIndex = -1)
    {
        if (!IsAnimationEnabled)
        {
            element.Opacity = 1;
            return;
        }

        int index = staggerIndex >= 0 ? staggerIndex : GetStaggerIndex(element);
        double slideDistance = GetSlideDistance(element);
        var delay = TimeSpan.FromMilliseconds(index * 60);

        // Ensure TranslateTransform exists
        EnsureTranslateTransform(element);
        var translate = (TranslateTransform)element.RenderTransform;

        // Set initial state
        element.Opacity = 0;
        translate.Y = slideDistance;

        // Opacity animation
        var fadeIn = new DoubleAnimation(0, 1, EntranceDuration)
        {
            BeginTime = delay,
            EasingFunction = EaseOut,
        };

        // Slide animation
        var slideUp = new DoubleAnimation(slideDistance, 0, EntranceDuration)
        {
            BeginTime = delay,
            EasingFunction = EaseOut,
        };

        element.BeginAnimation(UIElement.OpacityProperty, fadeIn);
        translate.BeginAnimation(TranslateTransform.YProperty, slideUp);
    }

    /// <summary>
    /// Quick fade-out for view exits (faster than entrance — 75% duration).
    /// Returns after the animation completes via callback.
    /// </summary>
    public static void PlayExitAnimation(FrameworkElement element, Action? onComplete = null)
    {
        if (!IsAnimationEnabled)
        {
            element.Opacity = 0;
            onComplete?.Invoke();
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(180)))
        {
            EasingFunction = EaseOut,
        };

        if (onComplete != null)
            fadeOut.Completed += (_, _) => onComplete();

        element.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Animates a view transition: fades out old view, then fades+slides in new view.
    /// </summary>
    public static void TransitionViews(FrameworkElement? outgoing, FrameworkElement incoming)
    {
        if (!IsAnimationEnabled)
        {
            if (outgoing != null)
            {
                outgoing.Opacity = 1;
                outgoing.Visibility = Visibility.Collapsed;
            }
            incoming.Visibility = Visibility.Visible;
            incoming.Opacity = 1;
            return;
        }

        if (outgoing != null && outgoing.Visibility == Visibility.Visible)
        {
            PlayExitAnimation(outgoing, () =>
            {
                outgoing.Visibility = Visibility.Collapsed;
                outgoing.Opacity = 1; // Reset for next show
                ShowIncoming(incoming);
            });
        }
        else
        {
            ShowIncoming(incoming);
        }
    }

    /// <summary>
    /// Subtle scale pulse for feedback on actions (e.g., successful cleanup).
    /// Scales to targetScale then back to 1.0 over ~300ms.
    /// </summary>
    public static void PulseScale(FrameworkElement element, double targetScale = 1.04)
    {
        if (!IsAnimationEnabled) return;

        EnsureScaleTransform(element);

        var pulse = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
        };
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(targetScale, KeyTime.FromPercent(0.4), EaseOut));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1.0), EaseInOut));

        var scaleTransform = GetScaleTransform(element);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
        scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
    }

    /// <summary>
    /// Animates a progress bar value change smoothly.
    /// </summary>
    public static void AnimateProgressValue(FrameworkElement element, double fromValue, double toValue)
    {
        if (!IsAnimationEnabled || element is not System.Windows.Controls.Primitives.RangeBase rangeBase)
            return;

        var anim = new DoubleAnimation(fromValue, toValue, NormalDuration)
        {
            EasingFunction = EaseOut,
        };
        rangeBase.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
    }

    // ═══════════════════════════════════════
    //  PRIVATE HELPERS
    // ═══════════════════════════════════════

    private static void ShowIncoming(FrameworkElement incoming)
    {
        incoming.Opacity = 0;
        incoming.Visibility = Visibility.Visible;

        // Slight delay to allow layout pass
        incoming.Dispatcher.InvokeAsync(() =>
        {
            PlayEntranceAnimation(incoming, 0);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void EnsureTranslateTransform(FrameworkElement element)
    {
        if (element.RenderTransform is TranslateTransform) return;

        if (element.RenderTransform is TransformGroup group)
        {
            if (group.Children.OfType<TranslateTransform>().Any()) return;
            group.Children.Add(new TranslateTransform());
        }
        else
        {
            element.RenderTransform = new TranslateTransform();
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    private static void EnsureScaleTransform(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform) return;

        if (element.RenderTransform is TransformGroup group)
        {
            if (group.Children.OfType<ScaleTransform>().Any()) return;
            group.Children.Add(new ScaleTransform(1, 1));
        }
        else if (element.RenderTransform is TranslateTransform translate)
        {
            var newGroup = new TransformGroup();
            newGroup.Children.Add(new ScaleTransform(1, 1));
            newGroup.Children.Add(translate);
            element.RenderTransform = newGroup;
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
        else
        {
            element.RenderTransform = new ScaleTransform(1, 1);
            element.RenderTransformOrigin = new Point(0.5, 0.5);
        }
    }

    private static ScaleTransform GetScaleTransform(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform st) return st;
        if (element.RenderTransform is TransformGroup group)
            return group.Children.OfType<ScaleTransform>().First();
        throw new InvalidOperationException("No ScaleTransform found. Call EnsureScaleTransform first.");
    }

    // ═══════════════════════════════════════
    //  SMOOTH PROGRESS ATTACHED BEHAVIOR
    // ═══════════════════════════════════════

    public static readonly DependencyProperty SmoothProgressProperty =
        DependencyProperty.RegisterAttached(
            "SmoothProgress", typeof(bool), typeof(AnimationHelper),
            new PropertyMetadata(false, OnSmoothProgressChanged));

    public static bool GetSmoothProgress(DependencyObject obj) => (bool)obj.GetValue(SmoothProgressProperty);
    public static void SetSmoothProgress(DependencyObject obj, bool value) => obj.SetValue(SmoothProgressProperty, value);

    private static void OnSmoothProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.RangeBase rangeBase) return;

        if ((bool)e.NewValue)
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                typeof(System.Windows.Controls.Primitives.RangeBase));
            descriptor?.AddValueChanged(rangeBase, OnProgressValueChanged);
        }
    }

    private static void OnProgressValueChanged(object? sender, EventArgs e)
    {
        if (!IsAnimationEnabled) return;
        if (sender is not System.Windows.Controls.Primitives.RangeBase rangeBase) return;

        double targetValue = rangeBase.Value;

        // Remove binding temporarily to animate directly
        rangeBase.BeginAnimation(
            System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new DoubleAnimation(targetValue, NormalDuration) { EasingFunction = EaseOut });
    }
}
