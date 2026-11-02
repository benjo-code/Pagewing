using System.Collections.ObjectModel;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The mode "Redact" of the tab (milestone 14, design spec 5.1 and 5.6): the plan of marks (<see cref="RedactionPlan"/>, with
/// its undo history), the selection of marks, and the commands that stage a change. A mark is only a plan: nothing is removed
/// from the document, nothing is written to the file as an annotation and nothing is printed (the marks are drawn by the
/// app's own layer, never by PDFium) until the plan is applied (the apply flow is a later task). The plan belongs to the tab:
/// it moves with it into another window and ends when another document replaces the open one. While the mode is on,
/// annotations, form filling and organizing pages are locked (<see cref="RedactionLockReason"/>), and the mode itself is not
/// offered for an encrypted file or while pages are organized (<see cref="RedactionBlockedReason"/>), always with the reason.
/// </summary>
/// <remarks>
/// Nothing of it is made before the mode is first entered: the plan and its history do not exist
/// (<see cref="RedactionHistory"/> is <see langword="null"/>), and the commands of the mode are made by the first access.
/// Entering needs the first sharp page, and reads once per document whether the file is encrypted (a short job of the PDF
/// thread, never at open).
/// </remarks>
public sealed partial class TabViewModel
{
    private static IReadOnlySet<Guid> NoMarks { get; } = new ReadOnlySet<Guid>(new HashSet<Guid>());

    private bool _isRedacting;
    private RedactionHistory? _redactionHistory;
    private IReadOnlySet<Guid> _selectedMarks = NoMarks;

    // The versions of histories that were dropped (plus one each), so that ChangeVersion never goes back.
    private int _redactionVersionCarry;
    private string? _redactionNotice;
    private (PdfDocument Document, bool Encrypted)? _encryption;
    private Task<bool>? _encryptionRead;

    private RelayCommand? _redactCommand;
    private RelayCommand? _deleteMarksCommand;
    private RelayCommand? _undoRedactionCommand;
    private RelayCommand? _redoRedactionCommand;
    private RelayCommand? _discardRedactionsCommand;
    private RelayCommand? _dismissRedactionNoticeCommand;

    /// <summary>
    /// Gets whether the mode "Redact" is on. One of the modes at a time (entering ends "Comment" and "Fill in form"; organizing
    /// pages blocks it).
    /// </summary>
    public bool IsRedacting
    {
        get => _isRedacting;
        private set
        {
            if (SetProperty(ref _isRedacting, value))
            {
                // The other modes read it (CanRequestAnnotate, CanBeginFill, OrganizeUnavailableReason) and show the reason.
                OnAnnotateAvailabilityChanged();
                RaiseRedactionStateChanged();
            }
        }
    }

    /// <summary>
    /// Gets the history of the plan (<see cref="Pagewing.Core.Redaction.RedactionHistory.Current"/> is the plan), or
    /// <see langword="null"/> when the mode is off. Made when the mode is entered, dropped when it ends; never in the startup path.
    /// </summary>
    public RedactionHistory? RedactionHistory => _redactionHistory;

    /// <summary>Gets the plan as it is now, or <see langword="null"/> when the mode is off; changes with every staged change, undo and redo.</summary>
    public RedactionPlan? RedactionPlan => _redactionHistory?.Current;

    /// <summary>
    /// Gets whether the plan has at least one mark: an unsaved change that closing and the update ask about ("Discard the
    /// marks?"), and that "Apply" turns into a redaction. A plan with every mark undone is none.
    /// </summary>
    public bool HasPendingRedactions => _redactionHistory is { } history && !history.Current.IsEmpty;

    /// <summary>Gets the ids of the selected marks, a set. Only in the mode; marks that left the plan (delete, undo) leave the selection.</summary>
    public IReadOnlySet<Guid> SelectedMarks => _selectedMarks;

    /// <summary>
    /// Gets or sets the colour of the areas the marks leave (black at first). Setting it in the mode is a step of the history,
    /// like a mark; outside the mode, or with a card over the tab, it does nothing.
    /// </summary>
    public RedactionFill RedactionFill
    {
        get => _redactionHistory?.Current.Fill ?? RedactionFill.Black;
        set
        {
            StageRedaction(plan => plan.WithFill(value), null);
        }
    }

    /// <summary>
    /// Gets why the last mark was refused (the plan is full, the rectangle is not a rectangle), or <see langword="null"/>:
    /// cleared by the next change that works and by <see cref="DismissRedactionNoticeCommand"/>. Every refusal is also told as a
    /// <see cref="RedactionHint"/>, each time it happens: the string is the same for the same kind of refusal, so a change of the
    /// property could not tell the second one.
    /// </summary>
    public string? RedactionNotice
    {
        get => _redactionNotice;
        private set
        {
            SetProperty(ref _redactionNotice, value);
            if (value is not null)
            {
                RedactionHint?.Invoke(value);
            }
        }
    }

    /// <summary>
    /// Gets why annotating, filling in a form and organizing pages are locked, or <see langword="null"/> while the mode is off:
    /// marks that wait for "Apply", or the mode is on without a mark and has to end first. The panels show it where they would
    /// offer those modes.
    /// </summary>
    public string? RedactionLockReason => !_isRedacting ? null : HasPendingRedactions ? Strings.RedactMarksPending : Strings.RedactActive;

    /// <summary>
    /// Gets why the mode cannot be entered, or <see langword="null"/> (also while it is on): no sharp page shown yet, pages
    /// are organized, or the file is encrypted (Pagewing does not redact encrypted files; known after the first request for the
    /// document). The text is in the language of the interface.
    /// </summary>
    public string? RedactionBlockedReason =>
        _isRedacting ? null
        : _disposed || !IsReady || _document is null || !_hasShownSharpPage ? Strings.RedactNotReady
        : _isOrganizing ? PageEditsBlockedReason
        : _encryption is { Encrypted: true } known && ReferenceEquals(known.Document, _document) ? Strings.RedactEncrypted
        : null;

    /// <summary>Gets whether the mode is on or can be entered (<see cref="RedactionBlockedReason"/> is <see langword="null"/>).</summary>
    public bool CanRedact => RedactionBlockedReason is null;

    /// <summary>
    /// The command "Redact" (catalog, Ctrl+Shift+P): enters the mode, or leaves it while no mark is pending (with marks,
    /// "Apply" or "Discard" ends it).
    /// </summary>
    public ICommand RedactCommand => _redactCommand ??= new RelayCommand(ToggleRedact, CanToggleRedact);

    /// <summary>Removes the selected marks from the plan in one step.</summary>
    public ICommand DeleteMarksCommand => _deleteMarksCommand ??= new RelayCommand(DeleteSelectedMarks, () => CanEditRedactions && _selectedMarks.Count > 0);

    /// <summary>Undoes the newest staged change (Ctrl+Z does this through <see cref="UndoCommand"/> in the mode).</summary>
    public ICommand UndoRedactionCommand => _undoRedactionCommand ??= new RelayCommand(UndoRedaction, () => CanStepRedactions(undo: true));

    /// <summary>Applies the newest undone staged change again (Ctrl+Y through <see cref="RedoCommand"/> in the mode).</summary>
    public ICommand RedoRedactionCommand => _redoRedactionCommand ??= new RelayCommand(RedoRedaction, () => CanStepRedactions(undo: false));

    /// <summary>
    /// Drops the plan with every mark and leaves the mode. The document was never touched. The button of the toolbar asks
    /// nothing: a view that wants a confirmation puts it in front.
    /// </summary>
    public ICommand DiscardRedactionsCommand => _discardRedactionsCommand ??= new RelayCommand(DropRedactions, () => CanEditRedactions);

    /// <summary>Closes the notice about a refused mark (<see cref="RedactionNotice"/>).</summary>
    public ICommand DismissRedactionNoticeCommand => _dismissRedactionNoticeCommand ??= new RelayCommand(() => RedactionNotice = null);

    /// <summary>Gets or sets how the encryption of a document is read: <see cref="PdfDocument.GetPrintInfoAsync"/> (replaceable for tests).</summary>
    internal Func<PdfDocument, CancellationToken, Task<bool>> EncryptionReader { get; set; } =
        static async (document, cancellationToken) => (await document.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted;

    /// <summary>Gets whether the encryption of the document was asked for (for tests: nothing asks before the mode is requested).</summary>
    internal bool HasReadEncryption => _encryption is not null || _encryptionRead is not null;

    /// <summary>Gets whether a command of the mode was made (for tests: asking the catalog makes none; the mode command itself is no obstacle).</summary>
    internal bool HasRedactionCommands =>
        _deleteMarksCommand is not null || _undoRedactionCommand is not null || _redoRedactionCommand is not null
        || _discardRedactionsCommand is not null || _markSelectionCommand is not null || _chooseRedactionToolCommand is not null
        || _setRedactionFillCommand is not null || _setRedactionImageTreatmentCommand is not null || _setRedactionImageStrengthCommand is not null || _toggleRedactionPanelCommand is not null || _applyRedactionsCommand is not null
        || _toggleRedactionPreviewCommand is not null || _toggleRasterPageCommand is not null || _setRasterDpiCommand is not null;

    /// <summary>
    /// Gets whether the only thing closing would lose is the plan of marks (<see cref="HasPendingRedactions"/>): "Save" cannot
    /// write it, so the question is "Discard the marks?" (<see cref="PendingSave.DiscardRedactions"/>).
    /// </summary>
    internal bool AsksOnlyAboutRedactions => !_disposed && HasPendingRedactions && !AsksAboutDocumentChanges;

    /// <summary>
    /// Gets the version of the plan for <see cref="ChangeVersion"/>: the steps of the plan's history, plus those of plans that
    /// were dropped (it never goes back).
    /// </summary>
    private int RedactionVersion => _redactionVersionCarry + (_redactionHistory?.Version ?? 0);

    /// <summary>
    /// Gets whether staged changes are taken now: the mode is on and neither a card of the save flow asks about the tab nor the
    /// installer of an update has locked editing, and nothing is being saved (applying the plan is a save: the plan it writes is
    /// the one it started with, so a change made meanwhile would be dropped unseen). A print job does not matter: the marks are
    /// not the document and are never printed.
    /// </summary>
    private bool CanEditRedactions => !_disposed && _isRedacting && _redactionHistory is not null && !_isAskedAbout && !_isEditingFrozen && !_isSaving;

    // The apply flow is the one that saves and asks: it stages the choice of a rejected page itself, only an update's lock stops it.
    private bool CanEditDuringApply => !_disposed && _isRedacting && _redactionHistory is not null && !_isEditingFrozen;

    /// <summary>
    /// Adds a rectangle mark. The area is normalized here (a drag from the lower right to the upper left gives the same
    /// rectangle); a click without a drag (no size) is ignored without a notice. A mark the plan refuses (not finite, plan
    /// full) changes nothing and sets <see cref="RedactionNotice"/>. Only in the mode, on a page of the document.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="area">The rectangle in points on that page, origin top left.</param>
    /// <returns>Whether a mark was added.</returns>
    public bool AddAreaMark(int page, PageRect area)
    {
        if (!CanEditRedactions || page < 0 || page >= PageCount)
        {
            return false;
        }

        // A drag may run in any direction. NaN stays as it is: the plan refuses it with a notice.
        var normalized = new PageRect(
            Math.Min(area.Left, area.Right),
            Math.Min(area.Top, area.Bottom),
            Math.Max(area.Left, area.Right),
            Math.Max(area.Top, area.Bottom));
        if (double.IsFinite(normalized.Left) && double.IsFinite(normalized.Top) && double.IsFinite(normalized.Right)
            && double.IsFinite(normalized.Bottom) && (!(normalized.Right > normalized.Left) || !(normalized.Bottom > normalized.Top)))
        {
            return false;
        }

        var mark = new RedactionMark(Guid.NewGuid(), RedactionKind.Area, page, [normalized], null, false);
        return StageRedaction(plan => plan.With(mark), [mark.Id]);
    }

    /// <summary>
    /// Sets the selection to the marks with <paramref name="ids"/> (marks that are not in the plan are ignored). Only in the mode.
    /// </summary>
    /// <param name="ids">The ids of the marks.</param>
    public void SelectMarks(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (_redactionHistory is { } history)
        {
            var present = new HashSet<Guid>(history.Current.Marks.Select(mark => mark.Id));
            SetMarkSelection(ids.Where(present.Contains));
        }
    }

    /// <summary>Clears the selection of marks.</summary>
    public void ClearMarkSelection() => SetMarkSelection([]);

    /// <summary>
    /// Drops the plan and leaves the mode on behalf of the window (the user answered "discard" to the question before an update
    /// or closing; the written file replaced the document and the view could not follow). Nothing is written.
    /// </summary>
    internal void AbandonRedactions() => DropRedactions();

    /// <summary>Whether the mode can start now: it may be asked for, no card asks about the tab, editing is not locked, no save runs.</summary>
    private bool CanBeginRedact() =>
        !_disposed && !_isRedacting && RedactionBlockedReason is null && !_isAskedAbout && !_isEditingFrozen && !_isSaving;

    private bool CanToggleRedact() => _isRedacting ? !_disposed && !HasPendingRedactions : CanBeginRedact();

    private void ToggleRedact()
    {
        if (_isRedacting)
        {
            // Without a mark the mode just ends; a pending plan is applied or discarded, never lost by a toggle.
            if (!HasPendingRedactions)
            {
                DropRedactions();
            }

            return;
        }

        _ = RequestRedactAsync();
    }

    /// <summary>
    /// Starts the mode once the file is known not to be encrypted (read on the first request for this document, by a short job
    /// of the PDF thread). An encrypted file gets no mode: <see cref="RedactionBlockedReason"/> says why, for the panel and the
    /// palette to show.
    /// </summary>
    private async Task RequestRedactAsync()
    {
        if (!CanBeginRedact() || _document is not { } document)
        {
            return;
        }

        bool encrypted;
        if (_encryption is { } known && ReferenceEquals(known.Document, document))
        {
            encrypted = known.Encrypted;
        }
        else
        {
            try
            {
                _encryptionRead ??= EncryptionReader(document, CancellationToken.None);
                encrypted = await _encryptionRead;
            }
            catch (Exception)
            {
                // The document went meanwhile, or PDFium could not tell: a later request reads again (never "not encrypted" from not
                // knowing). The user asked and gets told that nothing started, unless the document is gone.
                _encryptionRead = null;
                if (!_disposed && ReferenceEquals(document, _document))
                {
                    RedactionHint?.Invoke(Strings.RedactCheckFailed);
                }

                return;
            }

            _encryptionRead = null;
            if (_disposed || !ReferenceEquals(document, _document))
            {
                return;
            }

            _encryption = (document, encrypted);
            OnAnnotateAvailabilityChanged();
            RaiseRedactionStateChanged();
        }

        if (!encrypted)
        {
            EnterRedact();
        }
        else if (RedactionBlockedReason is { } reason)
        {
            // The first request for an encrypted file: the mode is not offered from now on, and the user is told why right here
            // (later the command is not executable, and its reason is shown where it is offered).
            RedactionHint?.Invoke(reason);
        }
    }

    private void EnterRedact()
    {
        if (!CanBeginRedact())
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
        RedactionNotice = null;
        _redactionHistory = new RedactionHistory(RedactionPlan.Empty);
        ResetRedactionMarking();
        IsRedacting = true;
    }

    /// <summary>Drops the plan and its history and leaves the mode (discard, leaving without a mark).</summary>
    private void DropRedactions()
    {
        // The preview ends with the plan: a run of its numbers that is working is stopped.
        EndRedactionPreview();
        if (_redactionHistory is { } history)
        {
            // The versions of the dropped history stay counted (plus one: dropping is a change of what closing asks about).
            _redactionVersionCarry += history.Version + 1;
            _redactionHistory = null;
        }

        _selectedMarks = NoMarks;
        RedactionNotice = null;
        OnPropertyChanged(nameof(SelectedMarks));

        // The panel lists the marks of the plan and the hits of a search in it: both end with the plan.
        DiscardRedactionPanel();
        if (_isRedacting)
        {
            IsRedacting = false;
        }
        else
        {
            RaiseRedactionStateChanged();
        }
    }

    /// <summary>Another document replaced the open one, or none is open: the plan described the old pages and goes, and so does what was read of the old file.</summary>
    private void ResetRedaction()
    {
        _encryption = null;
        _encryptionRead = null;
        if (_redactionHistory is not null || _isRedacting || _selectedMarks.Count > 0 || _redactionNotice is not null)
        {
            DropRedactions();
        }
        else
        {
            RaiseRedactionStateChanged();
        }
    }

    /// <summary>The saved file replaced the document: the same content, the same encryption.</summary>
    private void RebindRedaction(PdfDocument old, PdfDocument adopted)
    {
        if (_encryption is { } known && ReferenceEquals(known.Document, old))
        {
            _encryption = (adopted, known.Encrypted);
        }
    }

    private void DeleteSelectedMarks()
    {
        var selected = _selectedMarks;
        StageRedaction(plan => plan.Without([.. selected]), []);
    }

    /// <summary>
    /// Stages the plan <paramref name="edit"/> makes from the current one. A refused edit changes nothing and sets
    /// <see cref="RedactionNotice"/>; an edit that gives the same plan is no step: the history, its version and the unsaved
    /// state stay as they are.
    /// </summary>
    /// <param name="edit">The change; may throw <see cref="RedactionException"/>.</param>
    /// <param name="selectionAfter">The selection after the change, or <see langword="null"/> to keep it.</param>
    /// <param name="amend">Whether the change belongs to the last step (a run of arrow keys) and is merged into it.</param>
    /// <param name="duringApply">Whether the apply flow stages it (the choice of the card of a rejected page): that flow holds the save lock itself.</param>
    /// <returns>Whether the plan changed.</returns>
    private bool StageRedaction(Func<RedactionPlan, RedactionPlan> edit, IEnumerable<Guid>? selectionAfter, bool amend = false, bool duringApply = false)
    {
        if (!(duringApply ? CanEditDuringApply : CanEditRedactions))
        {
            return false;
        }

        var history = _redactionHistory!;
        RedactionPlan next;
        try
        {
            next = edit(history.Current);
        }
        catch (RedactionException ex)
        {
            RedactionNotice = NoticeOf(ex.Error);
            return false;
        }

        RedactionNotice = null;
        if (ReferenceEquals(next, history.Current))
        {
            return false;
        }

        if (amend)
        {
            history.Amend(next);
        }
        else
        {
            history.Do(next);
        }

        if (selectionAfter is not null)
        {
            SetMarkSelection(selectionAfter);
        }
        else
        {
            KeepSelectionInPlan();
        }

        RaiseRedactionStateChanged();
        return true;
    }

    private bool CanStepRedactions(bool undo) => CanEditRedactions && (undo ? _redactionHistory!.CanUndo : _redactionHistory!.CanRedo);

    private void UndoRedaction()
    {
        if (CanStepRedactions(undo: true))
        {
            _redactionHistory!.Undo();
            AfterRedactionStep();
        }
    }

    private void RedoRedaction()
    {
        if (CanStepRedactions(undo: false))
        {
            _redactionHistory!.Redo();
            AfterRedactionStep();
        }
    }

    // Marks that are not in the plan any more cannot stay selected.
    private void AfterRedactionStep()
    {
        RedactionNotice = null;
        KeepSelectionInPlan();
        RaiseRedactionStateChanged();
    }

    private void KeepSelectionInPlan()
    {
        if (_selectedMarks.Count > 0 && _redactionHistory is { } history)
        {
            var present = new HashSet<Guid>(history.Current.Marks.Select(mark => mark.Id));
            SetMarkSelection(_selectedMarks.Where(present.Contains));
        }
    }

    private void SetMarkSelection(IEnumerable<Guid> ids)
    {
        var next = new HashSet<Guid>(ids);
        if (next.SetEquals(_selectedMarks))
        {
            return;
        }

        _selectedMarks = next.Count == 0 ? NoMarks : new ReadOnlySet<Guid>(next);
        OnPropertyChanged(nameof(SelectedMarks));
        OnPropertyChanged(nameof(RasterTargetsAreImages));
        RaiseRedactionCanExecuteChanged();
    }

    private static string NoticeOf(RedactionError error) => error switch
    {
        RedactionError.LimitExceeded => Strings.RedactMarksLimit,
        _ => Strings.RedactMarkRefused,
    };

    // The properties the plan and the mode feed; raised after every change of either.
    private void RaiseRedactionStateChanged()
    {
        OnPropertyChanged(nameof(RedactionHistory));
        OnPropertyChanged(nameof(RedactionPlan));
        OnPropertyChanged(nameof(RedactionFill));
        OnPropertyChanged(nameof(IsRedactionFillBlack));
        OnPropertyChanged(nameof(IsRedactionFillWhite));
        OnPropertyChanged(nameof(IsRedactionFillCustom));
        OnPropertyChanged(nameof(RedactionFillHex));
        OnPropertyChanged(nameof(RedactionImageTreatment));
        OnPropertyChanged(nameof(RedactionImageStrength));
        OnPropertyChanged(nameof(IsRedactionImageSolid));
        OnPropertyChanged(nameof(IsRedactionImagePixelate));
        OnPropertyChanged(nameof(IsRedactionImageBlur));
        OnPropertyChanged(nameof(IsRedactionImageTreated));
        OnPropertyChanged(nameof(IsRedactionImageCoarse));
        OnPropertyChanged(nameof(IsRedactionImageVeryCoarse));
        OnPropertyChanged(nameof(RedactionImageTreatmentText));
        OnPropertyChanged(nameof(RedactionPreviewNote));
        OnPropertyChanged(nameof(RedactionCounterText));
        OnPropertyChanged(nameof(RedactionToolIsArea));
        OnPropertyChanged(nameof(RedactionToolIsText));
        OnPropertyChanged(nameof(HasPendingRedactions));
        OnPropertyChanged(nameof(RedactionLockReason));
        OnPropertyChanged(nameof(RedactionBlockedReason));
        OnPropertyChanged(nameof(CanRedact));
        OnPropertyChanged(nameof(TabAutomationName));
        OnPropertyChanged(nameof(AnnotateUnavailableReason));
        OnPropertyChanged(nameof(CanAnnotate));
        OnPropertyChanged(nameof(OffersFill));
        OnPropertyChanged(nameof(OrganizeUnavailableReason));
        OnPropertyChanged(nameof(CanOrganize));
        RaiseRasterStateChanged();

        // Splitting and exporting images read the document without the marks, so pending marks lock them.
        RaisePageOutputChanged();
        RaiseRedactionCanExecuteChanged();
        SchedulePreviewIfPlanChanged();
    }

    private void RaiseRedactionCanExecuteChanged()
    {
        // Ctrl+S and "Save as" apply the marks while some wait, and the button "Apply" asks the same.
        RaiseSaveCanExecuteChanged();
        RaiseRedactionApplyCanExecuteChanged();
        _redactCommand?.RaiseCanExecuteChanged();
        _deleteMarksCommand?.RaiseCanExecuteChanged();
        _undoRedactionCommand?.RaiseCanExecuteChanged();
        _redoRedactionCommand?.RaiseCanExecuteChanged();
        _setRedactionImageTreatmentCommand?.RaiseCanExecuteChanged();
        _setRedactionImageStrengthCommand?.RaiseCanExecuteChanged();
        _discardRedactionsCommand?.RaiseCanExecuteChanged();
        _toggleRedactionPanelCommand?.RaiseCanExecuteChanged();
        _toggleRasterPageCommand?.RaiseCanExecuteChanged();
        _setRasterDpiCommand?.RaiseCanExecuteChanged();
        RaiseMarkingCanExecuteChanged();

        // Ctrl+Z and Ctrl+Y are the plan's in the mode.
        _undoCommand?.RaiseCanExecuteChanged();
        _redoCommand?.RaiseCanExecuteChanged();
    }
}
