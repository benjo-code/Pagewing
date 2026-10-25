using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>
/// The shape of a drawing annotation in page points as the user sees the page (origin top left). Freehand, line
/// and arrow are polylines (<see cref="Strokes"/>); rectangle and ellipse are given by <see cref="Box"/> alone.
/// The value is immutable; <see cref="DrawingTransform"/> makes changed copies.
/// </summary>
public sealed record DrawingGeometry
{
    private DrawingGeometry(AnnotationKind kind, IReadOnlyList<IReadOnlyList<DisplayPoint>> strokes, PageRect box, double width)
    {
        Kind = kind;
        Strokes = strokes;
        Box = box;
        Width = width;
    }

    /// <summary>Gets the kind: <see cref="AnnotationKind.Ink"/>, <see cref="AnnotationKind.Line"/>, <see cref="AnnotationKind.Arrow"/>, <see cref="AnnotationKind.Rectangle"/> or <see cref="AnnotationKind.Ellipse"/>.</summary>
    public AnnotationKind Kind { get; }

    /// <summary>Gets the strokes; empty for a rectangle and an ellipse.</summary>
    public IReadOnlyList<IReadOnlyList<DisplayPoint>> Strokes { get; }

    /// <summary>
    /// Gets the geometric extent: the smallest rectangle around all points of the strokes, or the rectangle itself
    /// for a rectangle and an ellipse. It does not include half the stroke width.
    /// </summary>
    public PageRect Box { get; }

    /// <summary>Gets the stroke width in points.</summary>
    public double Width { get; }

    /// <summary>
    /// Gets whether there is nothing to draw: no stroke with two points, all points on one spot, a box without
    /// width or height (rectangle, ellipse) or any value that is not finite. Such a drawing is never created.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            if (!IsFinite(Box) || !double.IsFinite(Width))
            {
                return true;
            }

            if (Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
            {
                return Box.Width <= 0 || Box.Height <= 0;
            }

            var hasSegment = false;
            foreach (var stroke in Strokes)
            {
                if (stroke.Count >= 2)
                {
                    hasSegment = true;
                    break;
                }
            }

            return !hasSegment || (Box.Width <= 0 && Box.Height <= 0);
        }
    }

    /// <summary>Creates a freehand drawing.</summary>
    /// <param name="strokes">The strokes; the points are copied.</param>
    /// <param name="width">The stroke width in points.</param>
    /// <returns>The drawing.</returns>
    public static DrawingGeometry Ink(IEnumerable<IReadOnlyList<DisplayPoint>> strokes, double width) =>
        FromStrokes(AnnotationKind.Ink, strokes, width);

    /// <summary>Creates a straight line: one stroke of two points.</summary>
    /// <param name="start">The first end.</param>
    /// <param name="end">The second end.</param>
    /// <param name="width">The stroke width in points.</param>
    /// <returns>The drawing.</returns>
    public static DrawingGeometry Line(DisplayPoint start, DisplayPoint end, double width) =>
        FromStrokes(AnnotationKind.Line, ArrowGeometry.Strokes(start, end, width, head: false), width);

    /// <summary>Creates an arrow pointing at <paramref name="end"/>: a shaft and a head polyline.</summary>
    /// <param name="start">The tail.</param>
    /// <param name="end">The tip.</param>
    /// <param name="width">The stroke width in points.</param>
    /// <returns>The drawing.</returns>
    public static DrawingGeometry Arrow(DisplayPoint start, DisplayPoint end, double width) =>
        FromStrokes(AnnotationKind.Arrow, ArrowGeometry.Strokes(start, end, width, head: true), width);

    /// <summary>Creates a rectangle outline.</summary>
    /// <param name="box">The outer edge; a box dragged inside out is put right.</param>
    /// <param name="width">The stroke width in points.</param>
    /// <returns>The drawing.</returns>
    public static DrawingGeometry Rectangle(PageRect box, double width) =>
        new(AnnotationKind.Rectangle, [], Normalize(box), width);

    /// <summary>Creates an ellipse outline.</summary>
    /// <param name="box">The bounding box of the ellipse; a box dragged inside out is put right.</param>
    /// <param name="width">The stroke width in points.</param>
    /// <returns>The drawing.</returns>
    public static DrawingGeometry Ellipse(PageRect box, double width) =>
        new(AnnotationKind.Ellipse, [], Normalize(box), width);

    internal static DrawingGeometry FromStrokes(AnnotationKind kind, IEnumerable<IReadOnlyList<DisplayPoint>> strokes, double width)
    {
        ArgumentNullException.ThrowIfNull(strokes);
        var copy = new List<IReadOnlyList<DisplayPoint>>();
        double left = double.PositiveInfinity, top = double.PositiveInfinity, right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        foreach (var stroke in strokes)
        {
            var points = new DisplayPoint[stroke.Count];
            for (var i = 0; i < points.Length; i++)
            {
                var p = stroke[i];
                points[i] = p;
                left = Math.Min(left, p.X);
                top = Math.Min(top, p.Y);
                right = Math.Max(right, p.X);
                bottom = Math.Max(bottom, p.Y);
            }

            copy.Add(points);
        }

        var box = double.IsPositiveInfinity(left) ? default : new PageRect(left, top, right, bottom);
        return new DrawingGeometry(kind, copy, box, width);
    }

    internal static DrawingGeometry FromBox(AnnotationKind kind, PageRect box, double width) =>
        new(kind, [], Normalize(box), width);

    private static PageRect Normalize(PageRect box) => new(
        Math.Min(box.Left, box.Right), Math.Min(box.Top, box.Bottom), Math.Max(box.Left, box.Right), Math.Max(box.Top, box.Bottom));

    private static bool IsFinite(PageRect r) =>
        double.IsFinite(r.Left) && double.IsFinite(r.Top) && double.IsFinite(r.Right) && double.IsFinite(r.Bottom);
}
