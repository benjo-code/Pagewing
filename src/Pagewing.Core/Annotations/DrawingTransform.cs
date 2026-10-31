using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>
/// Moves and resizes drawings. The results stay on the page and never fall below <see cref="MinimumSize"/>;
/// the page is the rectangle of the displayed page in page points (origin top left).
/// </summary>
public static class DrawingTransform
{
    /// <summary>The smallest width and height of the box of a resized drawing, in points.</summary>
    public const double MinimumSize = 4;

    /// <summary>
    /// Moves a drawing by <paramref name="dx"/> and <paramref name="dy"/>, shortened so that its box stays on the
    /// page. A shift that is not finite counts as zero; along an axis on which the box is larger than the page
    /// the drawing does not move.
    /// </summary>
    /// <param name="g">The drawing.</param>
    /// <param name="dx">The shift to the right in points.</param>
    /// <param name="dy">The shift downwards in points.</param>
    /// <param name="page">The displayed page.</param>
    /// <returns>The moved drawing; the shape and the width are unchanged.</returns>
    public static DrawingGeometry Move(DrawingGeometry g, double dx, double dy, PageRect page)
    {
        ArgumentNullException.ThrowIfNull(g);
        var shiftX = ClampShift(dx, page.Left - g.Box.Left, page.Right - g.Box.Right);
        var shiftY = ClampShift(dy, page.Top - g.Box.Top, page.Bottom - g.Box.Bottom);
        if (shiftX == 0 && shiftY == 0)
        {
            return g;
        }

        if (g.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
        {
            return DrawingGeometry.FromBox(
                g.Kind, new PageRect(g.Box.Left + shiftX, g.Box.Top + shiftY, g.Box.Right + shiftX, g.Box.Bottom + shiftY), g.Width);
        }

        return DrawingGeometry.FromStrokes(g.Kind, Map(g.Strokes, p => new DisplayPoint(p.X + shiftX, p.Y + shiftY)), g.Width);
    }

    /// <summary>
    /// Fits a drawing into a new box, for dragging a handle. The box is put right if it is inside out, cut to the
    /// page and made at least <see cref="MinimumSize"/> wide and high (at the edge of the page it moves inward). A
    /// freehand drawing is scaled in x and y separately. A line or arrow has its two ends mapped and its head
    /// rebuilt, so the head keeps its shape.
    /// </summary>
    /// <param name="g">The drawing.</param>
    /// <param name="newBox">The wanted box.</param>
    /// <param name="page">The displayed page.</param>
    /// <returns>The resized drawing; a box that is not finite leaves the drawing as it is.</returns>
    public static DrawingGeometry Resize(DrawingGeometry g, PageRect newBox, PageRect page)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (!IsFinite(newBox))
        {
            return g;
        }

        var left = Math.Max(Math.Min(newBox.Left, newBox.Right), page.Left);
        var top = Math.Max(Math.Min(newBox.Top, newBox.Bottom), page.Top);
        var right = Math.Min(Math.Max(newBox.Left, newBox.Right), page.Right);
        var bottom = Math.Min(Math.Max(newBox.Top, newBox.Bottom), page.Bottom);
        (left, right) = Fit(left, right, page.Left, page.Right);
        (top, bottom) = Fit(top, bottom, page.Top, page.Bottom);
        var box = new PageRect(left, top, right, bottom);

        if (g.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
        {
            return DrawingGeometry.FromBox(g.Kind, box, g.Width);
        }

        var old = g.Box;
        DisplayPoint Scale(DisplayPoint p) => new(
            old.Width > 0 ? box.Left + ((p.X - old.Left) / old.Width * box.Width) : box.Left + (box.Width / 2),
            old.Height > 0 ? box.Top + ((p.Y - old.Top) / old.Height * box.Height) : box.Top + (box.Height / 2));

        if (g.Kind is AnnotationKind.Line or AnnotationKind.Arrow && g.Strokes.Count > 0 && g.Strokes[0].Count == 2)
        {
            return EndsToGeometry(g, Scale(g.Strokes[0][0]), Scale(g.Strokes[0][1]));
        }

        return DrawingGeometry.FromStrokes(g.Kind, Map(g.Strokes, Scale), g.Width);
    }

    /// <summary>
    /// Moves one end of a line or an arrow and keeps the other. An arrow gets its head rebuilt for the new
    /// direction instead of being stretched.
    /// </summary>
    /// <param name="g">A line or an arrow.</param>
    /// <param name="start">Whether to move the start (the tail of an arrow) rather than the end (its tip).</param>
    /// <param name="to">The new position of the end.</param>
    /// <returns>The changed drawing.</returns>
    /// <exception cref="ArgumentException"><paramref name="g"/> is neither a line nor an arrow.</exception>
    public static DrawingGeometry MoveEndpoint(DrawingGeometry g, bool start, DisplayPoint to)
    {
        ArgumentNullException.ThrowIfNull(g);
        if (g.Kind is not (AnnotationKind.Line or AnnotationKind.Arrow) || g.Strokes.Count == 0 || g.Strokes[0].Count != 2)
        {
            throw new ArgumentException("Only a line or an arrow has ends to move.", nameof(g));
        }

        var shaft = g.Strokes[0];
        return start ? EndsToGeometry(g, to, shaft[1]) : EndsToGeometry(g, shaft[0], to);
    }

    /// <summary>
    /// Moves one end of a line or an arrow like <see cref="MoveEndpoint(DrawingGeometry, bool, DisplayPoint)"/>,
    /// with the new position cut to the page first.
    /// </summary>
    /// <param name="g">A line or an arrow.</param>
    /// <param name="start">Whether to move the start rather than the end.</param>
    /// <param name="to">The new position of the end.</param>
    /// <param name="page">The displayed page.</param>
    /// <returns>The changed drawing.</returns>
    /// <exception cref="ArgumentException"><paramref name="g"/> is neither a line nor an arrow.</exception>
    public static DrawingGeometry MoveEndpoint(DrawingGeometry g, bool start, DisplayPoint to, PageRect page) =>
        MoveEndpoint(g, start, new DisplayPoint(Math.Clamp(to.X, page.Left, page.Right), Math.Clamp(to.Y, page.Top, page.Bottom)));

    private static DrawingGeometry EndsToGeometry(DrawingGeometry g, DisplayPoint start, DisplayPoint end) =>
        g.Kind == AnnotationKind.Arrow
            ? DrawingGeometry.Arrow(start, end, g.Width)
            : DrawingGeometry.Line(start, end, g.Width);

    private static List<IReadOnlyList<DisplayPoint>> Map(IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, Func<DisplayPoint, DisplayPoint> map)
    {
        var result = new List<IReadOnlyList<DisplayPoint>>(strokes.Count);
        foreach (var stroke in strokes)
        {
            var points = new DisplayPoint[stroke.Count];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = map(stroke[i]);
            }

            result.Add(points);
        }

        return result;
    }

    // The part of the wanted shift that keeps the box on the page; none along an axis the box does not fit on.
    private static double ClampShift(double shift, double lowest, double highest)
    {
        if (!double.IsFinite(shift) || !double.IsFinite(lowest) || !double.IsFinite(highest) || lowest > highest)
        {
            return 0;
        }

        return Math.Clamp(shift, lowest, highest);
    }

    // Makes [low, high] at least MinimumSize long inside [pageLow, pageHigh]; it moves inward at the edge.
    private static (double Low, double High) Fit(double low, double high, double pageLow, double pageHigh)
    {
        var size = Math.Max(high - low, MinimumSize);
        if (low + size > pageHigh)
        {
            low = pageHigh - size;
        }

        low = Math.Max(low, pageLow);
        return (low, low + size);
    }

    private static bool IsFinite(PageRect r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom);
}
