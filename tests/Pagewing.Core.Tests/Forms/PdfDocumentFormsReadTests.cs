using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Reading form fields through <see cref="PdfDocument"/> (milestone 11, task 6): the mapping to the Core model, the locks,
/// the hints, the required fields over all pages, and that a document without a form never gets a job or an environment.
/// </summary>
public sealed class PdfDocumentFormsReadTests : IDisposable
{
    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task PlainDocumentHasNoFormAndCreatesNoEnvironment()
    {
        await using var document = await OpenFixtureAsync("text-basic.pdf");

        Assert.False(document.HasAcroForm);
        Assert.Equal(0, document.FormEnvironmentsCreated);

        var info = await document.GetFormInfoAsync(Ct);
        Assert.False(info.HasFields);
        Assert.Equal(FormHint.None, info.Hints);
        Assert.Equal(0, info.FieldCount);
        Assert.Empty(info.PagesWithFields);
        Assert.Empty(await document.GetFormFieldsAsync(0, Ct));
        Assert.Empty(await document.GetEmptyRequiredFieldsAsync(Ct));
        Assert.Null(document.CachedPagesWithFields);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetFormFieldsAsync(document.PageCount, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetFormFieldsAsync(-1, Ct));
        Assert.Equal(0, document.FormEnvironmentsCreated);

        var disposed = await OpenFixtureAsync("text-basic.pdf");
        await disposed.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => disposed.HasAcroForm);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed.GetFormInfoAsync(Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => disposed.GetFormFieldsAsync(0, Ct));
    }

    [Fact]
    public async Task FieldsOfAPageComeAsFormFieldsInAnnotsOrder()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        Assert.True(document.HasAcroForm);

        var fields = await document.GetFormFieldsAsync(0, Ct);
        var annotations = await document.GetAnnotationsAsync(0, Ct);

        Assert.Equal(17, fields.Count);
        Assert.Equal(Enumerable.Range(0, 17), fields.Select(f => f.Index));
        Assert.Equal(
            ["Name", "Bemerkung", "Kamm", "Kurz", "Gesperrt", "Pflicht", "Passwort", "Agb", "Zustimmung", "Zahlung", "Zahlung", "Stufe", "Stufe", "Farbe", "Frei", "Liste", "Mehrfach"],
            fields.Select(f => f.Key.FieldName));
        Assert.All(fields, f => Assert.Equal(0, f.Key.Page));
        Assert.Equal(
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0, 0],
            fields.Select(f => f.Key.WidgetNumber));
        Assert.Equal(
            [
                FormFieldKind.Text, FormFieldKind.Text, FormFieldKind.Text, FormFieldKind.Text, FormFieldKind.Text, FormFieldKind.Text, FormFieldKind.Text,
                FormFieldKind.CheckBox, FormFieldKind.CheckBox,
                FormFieldKind.RadioButton, FormFieldKind.RadioButton, FormFieldKind.RadioButton, FormFieldKind.RadioButton,
                FormFieldKind.ComboBox, FormFieldKind.ComboBox, FormFieldKind.ListBox, FormFieldKind.ListBox,
            ],
            fields.Select(f => f.Kind));

        // The rectangles are those of the annotations (page points, origin top left).
        foreach (var field in fields)
        {
            Assert.Equal(annotations.Single(a => a.Index == field.Index).Bounds, field.Bounds);
        }

        // Traits, direct /MaxLen, options and the state of the field.
        Assert.True(fields.Single(f => f.Key.FieldName == "Bemerkung").Traits.HasFlag(FormFieldTraits.Multiline));
        Assert.True(fields.Single(f => f.Key.FieldName == "Kamm").Traits.HasFlag(FormFieldTraits.Comb));
        Assert.True(fields.Single(f => f.Key.FieldName == "Pflicht").Traits.HasFlag(FormFieldTraits.Required));
        Assert.True(fields.Single(f => f.Key.FieldName == "Passwort").Traits.HasFlag(FormFieldTraits.Password));
        Assert.Equal(5, fields.Single(f => f.Key.FieldName == "Kurz").MaxLength);
        Assert.Null(fields.Single(f => f.Key.FieldName == "Name").MaxLength);
        Assert.Equal(["Rot", "Gruen", "Blau"], fields.Single(f => f.Key.FieldName == "Farbe").Options.Select(o => o.Label));
        Assert.Equal(FormFieldState.OfText("Alt"), fields.Single(f => f.Key.FieldName == "Name").State);
        Assert.Equal(FormFieldState.OfText(string.Empty), fields.Single(f => f.Key.FieldName == "Bemerkung").State);
        Assert.Equal(FormFieldState.OfChecked(false), fields.Single(f => f.Key.FieldName == "Agb").State);
        Assert.Equal("Yes", fields.Single(f => f.Key.FieldName == "Agb").ExportValue);
        Assert.Equal(FormFieldState.OfSelection([]), fields.Single(f => f.Key.FieldName == "Liste").State);
        Assert.Equal(FormFieldState.OfSelection([]), fields.Single(f => f.Key.FieldName == "Farbe").State);

        // A field that is not locked by anything has no lock reason.
        Assert.All(fields.Where(f => f.Key.FieldName != "Gesperrt"), f =>
        {
            Assert.Equal(FormLock.None, f.Lock);
            Assert.Null(f.LockReason);
        });

        // Page two: the second widget of Name and the third of the payment group; numbering starts at 0 on each page.
        var second = await document.GetFormFieldsAsync(1, Ct);
        Assert.Equal(["Zahlung", "Name", "Telefon", "Ort"], second.Select(f => f.Key.FieldName));
        Assert.All(second, f => Assert.Equal(1, f.Key.Page));
        Assert.All(second, f => Assert.Equal(0, f.Key.WidgetNumber));
    }

    [Fact]
    public async Task RadioGroupHasSharedStateAndThreeWidgets()
    {
        await using var document = await OpenAsync(WriteChosenRadioForm());

        var first = await document.GetFormFieldsAsync(0, Ct);
        var second = await document.GetFormFieldsAsync(1, Ct);
        var group = first.Concat(second).Where(f => f.Key.FieldName == "Zahlung").ToList();

        Assert.Equal(3, group.Count);
        Assert.Equal([(0, 0), (0, 1), (1, 0)], group.Select(f => (f.Key.Page, f.Key.WidgetNumber)));
        Assert.Equal(["Bar", "Karte", "Rechnung"], group.Select(f => f.ExportValue));
        Assert.All(group, f => Assert.Equal(FormFieldKind.RadioButton, f.Kind));

        // Every widget of the group reports the state of the group: the export value of the chosen button.
        Assert.All(group, f => Assert.Equal(FormFieldState.OfText("Karte"), f.State));

        // Without a choice (forms-basic: /V /Off) the state is "none" on every widget of every page.
        await using var basic = await OpenFixtureAsync(FormsBasic);
        var none = (await basic.GetFormFieldsAsync(0, Ct)).Concat(await basic.GetFormFieldsAsync(1, Ct)).Where(f => f.Key.FieldName == "Zahlung").ToList();
        Assert.Equal(3, none.Count);
        Assert.All(none, f => Assert.Equal(FormFieldState.OfText(null), f.State));
    }

    [Fact]
    public async Task SignatureButtonAndNamelessAreUnsupportedAndReadOnlyIsReadOnly()
    {
        await using var signed = await OpenFixtureAsync(FormsSigned);
        var fields = await signed.GetFormFieldsAsync(0, Ct);

        Assert.Equal(4, fields.Count);
        var signatures = fields.Where(f => f.Kind == FormFieldKind.Signature).ToList();
        Assert.Equal(2, signatures.Count);
        Assert.All(signatures, f =>
        {
            Assert.Equal(FormLock.Unsupported, f.Lock);
            Assert.False(string.IsNullOrEmpty(f.LockReason));
            Assert.DoesNotContain("valid", f.LockReason, StringComparison.OrdinalIgnoreCase);
        });
        var button = fields.Single(f => f.Kind == FormFieldKind.PushButton);
        Assert.Equal(FormLock.Unsupported, button.Lock);
        Assert.False(string.IsNullOrEmpty(button.LockReason));
        Assert.Equal(FormLock.None, fields.Single(f => f.Key.FieldName == "Name").Lock);

        // A read-only flag locks without being unsupported.
        await using var basic = await OpenFixtureAsync(FormsBasic);
        var locked = (await basic.GetFormFieldsAsync(0, Ct)).Single(f => f.Key.FieldName == "Gesperrt");
        Assert.True(locked.Traits.HasFlag(FormFieldTraits.ReadOnly));
        Assert.Equal(FormLock.ReadOnly, locked.Lock);
        Assert.False(string.IsNullOrEmpty(locked.LockReason));
        Assert.Equal(FormFieldState.OfText("Fest"), locked.State);

        // Hostile form: a widget without a name has the empty name and is never editable; the plain one is.
        await using var hostile = await OpenFixtureAsync(FormsHostile);
        var hostileFields = await hostile.GetFormFieldsAsync(0, Ct);
        Assert.Equal([0, 1, 2, 4, 5, 6, 7], hostileFields.Select(f => f.Index));
        var nameless = hostileFields.Single(f => f.Index == 4);
        Assert.Equal(string.Empty, nameless.Key.FieldName);
        Assert.Equal(FormLock.Unsupported, nameless.Lock);
        Assert.False(string.IsNullOrEmpty(nameless.LockReason));
        Assert.Equal(FormLock.None, hostileFields.Single(f => f.Index == 0).Lock);
        Assert.Equal(FormLock.Unsupported, hostileFields.Single(f => f.Index == 5).Lock);
        Assert.Empty(hostileFields.Single(f => f.Index == 5).Options);
    }

    [Fact]
    public async Task CalculatedFieldIsNotLocked()
    {
        await using var document = await OpenFixtureAsync(FormsCalc);

        var fields = await document.GetFormFieldsAsync(0, Ct);
        var sum = fields.Single(f => f.Key.FieldName == "Summe");
        Assert.Equal(FormLock.None, sum.Lock);
        Assert.Null(sum.LockReason);
        Assert.Equal(FormFieldState.OfText("42"), sum.State);
        Assert.All(fields, f => Assert.Equal(FormLock.None, f.Lock));

        var info = await document.GetFormInfoAsync(Ct);
        Assert.True(info.HasFields);
        Assert.True(info.Hints.HasFlag(FormHint.Calculated));
        Assert.True(info.Hints.HasFlag(FormHint.FormatScripts));
        Assert.False(info.Hints.HasFlag(FormHint.Xfa));
        Assert.Equal(5, info.FieldCount);
    }

    [Fact]
    public async Task XfaAttrappeRaisesTheXfaHint()
    {
        await using var document = await OpenFixtureAsync(FormsXfa);

        Assert.True(document.HasAcroForm);
        var info = await document.GetFormInfoAsync(Ct);
        Assert.True(info.HasFields);
        Assert.Equal(FormHint.Xfa, info.Hints);
        Assert.Equal(1, info.FieldCount);

        // The AcroForm field stays fillable.
        var field = Assert.Single(await document.GetFormFieldsAsync(0, Ct));
        Assert.Equal("Name", field.Key.FieldName);
        Assert.Equal(FormLock.None, field.Lock);

        // The other hints, from the other fixtures.
        await using var signed = await OpenFixtureAsync(FormsSigned);
        Assert.Equal(FormHint.SignatureField | FormHint.Buttons, (await signed.GetFormInfoAsync(Ct)).Hints);
        await using var basic = await OpenFixtureAsync(FormsBasic);
        Assert.Equal(FormHint.None, (await basic.GetFormInfoAsync(Ct)).Hints);
    }

    [Fact]
    public async Task FormInfoIsKeptAfterTheFirstCompleteScan()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        Assert.Null(document.CachedPagesWithFields);

        var info = await document.GetFormInfoAsync(Ct);

        Assert.Equal(21, info.FieldCount);
        Assert.Equal([0, 1], info.PagesWithFields);
        Assert.Equal([0, 1], document.CachedPagesWithFields);
        Assert.Same(info, await document.GetFormInfoAsync(Ct));
    }

    [Fact]
    public async Task EmptyRequiredFieldsAreListedOnceAcrossPages()
    {
        await using var document = await OpenAsync(WriteChosenRadioForm());

        // Pflicht has a widget on each page and counts once; Zustimmung is an unchecked required box; the chosen radio group,
        // the filled field and the read-only field do not count.
        Assert.Equal(["Pflicht", "Zustimmung"], await document.GetEmptyRequiredFieldsAsync(Ct));
        Assert.Equal([0, 1], document.CachedPagesWithFields);

        await using var basic = await OpenFixtureAsync(FormsBasic);
        Assert.Equal(["Pflicht"], await basic.GetEmptyRequiredFieldsAsync(Ct));
    }

    [Fact]
    public async Task CancelStopsTheScanOfAllPages()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetEmptyRequiredFieldsAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetFormInfoAsync(canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetFormFieldsAsync(0, canceled.Token));

        // A canceled scan keeps nothing; the next one starts over and completes.
        Assert.Null(document.CachedPagesWithFields);
        Assert.Equal(["Pflicht"], await document.GetEmptyRequiredFieldsAsync(Ct));
        Assert.Equal([0, 1], document.CachedPagesWithFields);
    }

    /// <summary>
    /// Two pages. Page one: the payment group <c>Zahlung</c> (Bar, Karte chosen) and the required <c>Pflicht</c> text field,
    /// the unchecked required check box <c>Zustimmung</c>, the filled required <c>Gefuellt</c>, the read-only required
    /// <c>Gesperrt</c>. Page two: the third widget of <c>Zahlung</c> (Rechnung) and the second widget of <c>Pflicht</c>.
    /// </summary>
    private string WriteChosenRadioForm()
    {
        const string da = "/DA (/Helv 10 Tf 0 g)";
        const string appearance = "/AP << /N << /{0} 11 0 R /Off 11 0 R >> >>";
        string Radio(string rect, string page, string state, bool selected) =>
            $"<< /Type /Annot /Subtype /Widget /Rect [{rect}] /P {page} 0 R /F 4 /Parent 5 0 R /AS /{(selected ? state : "Off")} "
            + string.Format(System.Globalization.CultureInfo.InvariantCulture, appearance, state) + " >>";

        return WritePdf(folder, "chosen-radio.pdf", [
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 12 0 R 13 0 R 14 0 R 15 0 R] {da} /DR << /Font << /Helv 16 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [6 0 R 7 0 R 9 0 R 13 0 R 14 0 R 15 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [8 0 R 10 0 R] >>",
            "<< /FT /Btn /Ff 32770 /T (Zahlung) /V /Karte /Kids [6 0 R 7 0 R 8 0 R] >>",
            Radio("20 250 38 268", "3", "Bar", selected: false),
            Radio("60 250 78 268", "3", "Karte", selected: true),
            Radio("20 250 38 268", "4", "Rechnung", selected: false),
            $"<< /Type /Annot /Subtype /Widget /Rect [20 200 200 222] /P 3 0 R /F 4 /Parent 12 0 R {da} >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 200 200 222] /P 4 0 R /F 4 /Parent 12 0 R {da} >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 18 18] /Length 0 >>\nstream\n\nendstream",
            $"<< /FT /Tx /Ff 2 /T (Pflicht) {da} /Kids [9 0 R 10 0 R] >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 150 38 168] /P 3 0 R /F 4 /FT /Btn /Ff 2 /T (Zustimmung) /V /Off /AS /Off /AP << /N << /Yes 11 0 R /Off 11 0 R >> >> >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 100 200 122] /P 3 0 R /F 4 /FT /Tx /Ff 2 /T (Gefuellt) /V (x) {da} >>",
            $"<< /Type /Annot /Subtype /Widget /Rect [20 50 200 72] /P 3 0 R /F 4 /FT /Tx /Ff 3 /T (Gesperrt) {da} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ]);
    }
}
