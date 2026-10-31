namespace Pagewing.Core.Annotations;

/// <summary>The widths and vertical metrics of the glyphs a text box is set in, in em (fractions of the font size).</summary>
public interface IGlyphMetrics
{
    /// <summary>Gets how far the font reaches above the baseline, in em (positive).</summary>
    double Ascent { get; }

    /// <summary>Gets how far the font reaches below the baseline, in em (positive).</summary>
    double Descent { get; }

    /// <summary>Gets the advance width of a character, in em.</summary>
    /// <param name="character">A character of <see cref="TextBoxCharacters"/>.</param>
    /// <returns>The advance width; 0 for a character the font does not have.</returns>
    double AdvanceOf(char character);
}

/// <summary>A line of a text box: its text and where it starts, in points from the top left of the box.</summary>
/// <param name="Text">The text of the line, without the spaces at the break.</param>
/// <param name="X">Where the first glyph starts (the left padding).</param>
/// <param name="Baseline">The baseline, from the top of the box.</param>
/// <param name="Width">The advance width of <paramref name="Text"/>.</param>
public sealed record TextBoxLine(string Text, double X, double Baseline, double Width);

/// <summary>
/// The lines of a text box: left aligned, wrapped at spaces and after hyphens and dashes to the width of the box less
/// <see cref="Padding"/> on both sides; a word longer than a line breaks between characters; spaces at a break hang beyond
/// the line and are left out; a non-breaking space never breaks; a line feed always does. The first baseline lies
/// <see cref="Padding"/> plus the ascent below the top of the box, each further one a line height (ascent plus descent) lower,
/// as a text field of the app shows it with the same font. No kerning.
/// </summary>
/// <param name="Lines">The lines, at least one.</param>
/// <param name="LineHeight">The distance between two baselines, in points.</param>
/// <param name="ContentHeight">The height the lines need with the padding above and below, in points.</param>
public sealed record TextBoxLayout(IReadOnlyList<TextBoxLine> Lines, double LineHeight, double ContentHeight)
{
    /// <summary>The space between the box and the text on every side, in points.</summary>
    public const double Padding = 4;

    /// <summary>Lays out a text in a box of <paramref name="boxWidth"/>.</summary>
    /// <param name="text">The text; CR LF and CR count as line feeds.</param>
    /// <param name="fontSize">The font size in points (above 0).</param>
    /// <param name="boxWidth">The width of the box in points (finite, at least 0).</param>
    /// <param name="metrics">The glyph widths.</param>
    /// <returns>The lines.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The font size is not above 0 or the width is negative or not finite.</exception>
    public static TextBoxLayout Lay(string text, double fontSize, double boxWidth, IGlyphMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(metrics);
        if (!(fontSize > 0) || !double.IsFinite(fontSize))
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize), fontSize, "The font size must be above 0.");
        }

        if (!(boxWidth >= 0) || !double.IsFinite(boxWidth))
        {
            throw new ArgumentOutOfRangeException(nameof(boxWidth), boxWidth, "The width must be finite and at least 0.");
        }

        var available = Math.Max(0, boxWidth - (2 * Padding));
        var lineHeight = (metrics.Ascent + metrics.Descent) * fontSize;
        var first = Padding + (metrics.Ascent * fontSize);
        var lines = new List<TextBoxLine>();
        foreach (var paragraph in TextBoxSpec.NormalizeText(text).Split('\n'))
        {
            Wrap(paragraph, fontSize, available, metrics, line =>
                lines.Add(new TextBoxLine(line.Text, Padding, first + (lines.Count * lineHeight), line.Width)));
        }

        return new TextBoxLayout(lines, lineHeight, (2 * Padding) + (lines.Count * lineHeight));
    }

    private static bool IsBreakAfter(char c) => c is '-' or (char)0x2013 or (char)0x2014;

    /// <summary>Wraps one paragraph (no line feed in it); an empty paragraph is one empty line.</summary>
    private static void Wrap(string paragraph, double fontSize, double available, IGlyphMetrics metrics, Action<(string Text, double Width)> add)
    {
        var start = 0;
        do
        {
            // The width up to the last glyph that is no space, and the place of the last break opportunity with its width.
            var visible = 0.0;
            var running = 0.0;
            var breakAt = -1;
            var widthAtBreak = 0.0;
            var end = start;
            while (end < paragraph.Length)
            {
                var c = paragraph[end];
                var advance = metrics.AdvanceOf(c) * fontSize;
                if (c == ' ')
                {
                    // Spaces hang: they never push a line over its width.
                    running += advance;
                    end++;
                    if (end > start)
                    {
                        breakAt = end;
                        widthAtBreak = visible;
                    }

                    continue;
                }

                if (running + advance > available && end > start && HasGlyph(paragraph, start, end))
                {
                    break;
                }

                running += advance;
                visible = running;
                end++;
                if (IsBreakAfter(c))
                {
                    breakAt = end;
                    widthAtBreak = visible;
                }
            }

            if (end < paragraph.Length && breakAt > start)
            {
                // Break at the last opportunity; the spaces before it are left out of the line.
                end = breakAt;
                visible = widthAtBreak;
            }

            add((paragraph[start..end].TrimEnd(' '), visible));
            start = end;
            while (start < paragraph.Length && paragraph[start] == ' ' && end < paragraph.Length)
            {
                // The spaces at a break belong to no line.
                start++;
            }
        }
        while (start < paragraph.Length);
    }

    /// <summary>Whether the line from <paramref name="start"/> to <paramref name="end"/> has a glyph that is no space (so it may end here).</summary>
    private static bool HasGlyph(string paragraph, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (paragraph[i] != ' ')
            {
                return true;
            }
        }

        return false;
    }
}
