using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>The handles of a selected drawing (milestone 10): the corners and edge middles of its box, or the two ends of a line or arrow.</summary>
public enum HandleKind
{
    /// <summary>The top left corner of the box.</summary>
    TopLeft,

    /// <summary>The middle of the top edge.</summary>
    Top,

    /// <summary>The top right corner.</summary>
    TopRight,

    /// <summary>The middle of the right edge.</summary>
    Right,

    /// <summary>The bottom right corner.</summary>
    BottomRight,

    /// <summary>The middle of the bottom edge.</summary>
    Bottom,

    /// <summary>The bottom left corner.</summary>
    BottomLeft,

    /// <summary>The middle of the left edge.</summary>
    Left,

    /// <summary>The start of a line, the tail of an arrow.</summary>
    Start,

    /// <summary>The end of a line, the tip of an arrow.</summary>
    End,
}

/// <summary>A handle of a selected drawing and where it is, in page points from the top left of the page as shown.</summary>
/// <param name="Kind">Which handle it is.</param>
/// <param name="Position">Its middle on the page.</param>
public sealed record Handle(HandleKind Kind, DisplayPoint Position);

/// <summary>
/// The handles of a selection and what dragging one does to the box (milestone 10, spec "Setzungen nach der Gegenlese" 2 to
/// 4). A freehand drawing, a rectangle and an ellipse have eight handles on their box (<see cref="DrawingGeometry.Box"/>);
/// freehand scales as a set of points, also unevenly. A line or arrow Pagewing recognises has its two ends. A markup and a
/// drawing that cannot be redrawn without loss (not <see cref="PdfAnnotation.GeometryEditable"/>) have none: they are
/// selected with a frame only.
/// </summary>
public static class SelectionHandles
{
    /// <summary>Gets the handles of <paramref name="annotation"/>, in the order of <see cref="HandleKind"/>.</summary>
    /// <param name="annotation">The selected annotation; its <see cref="PdfAnnotation.Drawing"/> places the handles.</param>
    /// <returns>Eight handles, two ends, or none.</returns>
    public static IReadOnlyList<Handle> For(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!annotation.GeometryEditable || annotation.Drawing is not { } geometry)
        {
            return [];
        }

        switch (geometry.Kind)
        {
            case AnnotationKind.Line or AnnotationKind.Arrow when geometry.Strokes.Count > 0 && geometry.Strokes[0].Count == 2:
                return [new Handle(HandleKind.Start, geometry.Strokes[0][0]), new Handle(HandleKind.End, geometry.Strokes[0][1])];
            case AnnotationKind.Ink or AnnotationKind.Rectangle or AnnotationKind.Ellipse:
                var box = geometry.Box;
                var middleX = (box.Left + box.Right) / 2;
                var middleY = (box.Top + box.Bottom) / 2;
                return
                [
                    new Handle(HandleKind.TopLeft, new DisplayPoint(box.Left, box.Top)),
                    new Handle(HandleKind.Top, new DisplayPoint(middleX, box.Top)),
                    new Handle(HandleKind.TopRight, new DisplayPoint(box.Right, box.Top)),
                    new Handle(HandleKind.Right, new DisplayPoint(box.Right, middleY)),
                    new Handle(HandleKind.BottomRight, new DisplayPoint(box.Right, box.Bottom)),
                    new Handle(HandleKind.Bottom, new DisplayPoint(middleX, box.Bottom)),
                    new Handle(HandleKind.BottomLeft, new DisplayPoint(box.Left, box.Bottom)),
                    new Handle(HandleKind.Left, new DisplayPoint(box.Left, middleY)),
                ];
            default:
                return [];
        }
    }

    /// <summary>
    /// Gets the box after dragging a corner or edge handle to <paramref name="to"/>: the edges of the handle go there, the
    /// opposite ones stay. A moving edge stops <see cref="DrawingTransform.MinimumSize"/> before the opposite one (the box never
    /// turns inside out) and at the edge of the page. With <paramref name="keepAspect"/> (Shift) the proportions stay: a corner
    /// scales from the opposite corner by the larger of the two drags, an edge also scales the other side around its middle
    /// line; the box then stops where the first edge of the page is met, and keeps the minimum size where it can.
    /// </summary>
    /// <param name="box">The box before the drag.</param>
    /// <param name="handle">A corner or edge handle (not <see cref="HandleKind.Start"/> or <see cref="HandleKind.End"/>).</param>
    /// <param name="to">Where the handle is dragged, in page points.</param>
    /// <param name="keepAspect">Whether to keep the proportions of the box.</param>
    /// <param name="page">The displayed page, in page points (origin top left).</param>
    /// <returns>The new box; <paramref name="box"/> put right if <paramref name="to"/> is not finite.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="handle"/> is an end of a line, not a handle of a box.</exception>
    public static PageRect ResizeBy(PageRect box, HandleKind handle, DisplayPoint to, bool keepAspect, PageRect page)
    {
        if (handle is HandleKind.Start or HandleKind.End || !Enum.IsDefined(handle))
        {
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Only a corner or an edge handle resizes a box.");
        }

        var left = Math.Min(box.Left, box.Right);
        var top = Math.Min(box.Top, box.Bottom);
        var right = Math.Max(box.Left, box.Right);
        var bottom = Math.Max(box.Top, box.Bottom);
        if (!double.IsFinite(to.X) || !double.IsFinite(to.Y))
        {
            return new PageRect(left, top, right, bottom);
        }

        var x = Math.Clamp(to.X, page.Left, Math.Max(page.Left, page.Right));
        var y = Math.Clamp(to.Y, page.Top, Math.Max(page.Top, page.Bottom));
        var moveLeft = handle is HandleKind.TopLeft or HandleKind.Left or HandleKind.BottomLeft;
        var moveRight = handle is HandleKind.TopRight or HandleKind.Right or HandleKind.BottomRight;
        var moveTop = handle is HandleKind.TopLeft or HandleKind.Top or HandleKind.TopRight;
        var moveBottom = handle is HandleKind.BottomLeft or HandleKind.Bottom or HandleKind.BottomRight;
        var width = right - left;
        var height = bottom - top;

        if (keepAspect && width > 0 && height > 0)
        {
            return KeepingAspect(left, top, right, bottom, x, y, moveLeft, moveRight, moveTop, moveBottom, page);
        }

        const double min = DrawingTransform.MinimumSize;
        if (moveLeft)
        {
            left = Math.Max(Math.Min(x, right - min), page.Left);
        }
        else if (moveRight)
        {
            right = Math.Min(Math.Max(x, left + min), page.Right);
        }

        if (moveTop)
        {
            top = Math.Max(Math.Min(y, bottom - min), page.Top);
        }
        else if (moveBottom)
        {
            bottom = Math.Min(Math.Max(y, top + min), page.Bottom);
        }

        return new PageRect(left, top, right, bottom);
    }

    private static PageRect KeepingAspect(
        double left, double top, double right, double bottom, double x, double y, bool moveLeft, bool moveRight, bool moveTop, bool moveBottom, PageRect page)
    {
        var width = right - left;
        var height = bottom - top;
        var horizontal = moveLeft || moveRight;
        var vertical = moveTop || moveBottom;

        // The fixed side of each axis that moves, and how far the drag wants the box to grow along it (a factor of the old size).
        var anchorX = moveLeft ? right : left;
        var anchorY = moveTop ? bottom : top;
        var wantX = horizontal ? (moveLeft ? anchorX - x : x - anchorX) / width : double.NegativeInfinity;
        var wantY = vertical ? (moveTop ? anchorY - y : y - anchorY) / height : double.NegativeInfinity;
        var scale = Math.Max(wantX, wantY);

        // The room on the page: from the fixed side for an axis that moves, around the middle line for one that does not.
        var middleX = (left + right) / 2;
        var middleY = (top + bottom) / 2;
        var roomX = horizontal
            ? (moveLeft ? anchorX - page.Left : page.Right - anchorX)
            : 2 * Math.Min(middleX - page.Left, page.Right - middleX);
        var roomY = vertical
            ? (moveTop ? anchorY - page.Top : page.Bottom - anchorY)
            : 2 * Math.Min(middleY - page.Top, page.Bottom - middleY);
        var largest = Math.Min(roomX / width, roomY / height);
        var smallest = Math.Max(DrawingTransform.MinimumSize / width, DrawingTransform.MinimumSize / height);
        scale = Math.Min(Math.Max(scale, smallest), largest);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            return new PageRect(left, top, right, bottom);
        }

        var newWidth = width * scale;
        var newHeight = height * scale;
        var newLeft = horizontal ? (moveLeft ? anchorX - newWidth : anchorX) : middleX - (newWidth / 2);
        var newTop = vertical ? (moveTop ? anchorY - newHeight : anchorY) : middleY - (newHeight / 2);
        return new PageRect(newLeft, newTop, newLeft + newWidth, newTop + newHeight);
    }
}
