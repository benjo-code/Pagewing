using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The mode "Organize pages" of the tab (milestone 13, design spec 5.1 and 5.3): the plan of the pages the document will
/// have after "Apply" (<see cref="PagePlan"/>, with its undo history), the selection of pages in the list, and the commands
/// that stage a change. Nothing is written and the document the tab shows stays as it is until the plan is applied (the
/// apply flow is a later task). The plan belongs to the tab: it moves with it into another window and ends when another
/// document replaces the open one. While the mode is on, annotations and form filling are locked (a page number must not
/// change under them, <see cref="PageEditsBlockedReason"/>), and extracting, splitting and exporting are locked while the
/// plan changes anything or the document has unsaved changes (<see cref="PageOutputBlockedReason"/>).
/// </summary>
/// <remarks>
/// Nothing of it is made before the mode is first entered: the plan and its history do not exist (<see cref="PageHistory"/> is
/// <see langword="null"/>), and the commands of the mode are made by the first access. Entering needs the first sharp page.
/// </remarks>
public sealed partial class TabViewModel
{
    // A4 portrait in points, for a blank page beside no page of a known size.
    private static readonly BlankPage DefaultBlankPage = new(595, 842);

    private bool _isOrganizing;
    private PageEditHistory? _pageHistory;
    private IReadOnlySet<int> _selectedPages = NoSelection;

    // The versions of histories that were dropped (plus one each), so that ChangeVersion never goes back.
    private int _pageVersionCarry;
    private int _lastPageSource;
    private string? _pageEditNotice;
    private RewriteExpectation? _expectedRewrite;

    private RelayCommand? _organizeCommand;
    private RelayCommand<int>? _rotateSelectionCommand;
    private RelayCommand? _deletePagesCommand;
    private RelayCommand? _duplicateSelectionCommand;
    private RelayCommand? _insertBlankPageCommand;
    private RelayCommand? _undoPagesCommand;
    private RelayCommand? _redoPagesCommand;
    private RelayCommand? _discardPageEditsCommand;
    private RelayCommand? _dismissPageEditNoticeCommand;
    private RelayCommand? _moveEarlierCommand;
    private RelayCommand? _moveLaterCommand;
    private RelayCommand? _applyPageEditsCommand;

    private static IReadOnlySet<int> NoSelection { get; } = new ReadOnlySet<int>(new HashSet<int>());

    /// <summary>
    /// Gets whether the mode "Organize pages" is on: the pages panel shows the plan and its tools. One of the modes at a time
    /// (entering ends "Comment" and "Fill in form").
    /// </summary>
    public bool IsOrganizing
    {
        get => _isOrganizing;
        private set
        {
            if (SetProperty(ref _isOrganizing, value))
            {
                // The other modes read it (CanRequestAnnotate, CanBeginFill) and show the reason.
                OnAnnotateAvailabilityChanged();
                RaisePageStateChanged();
            }
        }
    }

    /// <summary>
    /// Gets the history of the page plan (<see cref="PageEditHistory.Current"/> is the plan), or <see langword="null"/> when the
    /// mode is off. Made when the mode is entered, dropped when it ends; never in the startup path.
    /// </summary>
    public PageEditHistory? PageHistory => _pageHistory;

    /// <summary>Gets the plan as it is now, or <see langword="null"/> when the mode is off; changes with every staged change, undo and redo.</summary>
    public PageEditPlan? PagePlan => _pageHistory?.Current;

    /// <summary>
    /// Gets whether the plan differs from the document (<see cref="PageEditPlan.IsIdentity"/> is false): an unsaved change that
    /// closing and the update ask about, and that "Apply" writes. A plan turned back to what it was is none.
    /// </summary>
    public bool HasPendingPageEdits => _pageHistory is { } history && !history.Current.IsIdentity;

    /// <summary>
    /// Gets the positions (0-based, in the plan) of the selected pages, a set. Only in the mode; cleared by everything that
    /// changes what the positions mean (delete, undo, redo), set by the staged changes to the pages they left behind.
    /// </summary>
    public IReadOnlySet<int> SelectedPagePositions => _selectedPages;

    /// <summary>
    /// Gets why the last staged change was refused (the last page cannot go, the plan is full), or <see langword="null"/>:
    /// cleared by the next change that works and by <see cref="DismissPageEditNoticeCommand"/>.
    /// </summary>
    public string? PageEditNotice
    {
        get => _pageEditNotice;
        private set => SetProperty(ref _pageEditNotice, value);
    }

    /// <summary>
    /// Gets why annotating and filling in a form are locked, or <see langword="null"/> while the mode is off: page changes
    /// wait for "Apply" (<see cref="HasPendingPageEdits"/>), or the mode is on without a change and has to end first. The
    /// panels show it where they would offer the modes.
    /// </summary>
    public string? PageEditsBlockedReason => !_isOrganizing ? null : HasPendingPageEdits ? Strings.PageEditsPending : Strings.PageOrganizeActive;

    /// <summary>
    /// Gets why the mode cannot be entered, or <see langword="null"/> (also while it is on): no sharp page shown yet, or more
    /// pages than a plan can hold (<see cref="PageEditLimits.MaxSlots"/>), or the mode "Redact" is on
    /// (<see cref="RedactionLockReason"/>).
    /// </summary>
    public string? OrganizeUnavailableReason =>
        _isOrganizing ? null
        : _isRedacting ? RedactionLockReason
        : _isTextEditing ? TextEditActiveReason
        : _disposed || !IsReady || _document is null || !_hasShownSharpPage ? Strings.PageOrganizeNotReady
        : OrganizeUnavailableReasonFor(PageCount);

    /// <summary>Gets whether the mode is on or can be entered (<see cref="OrganizeUnavailableReason"/> is <see langword="null"/>).</summary>
    public bool CanOrganize => OrganizeUnavailableReason is null;

    /// <summary>
    /// Gets why extracting pages, splitting and exporting images cannot run now, or <see langword="null"/>: they read the open
    /// document without the plan, so a plan that changes anything has to be applied or discarded first, and unsaved changes
    /// of the document (annotations, form values) saved first, or the result would lack what the view shows. A plan that
    /// equals the document is no obstacle.
    /// </summary>
    public string? PageOutputBlockedReason =>
        _isOcrRunning ? Strings.OcrBlockedRunning
        : HasPendingPageEdits ? Strings.PageOutputBlockedPlan
        : HasPendingRedactions ? Strings.PageOutputBlockedMarks
        : AsksAboutDocumentChanges ? Strings.PageOutputBlockedUnsaved
        : null;

    /// <summary>Gets whether extracting, splitting and exporting images may run now (an open document, <see cref="PageOutputBlockedReason"/> is <see langword="null"/>).</summary>
    public bool CanRunPageOutput => !_disposed && IsReady && _document is not null && PageOutputBlockedReason is null;

    /// <summary>
    /// The command "Organize pages" (catalog, Ctrl+Shift+O): enters the mode, or leaves it while no page change is pending
    /// (with one, "Apply" or "Discard" ends it).
    /// </summary>
    public ICommand OrganizeCommand => _organizeCommand ??= new RelayCommand(ToggleOrganize, CanToggleOrganize);

    /// <summary>Turns the selected pages (parameter: degrees, a multiple of 90; negative turns left) in the plan.</summary>
    public RelayCommand<int> RotateSelectionCommand => _rotateSelectionCommand ??= new RelayCommand<int>(
        degrees => RotateSelection(degrees),
        degrees => CanEditPages && _selectedPages.Count > 0 && degrees % 90 == 0);

    /// <summary>Removes the selected pages from the plan; the last page of the plan is refused with a reason (<see cref="PageEditNotice"/>).</summary>
    public ICommand DeletePagesCommand => _deletePagesCommand ??= new RelayCommand(DeletePages, () => CanEditPages && _selectedPages.Count > 0);

    /// <summary>Inserts a copy of each selected page right after it and selects the copies.</summary>
    public ICommand DuplicateSelectionCommand => _duplicateSelectionCommand ??= new RelayCommand(DuplicateSelection, () => CanEditPages && _selectedPages.Count > 0);

    /// <summary>
    /// Inserts a blank page after the last selected page, or at the end without a selection, as large as the page before it
    /// (else the page after it, else A4), and selects it.
    /// </summary>
    public ICommand InsertBlankPageCommand => _insertBlankPageCommand ??= new RelayCommand(InsertBlankPage, () => CanEditPages);

    /// <summary>Undoes the newest staged change (Ctrl+Z does this through <see cref="UndoCommand"/> in the mode).</summary>
    public ICommand UndoPagesCommand => _undoPagesCommand ??= new RelayCommand(UndoPages, () => CanStepPages(undo: true));

    /// <summary>Applies the newest undone staged change again (Ctrl+Y through <see cref="RedoCommand"/> in the mode).</summary>
    public ICommand RedoPagesCommand => _redoPagesCommand ??= new RelayCommand(RedoPages, () => CanStepPages(undo: false));

    /// <summary>
    /// Drops the plan with every staged change and leaves the mode. The document was never touched. The button of the toolbar
    /// asks nothing: a view that wants a confirmation puts it in front.
    /// </summary>
    public ICommand DiscardPageEditsCommand => _discardPageEditsCommand ??= new RelayCommand(DropPlan, () => CanEditPages);

    /// <summary>Closes the notice about a refused change (<see cref="PageEditNotice"/>).</summary>
    public ICommand DismissPageEditNoticeCommand => _dismissPageEditNoticeCommand ??= new RelayCommand(() => PageEditNotice = null);

    /// <summary>Moves the selected pages one position towards the start of the list (see <see cref="MoveSelectionBy"/>).</summary>
    public ICommand MoveSelectionEarlierCommand => _moveEarlierCommand ??= new RelayCommand(
        () => MoveSelectionBy(-1),
        () => CanEditPages && _selectedPages.Count > 0 && _selectedPages.Min() > 0);

    /// <summary>Moves the selected pages one position towards the end of the list (see <see cref="MoveSelectionBy"/>).</summary>
    public ICommand MoveSelectionLaterCommand => _moveLaterCommand ??= new RelayCommand(
        () => MoveSelectionBy(1),
        () => CanEditPages && _selectedPages.Count > 0 && _selectedPages.Max() < _pageHistory!.Current.Slots.Count - 1);

    /// <summary>
    /// The button "Apply" of the pages panel (and the catalog's <c>pages.apply</c>): asks the window to write the staged page
    /// changes (<see cref="PageApplyRequested"/>). It runs only with a change pending. What it does is the window's: it asks
    /// what to do about unsaved changes, shows what the rewrite loses and saves.
    /// </summary>
    public ICommand ApplyPageEditsCommand => _applyPageEditsCommand ??= new RelayCommand(
        () => PageApplyRequested?.Invoke(),
        () => CanEditPages && HasPendingPageEdits && !_isSaving && !_isPrinting);

    /// <summary>Raised by <see cref="ApplyPageEditsCommand"/>: the user wants the staged page changes written.</summary>
    internal event Action? PageApplyRequested;

    /// <summary>
    /// Whether the window may start applying the plan now (the flow of <see cref="MainViewModel.ApplyPageEditsAsync"/>): a plan
    /// that changes something, an open document, and no save, no print job, no card about the tab and no update lock. The
    /// button and Ctrl+S ask the same; <see cref="SaveBlockedReason"/> says why while a print job runs.
    /// </summary>
    internal bool CanApplyPages =>
        !_disposed && IsReady && _document is not null && HasPendingPageEdits && !_isSaving && !_isPrinting && !_isAskedAbout && !IsEditingFrozen;

    /// <summary>
    /// Tells the tab that the document the session is about to adopt is the rewritten file of the plan just applied
    /// (<see cref="Core.Pages.PageEditApplier"/>): the tab then keeps zoom, layout and panel, and shows the page that follows
    /// <paramref name="page"/>, instead of treating it as a newly opened file. Any thread; the claim holds for exactly that document.
    /// </summary>
    /// <param name="rewritten">The checked, reopened document.</param>
    /// <param name="page">The 0-based page of the new document to show (<see cref="PageEditPlan.FollowPage"/>).</param>
    internal void ExpectRewrittenDocument(PdfDocument rewritten, int page) =>
        Volatile.Write(ref _expectedRewrite, new RewriteExpectation(rewritten, page));

    /// <summary>Takes back <see cref="ExpectRewrittenDocument"/> (the apply failed after the claim was made).</summary>
    internal void ForgetRewrittenDocument() => Volatile.Write(ref _expectedRewrite, null);

    /// <summary>The claim of <see cref="ExpectRewrittenDocument"/>.</summary>
    private sealed record RewriteExpectation(PdfDocument Document, int Page);

    /// <summary>
    /// Gets the count of pages as the panel shows it while the mode is on: "12 pages" while the plan has as many as the
    /// document, "12 → 10 pages" when it has another number (the plural follows the number after the arrow); empty while the
    /// mode is off. Read in the language of the interface each time.
    /// </summary>
    public string PageCounterText
    {
        get
        {
            if (_pageHistory is not { } history)
            {
                return string.Empty;
            }

            var now = history.Current.Slots.Count;
            var was = history.Current.PrimaryPageCount;
            if (now == was)
            {
                return string.Format(Strings.Culture, CompositeFormat.Parse(now == 1 ? Strings.PageCounterSameOne : Strings.PageCounterSame), now.ToString("N0", Strings.Culture));
            }

            return string.Format(
                Strings.Culture,
                CompositeFormat.Parse(now == 1 ? Strings.PageCounterChangeOne : Strings.PageCounterChange),
                was.ToString("N0", Strings.Culture),
                now.ToString("N0", Strings.Culture));
        }
    }

    /// <summary>Gets whether a command of the mode was made (for tests: asking the catalog makes none).</summary>
    internal bool HasPageEditCommands =>
        _rotateSelectionCommand is not null || _deletePagesCommand is not null || _duplicateSelectionCommand is not null
        || _insertBlankPageCommand is not null || _undoPagesCommand is not null || _redoPagesCommand is not null
        || _discardPageEditsCommand is not null || _moveEarlierCommand is not null || _moveLaterCommand is not null
        || _applyPageEditsCommand is not null || _insertFileCommand is not null;

    /// <summary>
    /// Gets the version of the page plan for <see cref="ChangeVersion"/>: the steps of the plan's history, plus those of
    /// plans that were dropped (it never goes back).
    /// </summary>
    private int PageEditVersion => _pageVersionCarry + (_pageHistory?.Version ?? 0);

    /// <summary>
    /// Gets whether staged changes are taken now: the mode is on and neither a card of the save flow asks about the tab nor the
    /// installer of an update has locked editing, and nothing is being saved (applying the plan is a save: the plan it writes is
    /// the one it started with, so a change made meanwhile would be dropped unseen). A print job does not matter: the plan is
    /// not the document.
    /// </summary>
    private bool CanEditPages => !_disposed && _isOrganizing && _pageHistory is not null && !_isAskedAbout && !IsEditingFrozen && !_isSaving;

    /// <summary>
    /// Gets why a document of <paramref name="pageCount"/> pages cannot be organized, or <see langword="null"/>: the plan holds
    /// <see cref="PageEditLimits.MaxSlots"/> pages at most.
    /// </summary>
    internal static string? OrganizeUnavailableReasonFor(int pageCount) => pageCount > PageEditLimits.MaxSlots
        ? string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PageOrganizeTooManyPages), PageEditLimits.MaxSlots.ToString("N0", Strings.Culture))
        : null;

    /// <summary>
    /// Takes the next number for a file inserted into the plan. The numbers rise and are never reused in this tab (a page
    /// slot that refers to an earlier file must never mean a later one), also not after the plan was dropped.
    /// </summary>
    internal PageSourceId AllocatePageSource() => new(++_lastPageSource);

    /// <summary>
    /// Sets the selection to the pages at <paramref name="positions"/> of the plan (a set; positions outside the plan are
    /// ignored). Only in the mode.
    /// </summary>
    /// <param name="positions">The 0-based positions.</param>
    public void SelectPages(IEnumerable<int> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);
        if (_pageHistory is not { } history)
        {
            return;
        }

        var count = history.Current.Slots.Count;
        SetSelection(positions.Where(position => position >= 0 && position < count));
    }

    /// <summary>Clears the selection.</summary>
    public void ClearPageSelection() => SetSelection([]);

    /// <summary>
    /// Moves the selected pages to a gap of the plan (0 before the first page, the page count after the last), keeping their
    /// order, and leaves them selected where they come to rest. A gap that changes nothing (next to the selection) is no step.
    /// </summary>
    /// <param name="gap">The gap in the list as it is now.</param>
    /// <returns>Whether the plan changed.</returns>
    public bool MoveSelectionTo(int gap)
    {
        if (!CanEditPages || _selectedPages.Count == 0)
        {
            return false;
        }

        var moved = _selectedPages;
        var start = gap - moved.Count(position => position < gap);
        return Stage(plan => plan.Move(moved, gap), Enumerable.Range(start, moved.Count));
    }

    /// <summary>
    /// Moves the selected pages by one position: <paramref name="delta"/> -1 to the gap before the page that precedes the
    /// first selected one, +1 to the gap after the page that follows the last selected one. A selection that is not in one
    /// piece gathers there, as with a move by the mouse. At the ends of the list nothing happens.
    /// </summary>
    /// <param name="delta">-1 earlier, +1 later.</param>
    /// <returns>Whether the plan changed.</returns>
    public bool MoveSelectionBy(int delta)
    {
        if (!CanEditPages || _selectedPages.Count == 0 || delta == 0)
        {
            return false;
        }

        var count = _pageHistory!.Current.Slots.Count;
        var gap = delta < 0 ? _selectedPages.Min() - 1 : _selectedPages.Max() + 2;
        return gap >= 0 && gap <= count && MoveSelectionTo(gap);
    }

    /// <summary>Whether the mode can start now: it may be asked for, no card asks about the tab, editing is not locked, no save runs.</summary>
    private bool CanBeginOrganize() =>
        !_disposed && !_isOrganizing && OrganizeUnavailableReason is null && !_isAskedAbout && !IsEditingFrozen && !_isSaving;

    private bool CanToggleOrganize() => _isOrganizing ? !_disposed && !HasPendingPageEdits : CanBeginOrganize();

    private void ToggleOrganize()
    {
        if (_isOrganizing)
        {
            // Without a change the mode just ends; a pending plan is applied or discarded, never lost by a toggle.
            if (!HasPendingPageEdits)
            {
                DropPlan();
            }

            return;
        }

        EnterOrganize();
    }

    private void EnterOrganize()
    {
        if (!CanBeginOrganize())
        {
            return;
        }

        // One mode at a time: the nudge and the open text of the other modes are made first, "Fill in form" ends (a value it
        // cannot apply keeps it), "Comment" ends.
        FlushNudge();
        if (!EndFill())
        {
            return;
        }

        EndAnnotate();
        PageEditPlan start;
        try
        {
            start = PageEditPlan.Identity(PageCount);
        }
        catch (PageEditException ex)
        {
            // Beyond the limits of a plan: the mode stays off, with the reason.
            PageEditNotice = NoticeOf(ex.Error);
            return;
        }

        PageEditNotice = null;
        _pageHistory = new PageEditHistory(start);
        IsOrganizing = true;

        // The staged list lives in the pages panel, so the panel opens with the mode. Only a tab that shows a sharp page can
        // get here, so the one chance of the start panel is over (it opens a panel only into a tab without one).
        SidePanel = SidePanelKind.Pages;
    }

    /// <summary>
    /// Drops the plan and leaves the mode on behalf of the window (the user answered "discard" to the question before an update
    /// or closing; the written file replaced the document and the view could not follow). Nothing is written.
    /// </summary>
    internal void AbandonPageEdits() => DropPlan();

    /// <summary>Drops the plan and its history and leaves the mode (discard, leaving without a change).</summary>
    private void DropPlan()
    {
        if (_pageHistory is { } history)
        {
            // The versions of the dropped history stay counted (plus one: dropping is a change of what closing asks about).
            _pageVersionCarry += history.Version + 1;
            _pageHistory = null;
        }

        // The files inserted into the plan go with it.
        ReleaseInsertedSources();

        _selectedPages = NoSelection;
        PageEditNotice = null;
        OnPropertyChanged(nameof(SelectedPagePositions));
        if (_isOrganizing)
        {
            IsOrganizing = false;
        }
        else
        {
            RaisePageStateChanged();
        }
    }

    /// <summary>Another document replaced the open one, or none is open: the plan described the old pages and goes.</summary>
    private void ResetPageEdits()
    {
        if (_pageHistory is not null || _isOrganizing || _selectedPages.Count > 0 || _pageEditNotice is not null || _insertedSources.Count > 0)
        {
            DropPlan();
        }
    }

    private void RotateSelection(int degrees)
    {
        var selected = _selectedPages;
        Stage(plan => plan.Rotate(selected, degrees), null);
    }

    private void DeletePages()
    {
        var selected = _selectedPages;
        Stage(plan => plan.Delete(selected), []);
    }

    private void DuplicateSelection()
    {
        var selected = _selectedPages;

        // The copy of the k-th selected page (counted from 0, ascending) sits right behind it, k copies further down.
        var copies = selected.Order().Select((position, k) => position + k + 1).ToArray();
        Stage(plan => plan.Duplicate(selected), copies);
    }

    private void InsertBlankPage()
    {
        var selected = _selectedPages;
        var gap = selected.Count > 0 ? selected.Max() + 1 : _pageHistory!.Current.Slots.Count;
        Stage(plan => plan.InsertBlank(gap, BlankPageAt(plan, gap)), [gap]);
    }

    /// <summary>
    /// Stages the plan <paramref name="edit"/> makes from the current one. A refused edit changes nothing and sets
    /// <see cref="PageEditNotice"/>; an edit that gives an equal plan (a full turn, a move next to where the pages are) is no
    /// step: the history, its version and the unsaved state stay as they are.
    /// </summary>
    /// <param name="edit">The change; may throw <see cref="PageEditException"/>.</param>
    /// <param name="selectionAfter">The selection after the change, or <see langword="null"/> to keep it.</param>
    /// <returns>Whether the plan changed.</returns>
    private bool Stage(Func<PageEditPlan, PageEditPlan> edit, IEnumerable<int>? selectionAfter)
    {
        if (!CanEditPages)
        {
            return false;
        }

        var history = _pageHistory!;
        PageEditPlan next;
        try
        {
            next = edit(history.Current);
        }
        catch (PageEditException ex)
        {
            PageEditNotice = NoticeOf(ex.Error);
            return false;
        }

        PageEditNotice = null;
        if (SameSlots(history.Current, next))
        {
            return false;
        }

        history.Do(next);
        if (selectionAfter is not null)
        {
            SetSelection(selectionAfter);
        }

        RaisePageStateChanged();
        return true;
    }

    private bool CanStepPages(bool undo) => CanEditPages && (undo ? _pageHistory!.CanUndo : _pageHistory!.CanRedo);

    private void UndoPages()
    {
        if (CanStepPages(undo: true))
        {
            _pageHistory!.Undo();
            AfterPageStep();
        }
    }

    private void RedoPages()
    {
        if (CanStepPages(undo: false))
        {
            _pageHistory!.Redo();
            AfterPageStep();
        }
    }

    // What the positions meant is gone: the selection starts over.
    private void AfterPageStep()
    {
        PageEditNotice = null;
        SetSelection([]);
        RaisePageStateChanged();
    }

    private void SetSelection(IEnumerable<int> positions)
    {
        var next = new HashSet<int>(positions);
        if (next.SetEquals(_selectedPages))
        {
            return;
        }

        _selectedPages = next.Count == 0 ? NoSelection : new ReadOnlySet<int>(next);
        OnPropertyChanged(nameof(SelectedPagePositions));
        RaisePageCanExecuteChanged();
    }

    private static bool SameSlots(PageEditPlan a, PageEditPlan b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Slots.Count != b.Slots.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Slots.Count; i++)
        {
            if (a.Slots[i] != b.Slots[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string NoticeOf(PageEditError error) => error switch
    {
        PageEditError.LastPage => Strings.PageEditsLastPage,
        PageEditError.LimitExceeded => Strings.PageEditsLimit,
        _ => Strings.PageEditsRefused,
    };

    /// <summary>
    /// The size of a blank page at <paramref name="gap"/>: as the page before it looks on the sheet (a page turned by 90 or 270
    /// degrees has its sides swapped), else the page after it, else A4. A page whose size is not known yet (of this document
    /// or of an inserted file, whose sizes are read in batches) is skipped.
    /// </summary>
    private BlankPage BlankPageAt(PageEditPlan plan, int gap)
    {
        foreach (var index in new[] { gap - 1, gap })
        {
            if (index >= 0 && index < plan.Slots.Count && SizeOf(plan.Slots[index]) is { } size)
            {
                return size;
            }
        }

        return DefaultBlankPage;
    }

    private BlankPage? SizeOf(PageSlot slot)
    {
        double width;
        double height;
        if (slot.Blank is { } blank)
        {
            (width, height) = (blank.Width, blank.Height);
        }
        else if (slot.Source.Value == 0 && slot.SourceIndex >= 0 && slot.SourceIndex < _session.LoadedSizes.Count)
        {
            var size = _session.LoadedSizes[slot.SourceIndex];
            (width, height) = (size.Width, size.Height);
        }
        else if (slot.Source.Value != 0 && slot.SourceIndex >= 0 && _insertedSources.TryGetValue(slot.Source, out var inserted) && slot.SourceIndex < inserted.Sizes.Count)
        {
            // A page of an inserted file: the size its file reports (read in batches after the file went in).
            var size = inserted.Sizes[slot.SourceIndex];
            (width, height) = (size.Width, size.Height);
        }
        else
        {
            return null;
        }

        return slot.ExtraRotation is 90 or 270 ? new BlankPage(height, width) : new BlankPage(width, height);
    }

    // The properties the plan and the mode feed; raised after every change of either.
    private void RaisePageStateChanged()
    {
        OnPropertyChanged(nameof(PageHistory));
        OnPropertyChanged(nameof(PagePlan));
        OnPropertyChanged(nameof(HasPendingPageEdits));
        OnPropertyChanged(nameof(PageEditsBlockedReason));
        OnPropertyChanged(nameof(PageCounterText));
        OnPropertyChanged(nameof(TabAutomationName));
        OnPropertyChanged(nameof(AnnotateUnavailableReason));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(OffersFill));
        OnPageAvailabilityChanged();

        // Ctrl+S is "Apply" while a plan waits.
        RaiseSaveCanExecuteChanged();
    }

    // From OnAnnotateAvailabilityChanged (the document, the sharp page or the state changed).
    private void OnPageAvailabilityChanged()
    {
        OnPropertyChanged(nameof(OrganizeUnavailableReason));
        OnPropertyChanged(nameof(CanOrganize));
        RaisePageCanExecuteChanged();
    }

    private void RaisePageCanExecuteChanged()
    {
        _organizeCommand?.RaiseCanExecuteChanged();
        _rotateSelectionCommand?.RaiseCanExecuteChanged();
        _deletePagesCommand?.RaiseCanExecuteChanged();
        _duplicateSelectionCommand?.RaiseCanExecuteChanged();
        _insertBlankPageCommand?.RaiseCanExecuteChanged();
        _undoPagesCommand?.RaiseCanExecuteChanged();
        _redoPagesCommand?.RaiseCanExecuteChanged();
        _discardPageEditsCommand?.RaiseCanExecuteChanged();
        _moveEarlierCommand?.RaiseCanExecuteChanged();
        _moveLaterCommand?.RaiseCanExecuteChanged();
        _applyPageEditsCommand?.RaiseCanExecuteChanged();
        _insertFileCommand?.RaiseCanExecuteChanged();

        // Extracting, splitting and exporting depend on the plan, the selection and the document being ready.
        RaisePageOutputChanged();

        // Ctrl+Z and Ctrl+Y are the plan's in the mode.
        _undoCommand?.RaiseCanExecuteChanged();
        _redoCommand?.RaiseCanExecuteChanged();
    }
}
