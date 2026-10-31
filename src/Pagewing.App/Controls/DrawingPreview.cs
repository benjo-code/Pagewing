using Avalonia.Media;
using Pagewing.Core.Annotations;

namespace Pagewing.App.Controls;

/// <summary>
/// What a drawing tool shows while it is dragged (milestone 10): the page, the kind, the raw points of a freehand stroke or
/// the two ends of a shape, all in page points of the page as shown (so the stroke stays on the page when the view scrolls
/// or zooms meanwhile), the stroke width in points and the colour. One instance per document view, filled anew for every
/// stroke; the list of points is kept and reused.
/// </summary>
internal sealed class DrawingPreview
{
    private readonly List<DisplayPoint> _points = [];

    /// <summary>Gets the 0-based page the stroke is on.</summary>
    public int Page { get; private set; }

    /// <summary>Gets the kind of drawing: freehand, line, arrow, rectangle or ellipse.</summary>
    public AnnotationKind Kind { get; private set; }

    /// <summary>Gets the points of a freehand stroke (only <see cref="AnnotationKind.Ink"/>).</summary>
    public IReadOnlyList<DisplayPoint> Points => _points;

    /// <summary>Gets where the drag started: the tail of a line or arrow, a corner of a rectangle or ellipse.</summary>
    public DisplayPoint Anchor { get; private set; }

    /// <summary>Gets the other end of a line or arrow, or the opposite corner of a rectangle or ellipse (snapped with Shift).</summary>
    public DisplayPoint End { get; set; }

    /// <summary>Gets the stroke width in points.</summary>
    public double Width { get; private set; }

    /// <summary>Gets the colour of the stroke.</summary>
    public Color Color { get; private set; }

    /// <summary>Gets the smallest rectangle around the points of a freehand stroke (page points), kept up to date as points come.</summary>
    public (double Left, double Top, double Right, double Bottom) PointExtent { get; private set; }

    /// <summary>Starts a new stroke: the old points go, the list stays.</summary>
    public void Reset(int page, AnnotationKind kind, DisplayPoint anchor, double width, Color color)
    {
        _points.Clear();
        Page = page;
        Kind = kind;
        Anchor = anchor;
        End = anchor;
        Width = width;
        Color = color;
        PointExtent = (anchor.X, anchor.Y, anchor.X, anchor.Y);
        if (kind == AnnotationKind.Ink)
        {
            _points.Add(anchor);
        }
    }

    /// <summary>Adds a point to a freehand stroke.</summary>
    public void Add(DisplayPoint point)
    {
        _points.Add(point);
        var (left, top, right, bottom) = PointExtent;
        PointExtent = (Math.Min(left, point.X), Math.Min(top, point.Y), Math.Max(right, point.X), Math.Max(bottom, point.Y));
    }

    /// <summary>Moves the last point of a freehand stroke (a stroke that has all the points it may have).</summary>
    public void MoveLast(DisplayPoint point)
    {
        _points[^1] = point;
        var (left, top, right, bottom) = PointExtent;
        PointExtent = (Math.Min(left, point.X), Math.Min(top, point.Y), Math.Max(right, point.X), Math.Max(bottom, point.Y));
    }

    /// <summary>Forgets the points (the stroke ended); the list keeps its room for the next one.</summary>
    public void Clear() => _points.Clear();
}
