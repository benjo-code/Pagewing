using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;

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

    private RelayCommand? _organizeCommand;
    private RelayCommand<int>? _rotateSelectionCommand;
    private RelayCommand? _deletePagesCommand;
    private RelayCommand? _duplicateSelectionCommand;
    private RelayCommand? _insertBlankPageCommand;
    private RelayCommand? _undoPagesCommand;
    private RelayCommand? _redoPagesCommand;
    private RelayCommand? _discardPageEditsCommand;
    private RelayCommand? _dismissPageEditNoticeCommand;

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
    /// pages than a plan can hold (<see cref="PageEditLimits.MaxSlots"/>).
    /// </summary>
    public string? OrganizeUnavailableReason =>
        _isOrganizing ? null
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
        HasPendingPageEdits ? Strings.PageOutputBlockedPlan
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

    /// <summary>Gets whether a command of the mode was made (for tests: asking the catalog makes none).</summary>
    internal bool HasPageEditCommands =>
        _rotateSelectionCommand is not null || _deletePagesCommand is not null || _duplicateSelectionCommand is not null
        || _insertBlankPageCommand is not null || _undoPagesCommand is not null || _redoPagesCommand is not null
        || _discardPageEditsCommand is not null;

    /// <summary>
    /// Gets the version of the page plan for <see cref="ChangeVersion"/>: the steps of the plan's history, plus those of
    /// plans that were dropped (it never goes back).
    /// </summary>
    private int PageEditVersion => _pageVersionCarry + (_pageHistory?.Version ?? 0);

    /// <summary>
    /// Gets whether staged changes are taken now: the mode is on and neither a card of the save flow asks about the tab nor the
    /// installer of an update has locked editing. A save or a print job does not matter: the plan is not the document.
    /// </summary>
    private bool CanEditPages => !_disposed && _isOrganizing && _pageHistory is not null && !_isAskedAbout && !_isEditingFrozen;

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

    /// <summary>Whether the mode can start now: it may be asked for, no card asks about the tab, editing is not locked, no save runs.</summary>
    private bool CanBeginOrganize() =>
        !_disposed && !_isOrganizing && OrganizeUnavailableReason is null && !_isAskedAbout && !_isEditingFrozen && !_isSaving;

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
    }

    /// <summary>Drops the plan and its history and leaves the mode (discard, leaving without a change).</summary>
    private void DropPlan()
    {
        if (_pageHistory is { } history)
        {
            // The versions of the dropped history stay counted (plus one: dropping is a change of what closing asks about).
            _pageVersionCarry += history.Version + 1;
            _pageHistory = null;
        }

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
        if (_pageHistory is not null || _isOrganizing || _selectedPages.Count > 0 || _pageEditNotice is not null)
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
    /// degrees has its sides swapped), else the page after it, else A4. A page of another file is skipped until its size is
    /// known to the tab (a later task); so is a page of this document whose size is not loaded yet.
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
        OnPropertyChanged(nameof(PageOutputBlockedReason));
        OnPropertyChanged(nameof(CanRunPageOutput));
        OnPropertyChanged(nameof(AnnotateUnavailableReason));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(OffersFill));
        OnPageAvailabilityChanged();
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

        // Ctrl+Z and Ctrl+Y are the plan's in the mode.
        _undoCommand?.RaiseCanExecuteChanged();
        _redoCommand?.RaiseCanExecuteChanged();
    }
}
