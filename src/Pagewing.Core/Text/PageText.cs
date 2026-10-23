using System.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Text;

/// <summary>
/// The characters and links of one page, in the order and at the indexes the PDF engine reports them.
/// The data is immutable once created.
/// </summary>
public sealed class PageText
{
    /// <summary>The granularity <see cref="BodyFontSize"/> is rounded to, in points.</summary>
    private const double FontSizeStep = 0.5;

    /// <summary>Creates the text of a page.</summary>
    /// <param name="pageIndex">The 0-based page index.</param>
    /// <param name="width">The page width in points.</param>
    /// <param name="height">The page height in points.</param>
    /// <param name="chars">The characters.</param>
    /// <param name="links">The links.</param>
    public PageText(int pageIndex, double width, double height, IReadOnlyList<TextChar> chars, IReadOnlyList<PageLink> links)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(links);
        PageIndex = pageIndex;
        Width = width;
        Height = height;
        Chars = chars;
        Links = links;

        var sizes = new Dictionary<double, int>();
        foreach (var c in chars)
        {
            if (!c.IsGenerated)
            {
                HasText = true;
                var size = Math.Round(c.FontSize / FontSizeStep, MidpointRounding.AwayFromZero) * FontSizeStep;
                sizes[size] = sizes.GetValueOrDefault(size) + 1;
            }
        }

        // The most frequent size; on a tie the smaller one, so the result does not depend on the order.
        var best = 0.0;
        var bestCount = 0;
        foreach (var (size, count) in sizes)
        {
            if (count > bestCount || (count == bestCount && size < best))
            {
                best = size;
                bestCount = count;
            }
        }

        BodyFontSize = (float)best;
    }

    /// <summary>Gets the 0-based page index.</summary>
    public int PageIndex { get; }

    /// <summary>Gets the page width in points, after rotation and CropBox.</summary>
    public double Width { get; }

    /// <summary>Gets the page height in points, after rotation and CropBox.</summary>
    public double Height { get; }

    /// <summary>Gets the characters; empty for a page without text.</summary>
    public IReadOnlyList<TextChar> Chars { get; }

    /// <summary>Gets the links: link annotations first, then web addresses found in the text.</summary>
    public IReadOnlyList<PageLink> Links { get; }

    /// <summary>Gets whether any character is not generated.</summary>
    public bool HasText { get; }

    /// <summary>
    /// Gets the most frequent font size of the characters that are not generated, rounded to 0.5 points; 0 for a
    /// page without text.
    /// </summary>
    public float BodyFontSize { get; }

    /// <summary>Gets the characters <paramref name="start"/> (inclusive) to <paramref name="end"/> (exclusive) as a string, without any processing.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within <see cref="Chars"/>.</exception>
    public string GetText(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Chars.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, end);
        var builder = new StringBuilder(end - start);
        for (var i = start; i < end; i++)
        {
            builder.Append(Chars[i].Text);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Gets the index of the character that is not generated and whose box, inflated by 1 point, contains the point;
    /// a box that contains the point itself wins over one that only reaches it by the inflation.
    /// </summary>
    /// <param name="x">The x coordinate in page points.</param>
    /// <param name="y">The y coordinate in page points.</param>
    /// <returns>The index, or -1 if no character is there.</returns>
    public int CharAt(double x, double y)
    {
        var near = -1;
        for (var i = 0; i < Chars.Count; i++)
        {
            var c = Chars[i];
            if (c.IsGenerated)
            {
                continue;
            }

            if (c.Box.Contains(x, y))
            {
                return i;
            }

            if (near < 0 && c.Box.Inflate(1).Contains(x, y))
            {
                near = i;
            }
        }

        return near;
    }

    /// <summary>
    /// Gets the insertion position nearest to the point. The reading direction of each line is derived from its
    /// characters (the vector from the centre of its first to that of its last character): a line is vertical if that
    /// vector is more vertical than horizontal (text on a page rotated by 90 or 270 degrees), else horizontal; a line
    /// of a single character counts as horizontal, left to right. The nearest line is the one whose extent is nearest
    /// across its reading direction (for a vertical line in x, otherwise in y), ties go to the nearer one along it. In
    /// that line the result is the position before the first character whose centre lies beyond the point in reading
    /// direction, else after the line's last character. Beyond all lines across the reading direction of the text (the
    /// orientation of the first line of more than one character; horizontal, left to right, if there is none) the result is 0 before the first line and
    /// <see cref="Chars"/>.Count after the last one, whatever the position along the lines: for upright text above and
    /// below all text; lines follow each other downwards for horizontal left-to-right text, to the left for vertical
    /// top-to-bottom text (rotation by 90 degrees), to the right for vertical bottom-to-top text.
    /// </summary>
    /// <param name="x">The x coordinate in page points.</param>
    /// <param name="y">The y coordinate in page points.</param>
    /// <returns>A position from 0 to <see cref="Chars"/>.Count; 0 for a page without text.</returns>
    public int CaretAt(double x, double y)
    {
        var bestStart = -1;
        var bestEnd = -1;
        var bestVertical = false;
        var bestSign = 1;
        var bestPerp = double.PositiveInfinity;
        var bestPar = double.PositiveInfinity;

        // Extent of all lines across their reading direction, kept per orientation: [0] horizontal (y), [1] vertical (x).
        Span<double> extentMin = stackalloc double[] { double.PositiveInfinity, double.PositiveInfinity };
        Span<double> extentMax = stackalloc double[] { double.NegativeInfinity, double.NegativeInfinity };
        var dominant = -1;
        var dominantSign = 1;

        var start = 0;
        while (start < Chars.Count)
        {
            var end = SegmentEnd(start);
            var first = -1;
            var last = -1;
            var count = 0;
            var left = double.PositiveInfinity;
            var top = double.PositiveInfinity;
            var right = double.NegativeInfinity;
            var bottom = double.NegativeInfinity;
            for (var i = start; i < end; i++)
            {
                var c = Chars[i];
                if (c.IsGenerated)
                {
                    continue;
                }

                first = first < 0 ? i : first;
                last = i;
                count++;
                left = Math.Min(left, c.Box.Left);
                top = Math.Min(top, c.Box.Top);
                right = Math.Max(right, c.Box.Right);
                bottom = Math.Max(bottom, c.Box.Bottom);
            }

            if (count > 0)
            {
                var vertical = false;
                var sign = 1;
                if (count > 1)
                {
                    var a = Chars[first].Box;
                    var b = Chars[last].Box;
                    var along = ((b.Left + b.Right) - (a.Left + a.Right)) / 2;
                    var down = ((b.Top + b.Bottom) - (a.Top + a.Bottom)) / 2;
                    vertical = Math.Abs(down) > Math.Abs(along);
                    sign = (vertical ? down : along) < 0 ? -1 : 1;
                }

                var orientation = vertical ? 1 : 0;
                if (dominant < 0 && count > 1)
                {
                    dominant = orientation;
                    dominantSign = sign;
                }

                var perpStart = vertical ? left : top;
                var perpEnd = vertical ? right : bottom;
                extentMin[orientation] = Math.Min(extentMin[orientation], perpStart);
                extentMax[orientation] = Math.Max(extentMax[orientation], perpEnd);

                var perpPoint = vertical ? x : y;
                var parPoint = vertical ? y : x;
                var perp = Math.Max(Math.Max(perpStart - perpPoint, perpPoint - perpEnd), 0);
                var parStart = vertical ? top : left;
                var parEnd = vertical ? bottom : right;
                var par = Math.Max(Math.Max(parStart - parPoint, parPoint - parEnd), 0);
                if (perp < bestPerp || (perp == bestPerp && par < bestPar))
                {
                    bestPerp = perp;
                    bestPar = par;
                    bestStart = start;
                    bestEnd = end;
                    bestVertical = vertical;
                    bestSign = sign;
                }
            }

            start = end < Chars.Count && Chars[end].IsLineBreak ? end + 1 : end;
        }

        if (bestStart < 0)
        {
            return 0;
        }

        // Without a line of more than one character every line is horizontal, left to right.
        dominant = Math.Max(dominant, 0);

        // Lines follow each other with the reading sign for horizontal text and against it for vertical text.
        var lineSign = dominant == 1 ? -dominantSign : dominantSign;
        var across = dominant == 1 ? x : y;
        var beforeFirst = lineSign > 0 ? across < extentMin[dominant] : across > extentMax[dominant];
        if (beforeFirst)
        {
            return 0;
        }

        var afterLast = lineSign > 0 ? across > extentMax[dominant] : across < extentMin[dominant];
        if (afterLast)
        {
            return Chars.Count;
        }

        var point = bestVertical ? y : x;
        var caret = bestStart;
        for (var i = bestStart; i < bestEnd; i++)
        {
            var c = Chars[i];
            if (c.IsGenerated)
            {
                continue;
            }

            var centre = bestVertical ? (c.Box.Top + c.Box.Bottom) / 2 : (c.Box.Left + c.Box.Right) / 2;
            if ((point - centre) * bestSign < 0)
            {
                return i;
            }

            caret = i + 1;
        }

        return caret;
    }

    /// <summary>
    /// Gets the word around a character: the run of letters, digits and apostrophes that contains it, or only the
    /// character itself if it is none of these (or is generated).
    /// </summary>
    /// <param name="charIndex">The index of the character.</param>
    /// <returns>The range [Start, End).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="charIndex"/> is not within <see cref="Chars"/>.</exception>
    public (int Start, int End) WordAt(int charIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(charIndex, Chars.Count);
        if (!IsWordChar(Chars[charIndex]))
        {
            return (charIndex, charIndex + 1);
        }

        var start = charIndex;
        while (start > 0 && IsWordChar(Chars[start - 1]))
        {
            start--;
        }

        var end = charIndex + 1;
        while (end < Chars.Count && IsWordChar(Chars[end]))
        {
            end++;
        }

        return (start, end);
    }

    /// <summary>
    /// Gets the line around a character: the characters between two line breaks, the breaks excluded. A line also
    /// ends after a character with the <see cref="TextCharTraits.Hyphen"/> trait, which the engine follows with no
    /// line break.
    /// </summary>
    /// <param name="charIndex">The index of the character; for a line break the result is empty.</param>
    /// <returns>The range [Start, End).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="charIndex"/> is not within <see cref="Chars"/>.</exception>
    public (int Start, int End) LineAt(int charIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(charIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(charIndex, Chars.Count);
        if (Chars[charIndex].IsLineBreak)
        {
            return (charIndex, charIndex);
        }

        return (TextGeometry.LineStart(Chars, charIndex), SegmentEnd(charIndex));
    }

    /// <summary>
    /// Gets the rectangles that cover the characters of a range, one per line: the union of the boxes of the
    /// characters that are not generated, plus generated spaces with a box of non-zero area between them. Line
    /// breaks and generated characters at the ends of a line add nothing.
    /// </summary>
    /// <param name="start">The first character (inclusive).</param>
    /// <param name="end">The end of the range (exclusive).</param>
    /// <returns>The rectangles in text order; empty for an empty range or a range with only generated characters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not within <see cref="Chars"/>.</exception>
    public IReadOnlyList<PageRect> RectsOf(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(end, Chars.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(start, end);

        return TextGeometry.RectsOf(Chars, start, end);
    }

    /// <summary>
    /// Gets the link under the point. "Topmost" means first in list order: links come annotations first, then web
    /// addresses found in the text, and where several contain the point the first wins, so an explicit link
    /// annotation beats a detected address.
    /// </summary>
    /// <param name="x">The x coordinate in page points.</param>
    /// <param name="y">The y coordinate in page points.</param>
    /// <returns>The link, or <see langword="null"/> if there is none.</returns>
    public PageLink? LinkAt(double x, double y)
    {
        foreach (var link in Links)
        {
            foreach (var rect in link.Rects)
            {
                if (rect.Contains(x, y))
                {
                    return link;
                }
            }
        }

        return null;
    }

    private static bool IsWordChar(TextChar c)
    {
        if (c.IsGenerated)
        {
            return false;
        }

        return c.CodePoint is '\'' or '’' || (Rune.IsValid(c.CodePoint) && Rune.IsLetterOrDigit(new Rune(c.CodePoint)));
    }

    /// <summary>
    /// Gets the exclusive end of the line segment that contains <paramref name="from"/>: the index of the next line
    /// break, or the index after the next line-end hyphen, or <see cref="Chars"/>.Count. Lines are split the same way
    /// for selection rectangles and for copied text (<see cref="TextFlow"/>).
    /// </summary>
    internal int SegmentEnd(int from) => TextGeometry.SegmentEnd(Chars, from);

    /// <summary>Maps a character the PDFium layer read to the Core model.</summary>
    internal static TextChar FromPdfium(PdfiumChar c)
    {
        var flags = TextCharTraits.None;
        if (c.Generated)
        {
            flags |= TextCharTraits.Generated;
        }

        if (c.Hyphen)
        {
            flags |= TextCharTraits.Hyphen;
        }

        if (c.Bold)
        {
            flags |= TextCharTraits.Bold;
        }

        if (c.Italic)
        {
            flags |= TextCharTraits.Italic;
        }

        return new TextChar(c.CodePoint, new PageRect(c.Left, c.Top, c.Right, c.Bottom), flags, c.FontSize);
    }

    /// <summary>Maps what the PDFium layer read for a page to the Core model; links the engine cannot follow are dropped.</summary>
    internal static PageText FromPdfium(int pageIndex, double width, double height, PdfiumChar[] chars, PdfiumLink[] links)
    {
        var mapped = new TextChar[chars.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            mapped[i] = FromPdfium(chars[i]);
        }

        var mappedLinks = new List<PageLink>(links.Length);
        foreach (var link in links)
        {
            LinkTarget? target = link.Kind switch
            {
                PdfiumLinkKind.Page => new PageTarget(link.PageIndex, link.Top),
                PdfiumLinkKind.Uri when link.Uri is not null => new UriTarget(link.Uri),
                _ => null,
            };
            if (target is null)
            {
                continue;
            }

            var rects = new PageRect[link.Rects.Count];
            for (var i = 0; i < rects.Length; i++)
            {
                var (left, top, right, bottom) = link.Rects[i];
                rects[i] = new PageRect(left, top, right, bottom);
            }

            mappedLinks.Add(new PageLink(rects, target));
        }

        return new PageText(pageIndex, width, height, mapped, mappedLinks);
    }
}
