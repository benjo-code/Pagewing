using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.App.Controls;

/// <summary>
/// The mode "Organize pages" of <see cref="ThumbnailList"/> (milestone 13, design spec 5.3): while the tab's plan is open the
/// list shows the plan, one entry per position, not the pages of the document. The entries are updated in place (entry
/// <c>i</c> always stands for position <c>i</c>), so a staged change keeps the container, the focus and the scroll position of
/// the list. A thumbnail comes from the tab's <see cref="ThumbnailSource"/> by the source index of the entry and is drawn
/// turned by the entry's rotation; a blank page is drawn, a page of another file shows a stand-in tile. The list selects
/// several entries (Ctrl, Shift), mirrors the selection to <see cref="TabViewModel.SelectedPagePositions"/>, moves the
/// selected pages by dragging (an insertion marker shows the gap) and has a context menu with the tools of the mode.
/// </summary>
internal sealed partial class ThumbnailList
{
    /// <summary>The way in DIPs the pointer has to travel with the button down before the press becomes a drag.</summary>
    private const double DragThreshold = 6;

    /// <summary>The thickness in DIPs of the insertion marker: the same bar as the drop mark of a tab being dragged.</summary>
    private const double MarkerThickness = 2;

    /// <summary>Half the space between two entries: the marker lies in the middle of the gap.</summary>
    private const double HalfGap = 8;

    /// <summary>The distance to the top or bottom edge in DIPs within which a drag scrolls the list.</summary>
    private const double ScrollEdge = 24;

    private ObservableCollection<ThumbnailItem>? _planItems;
    private PageEditPlan? _shownPlan;
    private bool _planMode;
    private bool _syncingSelection;

    // The press that may become a drag: the entry under it, where, and whether it should narrow the selection when released.
    private int _pressIndex = -1;
    private Point _pressPoint;
    private bool _collapseOnRelease;
    private bool _dragging;
    private IPointer? _dragPointer;
    private int _insertionGap = -1;
    private Rect? _insertionMarker;

    /// <summary>Gets whether the list shows the plan of the mode "Organize pages" (for tests).</summary>
    internal bool IsOrganizing => _planMode;

    /// <summary>Gets whether a drag of the selected pages is under way (for tests).</summary>
    internal bool IsDragging => _dragging;

    /// <summary>Gets the gap a drop would put the pages in (0 before the first entry), or <see langword="null"/> when no drag is under way (for tests).</summary>
    internal int? InsertionGap => _dragging ? _insertionGap : null;

    /// <summary>Gets the insertion marker in the coordinates of the list, or <see langword="null"/> when no drag is under way.</summary>
    internal Rect? InsertionMarker => _dragging ? _insertionMarker : null;

    /// <summary>Sets up what the mode needs (called by the constructor): the handlers of the press, the move and the release, Esc.</summary>
    private void InitializeOrganize()
    {
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnPointerMovedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedTunnel, RoutingStrategies.Tunnel);
        PointerCaptureLost += (_, _) => CancelDrag();
        SelectionChanged += OnOrganizeSelectionChanged;

        // Esc ends a drag and drops nothing. A key binding of the list lies nearer the focus than the window's, so the key
        // goes on to the window when no drag is under way.
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(CancelDrag, () => _dragging) });
    }

    /// <summary>Shows the plan or the document, whichever the tab asks for now (mode entered, left, plan gone).</summary>
    private void SyncMode()
    {
        var want = _tab is { IsOrganizing: true, PagePlan: not null } && _document is not null && _docItems.Length > 0;
        if (want && !_planMode)
        {
            EnterPlanMode();
        }
        else if (!want && _planMode)
        {
            LeavePlanMode(showDocument: true);
        }
    }

    private void EnterPlanMode()
    {
        var tab = _tab!;
        var plan = tab.PagePlan!;

        // The entries of the plan have their own bitmaps (the thumbnails are drawn again from the cache by the next plan).
        StopWork();
        ReleaseBitmaps(keep: null);
        _stale.Clear();
        var items = new List<ThumbnailItem>(plan.Slots.Count);
        for (var position = 0; position < plan.Slots.Count; position++)
        {
            items.Add(NewPlanItem(position, plan.Slots[position]));
        }

        var shown = new ObservableCollection<ThumbnailItem>(items);
        _planItems = shown;
        _items = shown;
        _shownPlan = plan;
        _planMode = true;
        UpdateCopyMarks();
        Classes.Set("organizing", true);
        SelectionMode = SelectionMode.Multiple;
        ItemsSource = shown;
        ContextMenu = BuildContextMenu();
        ApplySelectionFromTab();

        // The list keeps the page it showed in view.
        var current = Math.Clamp(tab.CurrentPage - 1, 0, shown.Count - 1);
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(_items, shown))
                {
                    ScrollIntoView(current);
                }
            },
            DispatcherPriority.Loaded);
        SchedulePlan();
    }

    /// <summary>Back to the pages of the document (<paramref name="showDocument"/>), or just forgetting the plan (the document goes).</summary>
    private void LeavePlanMode(bool showDocument)
    {
        if (!_planMode)
        {
            return;
        }

        CancelDrag();
        StopWork();
        DetachForeign();
        ReleaseBitmaps(keep: null);
        _stale.Clear();
        _planMode = false;
        _planItems = null;
        _shownPlan = null;
        _syncingSelection = true;
        try
        {
            Classes.Set("organizing", false);
            ContextMenu = null;
            SelectionMode = SelectionMode.Single;
            if (showDocument)
            {
                _items = _docItems;
                ItemsSource = _docItems;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        if (showDocument)
        {
            UpdateCurrent(scroll: true);
            SchedulePlan();
        }
    }

    /// <summary>The plan of the tab changed: the entries follow it in place, then the selection.</summary>
    private void SyncPlan()
    {
        if (!_planMode || _tab?.PagePlan is not { } plan)
        {
            SyncMode();
            return;
        }

        if (ReferenceEquals(plan, _shownPlan))
        {
            return;
        }

        CancelDrag();
        var slots = plan.Slots;
        var items = _planItems!;
        _syncingSelection = true;
        try
        {
            var common = Math.Min(slots.Count, items.Count);
            for (var position = 0; position < common; position++)
            {
                if (items[position].Slot != slots[position])
                {
                    var item = items[position];
                    var page = item.RenderPage;
                    var foreign = item.ForeignKey;
                    item.Assign(position, slots[position], FormatOf(slots[position]));

                    // A turned page keeps its picture; an entry that stands for another page gets that one from the cache.
                    if (item.RenderPage != page || item.ForeignKey != foreign)
                    {
                        DropBitmap(position);
                    }
                }
            }

            while (items.Count > slots.Count)
            {
                DropBitmap(items.Count - 1);
                items.RemoveAt(items.Count - 1);
            }

            for (var position = common; position < slots.Count; position++)
            {
                items.Add(NewPlanItem(position, slots[position]));
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        _shownPlan = plan;
        UpdateCopyMarks();
        ApplySelectionFromTab();
        SchedulePlan();
    }

    /// <summary>The later entries of a page of the document that occurs more than once are copies and say so (one pass over the plan).</summary>
    private void UpdateCopyMarks()
    {
        var seen = new HashSet<(int Source, int Page)>();
        foreach (var item in _planItems!)
        {
            item.IsCopy = item.Slot is { Blank: null, SourceIndex: >= 0 } slot && !seen.Add((slot.Source.Value, slot.SourceIndex));
        }
    }

    private ThumbnailItem NewPlanItem(int position, PageSlot slot)
    {
        var item = new ThumbnailItem(position + 1, FormatOf(slot), _frameWidth);
        item.Assign(position, slot, FormatOf(slot));
        return item;
    }

    private void DropBitmap(int index)
    {
        _withBitmaps.Remove(index);
        _stale.Remove(index);
        if (index < _items.Count && _items[index].Bitmap is { } bitmap)
        {
            _items[index].Bitmap = null;
            bitmap.Dispose();
        }
    }

    /// <summary>
    /// The size of the page an entry shows before it is turned: a blank page its own, a page of the document the size the
    /// session knows (the format of the first page until it does), a page of an inserted file the size that file reported
    /// (the A4 stand-in until it has).
    /// </summary>
    private PdfPageSize FormatOf(PageSlot slot)
    {
        var sizes = _tab!.Session.LoadedSizes;
        if (slot.Blank is { } blank)
        {
            var size = new PdfPageSize(blank.Width, blank.Height);
            return IsUsable(size) ? size : StandInSize;
        }

        if (slot.Source.Value == 0)
        {
            if (slot.SourceIndex >= 0 && slot.SourceIndex < _knownSizes && slot.SourceIndex < sizes.Count && IsUsable(sizes[slot.SourceIndex]))
            {
                return sizes[slot.SourceIndex];
            }

            return sizes.Count > 0 && IsUsable(sizes[0]) ? sizes[0] : StandInSize;
        }

        return ForeignFormat(slot);
    }

    /// <summary>Makes the selection of the list the tab's (a command selected pages, a change moved them). Waits for the entries first.</summary>
    private void ApplySelectionFromTab()
    {
        if (!_planMode || _tab is null || !ReferenceEquals(_shownPlan, _tab.PagePlan))
        {
            return;
        }

        var wanted = _tab.SelectedPagePositions;
        var shown = Selection.SelectedIndexes;
        if (shown.Count == wanted.Count && shown.All(wanted.Contains))
        {
            return;
        }

        _syncingSelection = true;
        try
        {
            Selection.Clear();
            foreach (var position in wanted)
            {
                if (position >= 0 && position < _items.Count)
                {
                    Selection.Select(position);
                }
            }
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    private void OnOrganizeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_planMode)
        {
            return;
        }

        foreach (var item in e.RemovedItems.OfType<ThumbnailItem>())
        {
            item.IsSelected = false;
        }

        foreach (var item in e.AddedItems.OfType<ThumbnailItem>())
        {
            item.IsSelected = true;
        }

        if (!_syncingSelection)
        {
            _tab?.SelectPages(Selection.SelectedIndexes);
        }
    }

    // --- Drag and drop --------------------------------------------------------------------------------------------------

    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _pressIndex = -1;
        _collapseOnRelease = false;
        if (!_planMode || e.Handled || e.Pointer.Type == PointerType.Touch
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } container
            || IndexFromContainer(container) is not (>= 0 and var index))
        {
            return;
        }

        _pressIndex = index;
        _pressPoint = e.GetPosition(this);

        // A press on a page of a selection of several keeps the selection, so that it can travel together; a release without
        // a drag narrows it to this page, as a plain click does anywhere.
        if (e.KeyModifiers == KeyModifiers.None && Selection.IsSelected(index) && Selection.Count > 1)
        {
            _collapseOnRelease = true;
            e.Handled = true;
            container.Focus(NavigationMethod.Pointer);
        }
    }

    private void OnPointerMovedTunnel(object? sender, PointerEventArgs e)
    {
        if (!_planMode || _pressIndex < 0)
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelDrag();
            return;
        }

        var point = e.GetPosition(this);
        if (!_dragging)
        {
            if (Math.Abs(point.X - _pressPoint.X) < DragThreshold && Math.Abs(point.Y - _pressPoint.Y) < DragThreshold)
            {
                return;
            }

            // The pages that travel are the selected ones; a press that does not select its own page (Ctrl+click took it out) is none.
            if (_tab is null || !_tab.SelectedPagePositions.Contains(_pressIndex) || Selection.Count == 0)
            {
                _pressIndex = -1;
                return;
            }

            _dragging = true;
            _collapseOnRelease = false;
            _dragPointer = e.Pointer;
            e.Pointer.Capture(this);
        }

        ScrollNearEdge(point);
        _insertionGap = GapAt(point);
        _insertionMarker = MarkerFor(_insertionGap);
        InvalidateVisual();
        e.Handled = true;
    }

    private void OnPointerReleasedTunnel(object? sender, PointerReleasedEventArgs e)
    {
        var index = _pressIndex;
        _pressIndex = -1;
        if (!_planMode || index < 0)
        {
            return;
        }

        if (_dragging)
        {
            var gap = GapAt(e.GetPosition(this));
            EndDrag();
            _dragPointer = null;
            e.Pointer.Capture(null);
            _tab?.MoveSelectionTo(gap);
            e.Handled = true;
        }
        else if (_collapseOnRelease)
        {
            _collapseOnRelease = false;
            Selection.Clear();
            Selection.Select(index);
        }
    }

    /// <summary>Ends a drag without moving anything.</summary>
    private void CancelDrag()
    {
        _pressIndex = -1;
        _collapseOnRelease = false;
        if (_dragging)
        {
            EndDrag();
            _dragPointer?.Capture(null);
        }

        _dragPointer = null;
    }

    private void EndDrag()
    {
        _dragging = false;
        _insertionGap = -1;
        _insertionMarker = null;
        InvalidateVisual();
    }

    private void ScrollNearEdge(Point point)
    {
        if (this.FindDescendantOfType<ScrollViewer>() is not { } scroller)
        {
            return;
        }

        if (point.Y < ScrollEdge)
        {
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Offset.Y - ScrollEdge));
        }
        else if (point.Y > Bounds.Height - ScrollEdge)
        {
            scroller.Offset = new Vector(scroller.Offset.X, scroller.Offset.Y + ScrollEdge);
        }
    }

    /// <summary>
    /// The gap a drop at <paramref name="point"/> (list coordinates) would use: before or after the entry nearest to it, by
    /// the half of the entry the point lies in (the upper half in one column, the left half in several).
    /// </summary>
    private int GapAt(Point point)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        var bestBounds = default(Rect);
        foreach (var container in GetRealizedContainers())
        {
            var index = IndexFromContainer(container);
            if (index < 0 || BoundsOf(container) is not { } bounds)
            {
                continue;
            }

            var dx = Math.Max(Math.Max(bounds.Left - point.X, 0), point.X - bounds.Right);
            var dy = Math.Max(Math.Max(bounds.Top - point.Y, 0), point.Y - bounds.Bottom);
            var distance = (dx * dx) + (dy * dy);
            if (distance < bestDistance)
            {
                best = index;
                bestDistance = distance;
                bestBounds = bounds;
            }
        }

        if (best < 0)
        {
            return _items.Count;
        }

        var after = Columns > 1 ? point.X > bestBounds.Center.X : point.Y > bestBounds.Center.Y;
        return best + (after ? 1 : 0);
    }

    private Rect? BoundsOf(Control container) =>
        container.TranslatePoint(default, this) is { } origin ? new Rect(origin, container.Bounds.Size) : null;

    /// <summary>The bar that shows <paramref name="gap"/>: in the middle of the space before the entry there (or after the one before it), across the column or between the columns.</summary>
    private Rect? MarkerFor(int gap)
    {
        if (gap < _items.Count && ContainerFromIndex(gap) is { } next && BoundsOf(next) is { } after)
        {
            return Columns > 1
                ? new Rect(after.Left - HalfGap - (MarkerThickness / 2), after.Top, MarkerThickness, after.Height)
                : new Rect(after.Left, after.Top - HalfGap - (MarkerThickness / 2), after.Width, MarkerThickness);
        }

        if (gap > 0 && ContainerFromIndex(gap - 1) is { } previous && BoundsOf(previous) is { } before)
        {
            return Columns > 1
                ? new Rect(before.Right + HalfGap - (MarkerThickness / 2), before.Top, MarkerThickness, before.Height)
                : new Rect(before.Left, before.Bottom + HalfGap - (MarkerThickness / 2), before.Width, MarkerThickness);
        }

        return null;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_dragging && _insertionMarker is { } marker
            && this.TryFindResource("Pw.Brush.Accent.Default", ActualThemeVariant, out var brush) && brush is IBrush fill)
        {
            context.FillRectangle(fill, marker);
        }
    }

    // --- Context menu ---------------------------------------------------------------------------------------------------

    /// <summary>The tools of the mode as a menu on the list (right click, the menu key, Shift+F10); its entries show the keys of the catalog.</summary>
    private ContextMenu BuildContextMenu()
    {
        var tab = _tab!;
        var catalog = (TopLevel.GetTopLevel(this)?.DataContext as MainViewModel)?.Commands;
        MenuItem Entry(string header, System.Windows.Input.ICommand command, string id, object? parameter = null)
        {
            var item = new MenuItem { Header = header, Command = command, CommandParameter = parameter };
            if (catalog?.ById(id)?.Gesture is { } gesture)
            {
                item.InputGesture = gesture;
            }

            return item;
        }

        return new ContextMenu
        {
            ItemsSource = new Control[]
            {
                Entry(Strings.CommandRotatePagesLeft, tab.RotateSelectionCommand, "pages.rotateLeft", -90),
                Entry(Strings.CommandRotatePagesRight, tab.RotateSelectionCommand, "pages.rotateRight", 90),
                new Separator(),
                Entry(Strings.CommandMovePagesEarlier, tab.MoveSelectionEarlierCommand, "pages.moveEarlier"),
                Entry(Strings.CommandMovePagesLater, tab.MoveSelectionLaterCommand, "pages.moveLater"),
                new Separator(),
                Entry(Strings.CommandDuplicatePages, tab.DuplicateSelectionCommand, "pages.duplicate"),
                Entry(Strings.CommandInsertBlankPage, tab.InsertBlankPageCommand, "pages.insertBlank"),
                Entry(Strings.CommandInsertFile, tab.InsertFileCommand, "pages.insertFile"),
                Entry(Strings.CommandDeletePages, tab.DeletePagesCommand, "pages.delete"),
            },
        };
    }
}
