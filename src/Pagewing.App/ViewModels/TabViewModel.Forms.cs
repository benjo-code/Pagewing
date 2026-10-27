using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The mode "Fill in form" of the tab (milestone 11): whether it is on, the fields of the pages (read per page when first
/// asked for and kept), the field with the focus and the tab order, the open value of a text field (the draft), and the hints
/// about what the form contains that Pagewing does not run. It is a mode of its own next to "Comment": only one of them is on
/// at a time, and switching applies the open draft of the other first; undo, redo, save and the update lock are shared
/// through the editor (<see cref="Editor"/>).
/// </summary>
/// <remarks>
/// <para>
/// Nothing of it exists for a document without a form (<see cref="HasForm"/>): no read, no editor, no command that runs.
/// Nothing is read before the first sharp page (<see cref="HasShownSharpPage"/>): then the hints are read once per document in
/// the background (<see cref="EnsureFormInfoAsync"/>, a scan of all pages that a write needs as well, so it is shared); the
/// fields of a page are read when the mode or the view first asks for them (<see cref="FieldsOfPage"/>).
/// </para>
/// <para>
/// The text typed into a field is an unsaved change (<see cref="HasFormDraft"/>) and is applied by
/// <see cref="FlushFormDraft"/> before anything that ends or blocks editing (save, print, the end of the mode, the switch to
/// "Comment", close, the question before an update), while the tab still takes it. A value the field cannot take (characters
/// it cannot show, too long, a line break in a one-line field; PDFium refused it) is never shortened or dropped: it stays in
/// <see cref="CurrentDraft"/> with the reason, and so does a value the tab cannot take at the moment (<see cref="PendingFormDraft"/>).
/// </para>
/// </remarks>
public sealed partial class TabViewModel
{
    private static readonly IReadOnlyList<FormField> NoFields = [];

    private bool _isFilling;
    private bool _highlightFields = true;

    // What the session was last told about the highlight (ViewerSession.SetFormHighlight), so an unchanged state costs nothing.
    private bool _sessionFormHighlight;
    private FormWidgetKey? _focusedField;

    // The open value of a text field, and the one a commit is applying now (the same object until it ended).
    private FormDraft? _formDraft;
    private FormDraft? _formDraftInFlight;

    // Texts PDFium refused after the user had moved on to another field: kept per field (oldest first, at most
    // MaxKeptFormDrafts), so they come back with their field instead of being lost (see Refuse).
    private readonly List<FormDraft> _keptDrafts = [];

    // The fields read per page (0-based) of _formFieldsDocument; a page is read again when the editor's history moved on.
    private readonly Dictionary<int, FormPageFields> _formPages = [];
    private PdfDocument? _formFieldsDocument;
    private int _formFieldsSeenVersion;

    // What the document says about its form (hints, pages with fields), read once per document; the hints dismissed for it.
    private PdfDocument? _formInfoDocument;
    private FormInfo? _formInfo;
    private Task _formInfoLoad = Task.CompletedTask;
    private CancellationTokenSource? _formInfoCancellation;
    private FormHint _dismissedHints;

    // The offer of the mode in the hint bar was closed, or the mode was started (once per tab and document).
    private bool _fillOfferClosed;

    // The reason of the last refused value that no field shows (FormValueRefused), until a value is taken or it is closed.
    private string? _formNotice;

    private RelayCommand? _toggleFillCommand;
    private RelayCommand? _endFillCommand;
    private RelayCommand? _nextFieldCommand;
    private RelayCommand? _previousFieldCommand;
    private RelayCommand? _toggleHighlightCommand;
    private RelayCommand<FormHint>? _dismissHintCommand;
    private RelayCommand? _closeFillOfferCommand;
    private RelayCommand? _dismissFormNoticeCommand;

    /// <summary>
    /// Raised on the UI thread when fields of a page were read (the 0-based page), or with -1 when the history of the editor
    /// moved on and the fields of every page may have other values now: the view asks <see cref="FieldsOfPage"/> again.
    /// </summary>
    internal event Action<int>? FormFieldsChanged;

    /// <summary>
    /// Raised on the UI thread with the reason when a value was refused that no draft keeps (a check box, a choice, or a text
    /// whose field was left meanwhile), for the window to show.
    /// </summary>
    internal event Action<string>? FormValueRefused;

    /// <summary>
    /// Gets whether the open document has a form (an AcroForm, known from the open on, <see cref="PdfDocument.HasAcroForm"/>):
    /// only then is the mode offered.
    /// </summary>
    public bool HasForm
    {
        get
        {
            if (_disposed || _document is not { } document)
            {
                return false;
            }

            try
            {
                return document.HasAcroForm;
            }
            catch (ObjectDisposedException)
            {
                // Replaced or closed meanwhile; the change of the session follows.
                return false;
            }
        }
    }

    /// <summary>Gets whether the read of the form found fields (known after the first sharp page, <see cref="EnsureFormInfoAsync"/>).</summary>
    public bool HasFormFields => CurrentFormInfo is { HasFields: true };

    /// <summary>
    /// Gets whether the mode "Fill in form" is on: the fields can be filled in, Tab walks them, and the window shows the
    /// toolbar of the mode. Only one of the modes "Fill in form" and "Comment" is on at a time.
    /// </summary>
    public bool IsFilling
    {
        get => _isFilling;
        private set
        {
            if (SetProperty(ref _isFilling, value))
            {
                // The offer of the hint bar has done its job once the mode was on.
                _fillOfferClosed |= value;
                ApplyFormHighlight();
                OnPropertyChanged(nameof(ShowsFieldHighlight));
                OnPropertyChanged(nameof(OffersFill));
                RaiseFormCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Gets whether the fields are highlighted while filling (the switch of the toolbar; on at first). The tab keeps the
    /// choice for the next time the mode starts.
    /// </summary>
    public bool HighlightFields
    {
        get => _highlightFields;
        internal set
        {
            if (SetProperty(ref _highlightFields, value))
            {
                ApplyFormHighlight();
                OnPropertyChanged(nameof(ShowsFieldHighlight));
            }
        }
    }

    /// <summary>
    /// Gets whether the view highlights the fields now: in the mode, with <see cref="HighlightFields"/> on. The session renders
    /// with it (<see cref="ViewerSession.SetFormHighlight"/>, told before the change is raised, so the document view sets
    /// <c>DocumentCanvas.FormHighlight</c> only once the scheduler renders with the new setting).
    /// </summary>
    public bool ShowsFieldHighlight => _isFilling && _highlightFields;

    /// <summary>
    /// Gets what the form contains that Pagewing does not run and that was not dismissed for this document in this tab
    /// (<see cref="DismissHintCommand"/>); <see cref="FormHint.None"/> until the read after the first sharp page ended. A save
    /// that swaps the document for the written file keeps what was dismissed; another file brings its own hints.
    /// </summary>
    public FormHint PendingHints => CurrentFormInfo is { } info ? info.Hints & ~_dismissedHints : FormHint.None;

    /// <summary>
    /// Gets whether the window offers the mode in its hint bar: a document whose form has fields, after its first sharp page,
    /// the mode not on, and the offer neither closed (<see cref="CloseFillOfferCommand"/>) nor used before for this document
    /// in this tab (once the mode was on, it is not offered again).
    /// </summary>
    public bool OffersFill => CanReadForm && !_isFilling && !_fillOfferClosed && HasFormFields;

    /// <summary>
    /// Gets the reason of the last value that was refused and that no field shows (<see cref="FormValueRefused"/>), for the
    /// window to show as a notice; <see langword="null"/> without one. It stays with the tab until the next value is taken or
    /// it is closed (<see cref="DismissFormNoticeCommand"/>).
    /// </summary>
    public string? FormNotice
    {
        get => _formNotice;
        private set => SetProperty(ref _formNotice, value);
    }

    /// <summary>Closes the offer of the mode in the hint bar for this document in this tab, for good.</summary>
    public ICommand CloseFillOfferCommand => _closeFillOfferCommand ??= new RelayCommand(() =>
    {
        _fillOfferClosed = true;
        OnPropertyChanged(nameof(OffersFill));
    });

    /// <summary>Closes the notice of a refused value (<see cref="FormNotice"/>).</summary>
    public ICommand DismissFormNoticeCommand => _dismissFormNoticeCommand ??= new RelayCommand(() => FormNotice = null);

    /// <summary>Gets the widget that has the focus in the mode (set by the view and by <see cref="MoveToFieldAsync"/>), or <see langword="null"/>.</summary>
    public FormWidgetKey? FocusedField
    {
        get => _focusedField;
        internal set
        {
            if (SetProperty(ref _focusedField, value) && value is { } key && _isFilling)
            {
                // A text refused after the field was left comes back with the field.
                PromoteKeptDraft(key);
                PrefetchAround(key.Page);
            }
        }
    }

    /// <summary>
    /// Gets the open value of a text field: the field, the typed text, the value it had, and why it could not be applied
    /// (<see cref="FormDraft.Reason"/>); <see langword="null"/> without one.
    /// </summary>
    public FormDraft? CurrentDraft => _formDraft;

    /// <summary>
    /// Gets the open value that waits because the tab took no edit when it was to be applied (a save or print ran, a card
    /// asked about the tab, editing was locked for an update), or <see langword="null"/>. It is applied by the next flush.
    /// </summary>
    internal FormDraft? PendingFormDraft => _formDraft is { Waiting: true } waiting ? waiting : null;

    /// <summary>
    /// Gets whether a text typed into a field is not in the document yet: an unsaved change (<see cref="AsksBeforeClosing"/>,
    /// <see cref="IsDirty"/>). A field entered and left without a change is none; a refused text kept for a field the user left
    /// meanwhile is one.
    /// </summary>
    public bool HasFormDraft => !_disposed && ((_formDraft is { } draft && IsChanged(draft)) || _keptDrafts.Count > 0);

    /// <summary>The command "Fill in form": starts the mode or ends it (applying the open value first).</summary>
    public ICommand ToggleFillCommand => _toggleFillCommand ??= new RelayCommand(
        () =>
        {
            if (_isFilling)
            {
                EndFill();
            }
            else
            {
                _ = EnterFillAsync();
            }
        },
        () => _isFilling || CanBeginFill());

    /// <summary>"Done" in the toolbar of the mode: ends it; the values stay, unsaved.</summary>
    public ICommand EndFillCommand => _endFillCommand ??= new RelayCommand(() => EndFill(), () => _isFilling);

    /// <summary>The next field in the tab order (the toolbar); after the last one nothing moves.</summary>
    public ICommand NextFieldCommand => _nextFieldCommand ??= new RelayCommand(() => _ = MoveToFieldAsync(forward: true), () => _isFilling);

    /// <summary>The previous field in the tab order (the toolbar); before the first one nothing moves.</summary>
    public ICommand PreviousFieldCommand => _previousFieldCommand ??= new RelayCommand(() => _ = MoveToFieldAsync(forward: false), () => _isFilling);

    /// <summary>"Highlight fields" in the mode: switches <see cref="HighlightFields"/>.</summary>
    public ICommand ToggleHighlightCommand => _toggleHighlightCommand ??= new RelayCommand(() => HighlightFields = !_highlightFields, () => _isFilling);

    /// <summary>Closes hints (parameter: the <see cref="FormHint"/> flags) for this document in this tab, for good.</summary>
    public ICommand DismissHintCommand => _dismissHintCommand ??= new RelayCommand<FormHint>(DismissHints, hint => hint != FormHint.None && (PendingHints & hint) != 0);

    /// <summary>
    /// Gets or sets the 0-based pages the view shows (set by the document view); without it the row of
    /// <see cref="CurrentPage"/>. Entering the mode reads the fields of these pages.
    /// </summary>
    internal Func<IEnumerable<int>>? VisibleFormPages { get; set; }

    /// <summary>Gets or sets how the fields of a page are read: <see cref="PdfDocument.GetFormFieldsAsync"/> (replaceable for tests).</summary>
    internal Func<PdfDocument, int, CancellationToken, Task<IReadOnlyList<FormField>>> FormFieldReader { get; set; } =
        static (document, page, cancellationToken) => document.GetFormFieldsAsync(page, cancellationToken);

    /// <summary>Gets or sets how many refused texts of fields left meanwhile the tab keeps (8; lower in tests).</summary>
    internal int MaxKeptFormDrafts { get; set; } = 8;

    /// <summary>
    /// Gets or sets how a value is written: <see cref="AnnotationEditor.SetFormValueAsync"/> (replaceable for tests, to make PDFium
    /// refuse a value at a chosen moment).
    /// </summary>
    internal Func<AnnotationEditor, string, FormFieldState, Task<FormChange?>> FormValueWriter { get; set; } =
        static (editor, fieldName, state) => editor.SetFormValueAsync(fieldName, state);

    /// <summary>Gets or sets how the form of a document is read: <see cref="PdfDocument.GetFormInfoAsync"/> (replaceable for tests).</summary>
    internal Func<PdfDocument, CancellationToken, Task<FormInfo>> FormInfoReader { get; set; } =
        static (document, cancellationToken) => document.GetFormInfoAsync(cancellationToken);

    /// <summary>What the form of the open document says, once read for it; else <see langword="null"/>.</summary>
    private FormInfo? CurrentFormInfo =>
        !_disposed && _document is not null && ReferenceEquals(_formInfoDocument, _document) ? _formInfo : null;

    /// <summary>Whether form data may be read now: an open document with a form, whose first sharp page was shown.</summary>
    private bool CanReadForm => !_disposed && IsReady && _hasShownSharpPage && HasForm;

    /// <summary>Whether a value can be applied now: in the mode, no card asks about the tab, no lock, no save and no print job.</summary>
    private bool CanFillNow => _isFilling && CanRunMarkup() && !_isSaving && !_isPrinting;

    /// <summary>Whether a widget can be changed in Pagewing: it has no lock.</summary>
    /// <param name="field">The widget.</param>
    /// <returns>Whether it is editable.</returns>
    internal static bool IsEditable(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Lock == FormLock.None;
    }

    /// <summary>
    /// The text that says why a widget cannot be changed (tooltip and automation of its lock), or <see langword="null"/> for an
    /// editable one; never the English diagnostic of the document (<see cref="FormField.LockReason"/>).
    /// </summary>
    /// <param name="field">The widget.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    internal static string? LockTextOf(FormField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Lock switch
        {
            FormLock.None => null,
            FormLock.ReadOnly => Strings.FormLockedReadOnly,
            _ when field.Key.FieldName.Length == 0 => Strings.FormLockedNameless,
            _ when field.Kind == FormFieldKind.Signature => Strings.FormLockedSignature,
            _ when field.Kind == FormFieldKind.PushButton => Strings.FormLockedButton,
            _ when field.Kind == FormFieldKind.Text && field.Traits.HasFlag(FormFieldTraits.FileSelect) => Strings.FormLockedFile,
            _ => Strings.FormLockedOther,
        };
    }

    /// <summary>The text of one hint (one flag of <see cref="FormHint"/>).</summary>
    /// <param name="hint">The hint.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="hint"/> is not exactly one hint.</exception>
    internal static string HintTextOf(FormHint hint) => hint switch
    {
        FormHint.Calculated => Strings.FormHintCalculated,
        FormHint.FormatScripts => Strings.FormHintFormatScripts,
        FormHint.Xfa => Strings.FormHintXfa,
        FormHint.SignatureField => Strings.FormHintSignatureField,
        FormHint.Buttons => Strings.FormHintButtons,
        _ => throw new ArgumentOutOfRangeException(nameof(hint), hint, "Not exactly one hint."),
    };

    /// <summary>The reason shown for a refused text (<see cref="FormValueRules.CheckText"/>), or <see langword="null"/> when it is accepted.</summary>
    /// <param name="check">The outcome of the check.</param>
    /// <returns>The localized reason, or <see langword="null"/>.</returns>
    internal static string? ProblemTextOf(FormValueCheck check) => check.Problem switch
    {
        FormValueProblem.None => check.Accepted ? null : Strings.FormValueRefused,
        FormValueProblem.UnsupportedCharacters => Format(Strings.FormValueUnsupportedCharacters, check.Detail ?? string.Empty),
        FormValueProblem.TooLong => Format(Strings.FormValueTooLong, check.Detail ?? string.Empty),
        FormValueProblem.LineBreakNotAllowed => Strings.FormValueOneLine,
        FormValueProblem.ControlCharacter => Format(Strings.FormValueControlCharacter, check.Detail ?? string.Empty),
        _ => Strings.FormValueRefused,
    };

    /// <summary>
    /// Gets the widgets of a page in the order of its <c>/Annots</c>, as read for the open document; an empty list while they
    /// are being read (<see cref="FormFieldsChanged"/> tells when they are there), for a page without fields, and before the
    /// first sharp page or without a form (then nothing is read at all). A page is read once and kept until the history of the
    /// editor moves on (another value, undo, redo), then read again on the next request; another document starts afresh.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <returns>The widgets of the page.</returns>
    public IReadOnlyList<FormField> FieldsOfPage(int page)
    {
        if (!CanReadForm || page < 0 || page >= PageCount)
        {
            return NoFields;
        }

        var entry = PageEntry(page);
        if (!IsCurrent(entry))
        {
            StartFieldsRead(page, entry);
        }

        return entry.Fields;
    }

    /// <summary>
    /// The widgets of a page as <see cref="FieldsOfPage"/> has them, once read: complete at once when they are known and
    /// current, else when the read ended.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <returns>The widgets of the page; empty without a form, before the first sharp page or for a page that could not be read.</returns>
    internal Task<IReadOnlyList<FormField>> ReadFieldsAsync(int page)
    {
        if (!CanReadForm || page < 0 || page >= PageCount)
        {
            return Task.FromResult(NoFields);
        }

        var entry = PageEntry(page);
        return IsCurrent(entry) ? Task.FromResult(entry.Fields) : StartFieldsRead(page, entry);
    }

    /// <summary>
    /// Reads what the form of the open document contains (hints, pages with fields), once per document and tab, in the
    /// background: everything that needs it shares this read (the hints after the first sharp page, the tab order, a write,
    /// which would scan the form again otherwise). Never before the first sharp page and never without a form. A save that
    /// swaps the document for the written file keeps what was read.
    /// </summary>
    /// <returns>Completes when the read has ended; it never fails.</returns>
    internal Task EnsureFormInfoAsync()
    {
        if (!CanReadForm || _document is not { } document)
        {
            return Task.CompletedTask;
        }

        if (ReferenceEquals(document, _formInfoDocument))
        {
            return _formInfoLoad;
        }

        ResetFormInfo();
        var cancellation = new CancellationTokenSource();
        _formInfoDocument = document;
        _formInfoCancellation = cancellation;
        _formInfoLoad = LoadFormInfoAsync(document, cancellation);
        return _formInfoLoad;
    }

    /// <summary>
    /// Starts the mode: the open draft of "Comment" (a text box field, a nudge) is applied and that mode ends; then the fields
    /// of the pages the view shows are read. Only with a form, after the first sharp page.
    /// </summary>
    /// <returns>Completes when the fields of the visible pages are read.</returns>
    public async Task EnterFillAsync()
    {
        if (!CanBeginFill())
        {
            return;
        }

        if (_isAnnotating)
        {
            FlushNudge();
            EndAnnotate();
        }

        IsFilling = true;

        // A text that waits (refused while the mode was off, or kept for its field) is shown again, with the focus in its field.
        FocusOpenDraft();
        var known = CurrentFormInfo?.PagesWithFields;
        var reads = VisiblePagesForForms()
            .Where(page => known is null || known.Contains(page))
            .Select(ReadFieldsAsync)
            .ToList();
        await Task.WhenAll(reads);
    }

    /// <summary>
    /// Ends the mode ("Done", the command, Esc, the switch to "Comment"): the open value is applied first. A value that cannot be
    /// applied keeps the mode, with the focus in its field, so it is never dropped unseen.
    /// </summary>
    /// <returns>Whether the mode is off now.</returns>
    public bool EndFill()
    {
        if (!_isFilling)
        {
            return true;
        }

        if (!FlushFormDraft())
        {
            if (_formDraft is { } kept)
            {
                FocusedField = kept.Key;
            }

            return false;
        }

        if (_formDraft is { } open && !ReferenceEquals(open, _formDraftInFlight))
        {
            SetDraft(null);
        }

        FocusedField = null;
        IsFilling = false;
        return true;
    }

    /// <summary>
    /// Esc of the mode (DocumentView, when the field itself did not take it): drops the open value, else takes the focus from
    /// the field, else ends the mode.
    /// </summary>
    /// <returns>Whether Esc had something to end.</returns>
    internal bool EscapeFill()
    {
        if (!_isFilling)
        {
            return false;
        }

        if (_formDraft is { } open && !ReferenceEquals(open, _formDraftInFlight))
        {
            DiscardDraft();
        }
        else if (_focusedField is not null)
        {
            FocusedField = null;
        }
        else
        {
            EndFill();
        }

        return true;
    }

    /// <summary>
    /// Takes the text typed into a field as its open value (every change of the field's text): from now on it is an unsaved
    /// change. Only a text field or an editable combo box takes one; a field that cannot be changed, or one the tab has not
    /// read, takes none. While another field has an open value that cannot be applied, this one takes none either (the other's
    /// text would be lost).
    /// </summary>
    /// <param name="key">The widget.</param>
    /// <param name="text">The text of the field.</param>
    /// <returns>Whether the text is the open value now.</returns>
    internal bool EditDraft(FormWidgetKey key, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!_isFilling || _isEditingFrozen || !CanReadForm || FindWidget(key) is not { } field || !IsEditable(field)
            || !(field.Kind == FormFieldKind.Text || (field.Kind == FormFieldKind.ComboBox && field.Traits.HasFlag(FormFieldTraits.Edit))))
        {
            return false;
        }

        if (_formDraft is { } other && other.Key != key && !ReferenceEquals(other, _formDraftInFlight) && !FlushFormDraft())
        {
            return false;
        }

        var original = _formDraft is { } same && same.Key == key ? same.OriginalText : field.State.Text ?? string.Empty;
        var kept = _keptDrafts.FindIndex(each => SameField(each, key));
        if (kept >= 0)
        {
            // Typed over a refused text of this field that was kept: the new text replaces it, against the same value.
            original = _keptDrafts[kept].OriginalText;
            _keptDrafts.RemoveAt(kept);
        }

        SetDraft(new FormDraft(key, text, original));
        return true;
    }

    /// <summary>Drops the open value (Esc in the field); the field keeps the value the document has.</summary>
    internal void DiscardDraft()
    {
        if (_formDraft is not null)
        {
            SetDraft(null);
        }
    }

    /// <summary>
    /// Writes a value into a field (leaving a text field, a click on a check box or radio button, a choice) as one step of the
    /// history, through the editor, after the changes queued before it. A text is checked first
    /// (<see cref="FormValueRules.CheckText"/>); a refused one, or one the tab cannot take now, stays as the open value with the
    /// reason (<see cref="CurrentDraft"/>), as does one PDFium did not take. A value equal to the one the field has makes no step.
    /// </summary>
    /// <param name="fieldName">The full name of the field.</param>
    /// <param name="state">The value (see <see cref="PdfDocument.SetFormValueAsync(string, FormFieldState, CancellationToken)"/>).</param>
    /// <returns>
    /// <see langword="true"/> when the field has the value now (written, or it had it already); <see langword="false"/> when it
    /// was refused or could not be written (the reason is in the draft, or raised as <see cref="FormValueRefused"/>).
    /// </returns>
    public Task<bool> CommitFieldAsync(string fieldName, FormFieldState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentNullException.ThrowIfNull(state);
        return BeginCommit(fieldName, state) ?? Task.FromResult(false);
    }

    /// <summary>
    /// Applies the open value now, while the tab still takes edits: called before everything that would end or block editing
    /// (saving, printing, the end of the mode, closing, the question before an update; with <see cref="FlushNudge"/>). The
    /// write runs as a change the tab tracks (<see cref="WhenEditsSettledAsync"/> waits for it).
    /// </summary>
    /// <remarks>
    /// Values are applied only in the mode: outside it (the mode ended or "Comment" took over before PDFium refused a value) the
    /// open value waits and this returns <see langword="false"/>; the next start of the mode shows it and applies it.
    /// </remarks>
    /// <returns>
    /// Whether the open value was applied or is being applied (or there is none): <see langword="false"/> when it stays with a
    /// reason. Refused texts kept for other fields (<see cref="HasFormDraft"/>) are not retried: PDFium refused them already.
    /// </returns>
    internal bool FlushFormDraft()
    {
        if (_disposed || _formDraft is not { } draft || ReferenceEquals(draft, _formDraftInFlight) || !IsChanged(draft))
        {
            return true;
        }

        return BeginCommit(draft.Key.FieldName, FormFieldState.OfText(draft.Text)) is not null;
    }

    /// <summary>
    /// Moves the focus to the next or previous field that can be changed, in the tab order: the pages in turn, on a page the
    /// order of <c>/Annots</c>; the open value is applied first (one that cannot be applied keeps the focus in its field). The
    /// pages after the current one are read as they are needed, one at a time.
    /// </summary>
    /// <param name="forward">Tab (<see langword="true"/>) or Shift+Tab.</param>
    /// <returns>
    /// Whether the key was handled: <see langword="false"/> after the last field (or before the first), so the view lets Tab go on
    /// (Tab never traps the focus). Complete at once when the pages needed are read already.
    /// </returns>
    internal async Task<bool> MoveToFieldAsync(bool forward)
    {
        if (!_isFilling || !CanReadForm)
        {
            return false;
        }

        if (!FlushFormDraft())
        {
            // The value stays in its field with the reason, and so does the focus; but after the last field (before the first)
            // the key still goes on, so Tab never traps the focus (the value stays with the tab, nothing is lost).
            var before = _focusedField;
            if (_formDraft is { } kept)
            {
                FocusedField = kept.Key;
            }

            return before != _focusedField || HasFieldBeyond(forward);
        }

        var generation = _documentGeneration;
        await EnsureFormInfoAsync();
        if (generation != _documentGeneration || !_isFilling || !CanReadForm)
        {
            return false;
        }

        var current = _focusedField;
        var start = current?.Page ?? Math.Clamp(CurrentPage - 1, 0, Math.Max(0, PageCount - 1));
        var pages = PagesWithFields();
        var walk = new List<int>();
        if (pages.Contains(start))
        {
            walk.Add(start);
        }

        walk.AddRange(forward ? pages.Where(page => page > start) : pages.Where(page => page < start).Reverse());
        foreach (var page in walk)
        {
            var fields = await ReadFieldsAsync(page);
            if (generation != _documentGeneration || !_isFilling)
            {
                return false;
            }

            if (FormNavigation.Next(fields, page == start ? current : null, forward) is { } next)
            {
                FocusedField = next;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a field that can be changed follows the focused one in the tab order (<paramref name="forward"/>) or comes before
    /// it, as far as known now: a page with fields that is not read yet counts as one that has some.
    /// </summary>
    private bool HasFieldBeyond(bool forward)
    {
        if (_focusedField is not { } current)
        {
            return true;
        }

        if (_formPages.TryGetValue(current.Page, out var own) && FormNavigation.Next(own.Fields, current, forward) is not null)
        {
            return true;
        }

        foreach (var page in PagesWithFields())
        {
            if (forward ? page <= current.Page : page >= current.Page)
            {
                continue;
            }

            if (!_formPages.TryGetValue(page, out var entry) || entry.Version < 0 || FormNavigation.Next(entry.Fields, null, forward) is not null)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the mode can start now: a form, shown sharp, the mode not on.</summary>
    private bool CanBeginFill() => !_isFilling && CanReadForm;

    /// <summary>
    /// The checks of a write and its start. <see langword="null"/>: refused at once (the reason is in the draft, or raised);
    /// otherwise the write, which may be complete already (the field has the value).
    /// </summary>
    private Task<bool>? BeginCommit(string fieldName, FormFieldState state)
    {
        if (_disposed || !_isFilling || !CanReadForm)
        {
            return null;
        }

        var isText = state.Checked is null && state.SelectedOptions.Count == 0 && state.Text is not null;
        var draft = _formDraft is { } open && isText && string.Equals(open.Key.FieldName, fieldName, StringComparison.Ordinal)
            && string.Equals(open.Text, state.Text, StringComparison.Ordinal) ? open : null;
        var (field, current) = FindField(fieldName, draft?.Key);
        if (field is not null && !IsEditable(field))
        {
            Refuse(draft, LockTextOf(field)!);
            return null;
        }

        var textual = isText && (field is null ? draft is not null : field.Kind is FormFieldKind.Text or FormFieldKind.ComboBox);
        if (textual && field is not null)
        {
            var check = FormValueRules.CheckText(field, state.Text!);
            if (!check.Accepted)
            {
                KeepDraft(draft, field, state.Text!, ProblemTextOf(check)!, waiting: false);
                return null;
            }
        }

        if (current && field is not null && (field.Kind == FormFieldKind.Text
            ? string.Equals(field.State.Text ?? string.Empty, state.Text ?? string.Empty, StringComparison.Ordinal)
            : field.State.Equals(state)))
        {
            // Entered and left without a change: no step, nothing unsaved.
            if (draft is not null)
            {
                SetDraft(null);
            }

            FormNotice = null;
            return Task.FromResult(true);
        }

        if (!CanFillNow)
        {
            if (textual && field is not null)
            {
                KeepDraft(draft, field, state.Text!, Strings.FormValueNotNow, waiting: true);
            }
            else
            {
                NotifyRefused(Strings.FormValueNotNow);
            }

            return null;
        }

        if (draft is null && textual && field is not null)
        {
            draft = new FormDraft(field.Key, state.Text!, field.State.Text ?? string.Empty);
            SetDraft(draft);
        }
        else if (draft is { Reason: not null })
        {
            draft = draft with { Reason = null, Waiting = false };
            SetDraft(draft);
        }

        _formDraftInFlight = draft;
        var run = CommitCoreAsync(_documentGeneration, fieldName, state, draft, _drawingTail, EditVersion);
        Track(run);
        _drawingTail = run;
        return run;
    }

    /// <summary>
    /// One write: after the changes queued before it (undo waits for it like for a drawing), through the editor; counted as a
    /// change from the call to its end (<see cref="Track"/>). Never fails.
    /// </summary>
    private async Task<bool> CommitCoreAsync(int generation, string fieldName, FormFieldState state, FormDraft? draft, Task before, int versionBefore)
    {
        try
        {
            _pendingEdits++;
            _drawingsInFlight++;
            RaiseStepCanExecuteChanged();
            try
            {
                await before;
            }
            catch (Exception)
            {
                // The changes before it never fail; only their end matters.
            }

            // The write needs the scan of the form (the pages of the field): the tab's, never a second one beside it.
            await EnsureFormInfoAsync();
            if (_disposed || generation != _documentGeneration)
            {
                return false;
            }

            FormChange? change;
            try
            {
                change = await FormValueWriter(Editor, fieldName, state);
            }
            catch (FormWriteException ex)
            {
                Refuse(draft, ex.Restored ? Strings.FormValueNotTaken : Strings.FormValueNotTakenChanged);
                return false;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                // The document went meanwhile: a draft stays as it is (still unsaved), nothing else to say.
                return false;
            }
            catch (Exception)
            {
                // Refused before anything was written (the value, a lock, the pages of the field not known).
                Refuse(draft, Strings.FormValueRefused);
                return false;
            }

            if (_disposed || generation != _documentGeneration)
            {
                return change is not null;
            }

            if (change is not null)
            {
                RememberValue(fieldName, change.After, versionBefore);
            }

            // A value was taken: an earlier refusal is no longer news.
            FormNotice = null;

            if (draft is not null && ReferenceEquals(_formDraft, draft))
            {
                SetDraft(null);
            }
            else if (_formDraft is { } newer && isSameField(newer) && state.Text is { } written)
            {
                // Typed on while it was written: the newer text is still open, against the value written now.
                SetDraft(newer with { OriginalText = written });
            }

            return true;
        }
        finally
        {
            if (ReferenceEquals(_formDraftInFlight, draft))
            {
                _formDraftInFlight = null;
            }

            _pendingEdits--;
            _drawingsInFlight--;
            RaiseStepCanExecuteChanged();
            SyncFromEditor();
        }

        bool isSameField(FormDraft other) => string.Equals(other.Key.FieldName, fieldName, StringComparison.Ordinal);
    }

    /// <summary>
    /// A value was refused; never is a typed text lost unseen. The open value keeps it with the reason and the focus goes back to
    /// its field: in the mode at once; when the mode ended meanwhile, the mode starts again where it can, else the window is told
    /// and the next start of the mode shows it. When the user typed into another field meanwhile, the refused text is kept for
    /// its field (<see cref="KeepRefused"/>) and the window is told. A text typed on in the same field replaces it.
    /// </summary>
    private void Refuse(FormDraft? draft, string reason)
    {
        if (draft is null)
        {
            NotifyRefused(reason);
            return;
        }

        var refused = draft with { Reason = reason, Waiting = false };
        if (_formDraft is null || ReferenceEquals(_formDraft, draft))
        {
            SetDraft(refused);
            if (_isFilling)
            {
                FocusedField = draft.Key;
            }
            else if (!TryReenterFill(draft.Key))
            {
                NotifyRefused(reason);
            }

            return;
        }

        if (!SameField(_formDraft, draft.Key))
        {
            KeepRefused(refused);
        }

        NotifyRefused(reason);
    }

    /// <summary>
    /// Keeps a refused text of a field the user left (one per field, the newest wins); beyond <see cref="MaxKeptFormDrafts"/> the
    /// oldest goes, and the window is told its reason once more.
    /// </summary>
    private void KeepRefused(FormDraft refused)
    {
        _keptDrafts.RemoveAll(each => SameField(each, refused.Key));
        _keptDrafts.Add(refused);
        while (_keptDrafts.Count > Math.Max(0, MaxKeptFormDrafts))
        {
            var dropped = _keptDrafts[0];
            _keptDrafts.RemoveAt(0);
            NotifyRefused(dropped.Reason ?? Strings.FormValueRefused);
        }

        OnPropertyChanged(nameof(HasFormDraft));
        SyncFromEditor();
    }

    /// <summary>
    /// Makes the refused text kept for the field of <paramref name="key"/> the open value again, at that widget, unless the open
    /// value has a change of its own that is not being applied.
    /// </summary>
    /// <returns>Whether it did.</returns>
    private bool PromoteKeptDraft(FormWidgetKey key)
    {
        var index = _keptDrafts.FindIndex(each => SameField(each, key));
        if (index < 0 || (_formDraft is { } open && !ReferenceEquals(open, _formDraftInFlight) && IsChanged(open)))
        {
            return false;
        }

        var kept = _keptDrafts[index];
        _keptDrafts.RemoveAt(index);
        SetDraft(kept with { Key = key });
        return true;
    }

    /// <summary>The open value, or else the oldest kept refused text, gets the focus (the mode is on).</summary>
    private void FocusOpenDraft()
    {
        if (_formDraft is null && _keptDrafts.Count > 0)
        {
            PromoteKeptDraft(_keptDrafts[0].Key);
        }

        if (_formDraft is { } open)
        {
            FocusedField = open.Key;
        }
    }

    /// <summary>
    /// Starts the mode again for a value refused after it ended, with the focus in its field: not while "Comment" is on (the
    /// user chose it), editing is locked, or a card asks about the tab.
    /// </summary>
    /// <returns>Whether the mode is on now.</returns>
    private bool TryReenterFill(FormWidgetKey key)
    {
        if (_isAnnotating || _isEditingFrozen || _isAskedAbout || !CanBeginFill())
        {
            return false;
        }

        IsFilling = true;
        FocusedField = key;
        return true;
    }

    /// <summary>
    /// Shows the typed text that keeps the tab "unsaved" although a save cannot write it (PDFium refused it): the mode starts with
    /// the focus in its field where it can, and the window is told the reason. The question before closing stops here instead of
    /// asking again after every save (<see cref="MainViewModel"/>).
    /// </summary>
    internal void RevealFormDraft()
    {
        if (!HasFormDraft)
        {
            return;
        }

        if (_keptDrafts.Count > 0 && (_formDraft is null || (!IsChanged(_formDraft) && !ReferenceEquals(_formDraft, _formDraftInFlight))))
        {
            SetDraft(null);
            PromoteKeptDraft(_keptDrafts[0].Key);
        }

        if (_formDraft is not { } draft)
        {
            return;
        }

        if (_isFilling)
        {
            FocusedField = draft.Key;
        }
        else
        {
            TryReenterFill(draft.Key);
        }

        NotifyRefused(draft.Reason ?? Strings.FormValueRefused);
    }

    /// <summary>Gets whether the editor has changes to save (form values and annotations written into the document).</summary>
    internal bool HasEditorChanges => _editor?.IsDirty == true;

    /// <summary>
    /// Gets whether the changes since the last save are form values only (<see cref="AnnotationEditor.HasOnlyFormChanges"/>): the
    /// warning about a signature that allows form fields says so.
    /// </summary>
    internal bool HasOnlyFormChanges => _editor?.HasOnlyFormChanges == true;

    /// <summary>
    /// Gets or sets the version of the history (<see cref="EditVersion"/>) for which the user chose "Save anyway" on the card
    /// about empty required fields, or <see langword="null"/>: a save of the same changes (again after a failed one) does not ask
    /// again; any later change does.
    /// </summary>
    internal int? RequiredFieldsAcceptedAt { get; set; }

    /// <summary>
    /// The first widget of the field <paramref name="fieldName"/> in the tab order (the pages with fields in turn), read for the
    /// open document; <see langword="null"/> when it is not found (or the document changed meanwhile).
    /// </summary>
    /// <param name="fieldName">The full name of the field.</param>
    /// <returns>The widget, or <see langword="null"/>.</returns>
    internal async Task<FormField?> FindFormFieldAsync(string fieldName)
    {
        ArgumentNullException.ThrowIfNull(fieldName);
        var generation = _documentGeneration;
        await EnsureFormInfoAsync();
        foreach (var page in PagesWithFields())
        {
            var fields = await ReadFieldsAsync(page);
            if (generation != _documentGeneration)
            {
                return null;
            }

            if (fields.FirstOrDefault(field => string.Equals(field.Key.FieldName, fieldName, StringComparison.Ordinal)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// "Go to the first empty field" of the card about required fields: starts the mode "Fill in form" when it is off and gives
    /// <paramref name="key"/> the focus (the view scrolls it into view and opens its input). An open value that cannot be applied
    /// keeps the focus in its own field instead, so it is never hidden.
    /// </summary>
    /// <param name="key">The widget, or <see langword="null"/> when it could not be found (then only the mode starts).</param>
    /// <returns>Whether the focus is in <paramref name="key"/> now.</returns>
    internal async Task<bool> GoToFormFieldAsync(FormWidgetKey? key)
    {
        if (!_isFilling)
        {
            await EnterFillAsync();
        }

        if (!_isFilling || key is not { } target)
        {
            return false;
        }

        if (_formDraft is { } open && IsChanged(open) && !ReferenceEquals(open, _formDraftInFlight) && !SameField(open, target))
        {
            FocusedField = open.Key;
            return false;
        }

        var generation = _documentGeneration;
        await ReadFieldsAsync(target.Page);
        if (generation != _documentGeneration || !_isFilling)
        {
            return false;
        }

        FocusedField = target;
        return true;
    }

    /// <summary>
    /// Shows a short notice about a field in the hint bar of the window (<see cref="FormNotice"/>): why a field that was clicked
    /// cannot be changed (<see cref="LockTextOf"/>: read-only, a signature field, a button whose action never runs).
    /// </summary>
    /// <param name="text">The localized text.</param>
    internal void ShowFormNotice(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        NotifyRefused(text);
    }

    /// <summary>Tells the session whether it highlights the fields (<see cref="ShowsFieldHighlight"/>), before the change is raised.</summary>
    private void ApplyFormHighlight()
    {
        var highlight = ShowsFieldHighlight;
        if (highlight == _sessionFormHighlight || _disposed)
        {
            return;
        }

        _sessionFormHighlight = highlight;
        _session.SetFormHighlight(highlight);
    }

    /// <summary>A value was refused that no draft shows: the tab keeps the reason as its notice, and the window is told.</summary>
    private void NotifyRefused(string reason)
    {
        FormNotice = reason;
        FormValueRefused?.Invoke(reason);
    }

    private static bool IsChanged(FormDraft draft) => !string.Equals(draft.Text, draft.OriginalText, StringComparison.Ordinal);

    private static bool SameField(FormDraft draft, FormWidgetKey key) => string.Equals(draft.Key.FieldName, key.FieldName, StringComparison.Ordinal);

    /// <summary>Keeps a text that cannot be applied as the open value, with the reason.</summary>
    private void KeepDraft(FormDraft? draft, FormField field, string text, string reason, bool waiting)
    {
        if (draft is null && _formDraft is { } other && other.Key != field.Key && IsChanged(other))
        {
            // Another field has an open value: it is not replaced (its text would be lost); this one is only refused.
            NotifyRefused(reason);
            return;
        }

        var kept = draft ?? (_formDraft is { } open && open.Key == field.Key ? open with { Text = text } : new FormDraft(field.Key, text, field.State.Text ?? string.Empty));
        SetDraft(kept with { Reason = reason, Waiting = waiting });
    }

    private void SetDraft(FormDraft? draft)
    {
        if (ReferenceEquals(_formDraft, draft))
        {
            return;
        }

        _formDraft = draft;
        OnPropertyChanged(nameof(CurrentDraft));
        OnPropertyChanged(nameof(HasFormDraft));
        SyncFromEditor();
    }

    private void DismissHints(FormHint hints)
    {
        var before = PendingHints;
        _dismissedHints |= hints;
        if (PendingHints != before)
        {
            OnPropertyChanged(nameof(PendingHints));
            _dismissHintCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The 0-based pages the view shows (or the row of the current page), within the document.</summary>
    private List<int> VisiblePagesForForms()
    {
        var pages = VisibleFormPages?.Invoke()
            ?? Enumerable.Range(CurrentPage - 1, Math.Max(1, Math.Max(CurrentPage, CurrentPageLast) - CurrentPage + 1));
        return pages.Where(page => page >= 0 && page < PageCount).Distinct().ToList();
    }

    /// <summary>The 0-based pages with widgets, ascending: from the read of the form, or every page when it could not be read.</summary>
    private IReadOnlyList<int> PagesWithFields() => CurrentFormInfo?.PagesWithFields ?? [.. Enumerable.Range(0, PageCount)];

    /// <summary>Reads the pages before and after <paramref name="page"/> that have fields, so Tab can answer at once.</summary>
    private void PrefetchAround(int page)
    {
        if (CurrentFormInfo is not { } info)
        {
            return;
        }

        var pages = info.PagesWithFields;
        var after = pages.FirstOrDefault(each => each > page, -1);
        if (after >= 0)
        {
            _ = FieldsOfPage(after);
        }

        var before = pages.LastOrDefault(each => each < page, -1);
        if (before >= 0)
        {
            _ = FieldsOfPage(before);
        }
    }

    /// <summary>The entry of a page for the open document; the entries of another document are dropped first.</summary>
    private FormPageFields PageEntry(int page)
    {
        if (!ReferenceEquals(_formFieldsDocument, _document))
        {
            _formPages.Clear();
            _formFieldsDocument = _document;
        }

        if (!_formPages.TryGetValue(page, out var entry))
        {
            entry = new FormPageFields();
            _formPages[page] = entry;
        }

        return entry;
    }

    /// <summary>Whether the fields of an entry were read at the current version of the history.</summary>
    private bool IsCurrent(FormPageFields entry) => entry.Version == EditVersion;

    private Task<IReadOnlyList<FormField>> StartFieldsRead(int page, FormPageFields entry)
    {
        if (entry.Read is { IsCompleted: false } running)
        {
            return running;
        }

        var read = ReadPageFieldsAsync(_document!, page, entry, EditVersion);
        if (!read.IsCompleted)
        {
            entry.Read = read;
        }

        return read;
    }

    /// <summary>Reads the fields of a page and keeps them, unless the tab moved on to another document meanwhile (UI thread).</summary>
    private async Task<IReadOnlyList<FormField>> ReadPageFieldsAsync(PdfDocument document, int page, FormPageFields entry, int version)
    {
        IReadOnlyList<FormField> fields;
        try
        {
            fields = await FormFieldReader(document, page, CancellationToken.None);
        }
        catch (Exception)
        {
            // A damaged page, or the document went: no fields to offer on it (asked again after the next change).
            fields = NoFields;
        }

        if (_disposed || !ReferenceEquals(document, _formFieldsDocument) || !_formPages.TryGetValue(page, out var now) || !ReferenceEquals(now, entry))
        {
            return NoFields;
        }

        entry.Fields = fields;
        entry.Version = version;
        entry.Read = null;
        FormFieldsChanged?.Invoke(page);
        return fields;
    }

    /// <summary>
    /// The widget of a field to check a value against: the one of <paramref name="preferred"/>, else the first one read, and
    /// whether its value is current (read at the current version of the history).
    /// </summary>
    private (FormField? Field, bool Current) FindField(string fieldName, FormWidgetKey? preferred)
    {
        if (!ReferenceEquals(_formFieldsDocument, _document))
        {
            return (null, false);
        }

        if (preferred is { } key && FindWidget(key) is { } exact)
        {
            return (exact, _formPages.TryGetValue(key.Page, out var own) && IsCurrent(own));
        }

        foreach (var (_, entry) in _formPages.OrderBy(pair => pair.Key))
        {
            foreach (var field in entry.Fields)
            {
                if (string.Equals(field.Key.FieldName, fieldName, StringComparison.Ordinal))
                {
                    return (field, IsCurrent(entry));
                }
            }
        }

        return (null, false);
    }

    /// <summary>The widget with this key as read for the open document (the view's editor and focus ring), or <see langword="null"/> while it is not read.</summary>
    /// <param name="key">The widget.</param>
    /// <returns>The widget, or <see langword="null"/>.</returns>
    internal FormField? FieldOf(FormWidgetKey key) => CanReadForm ? FindWidget(key) : null;

    /// <summary>The widget with this key as read for the open document, or <see langword="null"/>.</summary>
    private FormField? FindWidget(FormWidgetKey key)
    {
        if (!ReferenceEquals(_formFieldsDocument, _document) || !_formPages.TryGetValue(key.Page, out var entry))
        {
            return null;
        }

        foreach (var field in entry.Fields)
        {
            if (field.Key == key)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>
    /// The value a write gave a field, in the pages read: they stay current when this write was the only step since they were
    /// read; otherwise they are read again on the next request.
    /// </summary>
    private void RememberValue(string fieldName, FormFieldState after, int versionBefore)
    {
        var version = EditVersion;
        foreach (var (_, entry) in _formPages)
        {
            if (entry.Version != versionBefore || version != versionBefore + 1)
            {
                continue;
            }

            entry.Fields = [.. entry.Fields.Select(field =>
                string.Equals(field.Key.FieldName, fieldName, StringComparison.Ordinal) ? field with { State = after } : field)];
            entry.Version = version;
        }

        _formFieldsSeenVersion = version;
        FormFieldsChanged?.Invoke(-1);
    }

    /// <summary>The history of the editor moved on (UI thread, <see cref="SyncFromEditor"/>): the fields may have other values now.</summary>
    private void OnFormEditVersion()
    {
        var version = EditVersion;
        if (version == _formFieldsSeenVersion)
        {
            return;
        }

        _formFieldsSeenVersion = version;
        if (_formPages.Count > 0)
        {
            FormFieldsChanged?.Invoke(-1);
        }
    }

    /// <summary>Reads the form on the PDFium thread and keeps the result, unless the tab moved on meanwhile (UI thread).</summary>
    private async Task LoadFormInfoAsync(PdfDocument document, CancellationTokenSource cancellation)
    {
        FormInfo? info = null;
        try
        {
            info = await FormInfoReader(document, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Another document or a closed tab (ResetFormInfo): nothing to keep.
            return;
        }
        catch (Exception)
        {
            // A damaged form, or the document went: no hints, and Tab walks every page.
        }

        if (_disposed || !ReferenceEquals(cancellation, _formInfoCancellation))
        {
            return;
        }

        _formInfoCancellation = null;
        cancellation.Dispose();
        _formInfo = info;
        OnPropertyChanged(nameof(PendingHints));
        OnPropertyChanged(nameof(HasFormFields));
        OnPropertyChanged(nameof(OffersFill));
        _dismissHintCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Drops what was read of the form and cancels a read that is still running; its result will be ignored.</summary>
    private void ResetFormInfo()
    {
        // Cleared before cancelling: a continuation that runs inside Cancel sees that it is stale.
        var cancellation = _formInfoCancellation;
        _formInfoCancellation = null;
        _formInfoDocument = null;
        _formInfo = null;
        _formInfoLoad = Task.CompletedTask;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Another file is open in the tab, or none (from <see cref="ResetAnnotateMode"/>): the mode ends with the old document, and
    /// what belonged to it goes (its open value cannot be written into another file; the window asks before it replaces a
    /// document with changes).
    /// </summary>
    private void ResetFormState()
    {
        _formDraftInFlight = null;
        _keptDrafts.Clear();
        SetDraft(null);
        FocusedField = null;
        IsFilling = false;
        _formPages.Clear();
        _formFieldsDocument = null;
        ResetFormInfo();
        _dismissedHints = FormHint.None;
        _fillOfferClosed = false;
        RequiredFieldsAcceptedAt = null;
        FormNotice = null;
        OnPropertyChanged(nameof(PendingHints));
        OnPropertyChanged(nameof(HasFormFields));
        OnPropertyChanged(nameof(OffersFill));
    }

    /// <summary>
    /// The saved file replaced the document (the same content): the mode, the focused field, the open value and the dismissed
    /// hints stay; the fields are read again from the new document; what was read of the form stays with it.
    /// </summary>
    private void RebindFormState(PdfDocument old, PdfDocument adopted)
    {
        _formPages.Clear();
        _formFieldsDocument = null;
        if (ReferenceEquals(_formInfoDocument, old))
        {
            if (_formInfoCancellation is null)
            {
                _formInfoDocument = adopted;
            }
            else
            {
                // Still reading the closed document: read again from the new one.
                ResetFormInfo();
                _ = EnsureFormInfoAsync();
            }
        }

        FormFieldsChanged?.Invoke(-1);
    }

    /// <summary>
    /// The availability of the form may have changed (state, document, first sharp page): the commands are told, and the read of
    /// the hints starts after the first sharp page of a document with a form.
    /// </summary>
    private void OnFormAvailabilityChanged()
    {
        OnPropertyChanged(nameof(HasForm));
        OnPropertyChanged(nameof(OffersFill));
        RaiseFormCanExecuteChanged();
        if (CanReadForm)
        {
            _ = EnsureFormInfoAsync();
        }
    }

    private void RaiseFormCanExecuteChanged()
    {
        _toggleFillCommand?.RaiseCanExecuteChanged();
        _endFillCommand?.RaiseCanExecuteChanged();
        _nextFieldCommand?.RaiseCanExecuteChanged();
        _previousFieldCommand?.RaiseCanExecuteChanged();
        _toggleHighlightCommand?.RaiseCanExecuteChanged();
        _dismissHintCommand?.RaiseCanExecuteChanged();
    }

    /// <summary>Stops a read of the form that runs (the tab is disposed).</summary>
    private void DisposeForms() => ResetFormInfo();

    private static string Format(string format, string argument) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), argument);

    /// <summary>The fields of one page as read, the version of the history they were read at (-1: not yet), and a read that runs.</summary>
    private sealed class FormPageFields
    {
        public IReadOnlyList<FormField> Fields { get; set; } = NoFields;

        public int Version { get; set; } = -1;

        public Task<IReadOnlyList<FormField>>? Read { get; set; }
    }
}

/// <summary>The open value of a text field (see <see cref="TabViewModel.CurrentDraft"/>).</summary>
/// <param name="Key">The widget the text was typed into.</param>
/// <param name="Text">The typed text, exactly as typed.</param>
/// <param name="OriginalText">The value the field has in the document.</param>
public sealed record FormDraft(FormWidgetKey Key, string Text, string OriginalText)
{
    /// <summary>Gets why the text could not be applied (shown with the field), or <see langword="null"/>.</summary>
    public string? Reason { get; init; }

    /// <summary>Gets whether the text waits because the tab took no edit at the moment; the next flush applies it.</summary>
    public bool Waiting { get; init; }
}
