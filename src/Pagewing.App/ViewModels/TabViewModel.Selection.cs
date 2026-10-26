using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The selection of the select tool (milestone 10, spec "Bedienung, Auswählen"): one annotation of the document that a click
/// or Tab picked, which the arrows nudge, Delete deletes and Enter opens the comment of; the document view drags it and its
/// handles. Only a highlight, underline, strike-out or drawing that Pagewing may edit is selectable
/// (<see cref="DrawingHitTest.IsSelectable"/>); only a drawing that can be redrawn without loss moves
/// (<see cref="CanMoveSelection"/>). The selection exists in the mode with the select tool only and goes when the tool or
/// the mode ends, the document changes, the view leaves the tab or the annotation disappears.
/// </summary>
/// <remarks>
/// <para>
/// The selection follows its annotation by name (<c>/NM</c>), since the index changes with every replace: after each change
/// of its page it is read again and takes the new shape and index (without a name, by index and kind). The annotations of a
/// page are read on the first click or Tab of the select tool on it (never before the mode, the first sharp page or the
/// tool), through <see cref="AnnotationReader"/> on the PDFium thread, and kept per page until the history moves or the page
/// changes. A click whose page is not read yet is applied when the page arrives, unless a newer click or key came, or the
/// tool, mode or document changed meanwhile: then it is dropped.
/// </para>
/// <para>
/// A nudge (an arrow key) is shown at once (<see cref="SelectionGeometry"/>) and made when the keys rest for
/// <see cref="NudgeDelay"/>: nudges in a row are one step of the history. Another key, another selection, the end of the
/// tool or mode, leaving the tab, undo, redo, a save and the question before closing make a waiting nudge at once
/// (<see cref="FlushNudge"/>); a waiting nudge counts as an unsaved change.
/// </para>
/// </remarks>
public sealed partial class TabViewModel
{
    /// <summary>How long the arrow keys must rest before the nudges since the first one become one step of the history.</summary>
    internal static readonly TimeSpan NudgeDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>The nudge of an arrow key in points, and with Shift.</summary>
    private const double SmallNudge = 1;

    private const double LargeNudge = 10;

    // The annotations of the pages read for the select tool, the document and history version they belong to, and a count
    // per page that moves when the page changed (a read that started before is dropped).
    private readonly Dictionary<int, IReadOnlyList<PdfAnnotation>> _pageAnnotations = [];
    private readonly Dictionary<int, int> _pageAnnotationEpochs = [];
    private PdfDocument? _pageAnnotationsDocument;
    private int _pageAnnotationsVersion;

    private PdfAnnotation? _selectedAnnotation;

    // Counts the clicks and keys that pick a selection: one that waits for its page is dropped when a newer one came.
    private int _selectionRequest;

    // The shape a replace of the selection is on its way to (shown until it is made), the waiting nudge and its timer.
    private PendingGeometry? _pendingGeometry;
    private NudgeRun? _nudge;
    private ITimer? _nudgeTimer;
    private int _nudgeGeneration;

    private RelayCommand? _selectNextCommand;
    private RelayCommand? _selectPreviousCommand;
    private RelayCommand<(int Dx, int Dy, bool Large)>? _nudgeCommand;
    private RelayCommand? _deleteSelectionCommand;

    /// <summary>
    /// Gets the selected annotation as last read (its index is the one after the latest change), or <see langword="null"/>.
    /// </summary>
    public PdfAnnotation? SelectedAnnotation
    {
        get => _selectedAnnotation;
        private set
        {
            if (ReferenceEquals(_selectedAnnotation, value))
            {
                return;
            }

            _selectedAnnotation = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanMoveSelection));
            OnPropertyChanged(nameof(SelectionLockReason));
            OnPropertyChanged(nameof(SelectionGeometry));
            OnSelectionStyleChanged();
            RaiseSelectionCanExecuteChanged();
        }
    }

    /// <summary>
    /// Gets whether the selection can be moved and resized: a drawing that Pagewing can redraw without loss
    /// (<see cref="PdfAnnotation.GeometryEditable"/>). A markup hangs on its text, a locked drawing of another application
    /// offers only its comment and deleting.
    /// </summary>
    public bool CanMoveSelection => _selectedAnnotation is { GeometryEditable: true, SelectionShape: not null } selected && IsDrawKind(selected.Kind);

    /// <summary>
    /// Gets why a selected drawing cannot be moved or changed (another application drew it in a way Pagewing cannot keep), or
    /// <see langword="null"/>; for the toolbar and the "Comments" panel.
    /// </summary>
    public string? SelectionLockReason => _selectedAnnotation is { } selected && IsDrawKind(selected.Kind) && !CanMoveSelection
        ? (selected.Kind == AnnotationKind.TextBox ? Strings.SelectionLockedForeignTextBox : Strings.SelectionLockedForeign)
        : null;

    /// <summary>Tab in the document: selects the next selectable annotation of the page in reading order; after the last one the selection goes.</summary>
    public ICommand SelectNextCommand => _selectNextCommand ??= new RelayCommand(() => _ = WalkSelectionAsync(forward: true), () => CanSelect);

    /// <summary>Shift+Tab in the document: selects the previous one; before the first one the selection goes.</summary>
    public ICommand SelectPreviousCommand => _selectPreviousCommand ??= new RelayCommand(() => _ = WalkSelectionAsync(forward: false), () => CanSelect);

    /// <summary>
    /// An arrow key (parameter: the direction <c>(Dx, Dy)</c>, each -1, 0 or 1, and whether Shift is held): moves a movable
    /// selection by 1 point, with Shift by 10 (kept on the page); nudges in a row are one step (see the remarks of the class).
    /// </summary>
    public ICommand NudgeCommand => _nudgeCommand ??= new RelayCommand<(int Dx, int Dy, bool Large)>(
        step => Nudge(step.Dx, step.Dy, step.Large),
        _ => CanMoveSelection && CanDraw);

    /// <summary>Delete or Backspace in the document: deletes the selection (also a markup or a locked drawing); undo brings it back.</summary>
    public ICommand DeleteSelectionCommand => _deleteSelectionCommand ??= new RelayCommand(DeleteSelection, () => _selectedAnnotation is not null && CanDraw);

    /// <summary>
    /// Gets the shape the selection is shown with: a waiting nudge, a replace on its way, or the shape as read; <see langword="null"/>
    /// without a selection or for a markup.
    /// </summary>
    internal DrawingGeometry? SelectionGeometry =>
        _selectedAnnotation is not { } selected ? null
        : _nudge is { } nudge && SameAnnotation(nudge.Target, selected) ? nudge.Geometry
        : _pendingGeometry is { } pending && SameAnnotation(pending.Target, selected) ? pending.Geometry
        : selected.SelectionShape;

    /// <summary>Gets or sets the clock of the nudge timer (<see cref="NudgeDelay"/>); tests move it by hand.</summary>
    internal TimeProvider SelectionClock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets or sets how the document view drops a drag of the selection or one of its handles (set by the view from the press to
    /// the release): Esc of the mode drops it before the selection (<see cref="EscapeAnnotate"/>). Returns whether there was one.
    /// </summary>
    internal Func<bool>? CancelSelectionDrag { get; set; }

    /// <summary>Gets whether a selection may be picked now: the mode with the select tool on a shown document.</summary>
    internal bool CanSelect => !_disposed && _isAnnotating && _isSelectTool && CanRequestAnnotate();

    /// <summary>Clears the selection; a waiting nudge is made first and a drag of the view is dropped.</summary>
    public void ClearSelection()
    {
        _selectionRequest++;
        if (_selectedAnnotation is null && _nudge is null)
        {
            return;
        }

        FlushNudge();
        CancelSelectionDrag?.Invoke();
        SelectedAnnotation = null;
    }

    /// <summary>
    /// Picks the topmost selectable annotation of <paramref name="page"/> at <paramref name="point"/> (a click of the select
    /// tool); nothing there clears the selection. The page is read first if needed (see the remarks of the class).
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="point">The point in page points.</param>
    /// <param name="tolerance">How far from a stroke a click still hits it, in page points.</param>
    /// <returns>Completes when the click was applied or dropped; never fails.</returns>
    internal async Task SelectAtAsync(int page, DisplayPoint point, double tolerance)
    {
        if (!CanSelect)
        {
            return;
        }

        var request = ++_selectionRequest;
        var annotations = TryGetPageAnnotations(page, out var cached) ? cached : await GetPageAnnotationsAsync(page);
        if (annotations is null || request != _selectionRequest || !CanSelect)
        {
            return;
        }

        Select(DrawingHitTest.Pick(annotations, point, tolerance));
    }

    /// <summary>
    /// Gets whether <paramref name="page"/> has something to select, as far as it was read: <see langword="null"/> while it is
    /// not read (Tab is taken then and acts when it is).
    /// </summary>
    internal bool? HasSelectable(int page) =>
        TryGetPageAnnotations(page, out var annotations) ? annotations.Any(DrawingHitTest.IsSelectable) : null;

    /// <summary>
    /// Replaces the shape of the selection by <paramref name="geometry"/> (the release of a drag): one step of the history,
    /// nothing when the shape stays or the tab cannot take an edit. The new shape is shown until the replace is made.
    /// </summary>
    /// <returns>Completes when the replace was made or refused; never fails.</returns>
    internal Task ReplaceSelectionGeometryAsync(DrawingGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        FlushNudge();
        if (_selectedAnnotation is not { SelectionShape: { } drawing } selected || !CanMoveSelection || !CanDraw || geometry.IsEmpty
            || geometry.Kind != drawing.Kind || SameShape(SelectionGeometry ?? drawing, geometry))
        {
            return Task.CompletedTask;
        }

        return StartReplace(selected, geometry);
    }

    /// <summary>
    /// Makes a waiting nudge now (see the remarks of the class); nothing when there is none. While the tab takes no edit for a
    /// moment (it prints or saves, a card asks about it, editing is locked for an update) the nudge is not dropped: it stays
    /// waiting, still shown and counted as a change, and is tried again after <see cref="NudgeDelay"/> (or at the next key or
    /// flush). It goes only with its document, the mode or the tab.
    /// </summary>
    internal void FlushNudge()
    {
        if (_nudge is not { } nudge)
        {
            return;
        }

        _nudgeGeneration++;
        _nudgeTimer?.Dispose();
        _nudgeTimer = null;
        var changes = !_disposed && nudge.Generation == _documentGeneration && !SameShape(nudge.Original, nudge.Geometry);
        if (changes && _isAnnotating && !CanDraw)
        {
            // Not now: kept, and tried again later.
            StartNudgeTimer();
            return;
        }

        _nudge = null;
        var target = _selectedAnnotation is { } selected && SameAnnotation(selected, nudge.Target) ? selected : nudge.Target;
        if (changes)
        {
            _ = StartReplace(target, nudge.Geometry);
        }

        OnPropertyChanged(nameof(SelectionGeometry));
        SyncFromEditor();
    }

    /// <summary>
    /// Pages of the document changed (an edit, undo or redo; the view passes the event of the session on, UI thread): their
    /// annotations are read again when needed, and a selection on one of them follows its annotation.
    /// </summary>
    internal void OnSelectionPagesInvalidated(IReadOnlySet<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        foreach (var page in pages)
        {
            _pageAnnotations.Remove(page);
            _pageAnnotationEpochs[page] = _pageAnnotationEpochs.GetValueOrDefault(page) + 1;
        }

        if (_selectedAnnotation is { } selected && pages.Contains(selected.Page))
        {
            _ = FollowSelectionAsync(selected);
        }

        // The current page is read again at once too, so Tab after an edit decides from a current list (TabViewModel.Selection
        // keeps the keyboard walk independent of what is read, but a warm list spares the round trip).
        var current = Math.Max(0, CurrentPage - 1);
        if (CanSelect && pages.Contains(current) && _selectedAnnotation?.Page != current)
        {
            _ = GetPageAnnotationsAsync(current);
        }
    }

    /// <summary>Enter in the document: opens the "Comments" panel with the entry of the selection, its comment field focused.</summary>
    internal void OpenSelectionComment()
    {
        if (_selectedAnnotation is not { } selected)
        {
            return;
        }

        FlushNudge();
        if (RequestTextBoxEdit())
        {
            // A text box of Pagewing's: Enter edits its text on the page, its text is its comment.
            return;
        }

        SidePanel = SidePanelKind.Comments;
        Comments.BeginEdit(selected);
    }

    private static bool SameAnnotation(PdfAnnotation a, PdfAnnotation b) =>
        a.Page == b.Page && (a.Name is not null || b.Name is not null ? a.Name == b.Name : a.Index == b.Index && a.Kind == b.Kind);

    private static bool SameShape(DrawingGeometry a, DrawingGeometry b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Kind != b.Kind || a.Width != b.Width || a.Box != b.Box || a.Strokes.Count != b.Strokes.Count)
        {
            return false;
        }

        for (var s = 0; s < a.Strokes.Count; s++)
        {
            if (!a.Strokes[s].SequenceEqual(b.Strokes[s]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The selectable annotations of a page from top to bottom (by their top edge), then from left to right.</summary>
    private static List<PdfAnnotation> ReadingOrder(IReadOnlyList<PdfAnnotation> annotations) =>
        [.. annotations.Where(DrawingHitTest.IsSelectable).OrderBy(a => a.Bounds.Top).ThenBy(a => a.Bounds.Left).ThenBy(a => a.Index)];

    private void Select(PdfAnnotation? annotation)
    {
        if (annotation is null)
        {
            ClearSelection();
            return;
        }

        if (_selectedAnnotation is not { } selected || !SameAnnotation(selected, annotation))
        {
            FlushNudge();
            CancelSelectionDrag?.Invoke();
        }

        SelectedAnnotation = annotation;
    }

    /// <summary>
    /// Walks the selectable annotations of the page of the selection (without one, of the current page) in reading order:
    /// Tab (<paramref name="forward"/>) or Shift+Tab. Completes at once when the page is read, else once it is.
    /// </summary>
    /// <returns>
    /// <see cref="SelectionWalk.Selected"/> when another annotation is selected; <see cref="SelectionWalk.Ended"/> when the walk
    /// went past the last (or before the first) one, the page has none or cannot be read (the selection is gone then: the key
    /// is to move the focus on); <see cref="SelectionWalk.Dropped"/> when a newer click or key, or another tool, mode or
    /// document came meanwhile.
    /// </returns>
    internal async Task<SelectionWalk> WalkSelectionAsync(bool forward)
    {
        if (!CanSelect)
        {
            return SelectionWalk.Dropped;
        }

        var request = ++_selectionRequest;
        var page = _selectedAnnotation?.Page ?? Math.Max(0, CurrentPage - 1);
        var annotations = TryGetPageAnnotations(page, out var cached) ? cached : await GetPageAnnotationsAsync(page);
        if (request != _selectionRequest || !CanSelect)
        {
            return SelectionWalk.Dropped;
        }

        var ordered = annotations is null ? [] : ReadingOrder(annotations);
        var at = _selectedAnnotation is { } selected ? ordered.FindIndex(a => SameAnnotation(a, selected)) : -1;
        var next = at < 0 ? (forward ? 0 : ordered.Count - 1) : at + (forward ? 1 : -1);
        if (next < 0 || next >= ordered.Count)
        {
            // Past the last (or before the first), or nothing there: the selection goes, the keys go on (no trap).
            ClearSelection();
            return SelectionWalk.Ended;
        }

        Select(ordered[next]);
        return SelectionWalk.Selected;
    }

    /// <summary>Gets whether the annotations of <paramref name="page"/> are read and current (a walk there decides at once).</summary>
    internal bool IsPageRead(int page) => TryGetPageAnnotations(page, out _);

    /// <summary>
    /// Gets whether Tab (<paramref name="forward"/>) or Shift+Tab leaves the annotations of the page: the selection is the last
    /// (or first) in reading order; <see langword="false"/> without a selection or while its page is not read.
    /// </summary>
    internal bool SelectionWalkEnds(bool forward)
    {
        if (_selectedAnnotation is not { } selected || !TryGetPageAnnotations(selected.Page, out var annotations))
        {
            return false;
        }

        var ordered = ReadingOrder(annotations);
        var at = ordered.FindIndex(a => SameAnnotation(a, selected));
        return at >= 0 && (forward ? at == ordered.Count - 1 : at == 0);
    }

    private void Nudge(int dx, int dy, bool large)
    {
        if (!CanMoveSelection || !CanDraw || _selectedAnnotation is not { } selected || SelectionGeometry is not { } shape
            || PageRectOf(selected.Page) is not { } page)
        {
            return;
        }

        var step = large ? LargeNudge : SmallNudge;
        var nudge = _nudge ??= new NudgeRun(selected, shape, _documentGeneration);
        nudge.Geometry = DrawingTransform.Move(nudge.Geometry, Math.Sign(dx) * step, Math.Sign(dy) * step, page);

        // The timer starts again with every press: the step is made once the keys rest.
        StartNudgeTimer();
        OnPropertyChanged(nameof(SelectionGeometry));
        SyncFromEditor();
    }

    private void StartNudgeTimer()
    {
        var generation = ++_nudgeGeneration;
        _nudgeTimer?.Dispose();
        _nudgeTimer = SelectionClock.CreateTimer(_ => PostNudgeFlush(generation), null, NudgeDelay, Timeout.InfiniteTimeSpan);
    }

    // Any thread (the timer's): must not wait and must never throw.
    private void PostNudgeFlush(int generation)
    {
        try
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _nudgeGeneration)
                {
                    FlushNudge();
                }
            });
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nothing left to make.
        }
    }

    private void DeleteSelection()
    {
        if (_selectedAnnotation is not { } selected || !CanDraw)
        {
            return;
        }

        // A waiting nudge is made first (the editor runs the operations in this order), then the annotation goes.
        FlushNudge();
        var target = _selectedAnnotation ?? selected;
        if (WaitsForReplaceWithoutName(target))
        {
            return;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        ClearSelection();
        _ = RunEditAsync(editor => editor.DeleteAsync(locator));
    }

    private Task StartReplace(PdfAnnotation target, DrawingGeometry geometry)
    {
        if (!CanDraw || WaitsForReplaceWithoutName(target))
        {
            return Task.CompletedTask;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        var pending = new PendingGeometry(target, geometry);
        _pendingGeometry = pending;
        OnPropertyChanged(nameof(SelectionGeometry));
        return RunEditAsync(async editor =>
        {
            try
            {
                var replaced = target is { Kind: AnnotationKind.TextBox, TextBox: { } box }
                    ? await editor.ReplaceTextBoxAsync(locator, box with { Box = geometry.Box })
                    : await editor.ReplaceGeometryAsync(locator, geometry);
                if (_selectedAnnotation is { } now && SameAnnotation(now, target))
                {
                    SelectedAnnotation = replaced;
                }
            }
            finally
            {
                if (ReferenceEquals(_pendingGeometry, pending))
                {
                    _pendingGeometry = null;
                    OnPropertyChanged(nameof(SelectionGeometry));
                }
            }
        });
    }

    /// <summary>
    /// Whether an annotation without a name is on its way to another shape: it is found by its index, which the replace
    /// changes, so nothing else is done to it until the replace is made (and it has its new index and a name).
    /// </summary>
    private bool WaitsForReplaceWithoutName(PdfAnnotation target) => target.Name is null && (_pendingGeometry is not null || _pendingColor is not null);

    /// <summary>The page of the selection changed: it is read again, and the selection takes the annotation as it is now or goes with it.</summary>
    private async Task FollowSelectionAsync(PdfAnnotation selected)
    {
        var annotations = await GetPageAnnotationsAsync(selected.Page);
        if (annotations is null || _selectedAnnotation is not { } now || !SameAnnotation(now, selected))
        {
            return;
        }

        var found = annotations.FirstOrDefault(a => SameAnnotation(a, now));
        if (found is null || !DrawingHitTest.IsSelectable(found))
        {
            ClearSelection();
        }
        else
        {
            SelectedAnnotation = found;
        }
    }

    private bool TryGetPageAnnotations(int page, out IReadOnlyList<PdfAnnotation> annotations)
    {
        DropStalePageAnnotations();
        return _pageAnnotations.TryGetValue(page, out annotations!);
    }

    /// <summary>
    /// The annotations of a page for the select tool: kept, or read now (read again once when the page changed meanwhile);
    /// <see langword="null"/> when the tool may not select now, the document went or the page cannot be read.
    /// </summary>
    private async Task<IReadOnlyList<PdfAnnotation>?> GetPageAnnotationsAsync(int page)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!CanSelect || _document is not { } document || page < 0 || page >= PageCount)
            {
                return null;
            }

            if (TryGetPageAnnotations(page, out var cached))
            {
                return cached;
            }

            var epoch = _pageAnnotationEpochs.GetValueOrDefault(page);
            var version = EditVersion;
            IReadOnlyList<PdfAnnotation> annotations;
            try
            {
                annotations = await AnnotationReader(document, page, CancellationToken.None);
            }
            catch (Exception)
            {
                // The document went meanwhile, or the page is damaged: nothing to select there.
                return null;
            }

            if (_disposed || !ReferenceEquals(document, _document))
            {
                return null;
            }

            if (epoch != _pageAnnotationEpochs.GetValueOrDefault(page) || version != EditVersion)
            {
                // The page changed while it was read: once more.
                continue;
            }

            DropStalePageAnnotations();
            _pageAnnotations[page] = annotations;
            return annotations;
        }

        return null;
    }

    /// <summary>What was read belongs to one document and one version of the history; anything else is read again.</summary>
    private void DropStalePageAnnotations()
    {
        var version = EditVersion;
        if (!ReferenceEquals(_pageAnnotationsDocument, _document) || _pageAnnotationsVersion != version)
        {
            _pageAnnotations.Clear();
            _pageAnnotationsDocument = _document;
            _pageAnnotationsVersion = version;
        }
    }

    /// <summary>The displayed page in page points, once its size is loaded.</summary>
    private PageRect? PageRectOf(int page)
    {
        var sizes = _session.LoadedSizes;
        return page >= 0 && page < sizes.Count ? new PageRect(0, 0, sizes[page].Width, sizes[page].Height) : null;
    }

    private void RaiseSelectionCanExecuteChanged()
    {
        _selectNextCommand?.RaiseCanExecuteChanged();
        _selectPreviousCommand?.RaiseCanExecuteChanged();
        _nudgeCommand?.RaiseCanExecuteChanged();
        _deleteSelectionCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>The tab is disposed: a waiting nudge goes with it.</summary>
    private void DisposeSelection()
    {
        _nudgeTimer?.Dispose();
        _nudgeTimer = null;
        _nudge = null;
        _pageAnnotations.Clear();
    }

    /// <summary>What a walk of Tab or Shift+Tab did (<see cref="WalkSelectionAsync"/>).</summary>
    internal enum SelectionWalk
    {
        /// <summary>Another annotation is selected.</summary>
        Selected,

        /// <summary>Past the last or before the first, or nothing to select: the selection is gone, the focus is to move on.</summary>
        Ended,

        /// <summary>A newer click or key, or another tool, mode or document, came meanwhile: nothing happened.</summary>
        Dropped,
    }

    /// <summary>A shape a replace of <paramref name="Target"/> is on its way to.</summary>
    private sealed record PendingGeometry(PdfAnnotation Target, DrawingGeometry Geometry);

    /// <summary>The nudges in a row of one selection: the shape before the first one and the shape now.</summary>
    private sealed class NudgeRun(PdfAnnotation target, DrawingGeometry original, int generation)
    {
        public PdfAnnotation Target { get; } = target;

        public DrawingGeometry Original { get; } = original;

        public DrawingGeometry Geometry { get; set; } = original;

        /// <summary>The document the nudges were made on (<see cref="_documentGeneration"/>): another one makes nothing of them.</summary>
        public int Generation { get; } = generation;
    }
}
