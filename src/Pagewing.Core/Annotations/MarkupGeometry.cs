using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>Geometry of text markup: from selection rectangles to lines and quadrilaterals.</summary>
public static class MarkupGeometry
{
    /// <summary>Two rectangles are on one line if they overlap vertically by at least this share of the smaller height.</summary>
    private const double SameLineOverlap = 0.5;

    /// <summary>Two rectangles of one line are neighbours if the gap between them is at most this share of the smaller height (about a space).</summary>
    private const double NeighbourGap = 0.5;

    /// <summary>
    /// Gets one rectangle per line: neighbours on the same line are merged, rectangles that are empty, inverted or
    /// not finite are dropped. The order follows the first rectangle of each line in the input.
    /// </summary>
    /// <param name="selectionRects">The rectangles, for example from a text selection, in page points.</param>
    /// <returns>The lines.</returns>
    public static IReadOnlyList<PageRect> LinesFor(IEnumerable<PageRect> selectionRects)
    {
        ArgumentNullException.ThrowIfNull(selectionRects);
        var lines = new List<PageRect>();
        foreach (var rect in selectionRects)
        {
            if (!IsUsable(rect))
            {
                continue;
            }

            var merged = rect;
            var at = lines.Count;
            var k = 0;
            while (k < lines.Count)
            {
                if (AreNeighbours(lines[k], merged))
                {
                    merged = merged.Union(lines[k]);
                    at = Math.Min(at, k);
                    lines.RemoveAt(k);
                    k = 0;
                }
                else
                {
                    k++;
                }
            }

            lines.Insert(Math.Min(at, lines.Count), merged);
        }

        return lines;
    }

    /// <summary>Gets the quadrilateral of four corners in the order Acrobat reads it.</summary>
    /// <param name="topLeft">The top left corner.</param>
    /// <param name="topRight">The top right corner.</param>
    /// <param name="bottomRight">The bottom right corner.</param>
    /// <param name="bottomLeft">The bottom left corner.</param>
    /// <returns>The quadrilateral.</returns>
    public static Quad ToQuad(PagePoint topLeft, PagePoint topRight, PagePoint bottomRight, PagePoint bottomLeft) =>
        new(topLeft, topRight, bottomLeft, bottomRight);

    private static bool IsUsable(PageRect r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom)
        && r.Width > 0 && r.Height > 0;

    private static bool AreNeighbours(PageRect a, PageRect b)
    {
        var smaller = Math.Min(a.Height, b.Height);
        var overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        if (overlap < smaller * SameLineOverlap)
        {
            return false;
        }

        var gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
        return gap <= smaller * NeighbourGap;
    }
}
