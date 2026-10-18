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
    /// Gets the insertion position nearest to the point: above all text it is 0, below all text
    /// <see cref="Chars"/>.Count; otherwise the line nearest in y (ties: nearest in x), and in it the position before
    /// the first character whose centre is right of the point, else after the line's last character.
    /// </summary>
    /// <param name="x">The x coordinate in page points.</param>
    /// <param name="y">The y coordinate in page points.</param>
    /// <returns>A position from 0 to <see cref="Chars"/>.Count; 0 for a page without text.</returns>
    public int CaretAt(double x, double y)
    {
        var bestStart = -1;
        var bestEnd = -1;
        var bestDy = double.PositiveInfinity;
        var bestDx = double.PositiveInfinity;
        var pageTop = double.PositiveInfinity;
        var pageBottom = double.NegativeInfinity;

        var start = 0;
        while (start < Chars.Count)
        {
            var end = SegmentEnd(start);
            var found = false;
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

                found = true;
                left = Math.Min(left, c.Box.Left);
                top = Math.Min(top, c.Box.Top);
                right = Math.Max(right, c.Box.Right);
                bottom = Math.Max(bottom, c.Box.Bottom);
            }

            if (found)
            {
                pageTop = Math.Min(pageTop, top);
                pageBottom = Math.Max(pageBottom, bottom);
                var dy = Math.Max(Math.Max(top - y, y - bottom), 0);
                var dx = Math.Max(Math.Max(left - x, x - right), 0);
                if (dy < bestDy || (dy == bestDy && dx < bestDx))
                {
                    bestDy = dy;
                    bestDx = dx;
                    bestStart = start;
                    bestEnd = end;
                }
            }

            start = end < Chars.Count && Chars[end].IsLineBreak ? end + 1 : end;
        }

        if (bestStart < 0 || y < pageTop)
        {
            return 0;
        }

        if (y > pageBottom)
        {
            return Chars.Count;
        }

        var caret = bestStart;
        for (var i = bestStart; i < bestEnd; i++)
        {
            var c = Chars[i];
            if (c.IsGenerated)
            {
                continue;
            }

            if (x < (c.Box.Left + c.Box.Right) / 2)
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

        var start = charIndex;
        while (start > 0 && !EndsLine(start - 1))
        {
            start--;
        }

        return (start, SegmentEnd(charIndex));
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

        var rects = new List<PageRect>();
        var i = start;
        while (i < end)
        {
            var segmentEnd = Math.Min(SegmentEnd(i), end);
            var first = -1;
            var last = -1;
            for (var k = i; k < segmentEnd; k++)
            {
                if (!Chars[k].IsGenerated)
                {
                    first = first < 0 ? k : first;
                    last = k;
                }
            }

            if (first >= 0)
            {
                var union = Chars[first].Box;
                for (var k = first + 1; k <= last; k++)
                {
                    var box = Chars[k].Box;
                    if (!Chars[k].IsGenerated || (box.Width > 0 && box.Height > 0))
                    {
                        union = union.Union(box);
                    }
                }

                rects.Add(union);
            }

            i = segmentEnd;
            if (i < end && Chars[i].IsLineBreak)
            {
                i++;
            }
        }

        return rects;
    }

    /// <summary>
    /// Gets the link under the point. Links come annotations first, then web addresses found in the text; where
    /// several contain the point the first wins, so an explicit link annotation beats a detected address.
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

    /// <summary>Gets whether the character is the last one of its line: a generated line break or a line-end hyphen.</summary>
    private bool EndsLine(int index) => Chars[index].IsLineBreak || (Chars[index].Flags & TextCharTraits.Hyphen) != 0;

    /// <summary>
    /// Gets the exclusive end of the line segment that contains <paramref name="from"/>: the index of the next line
    /// break, or the index after the next line-end hyphen, or <see cref="Chars"/>.Count.
    /// </summary>
    private int SegmentEnd(int from)
    {
        for (var i = from; i < Chars.Count; i++)
        {
            if (Chars[i].IsLineBreak)
            {
                return i;
            }

            if ((Chars[i].Flags & TextCharTraits.Hyphen) != 0)
            {
                return i + 1;
            }
        }

        return Chars.Count;
    }

    /// <summary>Maps what the PDFium layer read for a page to the Core model; links the engine cannot follow are dropped.</summary>
    internal static PageText FromPdfium(int pageIndex, double width, double height, PdfiumChar[] chars, PdfiumLink[] links)
    {
        var mapped = new TextChar[chars.Length];
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
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

            mapped[i] = new TextChar(c.CodePoint, new PageRect(c.Left, c.Top, c.Right, c.Bottom), flags, c.FontSize);
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
