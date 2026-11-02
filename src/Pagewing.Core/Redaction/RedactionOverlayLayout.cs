using System.Collections;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>One line of overlay text: the text and where it starts, in points from the top left of the area.</summary>
/// <param name="Text">The text; the same string for every tile of a repeated text.</param>
/// <param name="X">Where the first glyph starts, from the left edge of the area.</param>
/// <param name="Baseline">The baseline, from the top edge of the area.</param>
/// <param name="Width">The advance width of <paramref name="Text"/> at the font size of the placement.</param>
public sealed record OverlayLine(string Text, double X, double Baseline, double Width);

/// <summary>Where the overlay text of one area goes.</summary>
/// <param name="Lines">The lines (the tiles of a repeated text), in reading order; empty when nothing is drawn. A computed list: it costs no memory per tile.</param>
/// <param name="FontSize">The font size in points that the lines are set in.</param>
/// <param name="Fits">Whether the text fits the area. <see langword="false"/> means nothing is drawn and the card counts the area ("the text does not fit"); the area is never overflowed.</param>
public sealed record OverlayPlacement(IReadOnlyList<OverlayLine> Lines, double FontSize, bool Fits);

/// <summary>
/// Lays out the overlay text of one area (milestone 14.1): one text centred in the area, or, with <see cref="RedactionOverlayStyle.Repeat"/>,
/// the text tiled over it in rows and columns. The size is the given one, or with <see cref="RedactionOverlayStyle.AutoSize"/> the
/// largest that fits, down to <see cref="RedactionLimits.MinOverlayFontSize"/>. The text is set in the font of the text boxes with its widths
/// (<see cref="IGlyphMetrics"/>), without kerning, as the appearance of a text box is. <b>Nothing ever reaches outside the area</b>: every
/// line lies inside a margin of <see cref="Inset"/> points on all sides, and a text that does not fit gives no lines and
/// <see cref="OverlayPlacement.Fits"/> = <see langword="false"/>. The work does not grow with the size of the area: a repeated text has at most
/// <see cref="MaxOverlayTiles"/> tiles (a larger tiling is spread more thinly), and the tiles are computed, not stored.
/// </summary>
public static class RedactionOverlayLayout
{
    /// <summary>The most tiles one area gets. Where more would fit, the tiling is spread more thinly over the whole area; it still fits.</summary>
    public const int MaxOverlayTiles = 10_000;

    /// <summary>The largest size, in points, an automatically sized text gets (a text on a large area is not blown up to its height).</summary>
    public const double MaxAutoFontSize = 72;

    /// <summary>The size, in points, of a text that is not sized automatically, and the largest size of a repeated text that is (the size of the text boxes of the annotation tools).</summary>
    public const double DefaultFontSize = 12;

    /// <summary>The margin, in points, between the area and the text on every side.</summary>
    public const double Inset = 1;

    /// <summary>The gap between two tiles in a row, in font sizes.</summary>
    public const double TileGap = 1;

    /// <summary>The distance between the baselines of two rows of tiles, in line heights.</summary>
    public const double RowSpacing = 1.5;

    /// <summary>The steps in which an automatic size goes down, in points; the size is rounded down to a multiple, so rounding never makes it too wide.</summary>
    private const double SizeStep = 0.05;

    /// <summary>Lays out <paramref name="text"/> in <paramref name="area"/>.</summary>
    /// <param name="text">The overlay text; only characters of <see cref="TextBoxCharacters"/>, in one line (the plan checks it when the mark enters).</param>
    /// <param name="area">The area in points; the lines are given from its top left.</param>
    /// <param name="style">How the text is drawn: repeated or once, with an automatic size or the given one.</param>
    /// <param name="baseFontSize">The size in points when the style has no automatic size, and the largest size of a repeated text with one.</param>
    /// <param name="metrics">The glyph widths.</param>
    /// <returns>The placement.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="baseFontSize"/> is not above 0 or not finite.</exception>
    public static OverlayPlacement Fit(string text, PageRect area, RedactionOverlayStyle style, double baseFontSize, IGlyphMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(metrics);
        if (!(baseFontSize > 0) || !double.IsFinite(baseFontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(baseFontSize), baseFontSize, "The font size must be finite and above 0.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new OverlayPlacement([], baseFontSize, Fits: true);
        }

        var width = area.Right - area.Left;
        var height = area.Bottom - area.Top;
        var availableWidth = width - (2 * Inset);
        var availableHeight = height - (2 * Inset);
        var lineEm = metrics.Ascent + metrics.Descent;
        if (!double.IsFinite(availableWidth) || !double.IsFinite(availableHeight) || !(availableWidth > 0) || !(availableHeight > 0)
            || !double.IsFinite(lineEm) || !(lineEm > 0))
        {
            return Miss(baseFontSize);
        }

        var textEm = 0.0;
        foreach (var character in text)
        {
            textEm += metrics.AdvanceOf(character);
        }

        if (!double.IsFinite(textEm) || textEm < 0)
        {
            return Miss(baseFontSize);
        }

        double size;
        if (style.AutoSize)
        {
            var start = Math.Min(availableHeight / lineEm, MaxAutoFontSize);
            if (style.Repeat)
            {
                start = Math.Min(start, baseFontSize);
            }

            if (textEm > 0)
            {
                start = Math.Min(start, availableWidth / textEm);
            }

            // Rounded down to a step, then checked against the real widths: a size that is a hair too wide goes down a step.
            size = Math.Floor((start / SizeStep) + 1e-9) * SizeStep;
            while (size >= RedactionLimits.MinOverlayFontSize && !Fits(size, textEm, lineEm, availableWidth, availableHeight))
            {
                size -= SizeStep;
            }

            if (size < RedactionLimits.MinOverlayFontSize - 1e-9)
            {
                return Miss(RedactionLimits.MinOverlayFontSize);
            }

            size = Math.Max(size, RedactionLimits.MinOverlayFontSize);
        }
        else
        {
            size = baseFontSize;
            if (!Fits(size, textEm, lineEm, availableWidth, availableHeight))
            {
                return Miss(size);
            }
        }

        var textWidth = textEm * size;
        var lineHeight = lineEm * size;
        var ascent = metrics.Ascent * size;
        if (!style.Repeat)
        {
            var line = new OverlayLine(
                text,
                Inset + ((availableWidth - textWidth) / 2),
                Inset + ((availableHeight - lineHeight) / 2) + ascent,
                textWidth);
            return new OverlayPlacement([line], size, Fits: true);
        }

        return new OverlayPlacement(Tile(text, textWidth, lineHeight, ascent, size, availableWidth, availableHeight), size, Fits: true);
    }

    private static OverlayPlacement Miss(double size) => new([], size, Fits: false);

    private static bool Fits(double size, double textEm, double lineEm, double availableWidth, double availableHeight) =>
        textEm * size <= availableWidth && lineEm * size <= availableHeight;

    /// <summary>Tiles the text over the area: as many columns and rows as fit with the gaps, centred; or, above the cap, spread evenly.</summary>
    private static TileList Tile(string text, double textWidth, double lineHeight, double ascent, double size, double availableWidth, double availableHeight)
    {
        var gapX = TileGap * size;
        var pitchY = RowSpacing * lineHeight;
        var gapY = pitchY - lineHeight;
        var columns = CountOf((availableWidth + gapX) / (textWidth + gapX));
        var rows = CountOf((availableHeight + gapY) / pitchY);
        var spreadX = 0.0;
        var spreadY = 0.0;
        var pitchX = textWidth + gapX;
        if (columns * rows > MaxOverlayTiles)
        {
            // Too many: fewer in both directions in proportion, spread over the whole area.
            var factor = Math.Sqrt((double)MaxOverlayTiles / ((double)columns * rows));
            columns = Math.Max(1, (long)Math.Floor(columns * factor));
            rows = Math.Max(1, (long)Math.Floor(rows * factor));
            while (columns * rows > MaxOverlayTiles)
            {
                if (columns >= rows)
                {
                    columns--;
                }
                else
                {
                    rows--;
                }
            }

            spreadX = columns > 1 ? (availableWidth - textWidth) / (columns - 1) : 0;
            spreadY = rows > 1 ? (availableHeight - lineHeight) / (rows - 1) : 0;
            pitchX = spreadX;
            pitchY = spreadY;
        }

        var spanX = ((columns - 1) * pitchX) + textWidth;
        var spanY = ((rows - 1) * pitchY) + lineHeight;
        var left = Inset + ((availableWidth - spanX) / 2);
        var firstBaseline = Inset + ((availableHeight - spanY) / 2) + ascent;
        return new TileList(text, textWidth, (int)columns, (int)rows, left, pitchX, firstBaseline, pitchY);
    }

    /// <summary>The number of tiles along one direction: at least one (the text fits once), and never more than a loop could count.</summary>
    private static long CountOf(double count) => double.IsFinite(count) ? (long)Math.Clamp(Math.Floor(count), 1, 1e9) : 1;

    /// <summary>The tiles of a repeated text as a list that is worked out on access, so an area of any size holds no memory per tile.</summary>
    private sealed class TileList(string text, double width, int columns, int rows, double left, double pitchX, double firstBaseline, double pitchY) : IReadOnlyList<OverlayLine>
    {
        public int Count => columns * rows;

        public OverlayLine this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                var row = index / columns;
                var column = index % columns;
                return new OverlayLine(text, left + (column * pitchX), firstBaseline + (row * pitchY), width);
            }
        }

        public IEnumerator<OverlayLine> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
