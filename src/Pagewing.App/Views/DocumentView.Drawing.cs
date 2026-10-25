using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// Drawing with a drawing tool of the mode "Comment" (milestone 10, design system 7.7): with freehand, rectangle, oval, line
/// or arrow a press of the left button with the mouse or the pen on a page starts a stroke on that page, the drag shows it
/// on the drawing layer (<see cref="DrawingOverlay"/>), and the release makes one annotation of it
/// (<see cref="TabViewModel.ApplyDrawingAsync"/>; PDFium runs only then). A finger (<see cref="PointerType.Touch"/>) does
/// not draw: it scrolls and zooms as it does without the tool. Shift snaps a line or an arrow to multiples of 45 degrees and
/// makes a rectangle a square and an oval a circle. A drawing tool owns the press: text is not selected meanwhile.
/// </summary>
/// <remarks>
/// <para>
/// A stroke stays on the page it started on: its points are page points of that page (so scrolling or zooming during the
/// drag keeps it in place), and a drag beyond the page keeps drawing along its edge. A click (a press that moves less than
/// <see cref="ClickSlop"/>) makes nothing, and neither does a stroke without extent. Esc, a lost pointer capture, another
/// tab, tool or document, a card that asks about the tab and a window that loses the activation drop the stroke without a
/// trace on the history.
/// </para>
/// <para>
/// Nothing of this runs before the first sharp page or while the tab cannot take an edit (<see cref="TabViewModel.CanDraw"/>):
/// a press then starts nothing. The layer is made on the first press of a drawing tool, never in the startup path or in the
/// reading view.
/// </para>
/// </remarks>
public sealed partial class DocumentView
{
    /// <summary>The smallest distance in points between two points of a freehand stroke; closer points are left out.</summary>
    private const double MinPointDistance = 0.25;

    /// <summary>The tolerance in points with which a freehand stroke is thinned before it is smoothed (see DECISIONS).</summary>
    private const double SimplifyTolerance = 0.5;

    /// <summary>The angle step of Shift for a line or an arrow, in degrees.</summary>
    private const double SnapStepDegrees = 45;

    /// <summary>
    /// The most points a freehand stroke collects (about a minute of a pen at 120 Hz); later points only move its end. Keeps
    /// thinning and the file within bounds whatever the pointer sends.
    /// </summary>
    internal const int MaxStrokePoints = 20_000;

    private DrawingOverlay? _drawingOverlay;
    private DrawingPreview? _drawingPreview;
    private DrawGesture? _gesture;
    private WindowBase? _gestureWindow;

    // The tab whose refused drawings the view reports (DrawingRefused), from its first stroke here until the view leaves it.
    private TabViewModel? _drawingTab;
    private Cursor? _drawCursor;

    /// <summary>Gets the drawing layer, or <see langword="null"/> before the first press of a drawing tool (for tests).</summary>
    internal DrawingOverlay? DrawingOverlay => _drawingOverlay;

    /// <summary>Gets whether a stroke of a drawing tool is being dragged.</summary>
    internal bool IsDrawing => _gesture is not null;

    /// <summary>Gets the cursor of a drawing tool over a page (crosshair).</summary>
    internal Cursor DrawCursor => _drawCursor ??= new Cursor(StandardCursorType.Cross);

    /// <summary>The drawing tool a press on a page draws with, or <see langword="null"/> (no tool, a text markup tool, the select tool).</summary>
    private static AnnotationKind? DrawsWith(TabViewModel tab) =>
        tab is { IsAnnotating: true, ActiveTool: { } tool } && TabViewModel.IsDrawKind(tool) ? tool : null;

    private static double PointDistance(DisplayPoint a, DisplayPoint b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>
    /// A press arrives: with a drawing tool it belongs to the tool (no text selection, no link), and a press of the left
    /// button with the mouse or the pen on a page starts a stroke when the tab takes an edit. A finger is left to the scroll
    /// viewer (not captured, so it scrolls).
    /// </summary>
    /// <returns>Whether the drawing tool took the press.</returns>
    private bool PressForDrawing(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        if (_viewModel is not { } tab || DrawsWith(tab) is not { } kind)
        {
            return false;
        }

        if (e.Pointer.Type == PointerType.Touch || properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || _layout is null || _gesture is not null || IsInScrollBar(e.Source) || !tab.CanDraw)
        {
            return true;
        }

        var position = e.GetPosition(PageCanvas);
        if (PageCanvas.PagePointAt(position) is not { } hit || !IsOnPage(hit.Page, hit.Position))
        {
            return true;
        }

        var start = new DisplayPoint(hit.Position.X, hit.Position.Y);
        var preview = _drawingPreview ??= new DrawingPreview();
        var color = tab.DrawColorOf(kind);
        preview.Reset(hit.Page, kind, start, tab.StrokeWidthOf(kind), Color.FromRgb(color.R, color.G, color.B));
        _gesture = new DrawGesture(tab, e.Pointer, start);
        e.Pointer.Capture(this);

        // Only now, never at connect: nothing of the drawing costs anything before the first stroke.
        tab.CancelDrawingGesture = EndGesture;
        if (!ReferenceEquals(_drawingTab, tab))
        {
            DisconnectDrawing();
            _drawingTab = tab;
            tab.DrawingRefused += ShowHint;
        }

        EnsureDrawingOverlay().Show(preview);
        if (_topLevel is WindowBase window)
        {
            _gestureWindow = window;
            window.Deactivated += OnGestureWindowDeactivated;
        }

        SetCursor(CursorKind.Draw);
        return true;
    }

    /// <summary>A move of the pointer of the stroke extends it; other pointers do nothing meanwhile.</summary>
    /// <returns>Whether a stroke is being dragged (the move is the tool's).</returns>
    private bool MoveForDrawing(PointerEventArgs e)
    {
        if (_gesture is not { } gesture)
        {
            return false;
        }

        if (ReferenceEquals(e.Pointer, gesture.Pointer))
        {
            UpdateGesture(gesture, e.GetPosition(PageCanvas), e.KeyModifiers);
        }

        return true;
    }

    /// <summary>The release of the pointer of the stroke: it becomes a drawing, unless it was a click or has no extent.</summary>
    /// <returns>Whether a stroke was being dragged (the release is the tool's).</returns>
    private bool ReleaseForDrawing(PointerReleasedEventArgs e)
    {
        if (_gesture is not { } gesture || _drawingPreview is not { } preview)
        {
            return false;
        }

        if (!ReferenceEquals(e.Pointer, gesture.Pointer))
        {
            return true;
        }

        var position = e.GetPosition(PageCanvas);
        UpdateGesture(gesture, position, e.KeyModifiers);
        var geometry = gesture.Moved ? GeometryOf(preview) : null;
        var page = preview.Page;
        EndGesture();
        if (geometry is { IsEmpty: false } && ReferenceEquals(gesture.Tab, _viewModel) && DrawsWith(gesture.Tab) == geometry.Kind)
        {
            _ = gesture.Tab.ApplyDrawingAsync(page, geometry);
        }

        UpdateCursor(position);
        return true;
    }

    /// <summary>
    /// Drops the stroke that is being dragged, if there is one: it goes from the layer, the capture is released and nothing
    /// is made. Esc, a lost capture, another tab, tool or document.
    /// </summary>
    /// <returns>Whether there was a stroke.</returns>
    private bool EndGesture()
    {
        if (_gesture is not { } gesture)
        {
            return false;
        }

        // First, so the capture lost below finds nothing to end.
        _gesture = null;
        gesture.Tab.CancelDrawingGesture = null;
        _drawingPreview?.Clear();
        _drawingOverlay?.Clear();
        if (_gestureWindow is { } window)
        {
            window.Deactivated -= OnGestureWindowDeactivated;
            _gestureWindow = null;
        }

        if (ReferenceEquals(gesture.Pointer.Captured, this))
        {
            gesture.Pointer.Capture(null);
        }

        return true;
    }

    /// <summary>After a change of the tab: a stroke whose tool, mode or tab is gone, or that the tab cannot take any more, is dropped.</summary>
    private void CheckGesture(string? property)
    {
        if (_gesture is not { } gesture)
        {
            return;
        }

        if (_viewModel is not { } tab || !ReferenceEquals(tab, gesture.Tab) || DrawsWith(tab) != _drawingPreview?.Kind || !tab.CanDraw
            || property == nameof(TabViewModel.LayoutMode))
        {
            EndGesture();
        }
    }

    /// <summary>The view scrolled or zoomed during a stroke: the layer draws it at the new place of its page.</summary>
    private void FollowGestureWithViewport()
    {
        if (_gesture is not null)
        {
            _drawingOverlay?.Refresh();
        }
    }

    /// <summary>The view leaves the tab: its refused drawings are no longer shown here.</summary>
    private void DisconnectDrawing()
    {
        if (_drawingTab is { } tab)
        {
            tab.DrawingRefused -= ShowHint;
            _drawingTab = null;
        }
    }

    private void OnGestureWindowDeactivated(object? sender, EventArgs e) => EndGesture();

    private DrawingOverlay EnsureDrawingOverlay()
    {
        if (_drawingOverlay is null)
        {
            // Right above the pages, below the scroll viewer and its bars; it takes no input.
            _drawingOverlay = new DrawingOverlay(PageCanvas) { Name = "DrawingOverlay" };
            DocumentLayers.Children.Insert(DocumentLayers.Children.IndexOf(PageCanvas) + 1, _drawingOverlay);
        }

        return _drawingOverlay;
    }

    /// <summary>Moves the stroke to the pointer: a new point of a freehand stroke, the other end of a shape (snapped with Shift).</summary>
    private void UpdateGesture(DrawGesture gesture, Point position, KeyModifiers modifiers)
    {
        if (_drawingPreview is not { } preview || _layout is not { } layout || PageCanvas.PointOnPage(preview.Page, position) is not { } onPage)
        {
            return;
        }

        var size = layout.GetPageSize(preview.Page);
        var point = new DisplayPoint(Math.Clamp(onPage.X, 0, size.Width), Math.Clamp(onPage.Y, 0, size.Height));
        if (!gesture.Moved && PointDistance(point, gesture.Start) * layout.Zoom * Core.Viewing.DocumentLayout.DipsPerPoint >= ClickSlop)
        {
            gesture.Moved = true;
        }

        var snap = (modifiers & KeyModifiers.Shift) != 0;
        switch (preview.Kind)
        {
            case AnnotationKind.Ink:
                var points = preview.Points;
                if (PointDistance(point, points[^1]) < MinPointDistance)
                {
                    return;
                }

                if (points.Count < MaxStrokePoints)
                {
                    preview.Add(point);
                }
                else
                {
                    preview.MoveLast(point);
                }

                break;
            case AnnotationKind.Line or AnnotationKind.Arrow:
                preview.End = KeepOnPage(preview.Anchor, snap ? ArrowGeometry.SnapToAngle(preview.Anchor, point, SnapStepDegrees) : point, size.Width, size.Height);
                break;
            default:
                preview.End = snap ? SquareCorner(preview.Anchor, point, size.Width, size.Height) : point;
                break;
        }

        _drawingOverlay?.Refresh();
    }

    /// <summary>The drawing of the stroke: the width of the tool's style, a freehand stroke thinned and smoothed.</summary>
    private static DrawingGeometry GeometryOf(DrawingPreview preview)
    {
        var width = preview.Width;
        return preview.Kind switch
        {
            AnnotationKind.Ink => DrawingGeometry.Ink([StrokeSimplifier.Smooth(StrokeSimplifier.Simplify(preview.Points, SimplifyTolerance))], width),
            AnnotationKind.Line => DrawingGeometry.Line(preview.Anchor, preview.End, width),
            AnnotationKind.Arrow => DrawingGeometry.Arrow(preview.Anchor, preview.End, width),
            AnnotationKind.Ellipse => DrawingGeometry.Ellipse(BoxOf(preview.Anchor, preview.End), width),
            _ => DrawingGeometry.Rectangle(BoxOf(preview.Anchor, preview.End), width),
        };
    }

    private static PageRect BoxOf(DisplayPoint a, DisplayPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    /// <summary>Shortens the segment from <paramref name="anchor"/> to <paramref name="end"/> along its direction so that it ends on the page.</summary>
    private static DisplayPoint KeepOnPage(DisplayPoint anchor, DisplayPoint end, double width, double height)
    {
        var dx = end.X - anchor.X;
        var dy = end.Y - anchor.Y;
        var t = 1.0;
        t = Math.Min(t, Limit(anchor.X, dx, width));
        t = Math.Min(t, Limit(anchor.Y, dy, height));
        return t >= 1 ? end : new DisplayPoint(anchor.X + (dx * t), anchor.Y + (dy * t));

        static double Limit(double from, double delta, double max) =>
            delta > 0 && from + delta > max ? (max - from) / delta
            : delta < 0 && from + delta < 0 ? -from / delta
            : 1;
    }

    /// <summary>The corner opposite <paramref name="anchor"/> of the square of Shift (<see cref="ArrowGeometry.SquareBox"/>), as large as fits on the page.</summary>
    private static DisplayPoint SquareCorner(DisplayPoint anchor, DisplayPoint point, double width, double height)
    {
        var box = ArrowGeometry.SquareBox(anchor, point);
        var right = point.X >= anchor.X;
        var down = point.Y >= anchor.Y;
        var side = Math.Min(box.Width, Math.Min(right ? width - anchor.X : anchor.X, down ? height - anchor.Y : anchor.Y));
        return new DisplayPoint(right ? anchor.X + side : anchor.X - side, down ? anchor.Y + side : anchor.Y - side);
    }

    /// <summary>A stroke of a drawing tool, from the press until the release (the points are in <see cref="DrawingPreview"/>).</summary>
    private sealed class DrawGesture(TabViewModel tab, IPointer pointer, DisplayPoint start)
    {
        /// <summary>The tab the stroke is drawn in.</summary>
        public TabViewModel Tab { get; } = tab;

        /// <summary>The pointer that draws (captured by the view).</summary>
        public IPointer Pointer { get; } = pointer;

        /// <summary>Where the press went down, in page points.</summary>
        public DisplayPoint Start { get; } = start;

        /// <summary>Whether the pointer moved by <see cref="ClickSlop"/> or more since the press: a click makes nothing.</summary>
        public bool Moved { get; set; }
    }
}
