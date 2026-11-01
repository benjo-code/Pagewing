using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// Marking on the page in the mode "Redact" (milestone 14, design spec 5.6, built like drawing and selecting of milestone 10): with
/// the area tool a press of the left button with the mouse or the pen on a page starts a rectangle, the drag shows it on the
/// redaction layer (<see cref="RedactionOverlay"/>), and the release makes one area mark of it
/// (<see cref="TabViewModel.AddAreaMark"/>); a press on a handle of the one selected area mark resizes it, a press on a mark
/// selects it and, for an area mark, drags it. Each drag is one step of the history. A finger does not mark: it scrolls and zooms as
/// without the mode. With the text tool a press selects text as in the reading view, and "Mark selection" makes the mark
/// (<see cref="TabViewModel.MarkSelectionCommand"/>); a press on a mark selects the mark with either tool.
/// </summary>
/// <remarks>
/// <para>
/// A drag stays on the page it started on: its points are page points of that page, and a drag beyond the page keeps its rectangle
/// on it. A click (a press that moves less than <see cref="ClickSlop"/>) makes no mark; on empty page it clears the selection of
/// marks. Esc, a lost pointer capture, another tab, document or layout, a card that asks about the tab and a window that loses the
/// activation drop the drag without a trace on the history.
/// </para>
/// <para>
/// The keys of the marks are taken in <see cref="OnKeyDown"/> only, in the mode: Tab and Shift+Tab walk the marks (after the last, or
/// before the first, the selection goes and the key moves the focus on: Tab never traps the keys in the document), the arrows move
/// the selected area marks by 1 point, Shift+arrow changes their size, Delete and Backspace delete the selected marks. Nothing of
/// this exists before the mode starts: the layer and its subscriptions are made when the first tab enters it.
/// </para>
/// </remarks>
public sealed partial class DocumentView
{
    private RedactionOverlay? _redactionOverlay;
    private RedactionGesture? _redactionGesture;
    private WindowBase? _redactionGestureWindow;
    private TabViewModel? _redactionTab;

    /// <summary>Gets the redaction layer, or <see langword="null"/> before the first tab of the window entered the mode (for tests).</summary>
    internal RedactionOverlay? RedactionOverlay => _redactionOverlay;

    /// <summary>Gets whether an area is being dragged out, or a mark or one of its handles is being dragged.</summary>
    internal bool IsDraggingRedaction => _redactionGesture is not null;

    /// <summary>
    /// A press arrives in the mode "Redact": it belongs to the mode with the area tool (no text selection, no link), and with the
    /// text tool where it hits a mark or a handle. On a handle of the selected area mark it starts a resize, on a mark it selects it
    /// (and drags an area mark), on empty page with the area tool it starts a rectangle. A finger is left to the scroll viewer.
    /// </summary>
    /// <returns>Whether the mode took the press.</returns>
    private bool PressForRedaction(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        if (_viewModel is not { IsRedacting: true } tab)
        {
            return false;
        }

        var area = tab.RedactionTool == RedactionTool.Area;
        if (e.Pointer.Type == PointerType.Touch || properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || _layout is null || _redactionGesture is not null || IsInScrollBar(e.Source) || !tab.CanMarkOnPage)
        {
            return area;
        }

        var position = e.GetPosition(PageCanvas);
        var overlay = EnsureRedactionOverlay();
        if (tab.SelectedAreaMark is { } selected && overlay.HandleAt(position) is { } handle
            && PageCanvas.PointOnPage(selected.Page, position) is { } onSelected)
        {
            BeginRedactionGesture(tab, e.Pointer, new RedactionGesture(tab, e.Pointer, selected.Page, selected.Id, handle, selected.Areas[0], new DisplayPoint(onSelected.X, onSelected.Y)));
            return true;
        }

        if (PageCanvas.PagePointAt(position) is not { } hit || !IsOnPage(hit.Page, hit.Position))
        {
            tab.ClearMarkSelection();
            return area;
        }

        var point = new DisplayPoint(hit.Position.X, hit.Position.Y);
        if (overlay.MarksOn(hit.Page).LastOrDefault(mark => mark.Areas.Any(rect => rect.Contains(point.X, point.Y))) is { } mark)
        {
            tab.SelectMarks([mark.Id]);
            if (mark.Kind == RedactionKind.Area && mark.Areas.Count == 1)
            {
                BeginRedactionGesture(tab, e.Pointer, new RedactionGesture(tab, e.Pointer, mark.Page, mark.Id, null, mark.Areas[0], point));
            }

            return true;
        }

        if (!area)
        {
            // The text tool: the press is a text press (it clears a selection of marks as it clears a text selection).
            tab.ClearMarkSelection();
            return false;
        }

        BeginRedactionGesture(tab, e.Pointer, new RedactionGesture(tab, e.Pointer, hit.Page, null, null, new PageRect(point.X, point.Y, point.X, point.Y), point));
        return true;
    }

    /// <summary>A move of the pointer of the drag shows the new rectangle; other pointers do nothing meanwhile.</summary>
    /// <returns>Whether a drag is going on (the move is the mode's).</returns>
    private bool MoveForRedaction(PointerEventArgs e)
    {
        if (_redactionGesture is not { } gesture)
        {
            return false;
        }

        if (ReferenceEquals(e.Pointer, gesture.Pointer))
        {
            UpdateRedactionGesture(gesture, e.GetPosition(PageCanvas), e.KeyModifiers);
        }

        return true;
    }

    /// <summary>The release of the pointer of the drag: one step of the history, or a click.</summary>
    /// <returns>Whether a drag was going on (the release is the mode's).</returns>
    private bool ReleaseForRedaction(PointerReleasedEventArgs e)
    {
        if (_redactionGesture is not { } gesture)
        {
            return false;
        }

        if (!ReferenceEquals(e.Pointer, gesture.Pointer))
        {
            return true;
        }

        var position = e.GetPosition(PageCanvas);
        UpdateRedactionGesture(gesture, position, e.KeyModifiers);
        var rect = gesture.Rect;
        EndRedactionGesture();
        if (ReferenceEquals(gesture.Tab, _viewModel) && gesture.Tab.IsRedacting)
        {
            if (gesture.MarkId is not { } id)
            {
                if (gesture.Moved && rect.Width >= 1 && rect.Height >= 1)
                {
                    gesture.Tab.AddAreaMark(gesture.Page, rect);
                }
                else
                {
                    // A click on empty page: nothing is marked, the selection of marks goes.
                    gesture.Tab.ClearMarkSelection();
                }
            }
            else if (gesture.Moved)
            {
                gesture.Tab.SetMarkAreas([(id, rect)]);
            }
        }

        UpdateCursor(position);
        return true;
    }

    /// <summary>
    /// Drops the drag, if there is one: the rectangle goes from the layer, the capture is released, nothing is made. Esc, a lost
    /// capture, another tab, document or layout.
    /// </summary>
    /// <returns>Whether there was a drag.</returns>
    private bool EndRedactionGesture()
    {
        if (_redactionGesture is not { } gesture)
        {
            return false;
        }

        // First, so the capture lost below finds nothing to end.
        _redactionGesture = null;
        gesture.Tab.CancelRedactionDrag = null;
        _redactionOverlay?.SetDrag(null);
        if (_redactionGestureWindow is { } window)
        {
            window.Deactivated -= OnRedactionGestureWindowDeactivated;
            _redactionGestureWindow = null;
        }

        if (ReferenceEquals(gesture.Pointer.Captured, this))
        {
            gesture.Pointer.Capture(null);
        }

        return true;
    }

    /// <summary>After a change of the tab: a drag whose tab, mode or document is gone, or that the tab cannot take any more, is dropped.</summary>
    private void CheckRedactionGesture(string? property)
    {
        if (_redactionGesture is not { } gesture)
        {
            return;
        }

        if (_viewModel is not { } tab || !ReferenceEquals(tab, gesture.Tab) || !tab.IsRedacting || !tab.CanMarkOnPage
            || property == nameof(TabViewModel.LayoutMode))
        {
            EndRedactionGesture();
        }
    }

    /// <summary>
    /// Shows the plan and the selection of the tab on the redaction layer (made when the mode first starts), or takes them away when
    /// the mode is off. A dragged mark that left the plan (undo, delete) ends its drag.
    /// </summary>
    private void SyncRedaction()
    {
        if (_viewModel is { IsRedacting: true } tab && _layout is not null)
        {
            var overlay = EnsureRedactionOverlay();
            overlay.SetPlan(tab.RedactionPlan);
            overlay.SetSelection(tab.SelectedMarks);
            if (_redactionGesture is { MarkId: { } id } && tab.RedactionPlan?.Marks.All(mark => mark.Id != id) != false)
            {
                EndRedactionGesture();
            }

            return;
        }

        EndRedactionGesture();
        if (_redactionOverlay is { } shown)
        {
            shown.SetDrag(null);
            shown.SetPlan(null);
            shown.SetSelection(new HashSet<Guid>());
        }
    }

    /// <summary>The view scrolled or zoomed: the marks are placed again.</summary>
    private void FollowRedactionWithViewport() => _redactionOverlay?.Refresh();

    /// <summary>The view takes the tab: what it tells the user when the mode did not start is shown as a hint.</summary>
    private void ConnectRedaction(TabViewModel tab)
    {
        _redactionTab = tab;
        tab.RedactionHint += ShowHint;
    }

    /// <summary>The view leaves the tab or its document: a drag goes, and the layer shows nothing.</summary>
    private void DisconnectRedaction()
    {
        EndRedactionGesture();
        if (_redactionTab is { } tab)
        {
            tab.RedactionHint -= ShowHint;
            _redactionTab = null;
        }

        if (_redactionOverlay is { } overlay)
        {
            overlay.SetPlan(null);
            overlay.SetSelection(new HashSet<Guid>());
        }
    }

    private RedactionOverlay EnsureRedactionOverlay()
    {
        if (_redactionOverlay is null)
        {
            // Right above the pages, below the scroll viewer and its bars; it takes no input.
            _redactionOverlay = new RedactionOverlay(PageCanvas) { Name = "RedactionOverlay" };
            DocumentLayers.Children.Insert(DocumentLayers.Children.IndexOf(PageCanvas) + 1, _redactionOverlay);
        }

        return _redactionOverlay;
    }

    private void BeginRedactionGesture(TabViewModel tab, IPointer pointer, RedactionGesture gesture)
    {
        _redactionGesture = gesture;
        pointer.Capture(this);
        tab.CancelRedactionDrag = EndRedactionGesture;
        if (_topLevel is WindowBase window)
        {
            _redactionGestureWindow = window;
            window.Deactivated += OnRedactionGestureWindowDeactivated;
        }
    }

    private void OnRedactionGestureWindowDeactivated(object? sender, EventArgs e) => EndRedactionGesture();

    /// <summary>Moves the rectangle of the drag to the pointer: the other corner of a new area, the whole mark, or a corner or edge of it.</summary>
    private void UpdateRedactionGesture(RedactionGesture gesture, Point position, KeyModifiers modifiers)
    {
        if (_layout is not { } layout || gesture.Page >= layout.PageCount || PageCanvas.PointOnPage(gesture.Page, position) is not { } onPage)
        {
            return;
        }

        var scale = layout.Zoom * DocumentLayout.DipsPerPoint;
        var point = new DisplayPoint(onPage.X, onPage.Y);
        var dx = point.X - gesture.Start.X;
        var dy = point.Y - gesture.Start.Y;
        if (!gesture.Moved && Math.Sqrt((dx * dx) + (dy * dy)) * scale < ClickSlop)
        {
            return;
        }

        gesture.Moved = true;
        var size = layout.GetPageSize(gesture.Page);
        var page = new PageRect(0, 0, size.Width, size.Height);
        var original = gesture.Original;
        gesture.Rect = (gesture.MarkId, gesture.Handle) switch
        {
            (null, _) => BoxOf(gesture.Start, new DisplayPoint(Math.Clamp(point.X, 0, size.Width), Math.Clamp(point.Y, 0, size.Height))),
            (_, null) => MovedWithin(original, dx, dy, page),
            (_, { } handle) => SelectionHandles.ResizeBy(original, handle, new DisplayPoint(gesture.HandlePosition.X + dx, gesture.HandlePosition.Y + dy), (modifiers & KeyModifiers.Shift) != 0, page),
        };
        _redactionOverlay?.SetDrag(new RedactionDragView(gesture.MarkId, gesture.Page, gesture.Rect));
    }

    // The rectangle moved by (dx, dy), kept on the page; one that is larger than the page stays at its edge.
    private static PageRect MovedWithin(PageRect rect, double dx, double dy, PageRect page)
    {
        var x = Math.Clamp(rect.Left + dx, page.Left, Math.Max(page.Left, page.Right - rect.Width));
        var y = Math.Clamp(rect.Top + dy, page.Top, Math.Max(page.Top, page.Bottom - rect.Height));
        return new PageRect(x, y, x + rect.Width, y + rect.Height);
    }

    // The point of a handle on a rectangle, the place a drag of that handle starts from.
    private static DisplayPoint HandlePointOf(PageRect rect, HandleKind handle)
    {
        var middleX = (rect.Left + rect.Right) / 2;
        var middleY = (rect.Top + rect.Bottom) / 2;
        return handle switch
        {
            HandleKind.TopLeft => new DisplayPoint(rect.Left, rect.Top),
            HandleKind.Top => new DisplayPoint(middleX, rect.Top),
            HandleKind.TopRight => new DisplayPoint(rect.Right, rect.Top),
            HandleKind.Right => new DisplayPoint(rect.Right, middleY),
            HandleKind.BottomRight => new DisplayPoint(rect.Right, rect.Bottom),
            HandleKind.Bottom => new DisplayPoint(middleX, rect.Bottom),
            HandleKind.BottomLeft => new DisplayPoint(rect.Left, rect.Bottom),
            _ => new DisplayPoint(rect.Left, middleY),
        };
    }

    /// <summary>
    /// The keys of the marks in the mode (see the class): Tab and Shift+Tab walk them, the arrows nudge the selected area marks,
    /// Delete and Backspace delete the selected marks. Without the mode nothing is taken; a key the marks cannot use (an arrow with
    /// only text marks selected, Tab past the last mark) is left to scroll the view or to move the focus on.
    /// </summary>
    /// <returns>Whether the key was taken.</returns>
    private bool TryRedactionKey(Key key, KeyModifiers modifiers)
    {
        if (_viewModel is not { IsRedacting: true } tab)
        {
            return false;
        }

        var plain = modifiers is KeyModifiers.None or KeyModifiers.Shift;
        switch (key)
        {
            case Key.Tab when plain:
                if (tab.WalkMarks(forward: modifiers != KeyModifiers.Shift))
                {
                    RevealSelectedMark(tab);
                    return true;
                }

                return false;
            case Key.Left or Key.Right or Key.Up or Key.Down when plain && tab.HasMovableMarkSelection && tab.CanMarkOnPage:
                var dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
                var dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
                tab.NudgeMarks(dx, dy, resize: modifiers == KeyModifiers.Shift);
                RevealSelectedMark(tab);
                return true;
            case Key.Delete or Key.Back when modifiers == KeyModifiers.None && tab.SelectedMarks.Count > 0 && tab.DeleteMarksCommand.CanExecute(null):
                tab.DeleteMarksCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Scrolls the view so that the selected mark is in it: its page is shown when it is not, then the mark is brought into the viewport.</summary>
    private void RevealSelectedMark(TabViewModel tab)
    {
        if (_layout is not { } layout || tab.RedactionPlan is not { } plan || tab.SelectedMarks.Count != 1
            || plan.Marks.FirstOrDefault(mark => tab.SelectedMarks.Contains(mark.Id)) is not { } selected)
        {
            return;
        }

        var offset = Scroller.Offset;
        var viewport = Scroller.Viewport;
        var (first, last) = layout.GetVisiblePages(offset.Y, offset.Y + viewport.Height);
        if (selected.Page < first || selected.Page > last)
        {
            tab.GoToPage(selected.Page + 1);
            offset = Scroller.Offset;
        }

        var rect = selected.Areas.Aggregate((a, b) => a.Union(b));
        var scale = layout.Zoom * DocumentLayout.DipsPerPoint;
        var top = layout.GetPageRect(selected.Page).Y + (rect.Top * scale);
        var bottom = layout.GetPageRect(selected.Page).Y + (rect.Bottom * scale);
        const double margin = 24;
        if (top < offset.Y + margin)
        {
            ScrollTo(new Vector(offset.X, Math.Max(0, top - margin)));
        }
        else if (bottom > offset.Y + viewport.Height - margin)
        {
            ScrollTo(new Vector(offset.X, Math.Max(0, bottom - viewport.Height + margin)));
        }
    }

    /// <summary>
    /// The cursor in the mode: a resize arrow over a handle of the selected area mark, the crosshair over a page with the area tool,
    /// else none of its own (the text tool shows the cursors of the reading view).
    /// </summary>
    /// <returns>Whether the mode chose the cursor.</returns>
    private bool UpdateRedactionCursor(Point position)
    {
        if (_viewModel is not { IsRedacting: true } tab || _redactionOverlay is not { } overlay)
        {
            return false;
        }

        if (tab.SelectedAreaMark is not null && overlay.HandleAt(position) is { } handle)
        {
            var type = handle switch
            {
                HandleKind.TopLeft or HandleKind.BottomRight => StandardCursorType.TopLeftCorner,
                HandleKind.TopRight or HandleKind.BottomLeft => StandardCursorType.TopRightCorner,
                HandleKind.Top or HandleKind.Bottom => StandardCursorType.TopSide,
                _ => StandardCursorType.LeftSide,
            };
            var cursors = _selectionCursors ??= [];
            if (!cursors.TryGetValue(type, out var cursor))
            {
                cursors[type] = cursor = new Cursor(type);
            }

            _cursorKind = CursorKind.Selection;
            Scroller.Cursor = cursor;
            return true;
        }

        if (tab.RedactionTool == RedactionTool.Area)
        {
            SetCursor(PageCanvas.PagePointAt(position) is { } on && IsOnPage(on.Page, on.Position) ? CursorKind.Draw : CursorKind.Arrow);
            return true;
        }

        return false;
    }

    /// <summary>A drag of the mode, from the press until the release: a new area, a mark being moved, or one of its handles.</summary>
    private sealed class RedactionGesture
    {
        public RedactionGesture(TabViewModel tab, IPointer pointer, int page, Guid? markId, HandleKind? handle, PageRect original, DisplayPoint start)
        {
            Tab = tab;
            Pointer = pointer;
            Page = page;
            MarkId = markId;
            Handle = handle;
            Original = original;
            Start = start;
            HandlePosition = handle is { } kind ? HandlePointOf(original, kind) : start;
            Rect = original;
        }

        /// <summary>The tab the drag marks in.</summary>
        public TabViewModel Tab { get; }

        /// <summary>The pointer that drags (captured by the view).</summary>
        public IPointer Pointer { get; }

        /// <summary>The page the drag stays on.</summary>
        public int Page { get; }

        /// <summary>The mark that is dragged, or <see langword="null"/> for a new area.</summary>
        public Guid? MarkId { get; }

        /// <summary>The handle that is dragged, or <see langword="null"/> for the whole mark or a new area.</summary>
        public HandleKind? Handle { get; }

        /// <summary>The rectangle of the mark when the drag started (a point for a new area).</summary>
        public PageRect Original { get; }

        /// <summary>Where the press went down, in page points.</summary>
        public DisplayPoint Start { get; }

        /// <summary>Where the dragged handle was at the press (the press point otherwise), in page points.</summary>
        public DisplayPoint HandlePosition { get; }

        /// <summary>Whether the pointer moved by <see cref="ClickSlop"/> or more since the press.</summary>
        public bool Moved { get; set; }

        /// <summary>The rectangle the drag has come to.</summary>
        public PageRect Rect { get; set; }
    }
}
