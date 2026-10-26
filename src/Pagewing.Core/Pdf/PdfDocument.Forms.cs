using Pagewing.Core.Forms;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;

namespace Pagewing.Core.Pdf;

/// <summary>
/// The form fields of the document (AcroForm; milestone 11): reading what the form-fill environment of PDFium reports and
/// mapping it to the UI-free model of <c>Pagewing.Core.Forms</c>. Every job runs on the PDFium thread; a document without a
/// form never gets a job from here. See <c>Docs/DECISIONS.md</c>, entries "Formulare … (M11)".
/// </summary>
/// <remarks>
/// Every page is loaded for each job and closed again before the job ends. The form-fill environment maps a field to the view
/// of the page it loaded last, so no job here may run while another handle of the same page is held (a view that has drawn
/// before a write must not draw after it).
/// </remarks>
public sealed partial class PdfDocument
{
    /// <summary>All the flags <see cref="FormFieldTraits"/> names; PDFium's other bits (radio, push button) are not traits.</summary>
    private const uint KnownTraits =
        (uint)(FormFieldTraits.ReadOnly | FormFieldTraits.Required | FormFieldTraits.NoExport | FormFieldTraits.Multiline
            | FormFieldTraits.Password | FormFieldTraits.NoToggleToOff | FormFieldTraits.Combo | FormFieldTraits.Edit
            | FormFieldTraits.FileSelect | FormFieldTraits.MultiSelect | FormFieldTraits.DoNotSpellCheck
            | FormFieldTraits.DoNotScroll | FormFieldTraits.Comb | FormFieldTraits.RichText);

    private static readonly IReadOnlyList<FormField> NoFields = [];

    /// <summary>What the first complete scan found (the pages with fields feed the search for all widgets of a field).</summary>
    private FormInfo? formInfo;

    /// <summary>
    /// Gets whether the document has an AcroForm (<c>FPDF_GetFormType</c> was not <c>NONE</c> when it was opened; set by
    /// the open job, no extra job). An XFA form counts when it has AcroForm fields; Pagewing shows the fields and hints at
    /// the XFA part (<see cref="FormHint.Xfa"/>).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public bool HasAcroForm
    {
        get
        {
            ThrowIfDisposed();
            return formType != FpdfConstants.FormTypeNone;
        }
    }

    /// <summary>
    /// Gets the zero-based pages with at least one widget, ascending, as the first complete <see cref="GetFormInfoAsync"/>
    /// found them, or <see langword="null"/> before (and for a document that never finished a scan). Any thread.
    /// </summary>
    internal IReadOnlyList<int>? CachedPagesWithFields => Volatile.Read(ref formInfo)?.PagesWithFields;

    /// <summary>
    /// Reads what the form of the document contains, page by page in short jobs (one per page,
    /// <see cref="PdfPriority.Background"/>), and keeps the result for the next call. A document without a form gives
    /// <c>FormInfo(false, None, 0, [])</c> at once, without a job. The hint <see cref="FormHint.Xfa"/> comes from the form
    /// type, the others from the scripts and kinds of the fields.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the scan ended; nothing is kept.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public async Task<FormInfo> GetFormInfoAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (formType == FpdfConstants.FormTypeNone)
        {
            return new FormInfo(false, FormHint.None, 0, []);
        }

        if (Volatile.Read(ref formInfo) is { } known)
        {
            return known;
        }

        var xfa = formType is FpdfConstants.FormTypeXfaFull or FpdfConstants.FormTypeXfaForeground;
        var pages = new PdfiumFormPageInfo[pageCount];
        for (var pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = pageIndex;
            pages[index] = await document.Thread.InvokeAsync(
                () =>
                {
                    try
                    {
                        // ReadPageInfo needs the page handle only for this call; no other handle of the page is open here.
                        using var page = document.LoadPage(index);
                        return PdfiumForms.ReadPageInfo(page);
                    }
                    catch (InvalidDataException)
                    {
                        // A page that cannot be loaded has no fields to read; the rest of the scan goes on.
                        return new PdfiumFormPageInfo(0, false, false, false, false);
                    }
                },
                PdfiumPriority.Background,
                cancellationToken).ConfigureAwait(false);
        }

        var combined = PdfiumFormDocumentInfo.Combine(xfa, pages);
        var hints = FormHint.None;
        hints |= combined.Calculated ? FormHint.Calculated : FormHint.None;
        hints |= combined.FormatScripts ? FormHint.FormatScripts : FormHint.None;
        hints |= combined.Xfa ? FormHint.Xfa : FormHint.None;
        hints |= combined.SignatureField ? FormHint.SignatureField : FormHint.None;
        hints |= combined.Buttons ? FormHint.Buttons : FormHint.None;
        var info = new FormInfo(combined.HasFields, hints, combined.FieldCount, combined.PagesWithFields);
        Volatile.Write(ref formInfo, info);
        return info;
    }

    /// <summary>
    /// Reads the widgets of a page in the order of the page's <c>/Annots</c>. Every widget with a <c>/Rect</c> is returned:
    /// one Pagewing cannot write (a signature, a push button, a file selection, a field without a name, or one beyond the
    /// limits against hostile forms) comes with <see cref="FormLock.Unsupported"/> and a reason, a field with the read-only flag
    /// with <see cref="FormLock.ReadOnly"/>; a field with a calculation, format or validation script is not locked. The
    /// <see cref="FormField.State"/> is that of the field, the same for all of its widgets. A document without a form gives an
    /// empty list without a job. Runs with <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the fields were read.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<IReadOnlyList<FormField>> GetFormFieldsAsync(int pageIndex, CancellationToken cancellationToken = default) =>
        ReadFormFieldsAsync(pageIndex, PdfiumPriority.Interactive, cancellationToken);

    /// <summary>
    /// Lists the names of the required fields that have no value (<see cref="FormInfo.EmptyRequired"/>), reading only the
    /// pages with widgets, one short <see cref="PdfPriority.Background"/> job per page. The pages come from
    /// <see cref="GetFormInfoAsync"/> (scanned first when not known yet). A document without a form gives an empty list.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the scan stops after the page in progress.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public async Task<IReadOnlyList<string>> GetEmptyRequiredFieldsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (formType == FpdfConstants.FormTypeNone)
        {
            return [];
        }

        var pages = CachedPagesWithFields ?? (await GetFormInfoAsync(cancellationToken).ConfigureAwait(false)).PagesWithFields;
        var all = new List<FormField>();
        foreach (var pageIndex in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            all.AddRange(await ReadFormFieldsAsync(pageIndex, PdfiumPriority.Background, cancellationToken).ConfigureAwait(false));
        }

        return FormInfo.EmptyRequired(all);
    }

    /// <summary>
    /// Writes the value of a form field through PDFium's form engine, which writes <c>/V</c> and the appearance of every widget,
    /// in one short <see cref="PdfPriority.Interactive"/> job: the field is found by its full name on every page with widgets
    /// (never by an annotation index; the pages come from <see cref="GetFormInfoAsync"/>, scanned first when not known yet), its
    /// state before is read, the value is written (a text: focus, select all, replace, leave; a check box or radio button: a
    /// click on the widget; a combo box or list box: its options), read back and compared with what was asked for. A value the
    /// engine did not take as it was (shortened at an inherited <c>/MaxLen</c>, changed, or drawn with a font other than the
    /// field's own) is written back to the state before in the same job and refused. A state equal to the one before changes
    /// nothing (not even the focus) and gives <see cref="FormChange.Before"/> equal to <see cref="FormChange.After"/>. Nothing on
    /// a session or in a history is touched; the caller redraws <see cref="FormChange.Pages"/>.
    /// </summary>
    /// <param name="fieldName">The fully qualified field name.</param>
    /// <param name="state">
    /// The value: <see cref="FormFieldState.Text"/> for a text field (<see langword="null"/> is empty; line breaks of a multiline
    /// field are written as CR LF) and for the typed text of an editable combo box; <see cref="FormFieldState.Checked"/> for a
    /// check box; <see cref="FormFieldState.Text"/> as the export value of the button to choose for a radio group;
    /// <see cref="FormFieldState.SelectedOptions"/> for a list box and for the option of a combo box (exactly one).
    /// </param>
    /// <param name="cancellationToken">Cancels before the job starts; a job that started finishes.</param>
    /// <returns>The state before, the state read back after the write, and every page with a widget of the field.</returns>
    /// <exception cref="ArgumentException"><paramref name="fieldName"/> is empty; the text is refused by <see cref="FormValueRules.CheckText"/> (the message is the reason); the state does not fit the kind of field (no export value of the group, several options of a single-choice field).</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option index is not an option of the field.</exception>
    /// <exception cref="FormFieldNotFoundException">No page has a widget of a field with that name.</exception>
    /// <exception cref="InvalidOperationException">The field is locked (<see cref="FormField.Lock"/>, the reason in the message); a radio group is asked for no choice (the engine never undoes a choice), or a combo box without typed text for no option; the engine did not take the value (the field is as before).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<FormChange> SetFormValueAsync(string fieldName, FormFieldState state, CancellationToken cancellationToken = default) =>
        SetFormValueAsync(fieldName, state, checkCharacters: true, cancellationToken);

    /// <summary>
    /// Like the public overload; <paramref name="checkCharacters"/> <see langword="false"/> skips <see cref="FormValueRules"/>
    /// so tests can reach the read-back with a text the rules would refuse.
    /// </summary>
    internal async Task<FormChange> SetFormValueAsync(string fieldName, FormFieldState state, bool checkCharacters, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentNullException.ThrowIfNull(state);
        if (formType == FpdfConstants.FormTypeNone)
        {
            throw NotFound(fieldName);
        }

        var pages = CachedPagesWithFields ?? (await GetFormInfoAsync(cancellationToken).ConfigureAwait(false)).PagesWithFields;
        return await document.Thread.InvokeAsync(
            () => WriteFormValue(fieldName, state, checkCharacters, pages),
            PdfiumPriority.Interactive,
            cancellationToken).ConfigureAwait(false);
    }

    private static FormFieldNotFoundException NotFound(string fieldName) => new($"The form has no field named '{fieldName}'.");

    /// <summary>
    /// The job of <see cref="SetFormValueAsync(string, FormFieldState, bool, CancellationToken)"/>. Every page with a widget of
    /// the field stays loaded until the job ends, so the engine renews the appearance of all of them (it reaches only widgets of
    /// loaded pages: no <c>FFI_GetPage</c> callback); the other pages are closed right after reading.
    /// </summary>
    private FormChange WriteFormValue(string fieldName, FormFieldState state, bool checkCharacters, IReadOnlyList<int> pages)
    {
        var widgets = new List<FormWidget>();
        var loaded = new List<PdfiumPage>();
        try
        {
            foreach (var pageIndex in pages)
            {
                PdfiumPage page;
                try
                {
                    page = document.LoadPage(pageIndex);
                }
                catch (InvalidDataException)
                {
                    // A damaged page has no widget to write; the scan skipped it as well.
                    continue;
                }

                var read = PdfiumForms.ReadPage(page);
                var fields = ToFormFields(pageIndex, read);
                var before = widgets.Count;
                for (var i = 0; i < read.Length; i++)
                {
                    if (string.Equals(read[i].Name, fieldName, StringComparison.Ordinal))
                    {
                        widgets.Add(new FormWidget(page, read[i], fields[i]));
                    }
                }

                if (widgets.Count > before)
                {
                    loaded.Add(page);
                }
                else
                {
                    page.Dispose();
                }
            }

            if (widgets.Count == 0)
            {
                throw NotFound(fieldName);
            }

            return Write(fieldName, widgets, state, checkCharacters);
        }
        finally
        {
            for (var i = loaded.Count - 1; i >= 0; i--)
            {
                loaded[i].Dispose();
            }
        }
    }

    private static FormChange Write(string fieldName, List<FormWidget> widgets, FormFieldState state, bool checkCharacters)
    {
        var field = widgets[0].Field;
        if (widgets.FirstOrDefault(w => w.Field.Lock != FormLock.None) is { } locked)
        {
            throw new InvalidOperationException($"The field '{fieldName}' cannot be changed: {locked.Field.LockReason}");
        }

        var before = field.State;
        var pages = new SortedSet<int>(widgets.Select(w => w.Field.Key.Page));
        var write = Plan(field, widgets, state, checkCharacters);
        if (write.Matches(before))
        {
            return new FormChange(before, before, pages);
        }

        write.Refuse?.Invoke();
        write.Apply();
        var after = write.ReadBack();
        var deviation = write.Verify(after);
        if (deviation is null)
        {
            return new FormChange(before, after, pages);
        }

        var restored = Restore(field, widgets, before);
        throw new InvalidOperationException(
            $"The value was not accepted by the form engine: {deviation}" + (restored ? string.Empty : " The value before could not be restored."));
    }

    /// <summary>How one value is written into one field, read back and checked; validated against the field before anything is written.</summary>
    private static FormWrite Plan(FormField field, List<FormWidget> widgets, FormFieldState state, bool checkCharacters)
    {
        var first = widgets[0];
        switch (field.Kind)
        {
            case FormFieldKind.Text:
                return PlanText(field, first, state.Text ?? string.Empty, checkCharacters);

            case FormFieldKind.CheckBox:
                var wanted = state.Checked ?? throw new ArgumentException("A check box takes a checked state.", nameof(state));
                return new FormWrite(
                    before => before.Checked == wanted,
                    () => PdfiumForms.Click(first.Page, first.Widget.Index),
                    () => first.ReadState(),
                    after => after.Checked == wanted ? null : "the check box did not change.");

            case FormFieldKind.RadioButton:
                var export = state.Text
                    ?? throw new InvalidOperationException("A radio group cannot be set to no choice: the form engine never undoes a choice.");
                var button = widgets.FirstOrDefault(w => string.Equals(w.Widget.ExportValue, export, StringComparison.Ordinal))
                    ?? throw new ArgumentException($"No button of the group '{field.Key.FieldName}' has the export value '{export}'.", nameof(state));
                return new FormWrite(
                    before => string.Equals(before.Text, export, StringComparison.Ordinal),
                    () => PdfiumForms.Click(button.Page, button.Widget.Index),
                    () => button.ReadState(),
                    after => string.Equals(after.Text, export, StringComparison.Ordinal) && button.IsChecked() ? null : "the button was not chosen.");

            case FormFieldKind.ComboBox:
                if (state.SelectedOptions.Count == 0 && field.Traits.HasFlag(FormFieldTraits.Edit))
                {
                    return PlanText(field, first, state.Text ?? string.Empty, checkCharacters);
                }

                if (state.SelectedOptions.Count > 1)
                {
                    throw new ArgumentException("A combo box takes one option.", nameof(state));
                }

                return PlanOptions(field, first, state.SelectedOptions, refuseNone: true);

            case FormFieldKind.ListBox:
                if (state.SelectedOptions.Distinct().Count() > 1 && !field.Traits.HasFlag(FormFieldTraits.MultiSelect))
                {
                    throw new ArgumentException("The list takes one option only.", nameof(state));
                }

                return PlanOptions(field, first, state.SelectedOptions, refuseNone: false);

            default:
                // Locked as Unsupported when read; kept for a kind added later.
                throw new InvalidOperationException($"The field '{field.Key.FieldName}' cannot be changed.");
        }
    }

    private static FormWrite PlanText(FormField field, FormWidget widget, string text, bool checkCharacters)
    {
        if (checkCharacters && FormValueRules.CheckText(field, text) is { Accepted: false } check)
        {
            throw new ArgumentException(check.Reason);
        }

        var multiline = field.Traits.HasFlag(FormFieldTraits.Multiline);
        var wanted = multiline ? NormalizeBreaks(text) : text;
        bool Same(string? value) => string.Equals(multiline ? NormalizeBreaks(value ?? string.Empty) : value ?? string.Empty, wanted, StringComparison.Ordinal);
        return new FormWrite(
            before => Same(before.Text),
            () => PdfiumForms.ReplaceText(widget.Page, widget.Widget.Index, wanted),
            () => widget.ReadState(),
            after =>
            {
                if (!Same(after.Text))
                {
                    return "the field holds a different text (shortened or changed).";
                }

                // /V keeps characters the appearance lacks (spike point 8): only the field's own font may draw the text.
                var drawn = PdfiumForms.AppearanceFonts(widget.Page, widget.Widget.Index);
                var own = PdfiumForms.DefaultAppearanceFonts(widget.Page, widget.Widget.Index);
                if (drawn is null)
                {
                    return "the appearance of the field could not be checked.";
                }

                var foreign = own is null ? drawn.Skip(1) : drawn.Except(own, StringComparer.Ordinal);
                return foreign.Any() ? "the text needs a font the field does not have." : null;
            });
    }

    private static FormWrite PlanOptions(FormField field, FormWidget widget, IReadOnlyList<int> selected, bool refuseNone)
    {
        var wanted = selected.Distinct().Order().ToArray();
        foreach (var option in wanted)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(option, nameof(selected));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(option, field.Options.Count, nameof(selected));
        }

        bool Same(FormFieldState state) => state.SelectedOptions.SequenceEqual(wanted);
        return new FormWrite(
            Same,
            () => PdfiumForms.SelectOptions(widget.Page, widget.Widget.Index, wanted),
            () => widget.ReadState(),
            after => Same(after) ? null : "the options were not chosen.")
        {
            Refuse = refuseNone && wanted.Length == 0
                ? () => throw new InvalidOperationException("A combo box cannot be set to no option: the form engine cannot deselect it.")
                : null,
        };
    }

    /// <summary>Writes the state before back after a refused write; whether the field reads as before afterwards.</summary>
    private static bool Restore(FormField field, List<FormWidget> widgets, FormFieldState before)
    {
        try
        {
            var first = widgets[0];
            switch (field.Kind)
            {
                case FormFieldKind.Text:
                case FormFieldKind.ComboBox when before.SelectedOptions.Count == 0 && field.Traits.HasFlag(FormFieldTraits.Edit):
                    PdfiumForms.ReplaceText(first.Page, first.Widget.Index, before.Text ?? string.Empty);
                    break;
                case FormFieldKind.CheckBox when first.ReadState().Checked != before.Checked:
                    PdfiumForms.Click(first.Page, first.Widget.Index);
                    break;
                case FormFieldKind.RadioButton when widgets.FirstOrDefault(w => string.Equals(w.Widget.ExportValue, before.Text, StringComparison.Ordinal)) is { } button:
                    PdfiumForms.Click(button.Page, button.Widget.Index);
                    break;
                case FormFieldKind.ComboBox:
                case FormFieldKind.ListBox:
                    PdfiumForms.SelectOptions(first.Page, first.Widget.Index, before.SelectedOptions);
                    break;
            }

            return first.ReadState().Equals(before);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Line breaks as the engine writes them in a multiline field: CR LF.</summary>
    private static string NormalizeBreaks(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

    /// <summary>A widget of the field being written, on a page loaded for the whole job.</summary>
    private sealed record FormWidget(PdfiumPage Page, PdfiumFormField Widget, FormField Field)
    {
        /// <summary>The state of the field read again through this widget.</summary>
        public FormFieldState ReadState() =>
            PdfiumForms.ReadWidget(Page, Widget.Index) is { } read
                ? StateOf(Field.Kind, read)
                : throw new InvalidOperationException("The field could not be read back.");

        /// <summary>Whether this widget (a check box or radio button) is checked now.</summary>
        public bool IsChecked() => PdfiumForms.ReadWidget(Page, Widget.Index)?.Checked == true;
    }

    /// <summary>The steps of one write; <see cref="Verify"/> gives the deviation or <see langword="null"/>.</summary>
    private sealed record FormWrite(
        Func<FormFieldState, bool> Matches,
        Action Apply,
        Func<FormFieldState> ReadBack,
        Func<FormFieldState, string?> Verify)
    {
        /// <summary>Throws when the state cannot be made (checked after <see cref="Matches"/>, so an equal state stays a no-op).</summary>
        public Action? Refuse { get; init; }
    }

    private Task<IReadOnlyList<FormField>> ReadFormFieldsAsync(int pageIndex, PdfiumPriority priority, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        if (formType == FpdfConstants.FormTypeNone)
        {
            return Task.FromResult(NoFields);
        }

        return document.Thread.InvokeAsync<IReadOnlyList<FormField>>(
            () =>
            {
                using var page = document.LoadPage(pageIndex);
                return ToFormFields(pageIndex, PdfiumForms.ReadPage(page));
            },
            priority,
            cancellationToken);
    }

    /// <summary>Maps the widgets PDFium read to the model, in the order they come (the order of <c>/Annots</c>).</summary>
    private static FormField[] ToFormFields(int pageIndex, PdfiumFormField[] widgets)
    {
        var result = new FormField[widgets.Length];
        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < widgets.Length; i++)
        {
            var name = widgets[i].Name ?? string.Empty;
            numbers.TryGetValue(name, out var number);
            numbers[name] = number + 1;
            result[i] = ToFormField(pageIndex, name, number, widgets[i]);
        }

        return result;
    }

    private static FormField ToFormField(int pageIndex, string name, int widgetNumber, PdfiumFormField widget)
    {
        var kind = widget.Kind switch
        {
            PdfiumFormKind.Text => FormFieldKind.Text,
            PdfiumFormKind.CheckBox => FormFieldKind.CheckBox,
            PdfiumFormKind.RadioButton => FormFieldKind.RadioButton,
            PdfiumFormKind.ComboBox => FormFieldKind.ComboBox,
            PdfiumFormKind.ListBox => FormFieldKind.ListBox,
            PdfiumFormKind.PushButton => FormFieldKind.PushButton,
            PdfiumFormKind.Signature => FormFieldKind.Signature,
            _ => FormFieldKind.Unknown,
        };
        var traits = (FormFieldTraits)(widget.Flags & KnownTraits);
        var (fieldLock, reason) = LockOf(kind, traits, name, widget.SkipReason);
        var options = new FormOption[widget.OptionLabels.Length];
        for (var i = 0; i < options.Length; i++)
        {
            options[i] = new FormOption(widget.OptionLabels[i]);
        }

        var (left, top, right, bottom) = widget.Rect;
        return new FormField(
            new FormWidgetKey(name, pageIndex, widgetNumber),
            widget.Index,
            kind,
            traits,
            new PageRect(left, top, right, bottom),
            widget.AlternateName,
            widget.ExportValue,
            widget.MaxLength,
            options,
            StateOf(kind, widget),
            fieldLock,
            reason);
    }

    /// <summary>
    /// What Pagewing allows for a field: unsupported kinds (signature, push button, file selection, unknown), a field
    /// without a name and a widget PDFium or the limits ruled out win over the read-only flag, because Pagewing cannot write
    /// them in any case. A field with scripts is not locked.
    /// </summary>
    private static (FormLock Lock, string? Reason) LockOf(FormFieldKind kind, FormFieldTraits traits, string name, string? skipReason)
    {
        if (skipReason is not null)
        {
            return (FormLock.Unsupported, skipReason);
        }

        if (name.Length == 0)
        {
            return (FormLock.Unsupported, "The field has no name.");
        }

        if (kind == FormFieldKind.Signature)
        {
            return (FormLock.Unsupported, "Pagewing does not support digital signatures.");
        }

        if (kind == FormFieldKind.PushButton)
        {
            return (FormLock.Unsupported, "Pagewing does not run the actions of buttons.");
        }

        if (kind == FormFieldKind.Unknown)
        {
            return (FormLock.Unsupported, "The kind of the field is not supported.");
        }

        if (kind == FormFieldKind.Text && traits.HasFlag(FormFieldTraits.FileSelect))
        {
            return (FormLock.Unsupported, "File selection fields are not supported.");
        }

        return traits.HasFlag(FormFieldTraits.ReadOnly) ? (FormLock.ReadOnly, "The field is read-only.") : (FormLock.None, null);
    }

    /// <summary>
    /// The value of the field a widget belongs to. PDFium reports the value of the field for every widget of a group, so all
    /// widgets of a radio group give the same state: the export value of the chosen button, or no choice (also for
    /// <c>Off</c>).
    /// </summary>
    private static FormFieldState StateOf(FormFieldKind kind, PdfiumFormField widget) => kind switch
    {
        FormFieldKind.CheckBox => FormFieldState.OfChecked(widget.Checked),
        FormFieldKind.RadioButton => FormFieldState.OfText(
            string.IsNullOrEmpty(widget.Value) || string.Equals(widget.Value, "Off", StringComparison.Ordinal) ? null : widget.Value),
        FormFieldKind.ComboBox => new FormFieldState
        {
            Text = string.IsNullOrEmpty(widget.Value) ? null : widget.Value,
            SelectedOptions = widget.Selected,
        },
        FormFieldKind.ListBox => FormFieldState.OfSelection(widget.Selected),
        FormFieldKind.Text => FormFieldState.OfText(widget.Value ?? string.Empty),
        _ => FormFieldState.OfText(null),
    };
}
