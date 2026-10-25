using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Controls;

/// <summary>
/// The drawing layer of the document view: draws the stroke of a drawing tool while it is dragged, or the preview of the
/// shape (design system 7.7), in viewport coordinates like <see cref="DocumentCanvas"/>, over the pages and under the scroll
/// bars. It takes no input and is made on the first press of a drawing tool (never in the startup path or in the reading
/// view).
/// </summary>
/// <remarks>
/// <para>
/// The layer draws nothing but the stroke, no background: Avalonia's compositor redraws the bounds of what a visual drew
/// before and after a change (the bounds of its render data), so a move redraws only the rectangle of the stroke and its new
/// segment, never the pages around it. <see cref="LastInvalidatedRect"/> is that rectangle as the layer computes it: the
/// union of the bounds of the stroke before and after the change, each widened by half the pen and 2 DIPs.
/// </para>
/// <para>
/// The preview has the colour of the tool also on a dark page (it is not lightened as the dark page lightens the drawing,
/// <c>PdfiumDarkDrawings</c>); its pen is the stroke width times the zoom, at least 1 DIP. A pointer move allocates nothing
/// here: the points are in the reused list of <see cref="DrawingPreview"/>, and the pen is made again only for another colour
/// or width.
/// </para>
/// </remarks>
internal sealed class DrawingOverlay : Control
{
    /// <summary>DIPs around the stroke that a change redraws besides half the pen (anti-aliasing).</summary>
    private const double InvalidationMargin = 2;

    private readonly DocumentCanvas _canvas;
    private DrawingPreview? _preview;
    private Rect _strokeBounds;
    private bool _hasStrokeBounds;
    private Pen? _pen;

    static DrawingOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<DrawingOverlay>(false);
        FocusableProperty.OverrideDefaultValue<DrawingOverlay>(false);
    }

    /// <summary>Initializes a layer that places its stroke on the pages of <paramref name="canvas"/>.</summary>
    public DrawingOverlay(DocumentCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvas = canvas;
    }

    /// <summary>Gets whether a stroke is shown.</summary>
    internal bool HasPreview => _preview is not null;

    /// <summary>Gets the rectangle (viewport DIPs) the last change of the layer redraws (see the remarks).</summary>
    internal Rect LastInvalidatedRect { get; private set; }

    /// <summary>Gets how often the layer asked to be redrawn (for tests).</summary>
    internal int InvalidationCount { get; private set; }

    /// <summary>Shows <paramref name="preview"/> (the stroke that starts).</summary>
    internal void Show(DrawingPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        _preview = preview;
        Refresh();
    }

    /// <summary>The stroke changed, or the view scrolled or zoomed: draws it again.</summary>
    internal void Refresh()
    {
        if (_preview is not { } preview)
        {
            return;
        }

        if (BoundsOf(preview) is not { } bounds)
        {
            Clear();
            return;
        }

        Invalidate(_hasStrokeBounds ? _strokeBounds.Union(bounds) : bounds);
        _strokeBounds = bounds;
        _hasStrokeBounds = true;
    }

    /// <summary>The stroke ended or was dropped: it goes from the layer.</summary>
    internal void Clear()
    {
        if (_preview is null)
        {
            return;
        }

        _preview = null;
        if (_hasStrokeBounds)
        {
            Invalidate(_strokeBounds);
        }

        _hasStrokeBounds = false;
    }

    public override void Render(DrawingContext context)
    {
        if (_preview is not { } preview || _canvas.PagePlacement(preview.Page) is not { } placement)
        {
            return;
        }

        var (pageRect, scale) = placement;
        var pen = PenFor(preview.Color, Math.Max(1, preview.Width * scale));
        Point ToView(DisplayPoint p) => new(pageRect.X + (p.X * scale), pageRect.Y + (p.Y * scale));
        switch (preview.Kind)
        {
            case AnnotationKind.Ink:
                var points = preview.Points;
                for (var i = 1; i < points.Count; i++)
                {
                    context.DrawLine(pen, ToView(points[i - 1]), ToView(points[i]));
                }

                break;
            case AnnotationKind.Line:
                context.DrawLine(pen, ToView(preview.Anchor), ToView(preview.End));
                break;
            case AnnotationKind.Arrow:
                foreach (var stroke in ArrowGeometry.Strokes(preview.Anchor, preview.End, preview.Width, head: true))
                {
                    for (var i = 1; i < stroke.Count; i++)
                    {
                        context.DrawLine(pen, ToView(stroke[i - 1]), ToView(stroke[i]));
                    }
                }

                break;
            case AnnotationKind.Rectangle or AnnotationKind.Ellipse:
                // The box is the outer edge of the stroke, as in the annotation.
                var box = new Rect(ToView(preview.Anchor), ToView(preview.End)).Deflate(pen.Thickness / 2);
                if (box.Width <= 0 || box.Height <= 0)
                {
                    break;
                }

                if (preview.Kind == AnnotationKind.Rectangle)
                {
                    context.DrawRectangle(null, pen, box);
                }
                else
                {
                    context.DrawEllipse(null, pen, box.Center, box.Width / 2, box.Height / 2);
                }

                break;
        }
    }

    /// <summary>The rectangle of the stroke in viewport DIPs, widened by half the pen and the margin; <see langword="null"/> off the document.</summary>
    private Rect? BoundsOf(DrawingPreview preview)
    {
        if (_canvas.PagePlacement(preview.Page) is not { } placement)
        {
            return null;
        }

        var (pageRect, scale) = placement;
        double left, top, right, bottom;
        if (preview.Kind == AnnotationKind.Ink)
        {
            (left, top, right, bottom) = preview.PointExtent;
        }
        else
        {
            left = Math.Min(preview.Anchor.X, preview.End.X);
            top = Math.Min(preview.Anchor.Y, preview.End.Y);
            right = Math.Max(preview.Anchor.X, preview.End.X);
            bottom = Math.Max(preview.Anchor.Y, preview.End.Y);
            if (preview.Kind == AnnotationKind.Arrow)
            {
                // The head reaches at most its length beyond the tip.
                var head = ArrowGeometry.HeadLength(preview.Width);
                (left, top, right, bottom) = (left - head, top - head, right + head, bottom + head);
            }
        }

        var thickness = Math.Max(1, preview.Width * scale);
        return new Rect(pageRect.X + (left * scale), pageRect.Y + (top * scale), (right - left) * scale, (bottom - top) * scale)
            .Inflate((thickness / 2) + InvalidationMargin);
    }

    private void Invalidate(Rect rect)
    {
        LastInvalidatedRect = rect;
        InvalidationCount++;
        InvalidateVisual();
    }

    private Pen PenFor(Color color, double thickness)
    {
        if (_pen is { Brush: ISolidColorBrush brush } pen && brush.Color == color && pen.Thickness == thickness)
        {
            return pen;
        }

        return _pen = new Pen(new SolidColorBrush(color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    }
}
