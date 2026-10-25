using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.Controls;

/// <summary>
/// The drawing layer of the document view: draws the stroke of a drawing tool while it is dragged, or the preview of the
/// shape (design system 7.7), and the selection of the select tool (its frame, its handles and, while it is dragged or
/// nudged, its new shape), in viewport coordinates like <see cref="DocumentCanvas"/>, over the pages and under the scroll
/// bars. It takes no input and is made on the first press of a drawing tool or the first selection (never in the startup
/// path or in the reading view).
/// </summary>
/// <remarks>
/// <para>
/// The layer draws nothing but the stroke, no background: Avalonia's compositor redraws the bounds of what a visual drew
/// before and after a change (the bounds of its render data), so a move redraws only the rectangle of the stroke and its new
/// segment, never the pages around it. <see cref="LastInvalidatedRect"/> is that rectangle as the layer computes it: the
/// union of the bounds of the stroke before and after the change, each widened by half the pen and 2 DIPs, within the
/// layer. The layer clips as <see cref="DocumentCanvas"/> clips its pages (its rounded corner, <see cref="DocumentCanvas.CornerRadius"/>).
/// </para>
/// <para>
/// The preview has the colour of the tool also on a dark page (it is not lightened as the dark page lightens the drawing,
/// <c>PdfiumDarkDrawings</c>); its pen is the stroke width times the zoom, at least 1 DIP. A pointer move allocates nothing
/// here: the points are in the reused list of <see cref="DrawingPreview"/>, and the pen is made again only for another colour
/// or width.
/// </para>
/// <para>
/// The selection is a dashed frame <see cref="FramePadding"/> outside the annotation in <c>accent.default</c> over a solid halo
/// in <c>accent.on</c>, and its handles are <see cref="HandleSize"/> squares (the two ends of a line or arrow: circles) in
/// the halo colour with an <c>accent.default</c> edge, so the state never rests on colour alone. The tokens come from the
/// light theme on a light page and from the dark theme on a dark page, whatever the theme of the app, so the frame and the
/// edges reach 3:1 against the page ground. The frame and each handle are also an element of the layer
/// (<see cref="AutomationProperties.NameProperty"/>, <see cref="AccessibilityView.Content"/>), a handle with the
/// <see cref="HandleHitSize"/> square the document view hits it in (<see cref="HandleAt"/>); they take no pointer themselves,
/// so the wheel and the scroll bars keep working over them.
/// </para>
/// </remarks>
internal sealed class DrawingOverlay : Control
{
    // The name of a handle with what it belongs to for screen readers, parsed once per language (UI thread only).
    private static (string Text, CompositeFormat Format)? _handleName;

    /// <summary>The side of a drawn handle in DIPs.</summary>
    internal const double HandleSize = 8;

    /// <summary>The side of the square a press takes a handle in, in DIPs (target size 24, WCAG 2.5.8).</summary>
    internal const double HandleHitSize = 24;

    /// <summary>How far the frame lies outside the annotation, in DIPs.</summary>
    internal const double FramePadding = 4;

    /// <summary>DIPs around the stroke that a change redraws besides half the pen (anti-aliasing).</summary>
    private const double InvalidationMargin = 2;

    private const double FrameThickness = 1.5;
    private const double HaloThickness = 3.5;

    private static readonly HandleKind[] HandleKinds = Enum.GetValues<HandleKind>();

    private readonly DocumentCanvas _canvas;
    private readonly Dictionary<HandleKind, Border> _handleElements = [];
    private DrawingPreview? _preview;
    private Rect _strokeBounds;
    private bool _hasStrokeBounds;
    private Pen? _pen;
    private SelectionView? _selection;
    private Border? _frameElement;
    private Pen? _selectionPreviewPen;
    private (Color Frame, Color Halo, Pen Dashed, Pen HaloPen, Pen Edge, IBrush Fill)? _selectionPens;

    static DrawingOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<DrawingOverlay>(false);
        FocusableProperty.OverrideDefaultValue<DrawingOverlay>(false);
        ClipToBoundsProperty.OverrideDefaultValue<DrawingOverlay>(true);
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

    /// <summary>Gets the selection shown, or <see langword="null"/>.</summary>
    internal SelectionView? Selection => _selection;

    /// <summary>Gets the new shape of the selection shown while it is dragged or nudged, or <see langword="null"/>.</summary>
    internal DrawingGeometry? SelectionPreview => _selection?.Preview;

    /// <summary>Gets the colour of the dashed frame and of the edges of the handles on the page shown (for tests).</summary>
    internal Color SelectionFrameColor => SelectionColors().Frame;

    /// <summary>Gets the colour of the halo under the frame and of the inside of the handles (for tests).</summary>
    internal Color SelectionHaloColor => SelectionColors().Halo;

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

    /// <summary>Shows <paramref name="selection"/> (or nothing): its frame, its handles and the shape it is dragged to.</summary>
    internal void ShowSelection(SelectionView? selection)
    {
        if (selection is null && _selection is null)
        {
            return;
        }

        _selection = selection;
        UpdateSelectionElements();
        InvalidateArrange();
        InvalidateVisual();
    }

    /// <summary>The view scrolled or zoomed: the selection is placed and drawn again.</summary>
    internal void RefreshSelection()
    {
        if (_selection is not null)
        {
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    /// <summary>Gets the handle of the selection whose <see cref="HandleHitSize"/> square holds <paramref name="viewportPoint"/>, the nearest if several do.</summary>
    internal HandleKind? HandleAt(Point viewportPoint)
    {
        HandleKind? found = null;
        var best = double.PositiveInfinity;

        // A box smaller than a handle would be all handles: inside it a press moves the drawing, its handles are taken from
        // outside only. An end of a line wins only nearer to it than to the middle of the line, so the middle of a short line
        // moves it.
        var frame = FrameRect();
        var small = frame is { } f && (f.Width < HandleHitSize || f.Height < HandleHitSize);
        var centres = HandleCentres().ToList();
        var ends = centres.Where(handle => handle.Kind is HandleKind.Start or HandleKind.End).Select(handle => handle.Centre).ToList();
        var middle = ends.Count == 2 ? new Point((ends[0].X + ends[1].X) / 2, (ends[0].Y + ends[1].Y) / 2) : default;
        foreach (var (kind, centre) in centres)
        {
            var dx = Math.Abs(viewportPoint.X - centre.X);
            var dy = Math.Abs(viewportPoint.Y - centre.Y);
            if (kind is HandleKind.Start or HandleKind.End)
            {
                if (ends.Count == 2 && Distance(viewportPoint, centre) >= Distance(viewportPoint, middle))
                {
                    continue;
                }
            }
            else if (small && frame!.Value.Contains(viewportPoint))
            {
                continue;
            }

            if (dx <= HandleHitSize / 2 && dy <= HandleHitSize / 2 && dx + dy < best)
            {
                best = dx + dy;
                found = kind;
            }
        }

        return found;
    }

    /// <summary>Gets whether <paramref name="viewportPoint"/> lies inside the frame of the selection (a press there moves it).</summary>
    internal bool FrameContains(Point viewportPoint) => FrameRect() is { } frame && frame.Contains(viewportPoint);

    public override void Render(DrawingContext context)
    {
        // Clipped as the canvas clips its pages (the layer lies over the canvas with the same bounds): a stroke on a page
        // taller than the view never paints over the status bar, the toolbar or a panel. A clip does not widen what the
        // compositor redraws (the bounds of the render data are those of what is drawn inside it).
        using var clip = context.PushClip(new RoundedRect(new Rect(Bounds.Size), _canvas.CornerRadius));
        if (_preview is { } preview && _canvas.PagePlacement(preview.Page) is { } placement)
        {
            RenderPreview(context, preview, placement.PageRect, placement.DipsPerPoint);
        }

        if (_selection is { } selection)
        {
            RenderSelection(context, selection);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in VisualChildren)
        {
            if (child is Layoutable element)
            {
                element.Measure(Size.Infinity);
            }
        }

        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var frame = FrameRect();
        if (_frameElement is { } element)
        {
            element.Arrange(frame ?? default);
        }

        var centres = HandleCentres().ToDictionary(handle => handle.Kind, handle => handle.Centre);
        foreach (var (kind, handle) in _handleElements)
        {
            handle.Arrange(centres.TryGetValue(kind, out var centre)
                ? new Rect(centre.X - (HandleHitSize / 2), centre.Y - (HandleHitSize / 2), HandleHitSize, HandleHitSize)
                : default);
        }

        return finalSize;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    private static Point ToView(Rect pageRect, double scale, DisplayPoint point) => new(pageRect.X + (point.X * scale), pageRect.Y + (point.Y * scale));

    private static Border AutomationElement(string name, string automationName, double size, AutomationControlType type)
    {
        var element = new Border { Name = name, IsHitTestVisible = false, Focusable = false, IsVisible = false };
        if (size > 0)
        {
            element.Width = size;
            element.Height = size;
        }

        AutomationProperties.SetName(element, automationName);
        AutomationProperties.SetAccessibilityView(element, AccessibilityView.Content);
        AutomationProperties.SetControlTypeOverride(element, type);
        return element;
    }

    private void RenderPreview(DrawingContext context, DrawingPreview preview, Rect pageRect, double scale)
    {
        var pen = PenFor(preview.Color, Math.Max(1, preview.Width * scale));
        Point View(DisplayPoint p) => ToView(pageRect, scale, p);

        switch (preview.Kind)
        {
            case AnnotationKind.Ink:
                var points = preview.Points;
                for (var i = 1; i < points.Count; i++)
                {
                    context.DrawLine(pen, View(points[i - 1]), View(points[i]));
                }

                break;
            case AnnotationKind.Line:
                context.DrawLine(pen, View(preview.Anchor), View(preview.End));
                break;
            case AnnotationKind.Arrow:
                foreach (var stroke in ArrowGeometry.Strokes(preview.Anchor, preview.End, preview.Width, head: true))
                {
                    for (var i = 1; i < stroke.Count; i++)
                    {
                        context.DrawLine(pen, View(stroke[i - 1]), View(stroke[i]));
                    }
                }

                break;
            case AnnotationKind.Rectangle or AnnotationKind.Ellipse:
                // The box is the outer edge of the stroke, as in the annotation.
                DrawBoxShape(context, pen, preview.Kind, new Rect(View(preview.Anchor), View(preview.End)));
                break;
        }
    }

    private static void DrawBoxShape(DrawingContext context, Pen pen, AnnotationKind kind, Rect outer)
    {
        var box = outer.Normalize().Deflate(pen.Thickness / 2);
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        if (kind == AnnotationKind.Rectangle)
        {
            context.DrawRectangle(null, pen, box);
        }
        else
        {
            context.DrawEllipse(null, pen, box.Center, box.Width / 2, box.Height / 2);
        }
    }

    private void RenderSelection(DrawingContext context, SelectionView selection)
    {
        if (_canvas.PagePlacement(selection.Page) is not { } placement || FrameRect() is not { } frame)
        {
            return;
        }

        var (pageRect, scale) = placement;
        if (selection.Preview is { } shape)
        {
            // The shape it is dragged or nudged to, in its colour; the page still shows it where it was until the change is made.
            var thickness = Math.Max(1, shape.Width * scale);
            if (_selectionPreviewPen is not { Brush: ISolidColorBrush brush } previewPen || brush.Color != selection.Color || previewPen.Thickness != thickness)
            {
                _selectionPreviewPen = previewPen = new Pen(new SolidColorBrush(selection.Color), thickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            }

            if (shape.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
            {
                DrawBoxShape(context, previewPen, shape.Kind, new Rect(ToView(pageRect, scale, new(shape.Box.Left, shape.Box.Top)), ToView(pageRect, scale, new(shape.Box.Right, shape.Box.Bottom))));
            }
            else
            {
                foreach (var stroke in shape.Strokes)
                {
                    for (var i = 1; i < stroke.Count; i++)
                    {
                        context.DrawLine(previewPen, ToView(pageRect, scale, stroke[i - 1]), ToView(pageRect, scale, stroke[i]));
                    }
                }
            }
        }

        var colors = SelectionColors();
        context.DrawRectangle(null, colors.HaloPen, frame);
        context.DrawRectangle(null, colors.Dashed, frame);
        foreach (var (kind, centre) in HandleCentres())
        {
            var square = new Rect(centre.X - (HandleSize / 2), centre.Y - (HandleSize / 2), HandleSize, HandleSize);
            if (kind is HandleKind.Start or HandleKind.End)
            {
                context.DrawEllipse(colors.Fill, colors.Edge, centre, HandleSize / 2, HandleSize / 2);
            }
            else
            {
                context.DrawRectangle(colors.Fill, colors.Edge, square);
            }
        }
    }

    /// <summary>The frame of the selection in viewport DIPs, <see cref="FramePadding"/> outside it; <see langword="null"/> without one.</summary>
    private Rect? FrameRect()
    {
        if (_selection is not { } selection || _canvas.PagePlacement(selection.Page) is not { } placement)
        {
            return null;
        }

        var (pageRect, scale) = placement;
        var frame = selection.Frame;
        return new Rect(ToView(pageRect, scale, new(frame.Left, frame.Top)), ToView(pageRect, scale, new(frame.Right, frame.Bottom)))
            .Normalize()
            .Inflate(FramePadding);
    }

    /// <summary>The middles of the handles in viewport DIPs: those of a box on the frame, the ends of a line where they are.</summary>
    private IEnumerable<(HandleKind Kind, Point Centre)> HandleCentres()
    {
        if (_selection is not { } selection || _canvas.PagePlacement(selection.Page) is not { } placement || FrameRect() is not { } frame)
        {
            yield break;
        }

        var (pageRect, scale) = placement;
        foreach (var handle in selection.Handles)
        {
            yield return (handle.Kind, handle.Kind switch
            {
                HandleKind.TopLeft => frame.TopLeft,
                HandleKind.Top => new Point(frame.Center.X, frame.Top),
                HandleKind.TopRight => frame.TopRight,
                HandleKind.Right => new Point(frame.Right, frame.Center.Y),
                HandleKind.BottomRight => frame.BottomRight,
                HandleKind.Bottom => new Point(frame.Center.X, frame.Bottom),
                HandleKind.BottomLeft => frame.BottomLeft,
                HandleKind.Left => new Point(frame.Left, frame.Center.Y),
                _ => ToView(pageRect, scale, handle.Position),
            });
        }
    }

    /// <summary>The frame and the handles as elements of the layer, for screen readers; made with the first selection.</summary>
    private void UpdateSelectionElements()
    {
        if (_selection is null && _frameElement is null)
        {
            return;
        }

        if (_frameElement is null)
        {
            _frameElement = AutomationElement("SelectionFrame", Strings.SelectionFrameName, 0, AutomationControlType.Group);
            AddElement(_frameElement);
            foreach (var kind in HandleKinds)
            {
                var handle = AutomationElement("SelectionHandle" + kind, Strings.SelectionHandleName(kind), HandleHitSize, AutomationControlType.Thumb);
                _handleElements[kind] = handle;
                AddElement(handle);
            }
        }

        _frameElement.IsVisible = _selection is not null;
        AutomationProperties.SetName(_frameElement, _selection?.Name ?? Strings.SelectionFrameName);
        AutomationProperties.SetHelpText(_frameElement, _selection?.LockReason);
        foreach (var (kind, handle) in _handleElements)
        {
            var shown = _selection?.Handles.Any(h => h.Kind == kind) == true;
            handle.IsVisible = shown;
            if (shown && _selection is { KindName: { } subject } selection)
            {
                // What the handle belongs to: "Top left handle, Rectangle, page 1".
                AutomationProperties.SetName(handle, string.Format(Strings.Culture, HandleNameFormat(), Strings.SelectionHandleName(kind), subject, selection.Page + 1));
            }
        }
    }

    /// <summary>The parsed name of a handle with what it belongs to, parsed again only when the language changed (UI thread only).</summary>
    private static CompositeFormat HandleNameFormat()
    {
        var text = Strings.SelectionHandleNameOf;
        if (_handleName is not { } known || !string.Equals(known.Text, text, StringComparison.Ordinal))
        {
            known = (text, CompositeFormat.Parse(text));
            _handleName = known;
        }

        return known.Format;
    }

    /// <summary>
    /// The colours of the selection: the tokens of the light theme on a light page, of the dark theme on a dark one, so the
    /// frame keeps 3:1 against the ground whatever the theme of the app (pages stay light in high contrast).
    /// </summary>
    private (Color Frame, Color Halo, Pen Dashed, Pen HaloPen, Pen Edge, IBrush Fill) SelectionColors()
    {
        var variant = _canvas.PageTheme == PageTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var frame = ColorOf("Pw.Color.Accent.Default", variant, Colors.Black);
        var halo = ColorOf("Pw.Color.Accent.On", variant, Colors.White);
        if (_selectionPens is { } known && known.Frame == frame && known.Halo == halo)
        {
            return known;
        }

        var frameBrush = new SolidColorBrush(frame);
        var haloBrush = new SolidColorBrush(halo);
        var pens = (
            frame,
            halo,
            new Pen(frameBrush, FrameThickness, new DashStyle([3, 2], 0)),
            new Pen(haloBrush, HaloThickness),
            new Pen(frameBrush, FrameThickness),
            (IBrush)haloBrush);
        _selectionPens = pens;
        return pens;
    }

    private void AddElement(Control element)
    {
        LogicalChildren.Add(element);
        VisualChildren.Add(element);
    }

    private Color ColorOf(string key, ThemeVariant variant, Color fallback) =>
        this.TryFindResource(key, variant, out var value) && value is Color color ? color : fallback;

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
        // Nothing outside the layer is drawn (see Render).
        LastInvalidatedRect = Bounds.Width > 0 && Bounds.Height > 0 ? rect.Intersect(new Rect(Bounds.Size)) : rect;
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

/// <summary>
/// What the drawing layer shows of a selection (milestone 10): the page, the rectangle the frame goes round (page points), the
/// handles, the shape it is dragged or nudged to (or <see langword="null"/>) in the colour of the annotation, and why it is
/// locked, for screen readers.
/// </summary>
internal sealed record SelectionView(int Page, PageRect Frame, IReadOnlyList<Handle> Handles, DrawingGeometry? Preview, Color Color, string? LockReason, string? Name = null, string? KindName = null);
