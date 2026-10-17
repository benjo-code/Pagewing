using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Pagewing.App.Controls;

/// <summary>
/// One line of text that is shortened in the middle when it does not fit ("Sehr langer Dat…final v3.pdf"), so the
/// start and the file extension stay visible (design system section 7.2). The width limit is the available width
/// or <see cref="Layoutable.MaxWidth"/>, whichever is smaller; the shortened text never exceeds it. The number of
/// kept characters is found by bisection over <see cref="FormattedText"/> measurements.
/// </summary>
public sealed class MiddleEllipsisTextBlock : Control
{
    /// <summary>The character that marks the cut.</summary>
    public const string Ellipsis = "…";

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MiddleEllipsisTextBlock, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextElement.FontWeightProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<FontStyle> FontStyleProperty =
        TextElement.FontStyleProperty.AddOwner<MiddleEllipsisTextBlock>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MiddleEllipsisTextBlock>();

    private FormattedText? _formatted;
    private double _shortenedFor = double.NaN;

    static MiddleEllipsisTextBlock()
    {
        AffectsMeasure<MiddleEllipsisTextBlock>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, FontStyleProperty);
        AffectsRender<MiddleEllipsisTextBlock>(ForegroundProperty);
    }

    /// <summary>The full text.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>The text as drawn: <see cref="Text"/> or its shortened form.</summary>
    public string DisplayText { get; private set; } = string.Empty;

    /// <summary>Measures <see cref="DisplayText"/> with the current font (for tests).</summary>
    internal double MeasureDisplayText() => Format(DisplayText).WidthIncludingTrailingWhitespace;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ForegroundProperty)
        {
            if (_formatted is not null)
            {
                _formatted = Format(DisplayText);
            }
        }
        else if (change.Property == TextProperty || change.Property == FontFamilyProperty || change.Property == FontSizeProperty
            || change.Property == FontWeightProperty || change.Property == FontStyleProperty)
        {
            // The measure that these properties trigger shortens the text again.
            _formatted = null;
            _shortenedFor = double.NaN;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Layoutable has already limited availableSize to MaxWidth.
        Shorten(availableSize.Width);
        var formatted = _formatted!;
        return new Size(Math.Min(formatted.WidthIncludingTrailingWhitespace, availableSize.Width), formatted.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // A parent may arrange the text narrower than it measured it; shorten again so it never overflows.
        if (_formatted is null || _formatted.WidthIncludingTrailingWhitespace > finalSize.Width)
        {
            Shorten(finalSize.Width);
        }

        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        if (_formatted is null || DisplayText.Length == 0)
        {
            return;
        }

        var top = Math.Max(0, (Bounds.Height - _formatted.Height) / 2);
        context.DrawText(_formatted, new Point(0, top));
    }

    /// <summary>Sets <see cref="DisplayText"/> to the longest form of <see cref="Text"/> that fits into <paramref name="maxWidth"/>.</summary>
    private void Shorten(double maxWidth)
    {
        if (_formatted is not null && (maxWidth == _shortenedFor || (double.IsNaN(maxWidth) && double.IsNaN(_shortenedFor))))
        {
            return;
        }

        _shortenedFor = maxWidth;
        var text = Text ?? string.Empty;
        var full = Format(text);
        if (full.WidthIncludingTrailingWhitespace <= maxWidth || text.Length == 0)
        {
            SetDisplay(text, full);
            return;
        }

        // Bisection over the number of kept characters: the largest count whose shortened form fits.
        var low = 0;
        var high = text.Length - 1;
        string? best = null;
        FormattedText? bestFormatted = null;
        while (low <= high)
        {
            var kept = low + ((high - low) / 2);
            var candidate = Cut(text, kept);
            var formatted = Format(candidate);
            if (formatted.WidthIncludingTrailingWhitespace <= maxWidth)
            {
                best = candidate;
                bestFormatted = formatted;
                low = kept + 1;
            }
            else
            {
                high = kept - 1;
            }
        }

        // Not even the ellipsis fits: draw nothing rather than overflow.
        SetDisplay(best ?? string.Empty, bestFormatted ?? Format(string.Empty));
    }

    /// <summary>
    /// Keeps <paramref name="kept"/> characters of <paramref name="text"/>: about half at the end, but at least the
    /// file extension (when it fits), the rest at the start.
    /// </summary>
    internal static string Cut(string text, int kept)
    {
        var extension = Path.GetExtension(text).Length;
        var tail = Math.Min(kept, Math.Max(extension, kept / 2));
        var head = kept - tail;

        // Never split a surrogate pair.
        if (head > 0 && char.IsHighSurrogate(text[head - 1]))
        {
            head--;
        }

        var tailStart = text.Length - tail;
        if (tail > 0 && char.IsLowSurrogate(text[tailStart]))
        {
            tailStart++;
        }

        return string.Concat(text.AsSpan(0, head), Ellipsis, text.AsSpan(tailStart));
    }

    private void SetDisplay(string text, FormattedText formatted)
    {
        DisplayText = text;
        _formatted = formatted;
        InvalidateVisual();
    }

    private FormattedText Format(string text) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle, FontWeight),
            FontSize,
            Foreground);
}
