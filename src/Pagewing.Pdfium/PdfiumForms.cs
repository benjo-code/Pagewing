using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>The type of a form field as PDFium reports it; the values are <c>FPDF_FORMFIELD_*</c> (<c>fpdf_formfill.h</c>).</summary>
internal enum PdfiumFormKind
{
    /// <summary>No type PDFium knows, or the widget is no field for PDFium (type -1).</summary>
    Unknown = FpdfConstants.FormFieldUnknown,

    /// <summary>A push button.</summary>
    PushButton = FpdfConstants.FormFieldPushButton,

    /// <summary>A check box.</summary>
    CheckBox = FpdfConstants.FormFieldCheckBox,

    /// <summary>A radio button.</summary>
    RadioButton = FpdfConstants.FormFieldRadioButton,

    /// <summary>A combo box.</summary>
    ComboBox = FpdfConstants.FormFieldComboBox,

    /// <summary>A list box.</summary>
    ListBox = FpdfConstants.FormFieldListBox,

    /// <summary>A text field.</summary>
    Text = FpdfConstants.FormFieldTextField,

    /// <summary>A signature field.</summary>
    Signature = FpdfConstants.FormFieldSignature,
}

/// <summary>
/// One widget of a form field as PDFium reports it (Pagewing.Pdfium cannot reference the Core model; the mapping to
/// <c>FormField</c> lives in Core). A widget Pagewing cannot handle comes with a <see cref="SkipReason"/> and is never
/// changed; what was read before the reason stays.
/// </summary>
/// <param name="Index">The index in the page's <c>/Annots</c>.</param>
/// <param name="Kind">The field type.</param>
/// <param name="Flags">The field flags (<c>/Ff</c>, inherited), bit values as in the PDF specification.</param>
/// <param name="Rect">The <c>/Rect</c> as a box on the displayed page, in points from the top left: the same conversion as
/// <see cref="PdfiumAnnotation.Bounds"/>, so rotation and CropBox are applied by <c>FPDF_PageToDevice</c>.</param>
/// <param name="Name">The fully qualified field name, or <see langword="null"/> when it is missing or not usable.</param>
/// <param name="AlternateName">The alternate name (<c>/TU</c>), or <see langword="null"/>.</param>
/// <param name="Value">The field value (<c>/V</c>; a combo box gives the export value of its option, a check box or radio
/// group the state name); <see langword="null"/> for a list box (PDFium gives only the first entry of its array; the
/// selection is <paramref name="Selected"/>) and when it cannot be read.</param>
/// <param name="ExportValue">The export value of a check box or radio button widget, else <see langword="null"/>.</param>
/// <param name="MaxLength">The direct <c>/MaxLen</c> of the widget, or <see langword="null"/> (an inherited one cannot be read).</param>
/// <param name="OptionLabels">The option labels of a combo box or list box (the header gives no export values of options).</param>
/// <param name="Selected">The indices of the chosen options, read only through <c>FPDFAnnot_IsOptionSelected</c>.</param>
/// <param name="Checked">Whether a check box or radio button widget is checked.</param>
/// <param name="SkipReason">Why Pagewing does not handle the widget (English, for logs and tests), or <see langword="null"/>.</param>
internal sealed record PdfiumFormField(
    int Index,
    PdfiumFormKind Kind,
    uint Flags,
    (double Left, double Top, double Right, double Bottom) Rect,
    string? Name,
    string? AlternateName,
    string? Value,
    string? ExportValue,
    int? MaxLength,
    string[] OptionLabels,
    int[] Selected,
    bool Checked,
    string? SkipReason);

/// <summary>What one page contributes to <see cref="PdfiumFormDocumentInfo"/> (<see cref="PdfiumForms.ReadPageInfo"/>).</summary>
/// <param name="Widgets">The widgets with a usable <c>/Rect</c> (those <see cref="PdfiumForms.ReadPage"/> returns).</param>
/// <param name="Calculated">Whether a field has a calculation script (<c>/AA /C</c>).</param>
/// <param name="FormatScripts">Whether a field has a key stroke, format or validation script (<c>/AA /K</c>, <c>/F</c>, <c>/V</c>).</param>
/// <param name="SignatureField">Whether a field is a signature field.</param>
/// <param name="Buttons">Whether a field is a push button.</param>
internal sealed record PdfiumFormPageInfo(int Widgets, bool Calculated, bool FormatScripts, bool SignatureField, bool Buttons);

/// <summary>What a document's form contains, for the hints of milestone 11.</summary>
/// <param name="HasFields">Whether any page has a widget.</param>
/// <param name="Calculated">Whether a field has a calculation script (it never runs: no JavaScript engine).</param>
/// <param name="FormatScripts">Whether a field has a key stroke, format or validation script (none ever runs).</param>
/// <param name="Xfa">Whether the form declares XFA (<c>FORMTYPE_XFA_FULL</c> or <c>FORMTYPE_XFA_FOREGROUND</c>).</param>
/// <param name="SignatureField">Whether a field is a signature field.</param>
/// <param name="Buttons">Whether a field is a push button.</param>
/// <param name="FieldCount">The widgets counted on all pages.</param>
/// <param name="PagesWithFields">The zero-based pages with at least one widget, ascending.</param>
internal sealed record PdfiumFormDocumentInfo(
    bool HasFields,
    bool Calculated,
    bool FormatScripts,
    bool Xfa,
    bool SignatureField,
    bool Buttons,
    int FieldCount,
    int[] PagesWithFields)
{
    /// <summary>Combines the information of the pages; <paramref name="pages"/> holds one entry per page, in page order.</summary>
    public static PdfiumFormDocumentInfo Combine(bool xfa, IReadOnlyList<PdfiumFormPageInfo> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        long count = 0;
        bool calculated = false, formatScripts = false, signature = false, buttons = false;
        var withFields = new List<int>();
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index];
            count += page.Widgets;
            calculated |= page.Calculated;
            formatScripts |= page.FormatScripts;
            signature |= page.SignatureField;
            buttons |= page.Buttons;
            if (page.Widgets > 0)
            {
                withFields.Add(index);
            }
        }

        return new PdfiumFormDocumentInfo(count > 0, calculated, formatScripts, xfa, signature, buttons, (int)Math.Min(count, int.MaxValue), [.. withFields]);
    }
}

/// <summary>
/// Reads the form fields of a loaded page through PDFium's form handle (<see cref="PdfiumFormEnvironment.Handle"/>). Every
/// method must be called on the PDFium thread and throws <see cref="PdfiumThreadViolationException"/> otherwise; every
/// annotation handle is closed before a method returns. Reading never focuses a field and never runs a script.
/// </summary>
/// <remarks>
/// <para>
/// Limits against hostile forms (Docs/DECISIONS.md, "Formulare: Formular-Engine …", point 12): at most
/// <see cref="PdfiumAnnotations.MaxScannedPerPage"/> entries of <c>/Annots</c> are looked at and at most
/// <see cref="MaxFieldsPerPage"/> widgets read; a name longer than <see cref="MaxNameLength"/> or with a NUL, a field
/// without a name, a widget that is no field for PDFium (type -1, for example under cyclic <c>/Kids</c>) or of an unknown
/// type, a value longer than <see cref="MaxValueLength"/> and a field with more than <see cref="MaxOptionsPerField"/>
/// options (or longer option labels together than <see cref="MaxValueLength"/>) get a <see cref="PdfiumFormField.SkipReason"/>;
/// a widget without a usable <c>/Rect</c> is left out. Long strings are measured before they are copied.
/// </para>
/// <para>
/// No reference is resolved here (no <c>FPDFAnnot_GetLinkedAnnot</c>, no <c>FPDFPage_GetAnnotIndex</c>): PDFium finds the
/// field of a widget in the map it builds once, at the first <c>FORM_OnAfterLoadPage</c> of the document, so the work per
/// widget does not grow with the page, and <see cref="PdfiumAnnotations.MaxLinkWork"/> is not needed.
/// </para>
/// </remarks>
internal static unsafe class PdfiumForms
{
    /// <summary>The most widgets read on one page; further ones are ignored.</summary>
    public const int MaxFieldsPerPage = 5_000;

    /// <summary>
    /// The most options of a combo box or list box; a field with more is not supported. Focusing a combo grows faster than
    /// linearly with its options (1,000: 23 ms; 20,000: 7.1 s; spike point 12).
    /// </summary>
    public const int MaxOptionsPerField = 1_000;

    /// <summary>The longest field name (and alternate name) read, in UTF-16 code units.</summary>
    public const int MaxNameLength = 1_024;

    /// <summary>
    /// The longest value read, in UTF-16 code units; also the most characters of all option labels of one field together.
    /// Must equal <c>Pagewing.Core.Forms.FormValueRules.MaxValueLength</c> (checked by <c>PdfiumFormsReadTests</c>).
    /// </summary>
    public const int MaxValueLength = 100_000;

    /// <summary>
    /// Reads the widgets of a page in <c>/Annots</c> order. Empty for a page of a document without a form environment.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static PdfiumFormField[] ReadPage(PdfiumPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        if (page.Handle.FormEnvironment is not { } environment)
        {
            return [];
        }

        var form = environment.Handle;
        var transform = PdfiumPageText.TransformOf(page);
        var result = new List<PdfiumFormField>();
        ForEachWidget(native, (index, annotation) =>
        {
            if (ReadOne(form, transform, index, annotation) is { } field)
            {
                result.Add(field);
            }
        });

        return [.. result];
    }

    /// <summary>
    /// Reads what one page contributes to the document's form information; one short job per page (the caller combines
    /// the pages with <see cref="PdfiumFormDocumentInfo.Combine"/>). Nothing for a document without a form environment.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static PdfiumFormPageInfo ReadPageInfo(PdfiumPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        if (page.Handle.FormEnvironment is not { } environment)
        {
            return new PdfiumFormPageInfo(0, false, false, false, false);
        }

        var form = environment.Handle;
        var widgets = 0;
        bool calculated = false, formatScripts = false, signature = false, buttons = false;
        ForEachWidget(native, (index, annotation) =>
        {
            if (!HasUsableRect(annotation, out _))
            {
                return;
            }

            widgets++;
            var type = Fpdf.FPDFAnnot_GetFormFieldType(form, annotation);
            buttons |= type == FpdfConstants.FormFieldPushButton;
            signature |= type == FpdfConstants.FormFieldSignature;
            calculated = calculated || HasScript(form, annotation, FpdfConstants.AnnotActionCalculate);
            formatScripts = formatScripts
                || HasScript(form, annotation, FpdfConstants.AnnotActionKeyStroke)
                || HasScript(form, annotation, FpdfConstants.AnnotActionFormat)
                || HasScript(form, annotation, FpdfConstants.AnnotActionValidate);
        });

        return new PdfiumFormPageInfo(widgets, calculated, formatScripts, signature, buttons);
    }

    /// <summary>
    /// Reads the form information of the whole document in one call (each page loaded, read with
    /// <see cref="ReadPageInfo"/> and closed). For a long document the caller should call <see cref="ReadPageInfo"/> in
    /// one job per page instead. A document without a form environment loads no page.
    /// </summary>
    public static PdfiumFormDocumentInfo ReadDocumentInfo(PdfiumDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var formType = document.FormType;
        var xfa = formType is FpdfConstants.FormTypeXfaFull or FpdfConstants.FormTypeXfaForeground;
        if (document.FormEnvironment is null)
        {
            return PdfiumFormDocumentInfo.Combine(xfa, []);
        }

        var pages = new PdfiumFormPageInfo[document.PageCount];
        for (var index = 0; index < pages.Length; index++)
        {
            using var page = document.LoadPage(index);
            pages[index] = ReadPageInfo(page);
        }

        return PdfiumFormDocumentInfo.Combine(xfa, pages);
    }

    /// <summary>Calls <paramref name="read"/> for every widget of the page within the limits; the handle is closed afterwards.</summary>
    private static void ForEachWidget(nint page, Action<int, nint> read)
    {
        var count = Math.Clamp(Fpdf.FPDFPage_GetAnnotCount(page), 0, PdfiumAnnotations.MaxScannedPerPage);
        var widgets = 0;
        for (var index = 0; index < count && widgets < MaxFieldsPerPage; index++)
        {
            var annotation = Fpdf.FPDFPage_GetAnnot(page, index);
            if (annotation == 0)
            {
                continue;
            }

            try
            {
                if (Fpdf.FPDFAnnot_GetSubtype(annotation) != FpdfConstants.AnnotWidget)
                {
                    continue;
                }

                widgets++;
                read(index, annotation);
            }
            finally
            {
                Fpdf.FPDFPage_CloseAnnot(annotation);
            }
        }
    }

    private static PdfiumFormField? ReadOne(nint form, PdfiumPageText.PageTransform transform, int index, nint annotation)
    {
        if (!HasUsableRect(annotation, out var rect))
        {
            return null;
        }

        var bounds = transform.ToPage(rect.Left, rect.Top, rect.Right, rect.Bottom);
        var type = Fpdf.FPDFAnnot_GetFormFieldType(form, annotation);
        var kind = KindOf(type);
        var flags = unchecked((uint)Fpdf.FPDFAnnot_GetFormFieldFlags(form, annotation));
        var field = new PdfiumFormField(index, kind, flags, bounds, null, null, null, null, null, [], [], false, null);
        if (kind == PdfiumFormKind.Unknown)
        {
            return field with
            {
                SkipReason = type < 0 ? "PDFium does not read the widget as a form field." : $"The field type {type} is not supported.",
            };
        }

        var name = ReadText(&Fpdf.FPDFAnnot_GetFormFieldName, form, annotation, MaxNameLength, out var nameTooLong);
        if (nameTooLong)
        {
            return field with { SkipReason = $"The field name is longer than {MaxNameLength} characters." };
        }

        if (string.IsNullOrEmpty(name))
        {
            return field with { SkipReason = "The field has no name." };
        }

        if (name.Contains('\0', StringComparison.Ordinal))
        {
            return field with { SkipReason = "The field name contains a NUL character." };
        }

        var alternate = ReadText(&Fpdf.FPDFAnnot_GetFormFieldAlternateName, form, annotation, MaxNameLength, out _);
        field = field with
        {
            Name = name,
            AlternateName = string.IsNullOrEmpty(alternate) || alternate.Contains('\0', StringComparison.Ordinal) ? null : alternate,
            MaxLength = ReadMaxLength(annotation),
        };

        if (kind != PdfiumFormKind.ListBox)
        {
            var value = ReadText(&Fpdf.FPDFAnnot_GetFormFieldValue, form, annotation, MaxValueLength, out var valueTooLong);
            if (valueTooLong)
            {
                return field with { SkipReason = $"The value is longer than {MaxValueLength} characters." };
            }

            field = field with { Value = value };
        }

        if (kind is PdfiumFormKind.CheckBox or PdfiumFormKind.RadioButton)
        {
            var export = ReadText(&Fpdf.FPDFAnnot_GetFormFieldExportValue, form, annotation, MaxValueLength, out var exportTooLong);
            if (exportTooLong)
            {
                return field with { SkipReason = $"The export value is longer than {MaxValueLength} characters." };
            }

            return field with { ExportValue = export, Checked = Fpdf.FPDFAnnot_IsChecked(form, annotation) != 0 };
        }

        if (kind is PdfiumFormKind.ComboBox or PdfiumFormKind.ListBox)
        {
            return ReadOptions(form, annotation, field);
        }

        return field;
    }

    /// <summary>Reads the option labels and the selection, or sets a skip reason when the options are beyond the limits.</summary>
    private static PdfiumFormField ReadOptions(nint form, nint annotation, PdfiumFormField field)
    {
        var count = Fpdf.FPDFAnnot_GetOptionCount(form, annotation);
        if (count <= 0)
        {
            return field;
        }

        if (count > MaxOptionsPerField)
        {
            return field with { SkipReason = $"The field has more than {MaxOptionsPerField} options." };
        }

        var labels = new string[count];
        var selected = new List<int>();
        var budget = MaxValueLength;
        for (var option = 0; option < count; option++)
        {
            var length = Fpdf.FPDFAnnot_GetOptionLabel(form, annotation, option, null, new CULong(0)).Value;
            var label = string.Empty;
            if (length > 2 && length % 2 == 0)
            {
                var characters = (long)(length / 2) - 1;
                if (characters > budget)
                {
                    return field with { SkipReason = $"The option labels are longer than {MaxValueLength} characters together." };
                }

                budget -= (int)characters;
                var buffer = new char[(int)(length / 2)];
                nuint written;
                fixed (char* pointer = buffer)
                {
                    written = Fpdf.FPDFAnnot_GetOptionLabel(form, annotation, option, (ushort*)pointer, new CULong(length)).Value;
                }

                if (written == length)
                {
                    label = new string(buffer, 0, buffer.Length - 1);
                }
            }

            labels[option] = label;
            if (Fpdf.FPDFAnnot_IsOptionSelected(form, annotation, option) != 0)
            {
                selected.Add(option);
            }
        }

        return field with { OptionLabels = labels, Selected = [.. selected] };
    }

    private static PdfiumFormKind KindOf(int type) => type switch
    {
        FpdfConstants.FormFieldPushButton => PdfiumFormKind.PushButton,
        FpdfConstants.FormFieldCheckBox => PdfiumFormKind.CheckBox,
        FpdfConstants.FormFieldRadioButton => PdfiumFormKind.RadioButton,
        FpdfConstants.FormFieldComboBox => PdfiumFormKind.ComboBox,
        FpdfConstants.FormFieldListBox => PdfiumFormKind.ListBox,
        FpdfConstants.FormFieldTextField => PdfiumFormKind.Text,
        FpdfConstants.FormFieldSignature => PdfiumFormKind.Signature,
        _ => PdfiumFormKind.Unknown,
    };

    /// <summary>Whether the widget has a <c>/Rect</c> with finite coordinates within <see cref="PdfiumAnnotations.MaxCoordinate"/>, as <see cref="PdfiumAnnotations"/> requires.</summary>
    private static bool HasUsableRect(nint annotation, out FsRectF rect)
    {
        rect = default;
        fixed (FsRectF* pointer = &rect)
        {
            return PdfiumAnnotations.HasKey(annotation, "Rect\0"u8) && Fpdf.FPDFAnnot_GetRect(annotation, pointer) != 0
                && PdfiumAnnotations.IsSane(rect.Left) && PdfiumAnnotations.IsSane(rect.Top)
                && PdfiumAnnotations.IsSane(rect.Right) && PdfiumAnnotations.IsSane(rect.Bottom);
        }
    }

    /// <summary>The direct <c>/MaxLen</c> of the widget (an inherited one reads as missing), or <see langword="null"/>.</summary>
    private static int? ReadMaxLength(nint annotation)
    {
        float value;
        fixed (byte* key = "MaxLen\0"u8)
        {
            if (Fpdf.FPDFAnnot_GetNumberValue(annotation, key, &value) == 0)
            {
                return null;
            }
        }

        return float.IsFinite(value) && value >= 0 && value <= int.MaxValue ? (int)value : null;
    }

    /// <summary>Whether the field has a script for <paramref name="formEvent"/>; only the length is read (2 means none).</summary>
    private static bool HasScript(nint form, nint annotation, int formEvent) =>
        Fpdf.FPDFAnnot_GetFormAdditionalActionJavaScript(form, annotation, formEvent, null, new CULong(0)).Value > 2;

    /// <summary>
    /// Reads a string of a form field (UTF-16LE with terminator, length in bytes): <see langword="null"/> on an error,
    /// <see cref="string.Empty"/> for an empty one. One longer than <paramref name="maxCharacters"/> is not copied and sets
    /// <paramref name="tooLong"/>.
    /// </summary>
    private static string? ReadText(
        delegate*<nint, nint, ushort*, CULong, CULong> read,
        nint form,
        nint annotation,
        int maxCharacters,
        out bool tooLong)
    {
        tooLong = false;
        var length = read(form, annotation, null, new CULong(0)).Value;
        if (length < 2 || length % 2 != 0)
        {
            return null;
        }

        if (length > (nuint)(maxCharacters + 1) * 2)
        {
            tooLong = true;
            return null;
        }

        var buffer = new char[(int)(length / 2)];
        nuint written;
        fixed (char* pointer = buffer)
        {
            written = read(form, annotation, (ushort*)pointer, new CULong(length)).Value;
        }

        return written == length ? new string(buffer, 0, buffer.Length - 1) : null;
    }
}
