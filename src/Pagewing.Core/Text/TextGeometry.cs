namespace Pagewing.Core.Text;

/// <summary>Line segments and rectangles of a list of characters, shared by <see cref="PageText"/> and the search.</summary>
internal static class TextGeometry
{
    /// <summary>Gets whether the character is the last one of its line: a generated line break or a line-end hyphen.</summary>
    public static bool EndsLine(TextChar c) => c.IsLineBreak || (c.Flags & TextCharTraits.Hyphen) != 0;

    /// <summary>
    /// Gets the exclusive end of the line segment that contains <paramref name="from"/>: the index of the next line
    /// break, or the index after the next line-end hyphen, or the number of characters.
    /// </summary>
    public static int SegmentEnd(IReadOnlyList<TextChar> chars, int from)
    {
        for (var i = from; i < chars.Count; i++)
        {
            if (chars[i].IsLineBreak)
            {
                return i;
            }

            if ((chars[i].Flags & TextCharTraits.Hyphen) != 0)
            {
                return i + 1;
            }
        }

        return chars.Count;
    }

    /// <summary>Gets the start of the line that contains <paramref name="index"/>: the index after the previous line break or line-end hyphen, or 0.</summary>
    public static int LineStart(IReadOnlyList<TextChar> chars, int index)
    {
        var start = index;
        while (start > 0 && !EndsLine(chars[start - 1]))
        {
            start--;
        }

        return start;
    }

    /// <summary>
    /// Gets the rectangles that cover the characters <paramref name="start"/> (inclusive) to <paramref name="end"/>
    /// (exclusive), one per line: the union of the boxes of the characters that are not generated, plus generated
    /// spaces with a box of non-zero area between them. Line breaks and generated characters at the ends of a line
    /// add nothing. The range must lie within <paramref name="chars"/>.
    /// </summary>
    public static List<PageRect> RectsOf(IReadOnlyList<TextChar> chars, int start, int end)
    {
        var rects = new List<PageRect>();
        var i = start;
        while (i < end)
        {
            var segmentEnd = Math.Min(SegmentEnd(chars, i), end);
            var first = -1;
            var last = -1;
            for (var k = i; k < segmentEnd; k++)
            {
                if (!chars[k].IsGenerated)
                {
                    first = first < 0 ? k : first;
                    last = k;
                }
            }

            if (first >= 0)
            {
                var union = chars[first].Box;
                for (var k = first + 1; k <= last; k++)
                {
                    var box = chars[k].Box;
                    if (!chars[k].IsGenerated || (box.Width > 0 && box.Height > 0))
                    {
                        union = union.Union(box);
                    }
                }

                rects.Add(union);
            }

            i = segmentEnd;
            if (i < end && chars[i].IsLineBreak)
            {
                i++;
            }
        }

        return rects;
    }
}
