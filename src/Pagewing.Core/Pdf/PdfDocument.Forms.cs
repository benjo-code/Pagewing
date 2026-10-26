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
