using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>
/// Finds the annotation a click of the select tool picks (milestone 10): the topmost selectable annotation under a point of
/// the page. Drawings lie over markups, and of two of the same group the later one in the page (the higher index, drawn
/// later) lies over the earlier one. A freehand stroke, a line and an arrow are hit near any of their segments, a rectangle
/// and an ellipse only near their outline (they are never filled), a markup inside its lines. Everything is in page points
/// from the top left of the page as shown.
/// </summary>
public static class DrawingHitTest
{
    /// <summary>The number of straight pieces an ellipse is measured against (an error below 0.13 % of its larger radius).</summary>
    private const int EllipseSegments = 64;

    /// <summary>
    /// Gets whether the select tool can select an annotation: a highlight, underline or strike-out or a drawing (freehand,
    /// rectangle, ellipse, line, arrow) that Pagewing may delete and comment on (<see cref="PdfAnnotation.Editable"/>, so no
    /// reply points at it). A drawing of another application that cannot be redrawn without loss is selectable too; only its
    /// handles are missing (<see cref="SelectionHandles.For"/>). Links, notes, stamps and form fields are not.
    /// </summary>
    /// <param name="annotation">The annotation.</param>
    /// <returns>Whether a click or Tab may select it.</returns>
    public static bool IsSelectable(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        return annotation.Editable && (IsMarkup(annotation.Kind) || IsDrawing(annotation.Kind));
    }

    /// <summary>Picks the topmost selectable annotation at <paramref name="point"/>.</summary>
    /// <param name="onPage">The annotations of the page (as read, in the order of the page).</param>
    /// <param name="point">The point in page points.</param>
    /// <param name="tolerance">How far from a stroke or outline a point still hits it, in page points (half the stroke width is added).</param>
    /// <returns>The annotation, or <see langword="null"/> when nothing selectable is there.</returns>
    public static PdfAnnotation? Pick(IReadOnlyList<PdfAnnotation> onPage, DisplayPoint point, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(onPage);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            return null;
        }

        tolerance = double.IsFinite(tolerance) && tolerance > 0 ? tolerance : 0;
        PdfAnnotation? drawing = null;
        PdfAnnotation? markup = null;
        foreach (var annotation in onPage)
        {
            if (!IsSelectable(annotation))
            {
                continue;
            }

            var isDrawing = IsDrawing(annotation.Kind);
            var best = isDrawing ? drawing : markup;
            if (best is not null && best.Index > annotation.Index)
            {
                continue;
            }

            if (!Hits(annotation, point, tolerance))
            {
                continue;
            }

            if (isDrawing)
            {
                drawing = annotation;
            }
            else
            {
                markup = annotation;
            }
        }

        return drawing ?? markup;
    }

    private static bool IsMarkup(AnnotationKind kind) => kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut;

    private static bool IsDrawing(AnnotationKind kind) =>
        kind is AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Ellipse or AnnotationKind.Line or AnnotationKind.Arrow;

    private static bool Hits(PdfAnnotation annotation, DisplayPoint point, double tolerance)
    {
        if (IsMarkup(annotation.Kind))
        {
            var lines = annotation.QuadRects.Count > 0 ? annotation.QuadRects : [annotation.Bounds];
            foreach (var line in lines)
            {
                if (Inside(line, point, tolerance))
                {
                    return true;
                }
            }

            return false;
        }

        if (annotation.Drawing is not { } geometry)
        {
            // A drawing whose shape could not be read: its bounds stand in for it.
            return Inside(annotation.Bounds, point, tolerance);
        }

        var reach = tolerance + (double.IsFinite(geometry.Width) && geometry.Width > 0 ? geometry.Width / 2 : 0);
        if (!Inside(geometry.Box, point, reach))
        {
            // Far from the drawing: its segments are not measured (a page may hold many long strokes).
            return false;
        }

        switch (geometry.Kind)
        {
            case AnnotationKind.Rectangle:
                {
                    // The box is the outer edge of the stroke: its middle runs half the width inside.
                    var line = Centreline(geometry);
                    return DistanceToSegment(point, new(line.Left, line.Top), new(line.Right, line.Top)) <= reach
                        || DistanceToSegment(point, new(line.Right, line.Top), new(line.Right, line.Bottom)) <= reach
                        || DistanceToSegment(point, new(line.Right, line.Bottom), new(line.Left, line.Bottom)) <= reach
                        || DistanceToSegment(point, new(line.Left, line.Bottom), new(line.Left, line.Top)) <= reach;
                }

            case AnnotationKind.Ellipse:
                {
                    var line = Centreline(geometry);
                    var cx = (line.Left + line.Right) / 2;
                    var cy = (line.Top + line.Bottom) / 2;
                    var rx = line.Width / 2;
                    var ry = line.Height / 2;
                    var previous = new DisplayPoint(cx + rx, cy);
                    for (var i = 1; i <= EllipseSegments; i++)
                    {
                        var angle = 2 * Math.PI * i / EllipseSegments;
                        var next = new DisplayPoint(cx + (rx * Math.Cos(angle)), cy + (ry * Math.Sin(angle)));
                        if (DistanceToSegment(point, previous, next) <= reach)
                        {
                            return true;
                        }

                        previous = next;
                    }

                    return false;
                }

            default:
                foreach (var stroke in geometry.Strokes)
                {
                    if (stroke.Count == 1 && Distance(point, stroke[0]) <= reach)
                    {
                        return true;
                    }

                    for (var i = 1; i < stroke.Count; i++)
                    {
                        if (DistanceToSegment(point, stroke[i - 1], stroke[i]) <= reach)
                        {
                            return true;
                        }
                    }
                }

                return false;
        }
    }

    /// <summary>The middle of the stroke of a rectangle or ellipse: its box made smaller by half the width (not below a line).</summary>
    private static PageRect Centreline(DrawingGeometry geometry)
    {
        var box = geometry.Box;
        var inset = double.IsFinite(geometry.Width) && geometry.Width > 0 ? geometry.Width / 2 : 0;
        var dx = Math.Min(inset, box.Width / 2);
        var dy = Math.Min(inset, box.Height / 2);
        return new PageRect(box.Left + dx, box.Top + dy, box.Right - dx, box.Bottom - dy);
    }

    private static bool Inside(PageRect rect, DisplayPoint point, double tolerance) =>
        point.X >= Math.Min(rect.Left, rect.Right) - tolerance && point.X <= Math.Max(rect.Left, rect.Right) + tolerance
        && point.Y >= Math.Min(rect.Top, rect.Bottom) - tolerance && point.Y <= Math.Max(rect.Top, rect.Bottom) + tolerance;

    private static double Distance(DisplayPoint a, DisplayPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static double DistanceToSegment(DisplayPoint p, DisplayPoint a, DisplayPoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (!(lengthSquared > 0))
        {
            return Distance(p, a);
        }

        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Distance(p, new DisplayPoint(a.X + (t * dx), a.Y + (t * dy)));
    }
}
