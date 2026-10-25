using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Views;

/// <summary>
/// The select tool of the mode "Comment" (milestone 10, spec "Bedienung, Auswählen"): a click on a page picks the topmost
/// selectable annotation under the pointer (<see cref="TabViewModel.SelectAtAsync"/>, <see cref="DrawingHitTest"/>; a click
/// on nothing clears it); a press on a movable selection (inside its frame) or on one of its handles drags it, the drawing
/// layer shows the new shape (<see cref="DrawingOverlay"/>), and the release replaces the drawing once, one step of the
/// history (<see cref="TabViewModel.ReplaceSelectionGeometryAsync"/>). Shift keeps the proportions at a corner or edge and
/// snaps an end of a line or arrow to 45 degrees from the other end. The keys of the selection (Tab, the arrows, Delete,
/// Backspace, Enter) are taken in <see cref="OnKeyDown"/> only, in the mode with a selection (Tab also with the select tool
/// alone); otherwise they do what they did before.
/// </summary>
/// <remarks>
/// A drag that ends where it began (less than <see cref="ClickSlop"/>), or whose shape is the old one, changes nothing; Esc, a
/// lost capture, another tab, tool, layout or document, a card that asks about the tab and a window that loses the activation
/// drop it without a trace. The drag stays on the page of the selection and on that page (<see cref="DrawingTransform"/>).
/// A finger is left to the scroll viewer. Nothing of this exists before the first selection.
/// </remarks>
public sealed partial class DocumentView
{
    /// <summary>How far (DIPs) from a stroke or outline a click of the select tool still picks it.</summary>
    internal const double PickTolerance = 6;

    // The name of the selection frame for screen readers, parsed once per language (UI thread only).
    private static (string Text, CompositeFormat Format)? _frameName;

    private SelectionDrag? _selectionDrag;
    private WindowBase? _selectionDragWindow;
    private Dictionary<StandardCursorType, Cursor>? _selectionCursors;

    /// <summary>Gets whether the selection or one of its handles is being dragged.</summary>
    internal bool IsMovingSelection => _selectionDrag is not null;

    private static bool SelectsWith(TabViewModel tab) => tab is { IsAnnotating: true, IsSelectTool: true };

    private static bool SameAnnotation(PdfAnnotation a, PdfAnnotation b) =>
        a.Page == b.Page && (a.Name is not null || b.Name is not null ? a.Name == b.Name : a.Index == b.Index && a.Kind == b.Kind);

    /// <summary>The rectangle the frame goes round: the box of a shape (its outer edge), the strokes and half their width, or the bounds.</summary>
    private static PageRect FrameOf(PdfAnnotation annotation)
    {
        if (annotation.Drawing is not { } drawing)
        {
            return annotation.Bounds;
        }

        if (drawing.Kind is AnnotationKind.Rectangle or AnnotationKind.Ellipse)
        {
            return drawing.Box;
        }

        var half = double.IsFinite(drawing.Width) && drawing.Width > 0 ? drawing.Width / 2 : 0;
        return new PageRect(drawing.Box.Left - half, drawing.Box.Top - half, drawing.Box.Right + half, drawing.Box.Bottom + half);
    }

    /// <summary>
    /// A press arrives with the select tool: it belongs to the tool (no text selection, no link). On a handle or inside the
    /// frame of a movable selection it starts a drag; anywhere else on a page it picks what is there.
    /// </summary>
    /// <returns>Whether the select tool took the press.</returns>
    private bool PressForSelection(PointerPressedEventArgs e, PointerPointProperties properties)
    {
        if (_viewModel is not { } tab || !SelectsWith(tab))
        {
            return false;
        }

        if (e.Pointer.Type == PointerType.Touch || properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || _layout is null || _selectionDrag is not null || IsInScrollBar(e.Source))
        {
            return true;
        }

        var position = e.GetPosition(PageCanvas);
        if (tab is { SelectedAnnotation: { } selected, CanMoveSelection: true, CanDraw: true, SelectionGeometry: { } geometry } && _drawingOverlay is { } overlay)
        {
            var handle = overlay.HandleAt(position);
            if ((handle is not null || overlay.FrameContains(position)) && PageCanvas.PointOnPage(selected.Page, position) is { } start)
            {
                BeginSelectionDrag(tab, e.Pointer, selected, geometry, handle, new DisplayPoint(start.X, start.Y));
                return true;
            }
        }

        PickAt(tab, position);
        return true;
    }

    /// <summary>A click of the select tool at <paramref name="position"/> (viewport): picks what is there, or clears the selection off the pages.</summary>
    private void PickAt(TabViewModel tab, Point position)
    {
        if (_layout is not { } layout || PageCanvas.PagePointAt(position) is not { } hit || !IsOnPage(hit.Page, hit.Position))
        {
            tab.ClearSelection();
            return;
        }

        var tolerance = PickTolerance / (layout.Zoom * DocumentLayout.DipsPerPoint);
        _ = tab.SelectAtAsync(hit.Page, new DisplayPoint(hit.Position.X, hit.Position.Y), tolerance);
    }

    private void BeginSelectionDrag(TabViewModel tab, IPointer pointer, PdfAnnotation selected, DrawingGeometry geometry, HandleKind? handle, DisplayPoint start)
    {
        var handlePosition = handle is { } kind
            ? SelectionHandles.For(selected with { Drawing = geometry }).FirstOrDefault(h => h.Kind == kind)?.Position ?? start
            : start;
        _selectionDrag = new SelectionDrag(tab, pointer, selected, geometry, handle, start, handlePosition);
        pointer.Capture(this);
        tab.CancelSelectionDrag = EndSelectionDrag;
        if (_topLevel is WindowBase window)
        {
            _selectionDragWindow = window;
            window.Deactivated += OnSelectionDragWindowDeactivated;
        }
    }

    /// <summary>A move of the pointer of the drag: the new shape follows it; other pointers do nothing meanwhile.</summary>
    /// <returns>Whether a drag of the selection is going on (the move is the tool's).</returns>
    private bool MoveForSelection(PointerEventArgs e)
    {
        if (_selectionDrag is not { } drag)
        {
            return false;
        }

        if (ReferenceEquals(e.Pointer, drag.Pointer))
        {
            UpdateSelectionDrag(drag, e.GetPosition(PageCanvas), e.KeyModifiers);
        }

        return true;
    }

    /// <summary>The release of the pointer of the drag: the drawing gets its new shape (one step), or a click picks again.</summary>
    /// <returns>Whether a drag of the selection was going on.</returns>
    private bool ReleaseForSelection(PointerReleasedEventArgs e)
    {
        if (_selectionDrag is not { } drag)
        {
            return false;
        }

        if (!ReferenceEquals(e.Pointer, drag.Pointer))
        {
            return true;
        }

        var position = e.GetPosition(PageCanvas);
        UpdateSelectionDrag(drag, position, e.KeyModifiers);
        EndSelectionDrag();
        if (ReferenceEquals(drag.Tab, _viewModel) && SelectsWith(drag.Tab))
        {
            if (drag is { Moved: true, Geometry: { } geometry })
            {
                _ = drag.Tab.ReplaceSelectionGeometryAsync(geometry);
            }
            else if (!drag.Moved && drag.Handle is null)
            {
                // A click inside the frame: what lies there on top (perhaps another annotation) is picked.
                PickAt(drag.Tab, position);
            }
        }

        UpdateCursor(position);
        return true;
    }

    /// <summary>Drops a drag of the selection, if there is one: the new shape goes, the capture is released, nothing changes.</summary>
    /// <returns>Whether there was a drag.</returns>
    private bool EndSelectionDrag()
    {
        if (_selectionDrag is not { } drag)
        {
            return false;
        }

        // First, so the capture lost below finds nothing to end.
        _selectionDrag = null;
        drag.Tab.CancelSelectionDrag = null;
        if (_selectionDragWindow is { } window)
        {
            window.Deactivated -= OnSelectionDragWindowDeactivated;
            _selectionDragWindow = null;
        }

        if (ReferenceEquals(drag.Pointer.Captured, this))
        {
            drag.Pointer.Capture(null);
        }

        RefreshSelectionView();
        return true;
    }

    private void OnSelectionDragWindowDeactivated(object? sender, EventArgs e) => EndSelectionDrag();

    /// <summary>After a change of the tab: a drag whose tab, tool or selection is gone, or that the tab cannot take any more, is dropped.</summary>
    private void CheckSelectionDrag(string? property)
    {
        if (_selectionDrag is not { } drag)
        {
            return;
        }

        if (_viewModel is not { } tab || !ReferenceEquals(tab, drag.Tab) || !SelectsWith(tab) || !tab.CanDraw
            || tab.SelectedAnnotation is not { } selected || !SameAnnotation(selected, drag.Target) || property == nameof(TabViewModel.LayoutMode))
        {
            EndSelectionDrag();
        }
    }

    /// <summary>Moves the shape of the drag to the pointer: the whole drawing, a corner or edge of its box, or one end of a line.</summary>
    private void UpdateSelectionDrag(SelectionDrag drag, Point position, KeyModifiers modifiers)
    {
        if (_layout is not { } layout || drag.Target.Page >= layout.PageCount || PageCanvas.PointOnPage(drag.Target.Page, position) is not { } onPage)
        {
            return;
        }

        var point = new DisplayPoint(onPage.X, onPage.Y);
        var scale = layout.Zoom * DocumentLayout.DipsPerPoint;
        if (!drag.Moved && Math.Sqrt(((point.X - drag.Start.X) * (point.X - drag.Start.X)) + ((point.Y - drag.Start.Y) * (point.Y - drag.Start.Y))) * scale < ClickSlop)
        {
            return;
        }

        drag.Moved = true;
        var size = layout.GetPageSize(drag.Target.Page);
        var page = new PageRect(0, 0, size.Width, size.Height);
        var dx = point.X - drag.Start.X;
        var dy = point.Y - drag.Start.Y;
        var keep = (modifiers & KeyModifiers.Shift) != 0;
        var to = new DisplayPoint(drag.HandlePosition.X + dx, drag.HandlePosition.Y + dy);
        var original = drag.Original;
        drag.Geometry = drag.Handle switch
        {
            null => DrawingTransform.Move(original, dx, dy, page),
            HandleKind.Start or HandleKind.End => DrawingTransform.MoveEndpoint(
                original,
                drag.Handle == HandleKind.Start,
                keep ? ArrowGeometry.SnapToAngle(original.Strokes[0][drag.Handle == HandleKind.Start ? 1 : 0], to, SnapStepDegrees) : to,
                page),
            { } handle => DrawingTransform.Resize(original, SelectionHandles.ResizeBy(original.Box, handle, to, keep, page), page),
        };
        RefreshSelectionView();
    }

    /// <summary>Shows the selection of the tab on the drawing layer (made with the first selection), or takes it away.</summary>
    private void RefreshSelectionView()
    {
        if (_viewModel is not { SelectedAnnotation: { } selected } tab || _layout is null)
        {
            _drawingOverlay?.ShowSelection(null);
            return;
        }

        // A change of the selection (its colour or width in the toolbar) that PDFium refuses is told like a refused drawing.
        ListenForRefusedDrawings(tab);
        var geometry = _selectionDrag is { } drag && ReferenceEquals(drag.Tab, tab) ? drag.Geometry ?? drag.Original : tab.SelectionGeometry;
        var shown = geometry is not null ? selected with { Drawing = geometry } : selected;
        var preview = geometry is not null && !ReferenceEquals(geometry, selected.Drawing) ? geometry : null;
        var color = selected.Color is { } rgb ? Color.FromRgb(rgb.R, rgb.G, rgb.B) : Colors.Black;
        var kindName = Strings.KindName(selected.Kind) ?? selected.Subtype;
        var name = string.Format(Strings.Culture, FrameNameFormat(), kindName, selected.Page + 1);
        EnsureDrawingOverlay().ShowSelection(new SelectionView(selected.Page, FrameOf(shown), SelectionHandles.For(shown), preview, color, tab.SelectionLockReason, name, kindName));
    }

    /// <summary>The view scrolled or zoomed: the selection is placed again.</summary>
    private void FollowSelectionViewWithViewport() => _drawingOverlay?.RefreshSelection();

    /// <summary>The parsed name of the selection frame ("Rectangle selected, page 1"), parsed again only when the language changed.</summary>
    private static CompositeFormat FrameNameFormat()
    {
        var text = Strings.SelectionFrameNameOf;
        if (_frameName is not { } known || !string.Equals(known.Text, text, StringComparison.Ordinal))
        {
            known = (text, CompositeFormat.Parse(text));
            _frameName = known;
        }

        return known.Format;
    }

    /// <summary>The view leaves the tab or its document: a drag goes, and the selection with it (a waiting nudge is made first).</summary>
    private void DisconnectSelection()
    {
        EndSelectionDrag();
        _viewModel?.ClearSelection();
        _drawingOverlay?.ShowSelection(null);
    }

    /// <summary>
    /// The keys of the selection in the mode (see the class): Tab and Shift+Tab walk the selectable annotations of the page
    /// (also from no selection while the select tool is on, unless the page is known to have none; after the last, or before
    /// the first, the selection goes and the key moves the focus on as usual), the arrows nudge a movable
    /// selection (Shift: 10 points), Delete and Backspace delete it, Enter opens its comment. Any other key first makes a
    /// waiting nudge. Without the mode or a selection nothing is taken.
    /// </summary>
    /// <returns>Whether the key was taken.</returns>
    private bool TrySelectionKey(Key key, KeyModifiers modifiers)
    {
        if (_viewModel is not { IsAnnotating: true } tab)
        {
            return false;
        }

        var arrow = key is Key.Left or Key.Right or Key.Up or Key.Down;
        var plain = modifiers is KeyModifiers.None or KeyModifiers.Shift;
        if (!(arrow && plain) && key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            // Another key ends a run of nudges: it is one step now.
            tab.FlushNudge();
        }

        var selected = tab.SelectedAnnotation;
        switch (key)
        {
            case Key.Tab when plain:
                return TryWalkSelection(tab, selected, forward: modifiers != KeyModifiers.Shift);
            case Key.Left or Key.Right or Key.Up or Key.Down when plain && selected is not null && tab.CanMoveSelection:
                var dx = key == Key.Left ? -1 : key == Key.Right ? 1 : 0;
                var dy = key == Key.Up ? -1 : key == Key.Down ? 1 : 0;
                tab.NudgeCommand.Execute((dx, dy, modifiers == KeyModifiers.Shift));
                return true;
            case Key.Delete or Key.Back when modifiers == KeyModifiers.None && selected is not null:
                tab.DeleteSelectionCommand.Execute(null);
                return true;
            case Key.Enter when modifiers == KeyModifiers.None && selected is not null:
                tab.OpenSelectionComment();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Tab or Shift+Tab with the select tool or a selection. With the page read it decides at once: past the last (or before
    /// the first) annotation, or on a page without any, the selection goes and the key is not taken, so the focus moves on
    /// (Tab never traps the keys in the document). With the page not read (the first Tab, or right after a change) the key is
    /// taken and the walk acts once the page is there; when it then ends, the view moves the focus on itself, as the key would
    /// have done. So a Tab never goes without effect.
    /// </summary>
    /// <returns>Whether the key was taken.</returns>
    private bool TryWalkSelection(TabViewModel tab, PdfAnnotation? selected, bool forward)
    {
        if (!tab.CanSelect || (selected is null && !tab.IsSelectTool))
        {
            return false;
        }

        var page = selected?.Page ?? Math.Max(0, tab.CurrentPage - 1);
        if (tab.IsPageRead(page))
        {
            if (selected is null ? tab.HasSelectable(page) == false : tab.SelectionWalkEnds(forward))
            {
                tab.ClearSelection();
                return false;
            }

            // Read: the walk selects at once.
            _ = tab.WalkSelectionAsync(forward);
            return true;
        }

        _ = WalkWhenReadAsync(tab, forward);
        return true;
    }

    private async Task WalkWhenReadAsync(TabViewModel tab, bool forward)
    {
        var walk = await tab.WalkSelectionAsync(forward);
        if (walk == TabViewModel.SelectionWalk.Ended && ReferenceEquals(tab, _viewModel) && IsKeyboardFocusWithin)
        {
            // The key was taken while the page was read: the focus moves on now, as the key would have moved it.
            TopLevel.GetTopLevel(this)?.FocusManager?.TryMoveFocus(forward ? NavigationDirection.Next : NavigationDirection.Previous);
        }
    }

    /// <summary>The cursor of the select tool: a resize arrow over a handle, the move cross inside a movable selection, else the arrow.</summary>
    private Cursor? SelectionCursorAt(Point position)
    {
        if (_viewModel is not { SelectedAnnotation: not null, CanMoveSelection: true } || _drawingOverlay is not { } overlay)
        {
            return null;
        }

        StandardCursorType? type = overlay.HandleAt(position) switch
        {
            HandleKind.TopLeft or HandleKind.BottomRight => StandardCursorType.TopLeftCorner,
            HandleKind.TopRight or HandleKind.BottomLeft => StandardCursorType.TopRightCorner,
            HandleKind.Top or HandleKind.Bottom => StandardCursorType.TopSide,
            HandleKind.Left or HandleKind.Right => StandardCursorType.LeftSide,
            HandleKind.Start or HandleKind.End => StandardCursorType.Cross,
            _ => overlay.FrameContains(position) ? StandardCursorType.SizeAll : null,
        };
        if (type is not { } cursorType)
        {
            return null;
        }

        var cursors = _selectionCursors ??= [];
        if (!cursors.TryGetValue(cursorType, out var cursor))
        {
            cursor = new Cursor(cursorType);
            cursors[cursorType] = cursor;
        }

        return cursor;
    }

    /// <summary>A drag of the selection or of one of its handles, from the press until the release.</summary>
    private sealed class SelectionDrag(TabViewModel tab, IPointer pointer, PdfAnnotation target, DrawingGeometry original, HandleKind? handle, DisplayPoint start, DisplayPoint handlePosition)
    {
        public TabViewModel Tab { get; } = tab;

        /// <summary>The pointer that drags (captured by the view).</summary>
        public IPointer Pointer { get; } = pointer;

        /// <summary>The selected annotation the drag started on.</summary>
        public PdfAnnotation Target { get; } = target;

        /// <summary>The shape when the drag started.</summary>
        public DrawingGeometry Original { get; } = original;

        /// <summary>The handle dragged, or <see langword="null"/> for the whole drawing.</summary>
        public HandleKind? Handle { get; } = handle;

        /// <summary>Where the press went down, in page points.</summary>
        public DisplayPoint Start { get; } = start;

        /// <summary>Where the dragged handle was at the press (the press point for a move), in page points.</summary>
        public DisplayPoint HandlePosition { get; } = handlePosition;

        /// <summary>Whether the pointer moved by <see cref="ClickSlop"/> or more since the press.</summary>
        public bool Moved { get; set; }

        /// <summary>The shape the drag has come to, or <see langword="null"/> before the first move.</summary>
        public DrawingGeometry? Geometry { get; set; }
    }
}
