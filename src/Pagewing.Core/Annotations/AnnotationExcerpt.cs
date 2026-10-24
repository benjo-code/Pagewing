using System.Text;
using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>The text under a markup annotation, for lists and tooltips.</summary>
public static class AnnotationExcerpt
{
    private const string Ellipsis = "…";

    /// <summary>
    /// Gets the text under the quadrilaterals of an annotation in reading order (the order of the characters on the
    /// page): a character counts if the centre of its box lies inside one of <see cref="PdfAnnotation.QuadRects"/>,
    /// which are already in the page points of <paramref name="text"/> (origin top left, after rotation and CropBox),
    /// so no coordinate conversion happens here. Whitespace runs, line breaks included, become one space. Without
    /// <see cref="PdfAnnotation.QuadRects"/>, or if they cover no character, the text under
    /// <see cref="PdfAnnotation.Bounds"/> is used instead.
    /// </summary>
    /// <param name="annotation">The annotation.</param>
    /// <param name="text">The text of the annotation's page.</param>
    /// <param name="maxLength">The longest result in UTF-16 units, the ellipsis included; at least 1.</param>
    /// <returns>The text, shortened with an ellipsis if longer than <paramref name="maxLength"/>; empty if there is none.</returns>
    public static string For(PdfAnnotation annotation, PageText text, int maxLength = 120)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);

        var found = CharsUnder(text, annotation.QuadRects);
        if (found.Count == 0)
        {
            found = CharsUnder(text, [annotation.Bounds]);
        }

        var builder = new StringBuilder();
        var previous = -1;
        foreach (var index in found)
        {
            // A gap means generated spaces and line breaks, or characters of another column or line in between.
            if (previous >= 0 && index > previous + 1)
            {
                AppendSpace(builder);
            }

            var c = text.Chars[index].Text;
            if (c.Length == 1 && char.IsWhiteSpace(c[0]))
            {
                AppendSpace(builder);
            }
            else
            {
                builder.Append(c);
            }

            previous = index;
        }

        return Shorten(builder.ToString().Trim(), maxLength);
    }

    private static List<int> CharsUnder(PageText text, IReadOnlyList<PageRect> areas)
    {
        var found = new List<int>();
        for (var i = 0; i < text.Chars.Count; i++)
        {
            var c = text.Chars[i];
            if (c.IsGenerated)
            {
                continue;
            }

            var x = (c.Box.Left + c.Box.Right) / 2;
            var y = (c.Box.Top + c.Box.Bottom) / 2;
            foreach (var area in areas)
            {
                if (area.Contains(x, y))
                {
                    found.Add(i);
                    break;
                }
            }
        }

        return found;
    }

    private static void AppendSpace(StringBuilder builder)
    {
        if (builder.Length > 0 && builder[^1] != ' ')
        {
            builder.Append(' ');
        }
    }

    private static string Shorten(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var keep = maxLength - Ellipsis.Length;
        if (keep > 0 && char.IsHighSurrogate(value[keep - 1]))
        {
            keep--;
        }

        return value[..keep].TrimEnd() + Ellipsis;
    }
}
