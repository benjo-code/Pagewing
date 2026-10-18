using System.Text;

namespace Pagewing.Core.Text;

/// <summary>
/// Turns selected text into strings for the clipboard. All three forms share one line structure: a line ends at a
/// generated line break, after a line-end hyphen (<see cref="TextCharTraits.Hyphen"/>, which the engine follows with no
/// line break) and at the end of a slice; lines without visible characters are dropped.
/// </summary>
public static class TextFlow
{
    /// <summary>A gap between two lines larger than this times the font size of the upper line starts a paragraph.</summary>
    private const double ParagraphGapRatio = 0.5;

    /// <summary>The part of a line's em box above its baseline, in units of its font size.</summary>
    private const double EmAscent = 0.8;

    /// <summary>The part of a line's em box below its baseline, in units of its font size.</summary>
    private const double EmDescent = 0.2;

    /// <summary>A line with more visible characters than this is never a heading.</summary>
    private const int MaxHeadingLength = 100;

    /// <summary>The smallest ratio of a line's largest font size to the page's body size for h1, h2 and h3.</summary>
    private static readonly double[] HeadingRatios = [1.6, 1.3, 1.15];

    /// <summary>
    /// Selected text as continuous prose: lines of a paragraph joined, hyphens at line ends removed, ligatures expanded,
    /// paragraphs separated by one blank line.
    /// </summary>
    /// <param name="slices">The selected characters, in reading order.</param>
    /// <returns>The text; empty if the slices contain no visible character.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A slice is not within the characters of its page.</exception>
    public static string Plain(IReadOnlyList<TextSlice> slices)
    {
        var builder = new StringBuilder();
        foreach (var paragraph in Paragraphs(Lines(slices), headings: false))
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            foreach (var piece in paragraph.Pieces)
            {
                builder.Append(piece.Text);
            }
        }

        return builder.ToString();
    }

    /// <summary>The text as the PDF has it: PDFium line breaks become "\n", nothing joined, nothing removed.</summary>
    /// <param name="slices">The selected characters, in reading order.</param>
    /// <returns>The text; empty if the slices contain no visible character.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A slice is not within the characters of its page.</exception>
    public static string Raw(IReadOnlyList<TextSlice> slices)
    {
        var builder = new StringBuilder();
        var first = true;
        foreach (var line in Lines(slices))
        {
            if (!first)
            {
                builder.Append('\n');
            }

            first = false;
            builder.Append(line.Page.GetText(line.Start, line.End));
        }

        return builder.ToString();
    }

    /// <summary>
    /// An HTML fragment (no html/body): p, h1-h3, b, i, text escaped; no fonts, sizes in text, or colors. Each block is
    /// followed by "\n".
    /// </summary>
    /// <param name="slices">The selected characters, in reading order.</param>
    /// <returns>The fragment; empty if the slices contain no visible character.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A slice is not within the characters of its page.</exception>
    public static string Html(IReadOnlyList<TextSlice> slices)
    {
        var builder = new StringBuilder();
        foreach (var paragraph in Paragraphs(Lines(slices), headings: true))
        {
            var tag = paragraph.Level == 0 ? "p" : "h" + paragraph.Level;
            builder.Append('<').Append(tag).Append('>');
            var bold = false;
            var italic = false;
            foreach (var piece in paragraph.Pieces)
            {
                // A heading is bold by itself. Tags nest as <b><i>…</i></b>.
                var wantBold = paragraph.Level == 0 && (piece.Style & TextCharTraits.Bold) != 0;
                var wantItalic = (piece.Style & TextCharTraits.Italic) != 0;
                if (bold != wantBold)
                {
                    if (italic)
                    {
                        builder.Append("</i>");
                        italic = false;
                    }

                    builder.Append(wantBold ? "<b>" : "</b>");
                    bold = wantBold;
                }

                if (italic != wantItalic)
                {
                    builder.Append(wantItalic ? "<i>" : "</i>");
                    italic = wantItalic;
                }

                AppendEscaped(builder, piece.Text);
            }

            builder.Append(italic ? "</i>" : string.Empty).Append(bold ? "</b>" : string.Empty);
            builder.Append("</").Append(tag).Append(">\n");
        }

        return builder.ToString();
    }

    /// <summary>The slices a selection covers on the given pages; empty ranges are dropped.</summary>
    /// <param name="selection">The selection.</param>
    /// <param name="pageOf">Gets the loaded text of a page by its index, for every page the selection covers.</param>
    /// <returns>The slices in page order; carets beyond the characters of a page are clamped to it.</returns>
    public static IReadOnlyList<TextSlice> SlicesOf(TextSelection selection, Func<int, PageText> pageOf)
    {
        ArgumentNullException.ThrowIfNull(pageOf);
        var start = selection.Start;
        var end = selection.End;
        var slices = new List<TextSlice>();
        for (var index = start.Page; index <= end.Page; index++)
        {
            var page = pageOf(index);
            var count = page.Chars.Count;
            var from = Math.Clamp(index == start.Page ? start.Caret : 0, 0, count);
            var to = Math.Clamp(index == end.Page ? end.Caret : count, 0, count);
            if (from < to)
            {
                slices.Add(new TextSlice(page, from, to));
            }
        }

        return slices;
    }

    /// <summary>Splits the slices into lines that have at least one visible character.</summary>
    private static List<Line> Lines(IReadOnlyList<TextSlice> slices)
    {
        ArgumentNullException.ThrowIfNull(slices);
        var lines = new List<Line>();
        var bottoms = new List<double>();
        foreach (var slice in slices)
        {
            var page = slice.Page ?? throw new ArgumentException("A slice has no page.", nameof(slices));
            ArgumentOutOfRangeException.ThrowIfNegative(slice.Start, nameof(slices));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(slice.End, page.Chars.Count, nameof(slices));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(slice.Start, slice.End, nameof(slices));

            var i = slice.Start;
            while (i < slice.End)
            {
                var end = Math.Min(page.SegmentEnd(i), slice.End);
                if (MakeLine(page, i, end, bottoms) is { } line)
                {
                    lines.Add(line);
                }

                i = end;
                if (i < slice.End && page.Chars[i].IsLineBreak)
                {
                    i++;
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Makes a line of the range, or <see langword="null"/> if it has no visible character. Its extent is the em box of
    /// its largest font size around its baseline, not the union of the glyph boxes: those are tight, so the gap between
    /// two lines would depend on whether they have descenders and ascenders (in text-rich.pdf 4.75 pt below "für Win-",
    /// but 7.1 pt below "und bleibt" at the same 16 pt line distance). The baseline is the median of the bottoms of the
    /// visible glyphs; descenders are a minority in a line.
    /// </summary>
    private static Line? MakeLine(PageText page, int start, int end, List<double> bottoms)
    {
        var first = -1;
        var last = -1;
        var size = 0f;
        bottoms.Clear();
        for (var i = start; i < end; i++)
        {
            var c = page.Chars[i];
            if (!IsVisible(c))
            {
                continue;
            }

            first = first < 0 ? i : first;
            last = i;
            size = Math.Max(size, c.FontSize);
            bottoms.Add(c.Box.Bottom);
        }

        if (first < 0)
        {
            return null;
        }

        bottoms.Sort();
        var baseline = bottoms[(bottoms.Count - 1) / 2];
        return new Line(page, start, end, first, last, size, baseline - (EmAscent * size), baseline + (EmDescent * size));
    }

    /// <summary>Groups the lines into paragraphs of collapsed pieces.</summary>
    /// <param name="lines">The lines.</param>
    /// <param name="headings">Whether heading lines are recognised (each one is a paragraph of its own).</param>
    private static List<Paragraph> Paragraphs(List<Line> lines, bool headings)
    {
        var paragraphs = new List<Paragraph>();
        var pieces = new List<Piece>();
        var level = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineLevel = headings ? HeadingLevel(line) : 0;
            if (i == 0 || lineLevel > 0 || level > 0 || StartsParagraph(lines[i - 1], line))
            {
                if (i > 0)
                {
                    paragraphs.Add(new Paragraph(level, Collapse(pieces)));
                    pieces.Clear();
                }

                level = lineLevel;
            }
            else if (JoinsHyphenated(lines[i - 1], line))
            {
                // Drop the hyphen (and whatever spaces follow it on its line); no space between the parts of the word.
                while (pieces[^1].IsSpace)
                {
                    pieces.RemoveAt(pieces.Count - 1);
                }

                pieces.RemoveAt(pieces.Count - 1);
            }
            else
            {
                pieces.Add(Piece.Space);
            }

            for (var k = line.Start; k < line.End; k++)
            {
                var c = line.Page.Chars[k];
                pieces.Add(IsSpace(c) ? Piece.Space : new Piece(Expand(c), c.Flags & (TextCharTraits.Bold | TextCharTraits.Italic)));
            }
        }

        if (lines.Count > 0)
        {
            paragraphs.Add(new Paragraph(level, Collapse(pieces)));
        }

        return paragraphs;
    }

    /// <summary>
    /// Gets whether <paramref name="next"/> starts a paragraph after <paramref name="previous"/>: on the same page if
    /// the gap between their em boxes is larger than <see cref="ParagraphGapRatio"/> times the font size of the upper line,
    /// across pages only if the upper line ends with '.', '!', '?' or ':'.
    /// </summary>
    private static bool StartsParagraph(Line previous, Line next)
    {
        if (previous.Page.PageIndex != next.Page.PageIndex)
        {
            return previous.Page.Chars[previous.Last].CodePoint is '.' or '!' or '?' or ':';
        }

        return next.Top - previous.Bottom > ParagraphGapRatio * previous.Size;
    }

    /// <summary>
    /// Gets whether the upper line ends with a hyphen directly behind a letter and the lower line starts with a
    /// lowercase letter, so the hyphen is dropped and the parts are joined without a space.
    /// </summary>
    private static bool JoinsHyphenated(Line previous, Line next)
    {
        var chars = previous.Page.Chars;
        var hyphen = chars[previous.Last];
        var isHyphen = (hyphen.Flags & TextCharTraits.Hyphen) != 0 || hyphen.CodePoint is '-' or 0xAD;
        if (!isHyphen || previous.Last == previous.Start)
        {
            return false;
        }

        var before = chars[previous.Last - 1];
        var after = next.Page.Chars[next.First];
        return !before.IsGenerated && IsRune(before, Rune.IsLetter) && IsRune(after, Rune.IsLower);
    }

    /// <summary>Gets the heading level of a line: 1 to 3, or 0 for a line that is no heading.</summary>
    private static int HeadingLevel(Line line)
    {
        var body = line.Page.BodyFontSize;
        if (body <= 0)
        {
            return 0;
        }

        var visible = 0;
        for (var i = line.First; i <= line.Last; i++)
        {
            visible += IsVisible(line.Page.Chars[i]) ? 1 : 0;
        }

        if (visible > MaxHeadingLength)
        {
            return 0;
        }

        for (var level = 0; level < HeadingRatios.Length; level++)
        {
            if (line.Size >= HeadingRatios[level] * body)
            {
                return level + 1;
            }
        }

        return 0;
    }

    /// <summary>
    /// Collapses runs of spaces into one, drops spaces at both ends and gives each space the markup it shares with the
    /// characters on both sides, so a run of marked text keeps its inner spaces but never ends with one.
    /// </summary>
    private static List<Piece> Collapse(List<Piece> pieces)
    {
        var result = new List<Piece>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (piece.IsSpace && (result.Count == 0 || result[^1].IsSpace))
            {
                continue;
            }

            result.Add(piece);
        }

        if (result.Count > 0 && result[^1].IsSpace)
        {
            result.RemoveAt(result.Count - 1);
        }

        for (var i = 1; i < result.Count - 1; i++)
        {
            if (result[i].IsSpace)
            {
                result[i] = Piece.Space with { Style = result[i - 1].Style & result[i + 1].Style };
            }
        }

        return result;
    }

    /// <summary>Gets the text of a character with the ligatures U+FB00 to U+FB06 expanded.</summary>
    private static string Expand(TextChar c) => c.CodePoint switch
    {
        0xFB00 => "ff",
        0xFB01 => "fi",
        0xFB02 => "fl",
        0xFB03 => "ffi",
        0xFB04 => "ffl",
        0xFB05 or 0xFB06 => "st",
        _ => c.Text,
    };

    private static void AppendEscaped(StringBuilder builder, string text)
    {
        foreach (var ch in text)
        {
            _ = ch switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                _ => builder.Append(ch),
            };
        }
    }

    /// <summary>Gets whether a character is a space: generated (a space or a line break), a space, a tab, CR or LF.</summary>
    private static bool IsSpace(TextChar c) => c.IsGenerated || c.CodePoint is ' ' or '\t' or '\r' or '\n';

    /// <summary>Gets whether a character is drawn by the page and is no space.</summary>
    private static bool IsVisible(TextChar c) => !IsSpace(c);

    private static bool IsRune(TextChar c, Func<Rune, bool> test) => Rune.IsValid(c.CodePoint) && test(new Rune(c.CodePoint));

    /// <summary>
    /// A line of one page: the range [Start, End), its first and last visible character, its largest font size and the
    /// top and bottom of its em box.
    /// </summary>
    private readonly record struct Line(PageText Page, int Start, int End, int First, int Last, float Size, double Top, double Bottom);

    /// <summary>A paragraph: its heading level (0 for body text) and its pieces.</summary>
    private sealed record Paragraph(int Level, List<Piece> Pieces);

    /// <summary>The text of one character (a ligature expanded, a space as " ") and its markup.</summary>
    private readonly record struct Piece(string Text, TextCharTraits Style, bool IsSpace)
    {
        public Piece(string text, TextCharTraits style)
            : this(text, style, false)
        {
        }

        public static Piece Space { get; } = new(" ", TextCharTraits.None, true);
    }
}
