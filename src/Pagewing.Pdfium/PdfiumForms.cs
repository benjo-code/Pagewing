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
/// <param name="FieldNames">The usable full names of the fields with widgets on the page, each once, in <c>/Annots</c> order
/// (a name read within the time budget; none longer than <see cref="PdfiumForms.MaxNameLength"/> or with a NUL).</param>
/// <param name="NamesComplete">Whether the name of every widget with a usable <c>/Rect</c> was read: <see langword="false"/> when
/// the time budget ended the reading early or a first call was too slow (<paramref name="FieldNames"/> may then miss names).</param>
internal sealed record PdfiumFormPageInfo(
    int Widgets,
    bool Calculated,
    bool FormatScripts,
    bool SignatureField,
    bool Buttons,
    string[]? FieldNames = null,
    bool NamesComplete = true);

/// <summary>The field names of a page (<see cref="PdfiumForms.ReadPageFieldNames"/>).</summary>
/// <param name="Names">The usable full names, each once, in <c>/Annots</c> order.</param>
/// <param name="Complete">Whether the name of every widget with a usable <c>/Rect</c> was read within the time budget.</param>
internal sealed record PdfiumFieldNames(string[] Names, bool Complete);

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
/// Every form call (<c>FPDFAnnot_GetFormField*</c>, <c>GetOption*</c>, <c>IsChecked</c>,
/// <c>GetFormAdditionalActionJavaScript</c>) makes PDFium resolve the widget's field through its own field table, and to
/// find it PDFium builds the full field name by walking <c>/Parent</c> without a depth limit (only a cycle guard). A hostile
/// chain therefore makes each call slow (measured: 50,000 levels with names about 0.5 to 1 s per call, quadratic in the
/// name length), and no binding can measure the depth beforehand (<c>FPDFAnnot_GetLinkedAnnot</c> follows <c>/Parent</c>
/// only to nodes with <c>/Type /Annot</c>, which field nodes lack). Pagewing therefore bounds the <b>time</b> it spends, not
/// a single call: the first form call on each widget is timed, and one slower than <see cref="MaxFirstCallTicks"/> ends the
/// reading of that widget (<see cref="SlowReason"/>, no further call); once <see cref="MaxFormCallTimePerPage"/> is spent in
/// form calls on a page, the remaining widgets get the same reason without any form call. A single call still grows with the
/// file (Docs/DECISIONS.md, "Formulare: Zeitbudget beim Lesen der Felder (M11)").
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
    /// The longest first form call on a widget, in <see cref="TimeSpan"/> ticks (20 ms); a widget whose first call takes
    /// longer is not read further. A normal field answers in well under a millisecond.
    /// </summary>
    public const long MaxFirstCallTicks = 20 * TimeSpan.TicksPerMillisecond;

    /// <summary>The time of form calls one read of a page may spend; then the remaining widgets are skipped without a call.</summary>
    public static readonly TimeSpan MaxFormCallTimePerPage = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The longest appearance stream whose fonts are checked after a write, in bytes of UTF-16 (16 MiB); a longer one counts
    /// as not checkable. A text of <see cref="MaxValueLength"/> characters stays well below it.
    /// </summary>
    public const long MaxCheckedAppearanceBytes = 16L * 1024 * 1024;

    /// <summary>The <see cref="PdfiumFormField.SkipReason"/> of a widget skipped by the time budget.</summary>
    internal const string SlowReason = "The field tree is too deep or too slow to read.";

    /// <summary>
    /// Reads the widgets of a page in <c>/Annots</c> order. Empty for a page of a document without a form environment.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="clock">The clock of the time budget; <see cref="TimeProvider.System"/> without one (tests pass a fake).</param>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static PdfiumFormField[] ReadPage(PdfiumPage page, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        if (page.Handle.FormEnvironment is not { } environment)
        {
            return [];
        }

        var form = environment.Handle;
        var transform = PdfiumPageText.TransformOf(page);
        var budget = new CallBudget(clock ?? TimeProvider.System);
        var result = new List<PdfiumFormField>();
        ForEachWidget(native, (index, annotation) =>
        {
            if (ReadOne(form, transform, index, annotation, budget) is { } field)
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
    /// <param name="page">The page.</param>
    /// <param name="clock">The clock of the time budget (as for <see cref="ReadPage"/>); a widget beyond the budget is counted
    /// but contributes no hint.</param>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static PdfiumFormPageInfo ReadPageInfo(PdfiumPage page, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        if (page.Handle.FormEnvironment is not { } environment)
        {
            return new PdfiumFormPageInfo(0, false, false, false, false);
        }

        var form = environment.Handle;
        var budget = new CallBudget(clock ?? TimeProvider.System);
        var widgets = 0;
        bool calculated = false, formatScripts = false, signature = false, buttons = false, namesComplete = true;
        var names = new NameSet();
        ForEachWidget(native, (index, annotation) =>
        {
            if (!HasUsableRect(annotation, out _))
            {
                return;
            }

            widgets++;
            if (budget.Exhausted)
            {
                namesComplete = false;
                return;
            }

            var start = budget.Now();
            var type = Fpdf.FPDFAnnot_GetFormFieldType(form, annotation);
            var first = budget.Since(start);
            if (first.Ticks > MaxFirstCallTicks)
            {
                namesComplete = false;
                budget.Spend(first);
                return;
            }

            names.Add(ReadName(form, annotation));
            buttons |= type == FpdfConstants.FormFieldPushButton;
            signature |= type == FpdfConstants.FormFieldSignature;
            calculated = calculated || HasScript(form, annotation, FpdfConstants.AnnotActionCalculate);
            formatScripts = formatScripts
                || HasScript(form, annotation, FpdfConstants.AnnotActionKeyStroke)
                || HasScript(form, annotation, FpdfConstants.AnnotActionFormat)
                || HasScript(form, annotation, FpdfConstants.AnnotActionValidate);
            budget.Spend(budget.Since(start));
        });

        return new PdfiumFormPageInfo(widgets, calculated, formatScripts, signature, buttons, names.ToArray(), namesComplete);
    }

    /// <summary>
    /// Reads only the full names of the fields with widgets on a page, each once in <c>/Annots</c> order, within the same time
    /// budget as <see cref="ReadPage"/> (one name call per widget), and whether every name was read before the budget was
    /// spent. Empty and complete for a page of a document without a form environment.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static PdfiumFieldNames ReadPageFieldNames(PdfiumPage page, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        if (page.Handle.FormEnvironment is not { } environment)
        {
            return new PdfiumFieldNames([], true);
        }

        var form = environment.Handle;
        var budget = new CallBudget(clock ?? TimeProvider.System);
        var names = new NameSet();
        var complete = true;
        ForEachWidget(native, (index, annotation) =>
        {
            if (!HasUsableRect(annotation, out _))
            {
                return;
            }

            if (budget.Exhausted)
            {
                complete = false;
                return;
            }

            var start = budget.Now();
            names.Add(ReadName(form, annotation));
            budget.Spend(budget.Since(start));
        });

        return new PdfiumFieldNames(names.ToArray(), complete);
    }

    /// <summary>
    /// Reads the form information of the whole document in one call (each page loaded, read with
    /// <see cref="ReadPageInfo"/> and closed). For a long document the caller should call <see cref="ReadPageInfo"/> in
    /// one job per page instead. A document without a form environment loads no page.
    /// </summary>
    public static PdfiumFormDocumentInfo ReadDocumentInfo(PdfiumDocument document, TimeProvider? clock = null)
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
            pages[index] = ReadPageInfo(page, clock);
        }

        return PdfiumFormDocumentInfo.Combine(xfa, pages);
    }

    /// <summary>
    /// Reads one widget again (after a write), without the time budget: the caller found it by <see cref="ReadPage"/> in the
    /// same job, so its first call was fast. <see langword="null"/> when the index is no widget with a usable <c>/Rect</c>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static PdfiumFormField? ReadWidget(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (form, native) = FormOf(page);
        var transform = PdfiumPageText.TransformOf(page);
        return WithWidget(native, index, annotation =>
        {
            if (!HasUsableRect(annotation, out var rect))
            {
                return null;
            }

            var bounds = transform.ToPage(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return ReadField(form, index, annotation, bounds, Fpdf.FPDFAnnot_GetFormFieldType(form, annotation));
        });
    }

    /// <summary>
    /// Writes <paramref name="text"/> into a text field or editable combo box the way the spike found (Docs/DECISIONS.md,
    /// "Formulare: Formular-Engine …", point 1): focus the widget, select all, replace the selection, leave the focus. The
    /// engine writes <c>/V</c> and a new <c>/AP</c> for every widget of the field on a loaded page; it may shorten or change the
    /// text (<c>/MaxLen</c>, control characters), so the caller reads it back. The focus is always left.
    /// </summary>
    /// <exception cref="InvalidOperationException">PDFium did not focus the widget; nothing was written.</exception>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static void ReplaceText(PdfiumPage page, int index, string text)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(text);
        var (form, native) = FormOf(page);
        WithWidget<object?>(native, index, annotation =>
        {
            Focus(form, annotation);
            try
            {
                _ = Fpdf.FORM_SelectAllText(form, native);
                fixed (char* value = text)
                {
                    // A .NET string is NUL-terminated, as FPDF_WIDESTRING must be.
                    Fpdf.FORM_ReplaceSelection(form, native, (ushort*)value);
                }
            }
            finally
            {
                LeaveFocus(form);
            }

            return null;
        });
    }

    /// <summary>
    /// Clicks a check box or radio button widget in the middle of its <c>/Rect</c> (PDF user space, so rotation and CropBox play
    /// no part): a check box toggles, a radio button is chosen (a click on the chosen one never undoes the choice). The focus the
    /// click gives is left afterwards.
    /// </summary>
    /// <exception cref="InvalidOperationException">The widget has no usable rectangle.</exception>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static void Click(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        var (form, native) = FormOf(page);
        WithWidget<object?>(native, index, annotation =>
        {
            if (!HasUsableRect(annotation, out var rect))
            {
                throw new InvalidOperationException("The widget has no usable rectangle to click.");
            }

            var x = (rect.Left + (double)rect.Right) / 2;
            var y = (rect.Top + (double)rect.Bottom) / 2;
            try
            {
                _ = Fpdf.FORM_OnLButtonDown(form, native, 0, x, y);
                _ = Fpdf.FORM_OnLButtonUp(form, native, 0, x, y);
            }
            finally
            {
                LeaveFocus(form);
            }

            return null;
        });
    }

    /// <summary>
    /// Chooses exactly the options <paramref name="selected"/> of a combo box or list box: focus, deselect every chosen option
    /// that is not wanted (a list box only; the engine cannot deselect in a combo box), select the wanted ones, leave the focus
    /// (which writes <c>/V</c>, <c>/I</c> and <c>/AP</c>). A widget with more than <see cref="MaxOptionsPerField"/> options is
    /// never focused (focusing grows faster than linearly with the options; spike point 12).
    /// </summary>
    /// <exception cref="InvalidOperationException">The field has too many options, or PDFium did not focus the widget; nothing was written.</exception>
    /// <exception cref="ObjectDisposedException">The page or the environment is closed.</exception>
    public static void SelectOptions(PdfiumPage page, int index, IReadOnlyList<int> selected)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(selected);
        var (form, native) = FormOf(page);
        WithWidget<object?>(native, index, annotation =>
        {
            var count = Fpdf.FPDFAnnot_GetOptionCount(form, annotation);
            if (count > MaxOptionsPerField)
            {
                throw new InvalidOperationException($"The field has more than {MaxOptionsPerField} options.");
            }

            var combo = Fpdf.FPDFAnnot_GetFormFieldType(form, annotation) == FpdfConstants.FormFieldComboBox;
            Focus(form, annotation);
            try
            {
                if (!combo)
                {
                    for (var option = 0; option < count; option++)
                    {
                        if (!selected.Contains(option) && Fpdf.FPDFAnnot_IsOptionSelected(form, annotation, option) != 0)
                        {
                            _ = Fpdf.FORM_SetIndexSelected(form, native, option, 0);
                        }
                    }
                }

                foreach (var option in selected)
                {
                    if (option >= 0 && option < count)
                    {
                        _ = Fpdf.FORM_SetIndexSelected(form, native, option, 1);
                    }
                }
            }
            finally
            {
                LeaveFocus(form);
            }

            return null;
        });
    }

    /// <summary>
    /// The fonts the normal appearance (<c>/AP /N</c>) of a widget selects with <c>Tf</c>, each once; empty without an
    /// appearance. <see langword="null"/> when the appearance is longer than <see cref="MaxCheckedAppearanceBytes"/> or cannot
    /// be read (the caller treats that as a deviation). Read only (<c>FPDFAnnot_GetAP</c> does not touch the stream dictionary).
    /// </summary>
    /// <exception cref="ObjectDisposedException">The page is closed.</exception>
    public static IReadOnlyList<string>? AppearanceFonts(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        return WithWidget(native, index, annotation =>
        {
            var length = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, null, new CULong(0)).Value;
            if (length <= 2)
            {
                return [];
            }

            if (length > MaxCheckedAppearanceBytes || length % 2 != 0)
            {
                return null;
            }

            var buffer = new char[(int)(length / 2)];
            nuint written;
            fixed (char* pointer = buffer)
            {
                written = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, (ushort*)pointer, new CULong(length)).Value;
            }

            return written == length ? FontsOfContent(buffer.AsSpan(0, buffer.Length - 1)) : null;
        });
    }

    /// <summary>
    /// The fonts the widget's own <c>/DA</c> selects (normally one), or <see langword="null"/> when the widget has no
    /// <c>/DA</c> of its own: an inherited one (from the parent field or the AcroForm) cannot be read through PDFium's API.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The page is closed.</exception>
    public static IReadOnlyList<string>? DefaultAppearanceFonts(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        return WithWidget(native, index, annotation =>
            PdfiumAnnotations.ReadString(annotation, "DA\0"u8) is { } appearance ? FontsOfContent(appearance) : null);
    }

    /// <summary>
    /// The font names a content stream selects with the <c>Tf</c> operator (<c>/Name size Tf</c>), each once in the order they
    /// come first. A small tokenizer, no parser: literal strings (nested, with escapes), hex strings and comments are skipped,
    /// so text a user typed never counts as a font; anything but a name, a number and <c>Tf</c> in a row is no font selection.
    /// </summary>
    internal static IReadOnlyList<string> FontsOfContent(ReadOnlySpan<char> content)
    {
        var fonts = new List<string>();
        string? name = null;
        var sized = false;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (IsWhite(c))
            {
                i++;
                continue;
            }

            switch (c)
            {
                case '%':
                    while (i < content.Length && content[i] is not ('\r' or '\n'))
                    {
                        i++;
                    }

                    break;
                case '(':
                    i = SkipLiteralString(content, i);
                    name = null;
                    break;
                case '<':
                    i = i + 1 < content.Length && content[i + 1] == '<' ? i + 2 : SkipPast(content, i + 1, '>');
                    name = null;
                    break;
                case '/':
                    var end = EndOfToken(content, i + 1);
                    name = content[(i + 1)..end].ToString();
                    sized = false;
                    i = end;
                    break;
                default:
                    if (IsDelimiter(c))
                    {
                        name = null;
                        i++;
                        break;
                    }

                    var tokenEnd = EndOfToken(content, i);
                    var token = content[i..tokenEnd];
                    i = tokenEnd;
                    if (name is not null && !sized && IsNumber(token))
                    {
                        sized = true;
                        break;
                    }

                    if (name is not null && sized && token.SequenceEqual("Tf") && !fonts.Contains(name, StringComparer.Ordinal))
                    {
                        fonts.Add(name);
                    }

                    name = null;
                    break;
            }
        }

        return fonts;
    }

    private static bool IsWhite(char c) => c is '\0' or '\t' or '\n' or '\f' or '\r' or ' ';

    private static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    private static int EndOfToken(ReadOnlySpan<char> content, int start)
    {
        var end = start;
        while (end < content.Length && !IsWhite(content[end]) && !IsDelimiter(content[end]))
        {
            end++;
        }

        return end;
    }

    private static bool IsNumber(ReadOnlySpan<char> token) =>
        double.TryParse(token, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    /// <summary>The index after the literal string starting at <paramref name="start"/> (balanced parentheses, backslash escapes).</summary>
    private static int SkipLiteralString(ReadOnlySpan<char> content, int start)
    {
        var depth = 0;
        for (var i = start; i < content.Length; i++)
        {
            switch (content[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }

                    break;
            }
        }

        return content.Length;
    }

    private static int SkipPast(ReadOnlySpan<char> content, int start, char end)
    {
        var index = content[start..].IndexOf(end);
        return index < 0 ? content.Length : start + index + 1;
    }

    /// <summary>The form handle and the native page; throws when the page has no form environment.</summary>
    private static (nint Form, nint Page) FormOf(PdfiumPage page)
    {
        var native = page.Access();
        if (page.Handle.FormEnvironment is not { } environment)
        {
            throw new InvalidOperationException("The document has no form.");
        }

        return (environment.Handle, native);
    }

    /// <summary>Calls <paramref name="use"/> with the widget at <paramref name="index"/>; the handle is closed afterwards.</summary>
    /// <exception cref="InvalidOperationException">The index is no widget annotation of the page.</exception>
    private static T WithWidget<T>(nint page, int index, Func<nint, T> use)
    {
        var annotation = Fpdf.FPDFPage_GetAnnot(page, index);
        if (annotation == 0)
        {
            throw new InvalidOperationException($"The page has no annotation at index {index}.");
        }

        try
        {
            if (Fpdf.FPDFAnnot_GetSubtype(annotation) != FpdfConstants.AnnotWidget)
            {
                throw new InvalidOperationException($"The annotation at index {index} is no widget.");
            }

            return use(annotation);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>Focuses the widget, or throws when PDFium does not.</summary>
    private static void Focus(nint form, nint annotation)
    {
        if (Fpdf.FORM_SetFocusedAnnot(form, annotation) == 0)
        {
            LeaveFocus(form);
            throw new InvalidOperationException("The form engine did not focus the field.");
        }
    }

    /// <summary>
    /// Leaves the focus, which makes the engine write a changed value. Its result is not used: whether the value arrived is
    /// what the caller reads back.
    /// </summary>
    private static void LeaveFocus(nint form) => _ = Fpdf.FORM_ForceToKillFocus(form);

    /// <summary>Calls <paramref name="read"/> for every widget of the page within the limits; the handle is closed afterwards.</summary>
    internal static void ForEachWidget(nint page, Action<int, nint> read)
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

    /// <summary>
    /// Reads one widget within the time budget: nothing but the rectangle once the budget is spent, nothing more after a slow
    /// first form call; else <see cref="ReadField"/>, whose time counts towards the budget.
    /// </summary>
    private static PdfiumFormField? ReadOne(nint form, PdfiumPageText.PageTransform transform, int index, nint annotation, CallBudget budget)
    {
        if (!HasUsableRect(annotation, out var rect))
        {
            return null;
        }

        var bounds = transform.ToPage(rect.Left, rect.Top, rect.Right, rect.Bottom);
        if (budget.Exhausted)
        {
            return new PdfiumFormField(index, PdfiumFormKind.Unknown, 0, bounds, null, null, null, null, null, [], [], false, SlowReason);
        }

        var start = budget.Now();
        var type = Fpdf.FPDFAnnot_GetFormFieldType(form, annotation);
        var first = budget.Since(start);
        if (first.Ticks > MaxFirstCallTicks)
        {
            budget.Spend(first);
            return new PdfiumFormField(index, KindOf(type), 0, bounds, null, null, null, null, null, [], [], false, SlowReason);
        }

        try
        {
            return ReadField(form, index, annotation, bounds, type);
        }
        finally
        {
            budget.Spend(budget.Since(start));
        }
    }

    private static PdfiumFormField ReadField(nint form, int index, nint annotation, (double Left, double Top, double Right, double Bottom) bounds, int type)
    {
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
    internal static bool HasUsableRect(nint annotation, out FsRectF rect)
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

    /// <summary>The full name of the widget's field, or <see langword="null"/> when it is missing, too long or has a NUL (as <see cref="ReadField"/>).</summary>
    private static string? ReadName(nint form, nint annotation)
    {
        var name = ReadText(&Fpdf.FPDFAnnot_GetFormFieldName, form, annotation, MaxNameLength, out var tooLong);
        return tooLong || string.IsNullOrEmpty(name) || name.Contains('\0', StringComparison.Ordinal) ? null : name;
    }

    /// <summary>Names in the order they come first, each once.</summary>
    private sealed class NameSet
    {
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        private readonly List<string> names = [];

        public void Add(string? name)
        {
            if (name is not null && seen.Add(name))
            {
                names.Add(name);
            }
        }

        public string[] ToArray() => [.. names];
    }

    /// <summary>The time spent in form calls during one read of a page (<see cref="MaxFormCallTimePerPage"/>).</summary>
    private sealed class CallBudget(TimeProvider clock)
    {
        private TimeSpan spent;

        /// <summary>Gets whether the budget of the page is used up.</summary>
        public bool Exhausted => spent >= MaxFormCallTimePerPage;

        /// <summary>A timestamp of the clock.</summary>
        public long Now() => clock.GetTimestamp();

        /// <summary>The time since <paramref name="start"/>.</summary>
        public TimeSpan Since(long start) => clock.GetElapsedTime(start);

        /// <summary>Counts <paramref name="time"/> towards the budget.</summary>
        public void Spend(TimeSpan time) => spent += time;
    }
}
