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
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;

namespace Pagewing.App.Controls;

/// <summary>
/// The marks to redact on the pages (milestone 14, design spec 5.6): a red tint with a hatch and an edge, so a mark is never told
/// by colour alone, for every rectangle of every mark of the plan on the pages in view, and for the selected marks a dashed frame
/// with, for one selected area mark, eight handles. It draws in viewport coordinates like <see cref="DocumentCanvas"/>, over the
/// pages and under the scroll bars, takes no input, and is made when the mode starts (never in the startup path or in the
/// reading view).
/// </summary>
/// <remarks>
/// <para>
/// The layer is the app's own and nothing else: it is a sibling of the page canvas, not a part of it, and PDFium never sees it, so
/// neither the pages in the cache, nor the thumbnails, nor a print job, nor the check after applying contain a mark. A mark is a
/// plan, not a part of the document.
/// </para>
/// <para>
/// A mark is a rectangle in page points as the page is shown (origin top left, rotation and CropBox already applied by the layout
/// of the view), so a point is the page's rectangle in the viewport plus the point times the DIPs per point: no rotation or box
/// maths of its own (the one source of those is <c>FPDF_PageToDevice</c>). Only the pages in view are drawn, through an index of
/// the marks by page that is rebuilt when the plan changes, so a plan of 10,000 marks costs a frame no more than a few.
/// </para>
/// <para>
/// The colours: <c>Pw.Color.Redact.Edge</c> for the edge and the hatch and <c>Pw.Color.Redact.Fill</c> for the tint, the same on a
/// light and on a dark page (3:1 against both, <c>TokenTests</c>); the selection takes the tokens of the light theme on a light
/// page and of the dark theme on a dark page, as <see cref="DrawingOverlay"/> does. The frame and the handles are also elements of
/// the layer for screen readers (named by kind and page, never by the text of a mark), a handle with the square the document view
/// hits it in (<see cref="HandleAt"/>).
/// </para>
/// </remarks>
internal sealed class RedactionOverlay : Control
{
    /// <summary>The distance in DIPs of two lines of the hatch.</summary>
    internal const double HatchSpacing = 8;

    /// <summary>The thickness in DIPs of the edge of a mark.</summary>
    internal const double EdgeThickness = 2;

    /// <summary>The most selected marks that are elements for screen readers (the rest is drawn and counted in the toolbar).</summary>
    internal const int MaxAutomationFrames = 16;

    /// <summary>The most frames of selected marks that are drawn.</summary>
    internal const int MaxSelectedFrames = 64;

    private const double FrameThickness = 1.5;
    private const double HaloThickness = 3.5;
    private const double HatchThickness = 1;

    private static readonly HandleKind[] HandleKinds =
    [
        HandleKind.TopLeft, HandleKind.Top, HandleKind.TopRight, HandleKind.Right,
        HandleKind.BottomRight, HandleKind.Bottom, HandleKind.BottomLeft, HandleKind.Left,
    ];

    private static readonly Dictionary<int, List<RedactionMark>> NoMarks = [];

    private readonly DocumentCanvas _canvas;
    private readonly List<Border> _frameElements = [];
    private readonly Dictionary<HandleKind, Border> _handleElements = [];
    private readonly List<(Rect Frame, bool Single)> _frames = [];
    private RedactionPlan? _plan;
    private Dictionary<int, List<RedactionMark>> _byPage = NoMarks;
    private IReadOnlySet<Guid> _selected = new HashSet<Guid>();

    // The selected marks that are in the plan, at most MaxSelectedFrames (a selection of thousands is not drawn frame by frame).
    private List<RedactionMark> _selectedMarks = [];
    private RedactionDragView? _drag;
    private (Color Edge, Color Fill, Pen EdgePen, Pen HatchPen, Pen DashedPen, IBrush FillBrush)? _markPens;
    private (Color Frame, Color Halo, Pen Dashed, Pen HaloPen, Pen Edge, IBrush Fill)? _selectionPens;

    static RedactionOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RedactionOverlay>(false);
        FocusableProperty.OverrideDefaultValue<RedactionOverlay>(false);
        ClipToBoundsProperty.OverrideDefaultValue<RedactionOverlay>(true);
    }

    /// <summary>Initializes a layer that places the marks on the pages of <paramref name="canvas"/>.</summary>
    public RedactionOverlay(DocumentCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        _canvas = canvas;
    }

    /// <summary>Gets the plan shown, or <see langword="null"/>.</summary>
    internal RedactionPlan? Plan => _plan;

    /// <summary>Gets the area or the new place of a mark being dragged, or <see langword="null"/>.</summary>
    internal RedactionDragView? Drag => _drag;

    /// <summary>Gets how many times the plan was indexed (for tests: a scroll or a change of the selection indexes nothing).</summary>
    internal int IndexCount { get; private set; }

    /// <summary>Gets how many rectangles the last frame drew (for tests: only the pages in view are drawn).</summary>
    internal int LastDrawnRectangles { get; private set; }

    /// <summary>Gets the colour of the dashed frame of the selection on the page shown (for tests).</summary>
    internal Color SelectionFrameColor => SelectionColors().Frame;

    /// <summary>Gets the colour of the edge and the hatch of a mark (for tests).</summary>
    internal Color EdgeColor => MarkPens().Edge;

    /// <summary>Gets the colour of the tint inside a mark (for tests).</summary>
    internal Color FillColor => MarkPens().Fill;

    /// <summary>Shows the marks of <paramref name="plan"/> (or none): indexes them by page when the plan is another one.</summary>
    internal void SetPlan(RedactionPlan? plan)
    {
        if (ReferenceEquals(plan, _plan))
        {
            return;
        }

        _plan = plan;
        _byPage = plan is null || plan.IsEmpty ? NoMarks : Index(plan);
        IndexCount++;
        RebuildSelected();
        UpdateElements();
        InvalidateAll();
    }

    /// <summary>Shows the marks with these ids as selected: a frame for each and, for one area mark, its handles.</summary>
    internal void SetSelection(IReadOnlySet<Guid> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        _selected = selected;
        RebuildSelected();
        UpdateElements();
        InvalidateAll();
    }

    /// <summary>Shows the area being dragged out, or the place a mark is being dragged to (or nothing).</summary>
    internal void SetDrag(RedactionDragView? drag)
    {
        if (drag is null && _drag is null)
        {
            return;
        }

        _drag = drag;
        UpdateElements();
        InvalidateAll();
    }

    /// <summary>The view scrolled, zoomed or changed its page theme: the marks are placed and drawn again.</summary>
    internal void Refresh()
    {
        if (_plan is not null || _drag is not null)
        {
            InvalidateAll();
        }
    }

    /// <summary>Gets the marks of the plan on <paramref name="page"/> in the order they were made (the last is on top).</summary>
    internal IReadOnlyList<RedactionMark> MarksOn(int page) => _byPage.TryGetValue(page, out var marks) ? marks : [];

    /// <summary>Gets the handle whose <see cref="DrawingOverlay.HandleHitSize"/> square holds <paramref name="viewportPoint"/>, the nearest if several do.</summary>
    internal HandleKind? HandleAt(Point viewportPoint)
    {
        if (SingleHandleFrame() is not { } frame)
        {
            return null;
        }

        // A box smaller than a handle would be all handles: inside it a press moves the mark, its handles are taken from outside only.
        var small = frame.Width < DrawingOverlay.HandleHitSize || frame.Height < DrawingOverlay.HandleHitSize;
        HandleKind? found = null;
        var best = double.PositiveInfinity;
        foreach (var (kind, centre) in HandleCentres(frame))
        {
            if (small && frame.Contains(viewportPoint))
            {
                continue;
            }

            var dx = Math.Abs(viewportPoint.X - centre.X);
            var dy = Math.Abs(viewportPoint.Y - centre.Y);
            if (dx <= DrawingOverlay.HandleHitSize / 2 && dy <= DrawingOverlay.HandleHitSize / 2 && dx + dy < best)
            {
                best = dx + dy;
                found = kind;
            }
        }

        return found;
    }

    public override void Render(DrawingContext context)
    {
        var layout = _canvas.Layout;
        LastDrawnRectangles = 0;
        _frames.Clear();
        if (layout is null || layout.PageCount == 0 || (_plan is null && _drag is null))
        {
            return;
        }

        using var clip = context.PushClip(new RoundedRect(new Rect(Bounds.Size), _canvas.CornerRadius));
        var viewport = _canvas.Viewport;
        var (first, last) = layout.GetVisiblePages(viewport.Y, viewport.Bottom);
        var pens = MarkPens();
        var area = new Rect(Bounds.Size);
        for (var page = first; page <= last; page++)
        {
            if (_canvas.PagePlacement(page) is not { } placement)
            {
                continue;
            }

            var (pageRect, scale) = placement;
            foreach (var mark in MarksOn(page))
            {
                var dragged = _drag is { MarkId: { } id } && id == mark.Id;
                if (dragged)
                {
                    continue;
                }

                foreach (var rect in mark.Areas)
                {
                    DrawMark(context, pens, area, ViewRect(pageRect, scale, rect), dashed: false);
                }
            }

            if (_drag is { } drag && drag.Page == page)
            {
                DrawMark(context, pens, area, ViewRect(pageRect, scale, drag.Rect), dashed: drag.MarkId is null);
            }
        }

        DrawSelection(context, area);
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
        var frames = SelectedFrames();
        for (var i = 0; i < _frameElements.Count; i++)
        {
            _frameElements[i].Arrange(i < frames.Count ? frames[i].Frame : default);
        }

        var single = SingleHandleFrame();
        var centres = single is { } frame ? HandleCentres(frame).ToDictionary(handle => handle.Kind, handle => handle.Centre) : [];
        foreach (var (kind, handle) in _handleElements)
        {
            handle.Arrange(centres.TryGetValue(kind, out var centre)
                ? new Rect(centre.X - (DrawingOverlay.HandleHitSize / 2), centre.Y - (DrawingOverlay.HandleHitSize / 2), DrawingOverlay.HandleHitSize, DrawingOverlay.HandleHitSize)
                : default);
        }

        return finalSize;
    }

    // The selected marks that are in the plan (a walk of the plan, made when the plan or the selection changes, never per frame).
    private void RebuildSelected()
    {
        var found = new List<RedactionMark>();
        if (_plan is not null && _selected.Count > 0)
        {
            foreach (var mark in _plan.Marks)
            {
                if (_selected.Contains(mark.Id))
                {
                    found.Add(mark);
                    if (found.Count >= MaxSelectedFrames)
                    {
                        break;
                    }
                }
            }
        }

        _selectedMarks = found;
    }

    private static Dictionary<int, List<RedactionMark>> Index(RedactionPlan plan)
    {
        var byPage = new Dictionary<int, List<RedactionMark>>();
        foreach (var mark in plan.Marks)
        {
            if (!byPage.TryGetValue(mark.Page, out var list))
            {
                byPage[mark.Page] = list = [];
            }

            list.Add(mark);
        }

        return byPage;
    }

    private static Rect ViewRect(Rect pageRect, double scale, PageRect rect) =>
        new Rect(
            new Point(pageRect.X + (rect.Left * scale), pageRect.Y + (rect.Top * scale)),
            new Point(pageRect.X + (rect.Right * scale), pageRect.Y + (rect.Bottom * scale))).Normalize();

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

    private static IEnumerable<(HandleKind Kind, Point Centre)> HandleCentres(Rect frame)
    {
        foreach (var kind in HandleKinds)
        {
            yield return (kind, kind switch
            {
                HandleKind.TopLeft => frame.TopLeft,
                HandleKind.Top => new Point(frame.Center.X, frame.Top),
                HandleKind.TopRight => frame.TopRight,
                HandleKind.Right => new Point(frame.Right, frame.Center.Y),
                HandleKind.BottomRight => frame.BottomRight,
                HandleKind.Bottom => new Point(frame.Center.X, frame.Bottom),
                HandleKind.BottomLeft => frame.BottomLeft,
                _ => new Point(frame.Left, frame.Center.Y),
            });
        }
    }

    private void DrawMark(DrawingContext context, (Color Edge, Color Fill, Pen EdgePen, Pen HatchPen, Pen DashedPen, IBrush FillBrush) pens, Rect area, Rect rect, bool dashed)
    {
        if (!rect.Intersects(area))
        {
            return;
        }

        LastDrawnRectangles++;
        context.DrawRectangle(pens.FillBrush, null, rect);

        // The hatch: lines at 45 degrees, only over the part in view (a mark may be far taller than the window at a high zoom).
        var visible = rect.Intersect(area);
        if (visible.Width > 0 && visible.Height > 0)
        {
            using (context.PushClip(visible))
            {
                // Anchored to the left edge of the mark, so the hatch does not crawl while the view scrolls.
                var start = visible.Left - visible.Height;
                var phase = (((start - rect.Left) % HatchSpacing) + HatchSpacing) % HatchSpacing;
                for (var x = start - phase; x <= visible.Right; x += HatchSpacing)
                {
                    context.DrawLine(pens.HatchPen, new Point(x, visible.Bottom), new Point(x + visible.Height, visible.Top));
                }
            }
        }

        // The edge lies inside the rectangle, so two marks that meet do not hide where one ends.
        var edge = rect.Width > EdgeThickness && rect.Height > EdgeThickness ? rect.Deflate(EdgeThickness / 2) : rect;
        context.DrawRectangle(null, dashed ? pens.DashedPen : pens.EdgePen, edge);
    }

    private void DrawSelection(DrawingContext context, Rect area)
    {
        var colors = SelectionColors();
        var frames = SelectedFrames();
        foreach (var (frame, _) in frames)
        {
            if (!frame.Intersects(area))
            {
                continue;
            }

            context.DrawRectangle(null, colors.HaloPen, frame);
            context.DrawRectangle(null, colors.Dashed, frame);
        }

        if (SingleHandleFrame() is { } single)
        {
            foreach (var (_, centre) in HandleCentres(single))
            {
                var square = new Rect(centre.X - (DrawingOverlay.HandleSize / 2), centre.Y - (DrawingOverlay.HandleSize / 2), DrawingOverlay.HandleSize, DrawingOverlay.HandleSize);
                context.DrawRectangle(colors.Fill, colors.Edge, square);
            }
        }
    }

    /// <summary>The frames of the selected marks on the pages in view, in viewport DIPs, <see cref="DrawingOverlay.FramePadding"/> outside their rectangles.</summary>
    private List<(Rect Frame, bool Single)> SelectedFrames()
    {
        _frames.Clear();
        if (_selectedMarks.Count == 0 || _canvas.Layout is null)
        {
            return _frames;
        }

        foreach (var mark in _selectedMarks)
        {
            if (_canvas.PagePlacement(mark.Page) is not { } placement)
            {
                continue;
            }

            var (pageRect, scale) = placement;
            var rects = _drag is { MarkId: { } id } drag && id == mark.Id ? [drag.Rect] : mark.Areas;
            var union = rects[0];
            for (var i = 1; i < rects.Count; i++)
            {
                union = union.Union(rects[i]);
            }

            _frames.Add((ViewRect(pageRect, scale, union).Inflate(DrawingOverlay.FramePadding), _selected.Count == 1 && mark.Kind == RedactionKind.Area));
        }

        return _frames;
    }

    private Rect? SingleHandleFrame() => SelectedFrames() is [{ Single: true } only] ? only.Frame : null;

    /// <summary>The frames and the handles as elements of the layer, for screen readers; made with the first selection.</summary>
    private void UpdateElements()
    {
        var marks = _selectedMarks.Take(MaxAutomationFrames).ToList();
        if (marks.Count == 0 && _frameElements.Count == 0)
        {
            return;
        }

        while (_frameElements.Count < marks.Count)
        {
            var element = AutomationElement("RedactionMarkFrame" + _frameElements.Count, Strings.RedactMarkKindName, 0, AutomationControlType.Group);
            _frameElements.Add(element);
            AddElement(element);
        }

        for (var i = 0; i < _frameElements.Count; i++)
        {
            var shown = i < marks.Count;
            _frameElements[i].IsVisible = shown;
            if (shown)
            {
                AutomationProperties.SetName(_frameElements[i], Strings.RedactMarkName(marks[i].Page + 1));
            }
        }

        var single = marks.Count == 1 && _selected.Count == 1 && marks[0].Kind == RedactionKind.Area ? marks[0] : null;
        if (single is not null && _handleElements.Count == 0)
        {
            foreach (var kind in HandleKinds)
            {
                var handle = AutomationElement("RedactionHandle" + kind, Strings.SelectionHandleName(kind), DrawingOverlay.HandleHitSize, AutomationControlType.Thumb);
                _handleElements[kind] = handle;
                AddElement(handle);
            }
        }

        foreach (var (kind, handle) in _handleElements)
        {
            handle.IsVisible = single is not null;
            if (single is not null)
            {
                // What the handle belongs to: "Top left handle, Redaction mark, page 1".
                AutomationProperties.SetName(handle, string.Format(Strings.Culture, CompositeFormat.Parse(Strings.SelectionHandleNameOf), Strings.SelectionHandleName(kind), Strings.RedactMarkKindName, single.Page + 1));
            }
        }

        InvalidateArrange();
    }

    private void InvalidateAll()
    {
        InvalidateArrange();
        InvalidateVisual();
    }

    private (Color Edge, Color Fill, Pen EdgePen, Pen HatchPen, Pen DashedPen, IBrush FillBrush) MarkPens()
    {
        var variant = _canvas.PageTheme == PageTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var edge = ColorOf("Pw.Color.Redact.Edge", variant, Color.FromRgb(0xE5, 0x48, 0x4D));
        var fill = ColorOf("Pw.Color.Redact.Fill", variant, Color.FromArgb(0x26, 0xE5, 0x48, 0x4D));
        if (_markPens is { } known && known.Edge == edge && known.Fill == fill)
        {
            return known;
        }

        var edgeBrush = new SolidColorBrush(edge);
        var pens = (
            edge,
            fill,
            new Pen(edgeBrush, EdgeThickness),
            new Pen(edgeBrush, HatchThickness),
            new Pen(edgeBrush, EdgeThickness, new DashStyle([4, 3], 0)),
            (IBrush)new SolidColorBrush(fill));
        _markPens = pens;
        return pens;
    }

    /// <summary>
    /// The colours of the selection: the tokens of the light theme on a light page, of the dark theme on a dark one, so the frame
    /// keeps 3:1 against the ground whatever the theme of the app (pages stay light in high contrast).
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

    private Color ColorOf(string key, ThemeVariant variant, Color fallback) =>
        this.TryFindResource(key, variant, out var value) && value is Color color ? color : fallback;

    private void AddElement(Control element)
    {
        LogicalChildren.Add(element);
        VisualChildren.Add(element);
    }
}

/// <summary>
/// What the redaction layer shows of a drag (milestone 14): the area being dragged out (<paramref name="MarkId"/> is
/// <see langword="null"/>, shown dashed), or the place an area mark is being dragged to (its old place is not drawn meanwhile).
/// </summary>
/// <param name="MarkId">The mark that is dragged, or <see langword="null"/> for a new area.</param>
/// <param name="Page">The 0-based page.</param>
/// <param name="Rect">The rectangle in points on that page, origin top left.</param>
internal sealed record RedactionDragView(Guid? MarkId, int Page, PageRect Rect);
