using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;

namespace AuraClean.Views.Controls;

/// <summary>
/// Uppercase, letter-spaced label: the design system's eyebrow ("10px, uppercase,
/// letter-spacing .12em"). WPF's TextBlock has no letter spacing, so each text element is laid
/// out individually with <see cref="Tracking"/> × font size between them. Font and foreground
/// inherit like a TextBlock; screen readers get the original (not uppercased) text.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackedText),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Extra space between characters, in em (0.12 = 12% of the font size).</summary>
    public static readonly DependencyProperty TrackingProperty = DependencyProperty.Register(
        nameof(Tracking), typeof(double), typeof(TrackedText),
        new FrameworkPropertyMetadata(0.12,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double d && double.IsFinite(d) && d >= 0);

    public static readonly DependencyProperty UppercaseProperty = DependencyProperty.Register(
        nameof(Uppercase), typeof(bool), typeof(TrackedText),
        new FrameworkPropertyMetadata(true,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, InheritedLayout));

    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, InheritedLayout));

    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(FontWeights.Normal, InheritedLayout));

    public static readonly DependencyProperty FontStyleProperty = TextElement.FontStyleProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(FontStyles.Normal, InheritedLayout));

    public static readonly DependencyProperty FontStretchProperty = TextElement.FontStretchProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(FontStretches.Normal, InheritedLayout));

    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(TrackedText), new FrameworkPropertyMetadata(SystemColors.ControlTextBrush,
            FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    private const FrameworkPropertyMetadataOptions InheritedLayout =
        FrameworkPropertyMetadataOptions.Inherits |
        FrameworkPropertyMetadataOptions.AffectsMeasure |
        FrameworkPropertyMetadataOptions.AffectsRender;

    private readonly List<(FormattedText Glyph, double X)> _glyphs = [];

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Tracking
    {
        get => (double)GetValue(TrackingProperty);
        set => SetValue(TrackingProperty, value);
    }

    public bool Uppercase
    {
        get => (bool)GetValue(UppercaseProperty);
        set => SetValue(UppercaseProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => (FontStyle)GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public FontStretch FontStretch
    {
        get => (FontStretch)GetValue(FontStretchProperty);
        set => SetValue(FontStretchProperty, value);
    }

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _glyphs.Clear();

        var text = Text;
        if (string.IsNullOrEmpty(text))
            return new Size(0, 0);

        var culture = CultureInfo.CurrentUICulture;
        if (Uppercase)
            text = text.ToUpper(culture);

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var spacing = Tracking * FontSize;

        double x = 0, height = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            if (_glyphs.Count > 0)
                x += spacing;

            var glyph = new FormattedText(elements.GetTextElement(), culture, FlowDirection.LeftToRight,
                typeface, FontSize, Foreground, pixelsPerDip);
            _glyphs.Add((glyph, x));
            x += glyph.WidthIncludingTrailingWhitespace;
            height = Math.Max(height, glyph.Height);
        }

        return new Size(x, height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var foreground = Foreground;
        foreach (var (glyph, x) in _glyphs)
        {
            glyph.SetForegroundBrush(foreground);
            drawingContext.DrawText(glyph, new Point(x, 0));
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TrackedTextAutomationPeer(this);

    private sealed class TrackedTextAutomationPeer(TrackedText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(TrackedText);

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((TrackedText)Owner).Text : name;
        }
    }
}
