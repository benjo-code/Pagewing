using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.TextEdit;

namespace Pagewing.App.ViewModels;

/// <summary>
/// Editing a line of text (milestone 16; <c>Docs/superpowers/specs/2026-11-02-m16-text-bearbeiten-design.md</c>): the mode, the runs of the visible pages, the
/// field over one run with its live hints, and the commit as one step of the shared history. Lines, not paragraphs. A run that cannot be edited shows why
/// (<see cref="LockTextOf"/>); a new text that cannot be written stays in the field with the reason; the text of an open field is an unsaved change and is
/// applied by <see cref="FlushTextEditDraft"/> before saving, printing, the end of the mode, closing and the update. Nothing here is made before the mode is
/// entered, and nothing starts it but the user.
/// </summary>
public sealed partial class TabViewModel
{
    private readonly Dictionary<int, IReadOnlyList<TextRun>> _textRuns = [];
    private readonly HashSet<int> _textRunsLoading = [];
    private bool _isTextEditing;
    private bool _textEditNoticeShown;
    private string _textEditSaveNote = string.Empty;
    private RelayCommand? _toggleTextEditCommand;
    private int _textRunsGeneration;
    private int _textRunsEditVersion = -1;
    private TextRun? _hoverTextRun;
    private TextRun? _focusedTextRun;
    private TextRun? _openTextRun;
    private string _textEditDraft = string.Empty;
    private bool _textEditWholeRun;
    private TextEditDecision? _textEditDecision;
    private TextEditOverflow _textEditOverflow;
    private string? _textEditProblem;
    private int _textEditHintSerial;
    private PageText? _textEditPageText;
    private Task<bool>? _textEditCommit;
    private string? _textEditNotice;

    /// <summary>Raised on the UI thread when the runs of a page were read or dropped (-1: all pages).</summary>
    internal event Action<int>? TextRunsChanged;

    /// <summary>Raised when the run under the pointer, the run with the keyboard focus or the open field changed (the view redraws its layer and places the field).</summary>
    internal event Action? TextEditMarksChanged;

    /// <summary>Raised when a run was edited and the view should look at the page again; the argument is the 0-based page.</summary>
    internal event Action<int>? TextEditApplied;

    /// <summary>Gets the 0-based pages the view shows now (set by the view while the mode is on).</summary>
    internal Func<IEnumerable<int>>? VisibleTextEditPages { get; set; }

    /// <summary>Gets whether a text edit was made since the last save (<see cref="AnnotationEditor.HasTextEdits"/>), also one undone since.</summary>
    internal bool HasTextEdits => _editor?.HasTextEdits == true;

    /// <summary>Gets or sets whether the note about the old text was shown for the open document (once per tab and document).</summary>
    internal bool TextEditNoticeShown
    {
        get => _textEditNoticeShown;
        set => _textEditNoticeShown = value;
    }

    /// <summary>Gets the note of the status bar after the first save of edited text ("The old text stays in earlier versions of the file …"), empty otherwise.</summary>
    public string TextEditSaveNote
    {
        get => _textEditSaveNote;
        private set
        {
            if (_textEditSaveNote != value)
            {
                _textEditSaveNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasTextEditSaveNote));
            }
        }
    }

    /// <summary>Gets whether the status bar shows <see cref="TextEditSaveNote"/>.</summary>
    public bool HasTextEditSaveNote => _textEditSaveNote.Length > 0;

    /// <summary>Edited text was saved for the first time: the status bar says what stays in the file, once per tab and document.</summary>
    internal void NoteTextEditSaved()
    {
        _textEditNoticeShown = true;
        TextEditSaveNote = Strings.TextEditSaveNote;
    }

    /// <summary>
    /// Gets whether the mode "Edit text" is on. One of the modes at a time: entering needs the others off (the reason is shown), and while it is on they are
    /// not offered (<see cref="TextEditActiveReason"/>).
    /// </summary>
    public bool IsTextEditing
    {
        get => _isTextEditing;
        private set
        {
            if (SetProperty(ref _isTextEditing, value))
            {
                OnAnnotateAvailabilityChanged();
                RaiseTextEditStateChanged();
            }
        }
    }

    /// <summary>Gets the reason the other modes give while this one is on, or <see langword="null"/>.</summary>
    public string? TextEditActiveReason => _isTextEditing ? Strings.TextEditActive : null;

    /// <summary>
    /// Gets why the mode cannot be entered, or <see langword="null"/> (also while it is on): no sharp page shown yet, another mode is on, the file cannot be
    /// saved, or a save, a print job, a question or the update lock holds the tab.
    /// </summary>
    public string? TextEditBlockedReason =>
        _isTextEditing ? null
        : _disposed || !IsReady || _document is null || !_hasShownSharpPage ? Strings.TextEditNotReady
        : _isAnnotating || _isFilling || _isOrganizing || _isRedacting || _isOcrRunning ? Strings.TextEditBlockedMode
        : UnsaveableReason is not null ? Strings.TextEditLockNotSavable
        : _isSaving || _isPrinting || _isAskedAbout || _isEditingFrozen ? Strings.TextEditNotNow
        : null;

    /// <summary>Gets whether the mode is on or can be entered.</summary>
    public bool CanTextEdit => TextEditBlockedReason is null;

    /// <summary>The command "Edit text" (catalog <c>text.edit</c>): enters the mode or ends it.</summary>
    public RelayCommand ToggleTextEditCommand => _toggleTextEditCommand ??= new RelayCommand(
        () =>
        {
            if (_isTextEditing)
            {
                EndTextEdit();
            }
            else
            {
                BeginTextEdit();
            }
        },
        () => _isTextEditing ? !_disposed : TextEditBlockedReason is null);

    /// <summary>Enters the mode; does nothing while <see cref="TextEditBlockedReason"/> holds.</summary>
    public bool BeginTextEdit()
    {
        if (_isTextEditing)
        {
            return true;
        }

        if (TextEditBlockedReason is not null)
        {
            return false;
        }

        FlushNudge();
        IsTextEditing = true;
        return true;
    }

    /// <summary>
    /// Ends the mode. A text still in the field is applied first; one that cannot be applied stays in the field with the reason and the mode stays on.
    /// </summary>
    /// <returns>Whether the mode is off now.</returns>
    public bool EndTextEdit()
    {
        if (!_isTextEditing)
        {
            return true;
        }

        if (!FlushTextEditDraft())
        {
            return false;
        }

        CloseTextEditField();
        _textRuns.Clear();
        _textRunsGeneration++;
        HoverTextRun = null;
        FocusedTextRun = null;
        IsTextEditing = false;
        return true;
    }

    /// <summary>
    /// Esc of the mode (DocumentView, when the field itself did not take it): drops the open text, else clears the focus of a run, else ends the mode.
    /// </summary>
    /// <returns>Whether Esc had something to end.</returns>
    internal bool EscapeTextEdit()
    {
        if (!_isTextEditing)
        {
            return false;
        }

        if (_openTextRun is not null)
        {
            DiscardTextEditField();
        }
        else if (_focusedTextRun is not null || _textEditNotice is not null)
        {
            _textEditNotice = null;
            OnPropertyChanged(nameof(TextEditHint));
            FocusedTextRun = null;
        }
        else
        {
            EndTextEdit();
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------- runs

    /// <summary>Gets the runs of a page as read (empty before they are read, or when the page cannot be read).</summary>
    internal IReadOnlyList<TextRun> TextRunsOf(int page) => _textRuns.TryGetValue(page, out var runs) ? runs : [];

    /// <summary>Reads the runs of the pages that are not read yet, in short jobs at background priority; <see cref="TextRunsChanged"/> tells when a page is ready.</summary>
    internal void EnsureTextRuns(IEnumerable<int> pages)
    {
        if (!_isTextEditing || _document is not { } document)
        {
            return;
        }

        foreach (var page in pages)
        {
            if (page < 0 || page >= PageCount || _textRuns.ContainsKey(page) || !_textRunsLoading.Add(page))
            {
                continue;
            }

            _ = LoadTextRunsAsync(document, page, _textRunsGeneration);
        }
    }

    private async Task LoadTextRunsAsync(PdfDocument document, int page, int generation)
    {
        IReadOnlyList<TextRun> runs;
        try
        {
            runs = await document.GetTextRunsAsync(page);
        }
        catch (Exception ex) when (ex is InvalidDataException or ObjectDisposedException or OperationCanceledException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            runs = [];
        }

        if (generation != _textRunsGeneration || !_isTextEditing || !ReferenceEquals(document, _document))
        {
            _textRunsLoading.Remove(page);
            return;
        }

        _textRunsLoading.Remove(page);
        _textRuns[page] = LockUnsavable(runs);
        TextRunsChanged?.Invoke(page);
    }

    // Runs of a file that cannot be saved are locked with that reason (the field would only lose the text).
    private IReadOnlyList<TextRun> LockUnsavable(IReadOnlyList<TextRun> runs) =>
        UnsaveableReason is null ? runs : [.. runs.Select(static r => r.Lock is null ? r with { Lock = TextEditLockReason.NotSavable } : r)];

    /// <summary>Drops the runs read (they are read again when needed): after any change of the document.</summary>
    internal void InvalidateTextRuns()
    {
        _textRunsGeneration++;
        _textRuns.Clear();
        _textRunsLoading.Clear();
        TextRunsChanged?.Invoke(-1);
    }

    private void OnTextEditVersion()
    {
        if (_textRunsEditVersion == EditVersion)
        {
            return;
        }

        _textRunsEditVersion = EditVersion;
        if (_isTextEditing)
        {
            InvalidateTextRuns();
            if (VisibleTextEditPages is { } visible)
            {
                EnsureTextRuns(visible());
            }
        }
    }

    /// <summary>Gets the run under the pointer, or <see langword="null"/>.</summary>
    internal TextRun? HoverTextRun
    {
        get => _hoverTextRun;
        set
        {
            if (!Equals(_hoverTextRun, value))
            {
                _hoverTextRun = value;
                TextEditMarksChanged?.Invoke();
            }
        }
    }

    /// <summary>Gets the run with the keyboard focus (arrows move it, Enter opens it), or <see langword="null"/>.</summary>
    internal TextRun? FocusedTextRun
    {
        get => _focusedTextRun;
        set
        {
            if (!Equals(_focusedTextRun, value))
            {
                _focusedTextRun = value;
                OnPropertyChanged();
                TextEditMarksChanged?.Invoke();
            }
        }
    }

    /// <summary>The text of the reason a run is locked, in the language of the interface.</summary>
    public static string LockTextOf(TextEditLockReason reason) => reason switch
    {
        TextEditLockReason.InvisibleText => Strings.TextEditLockInvisibleText,
        TextEditLockReason.Type3Font => Strings.TextEditLockType3Font,
        TextEditLockReason.VerticalWriting => Strings.TextEditLockVerticalWriting,
        TextEditLockReason.UnsupportedRenderMode => Strings.TextEditLockUnsupportedRenderMode,
        TextEditLockReason.FormXObject => Strings.TextEditLockFormXObject,
        TextEditLockReason.MarkedContent => Strings.TextEditLockMarkedContent,
        TextEditLockReason.NotSavable => Strings.TextEditLockNotSavable,
        TextEditLockReason.PageNotRewritable => Strings.TextEditLockPageNotRewritable,
        TextEditLockReason.TooManyRuns => Strings.TextEditLockTooManyRuns,
        TextEditLockReason.RunTooLong => Strings.TextEditLockRunTooLong,
        _ => Strings.TextEditNotNow,
    };

    // ---------------------------------------------------------------------------------------------------------------- field

    /// <summary>Gets the run whose field is open, or <see langword="null"/>.</summary>
    public TextRun? OpenTextRun => _openTextRun;

    /// <summary>Gets the text in the field (the run's text when it was opened).</summary>
    public string TextEditDraft => _textEditDraft;

    /// <summary>Gets whether the text of the open field differs from the run (an unsaved change).</summary>
    public bool HasTextEditDraft => _openTextRun is { } run && !string.Equals(_textEditDraft, run.Text, StringComparison.Ordinal);

    /// <summary>Gets whether the user chose the whole line in the replacement font.</summary>
    public bool TextEditWholeRun => _textEditWholeRun;

    /// <summary>Gets whether the choice "Whole line in replacement font" is offered (every character of the new text is in Geist and the font lacks some).</summary>
    public bool CanChooseWholeRun => _textEditDecision is { WholeRunInGeistPossible: true } d && (d.Outcome is TextEditOutcome.SubstituteMissing or TextEditOutcome.WholeRunInGeist || d.Missing.Count > 0);

    /// <summary>Gets why the text in the field cannot be applied (it stays there), or <see langword="null"/>.</summary>
    public string? TextEditProblem => _textEditProblem;

    /// <summary>Gets the live hint for the open field (missing characters, a refused text, overflow, removal) or the reason a line is locked, empty when there is nothing to say.</summary>
    public string TextEditHint
    {
        get
        {
            if (_openTextRun is null)
            {
                return _textEditNotice ?? string.Empty;
            }

            var parts = new List<string>();
            if (_textEditProblem is { } problem)
            {
                parts.Add(problem);
            }
            else if (_textEditDecision is { } decision)
            {
                switch (decision.Outcome)
                {
                    case TextEditOutcome.Rejected when decision.Rejection == TextEditRejection.TooLong:
                        parts.Add(Format(Strings.TextEditTooLong, TextEditLimits.MaxCharsPerRun.ToString(Strings.Culture)));
                        break;
                    case TextEditOutcome.Rejected:
                        parts.Add(Format(Strings.TextEditRejected, string.Join(", ", decision.Unsupported)));
                        break;
                    case TextEditOutcome.SubstituteMissing:
                        parts.Add(Format(Strings.TextEditMissing, string.Join(", ", decision.Missing)));
                        break;
                    case TextEditOutcome.Remove:
                        parts.Add(Strings.TextEditRemoves);
                        break;
                }

                if (decision.Outcome is not TextEditOutcome.Rejected)
                {
                    if ((_textEditOverflow & TextEditOverflow.OverlapsText) != 0)
                    {
                        parts.Add(Strings.TextEditOverlaps);
                    }

                    if ((_textEditOverflow & TextEditOverflow.OutsidePage) != 0)
                    {
                        parts.Add(Strings.TextEditOutside);
                    }
                }
            }

            return string.Join(' ', parts);
        }
    }

    /// <summary>Says in the hint bar why the line the user pressed is locked (nothing is opened).</summary>
    internal void ShowTextEditNotice(string notice)
    {
        _textEditNotice = notice;
        OnPropertyChanged(nameof(TextEditHint));
    }

    /// <summary>Raised when the open run, the text or the hint changed (the field and the hint bar follow).</summary>
    internal event Action? TextEditFieldChanged;

    private void RaiseTextEditFieldChanged()
    {
        OnPropertyChanged(nameof(OpenTextRun));
        OnPropertyChanged(nameof(TextEditDraft));
        OnPropertyChanged(nameof(HasTextEditDraft));
        OnPropertyChanged(nameof(TextEditHint));
        OnPropertyChanged(nameof(TextEditProblem));
        OnPropertyChanged(nameof(TextEditWholeRun));
        OnPropertyChanged(nameof(CanChooseWholeRun));
        TextEditFieldChanged?.Invoke();
        TextEditMarksChanged?.Invoke();
        SyncFromEditor();
    }

    /// <summary>Opens the field over a run. A locked run does not open (the view shows why); another open field is applied first.</summary>
    /// <returns>Whether the field is open on the run.</returns>
    internal bool OpenTextRunField(TextRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!_isTextEditing || run.IsLocked || _document is null || !CanRunTextEdit())
        {
            return false;
        }

        if (_openTextRun is { } open && Equals(open, run))
        {
            return true;
        }

        if (_openTextRun is not null && !FlushTextEditDraft())
        {
            return false;
        }

        _textEditNotice = null;
        _openTextRun = run;
        _textEditDraft = run.Text;
        _textEditWholeRun = false;
        _textEditDecision = null;
        _textEditOverflow = TextEditOverflow.None;
        _textEditProblem = null;
        _textEditPageText = null;
        FocusedTextRun = run;
        _ = LoadPageTextForOverflowAsync(run);
        RaiseTextEditFieldChanged();
        return true;
    }

    private bool CanRunTextEdit() => !_disposed && IsReady && !_isSaving && !_isPrinting && !_isAskedAbout && !IsEditingFrozen;

    private async Task LoadPageTextForOverflowAsync(TextRun run)
    {
        try
        {
            var text = await _document!.GetPageTextAsync(run.Page);
            if (_openTextRun is { } open && Equals(open, run))
            {
                _textEditPageText = text;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // No overflow hint then.
        }
    }

    /// <summary>The user typed in the field: the text is kept and the hint is worked out again.</summary>
    internal void SetTextEditDraft(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_openTextRun is null || string.Equals(_textEditDraft, text, StringComparison.Ordinal))
        {
            return;
        }

        _textEditDraft = text;
        _textEditProblem = null;
        RaiseTextEditFieldChanged();
        _ = RefreshTextEditHintAsync();
    }

    /// <summary>The choice "Whole line in replacement font" was switched (never on by itself).</summary>
    internal void SetTextEditWholeRun(bool value)
    {
        if (_textEditWholeRun == value)
        {
            return;
        }

        _textEditWholeRun = value;
        RaiseTextEditFieldChanged();
        _ = RefreshTextEditHintAsync();
    }

    private async Task RefreshTextEditHintAsync()
    {
        if (_openTextRun is not { } run || _document is not { } document)
        {
            return;
        }

        var serial = ++_textEditHintSerial;
        var draft = _textEditDraft;
        var mode = _textEditWholeRun ? TextEditMode.WholeRunInGeist : TextEditMode.KeepFont;
        TextEditDecision decision;
        try
        {
            var known = run.Text.EnumerateRunes().Select(static r => r.Value).ToHashSet();
            var wanted = draft.EnumerateRunes().Select(static r => r.Value).Where(c => !known.Contains(c)).Distinct().ToArray();
            var coverage = wanted.Length == 0 ? new NoCoverage() : await document.GetFontCoverageAsync(run, wanted);
            decision = TextEditCheck.Evaluate(run.Text, draft, coverage, mode);
        }
        catch (Exception ex) when (ex is TextRunStaleException or TextRunLockedException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            return;
        }

        if (serial != _textEditHintSerial || _openTextRun is null || !Equals(_openTextRun, run))
        {
            return;
        }

        _textEditDecision = decision;
        _textEditOverflow = OverflowOf(run, draft, decision);
        RaiseTextEditFieldChanged();
    }

    private sealed class NoCoverage : IFontCoverage
    {
        public bool Covers(int codePoint) => false;
    }

    // The new box is estimated from the average width of a character of the run: the real width is known only after writing.
    private TextEditOverflow OverflowOf(TextRun run, string draft, TextEditDecision decision)
    {
        if (decision.Outcome is TextEditOutcome.Rejected or TextEditOutcome.Remove or TextEditOutcome.Unchanged || _textEditPageText is not { } page)
        {
            return TextEditOverflow.None;
        }

        var characters = Math.Max(1, run.Text.Length);
        var width = run.Bounds.Width * draft.Length / characters;
        var box = new PageRect(run.Bounds.Left, run.Bounds.Top, run.Bounds.Left + width, run.Bounds.Bottom);
        var others = page.Chars
            .Where(c => !c.IsGenerated && !(c.Box.Left >= run.Bounds.Left - 0.5 && c.Box.Right <= run.Bounds.Right + 0.5 && c.Box.Top >= run.Bounds.Top - 0.5 && c.Box.Bottom <= run.Bounds.Bottom + 0.5))
            .Select(static c => c.Box)
            .ToList();
        return TextEditOverlap.Find(box, others, new PageRect(0, 0, page.Width, page.Height));
    }

    /// <summary>Closes the field and drops its text (Esc).</summary>
    internal void DiscardTextEditField()
    {
        if (_openTextRun is null)
        {
            return;
        }

        CloseTextEditField();
    }

    private void CloseTextEditField()
    {
        if (_openTextRun is null)
        {
            return;
        }

        _textEditHintSerial++;
        _openTextRun = null;
        _textEditDraft = string.Empty;
        _textEditWholeRun = false;
        _textEditDecision = null;
        _textEditOverflow = TextEditOverflow.None;
        _textEditProblem = null;
        _textEditPageText = null;
        RaiseTextEditFieldChanged();
    }

    /// <summary>
    /// Applies the text of the open field (Enter, leaving the field, before saving and closing): as one step of the history, through the editor, after the
    /// changes queued before it. An unchanged text just closes the field. A text that is refused or cannot be written stays in the field with the reason.
    /// </summary>
    /// <returns><see langword="true"/> when nothing is left in the field.</returns>
    public Task<bool> CommitTextEditAsync()
    {
        if (_textEditCommit is { IsCompleted: false } running)
        {
            return running;
        }

        if (_openTextRun is not { } run)
        {
            return Task.FromResult(true);
        }

        if (!HasTextEditDraft)
        {
            CloseTextEditField();
            return Task.FromResult(true);
        }

        if (!CanRunTextEdit() || _document is null)
        {
            SetTextEditProblem(Strings.TextEditNotNow);
            return Task.FromResult(false);
        }

        var commit = CommitCoreAsync(run, _textEditDraft, _textEditWholeRun ? TextEditMode.WholeRunInGeist : TextEditMode.KeepFont);
        _textEditCommit = commit;
        _ = Track(commit);
        return commit;
    }

    private async Task<bool> CommitCoreAsync(TextRun run, string text, TextEditMode mode)
    {
        try
        {
            _pendingEdits++;
            var result = await Editor.ApplyTextEditAsync(run, text, mode);
            if (_openTextRun is { } open && Equals(open, run) && string.Equals(_textEditDraft, text, StringComparison.Ordinal))
            {
                CloseTextEditField();
            }

            if (result is not null)
            {
                FocusedTextRun = null;
                TextEditApplied?.Invoke(run.Page);
            }

            return true;
        }
        catch (TextEditRejectedException ex)
        {
            _textEditDecision = ex.Decision;
            SetTextEditProblem(null);
            RaiseTextEditFieldChanged();
        }
        catch (TextRunStaleException)
        {
            SetTextEditProblem(Strings.TextEditStale);
            InvalidateTextRuns();
        }
        catch (TextRunLockedException ex)
        {
            SetTextEditProblem(LockTextOf(ex.Reason));
        }
        catch (AnnotationHistoryFullException)
        {
            SetTextEditProblem(Strings.SignatureHistoryFull);
        }
        catch (TextEditWriteException)
        {
            SetTextEditProblem(Strings.TextEditWriteFailed);
            InvalidateTextRuns();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or OperationCanceledException)
        {
            SetTextEditProblem(Strings.TextEditNotNow);
        }
        finally
        {
            _pendingEdits--;
            SyncFromEditor();
        }

        return false;
    }

    private void SetTextEditProblem(string? problem)
    {
        _textEditProblem = problem;
        RaiseTextEditFieldChanged();
    }

    /// <summary>
    /// Applies the text of an open field, before anything that ends or blocks editing (saving, printing, the end of the mode, closing, the question before an
    /// update); a text that cannot be applied stays in the field with the reason.
    /// </summary>
    /// <returns><see langword="true"/> when nothing is waiting in the field now.</returns>
    internal bool FlushTextEditDraft()
    {
        if (_openTextRun is null)
        {
            return true;
        }

        if (!HasTextEditDraft)
        {
            CloseTextEditField();
            return true;
        }

        _ = CommitTextEditAsync();
        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------- state

    private void RaiseTextEditStateChanged()
    {
        OnPropertyChanged(nameof(TextEditBlockedReason));
        OnPropertyChanged(nameof(CanTextEdit));
        OnPropertyChanged(nameof(TextEditActiveReason));
        _toggleTextEditCommand?.RaiseCanExecuteChanged();
        RaiseSaveCanExecuteChanged();
        RaiseAnnotateCanExecuteChanged();
    }

    /// <summary>Another file is open in the tab: the mode ends, the runs are dropped and the note may come again.</summary>
    private void ResetTextEdit()
    {
        _textEditNoticeShown = false;
        TextEditSaveNote = string.Empty;
        if (_isTextEditing || _openTextRun is not null)
        {
            CloseTextEditField();
            _textRuns.Clear();
            _textRunsGeneration++;
            HoverTextRun = null;
            FocusedTextRun = null;
            IsTextEditing = false;
        }
    }
}
