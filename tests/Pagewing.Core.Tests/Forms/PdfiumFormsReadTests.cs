using Pagewing.Core.Forms;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Reading form fields through PDFium's form handle (milestone 11, task 5): kinds, flags, values, options, export values,
/// the widget rectangles (the same conversion as <see cref="PdfiumAnnotations"/>), the limits against hostile forms and the
/// document hints. Nothing here focuses a field; the combo with 20,000 options of forms-hostile.pdf is never focused.
/// </summary>
public sealed class PdfiumFormsReadTests : IDisposable
{
    /// <summary>The hostile form must be read well within this time (reading is cheap; only focusing a big combo is slow).</summary>
    private static readonly TimeSpan HostileTimeout = TimeSpan.FromSeconds(30);

    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PdfiumThread Pdfium => PdfiumThread.Shared;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task BasicFormListsEveryFieldWithKindAndFlags()
    {
        var fields = await ReadPageAsync(TestData.Pdf(FormsBasic), 0);

        Assert.Equal(17, fields.Length);
        Assert.Equal(Enumerable.Range(0, 17), fields.Select(f => f.Index));
        Assert.Equal(
            ["Name", "Bemerkung", "Kamm", "Kurz", "Gesperrt", "Pflicht", "Passwort", "Agb", "Zustimmung", "Zahlung", "Zahlung", "Stufe", "Stufe", "Farbe", "Frei", "Liste", "Mehrfach"],
            fields.Select(f => f.Name));
        Assert.Equal(
            [
                PdfiumFormKind.Text, PdfiumFormKind.Text, PdfiumFormKind.Text, PdfiumFormKind.Text, PdfiumFormKind.Text, PdfiumFormKind.Text, PdfiumFormKind.Text,
                PdfiumFormKind.CheckBox, PdfiumFormKind.CheckBox,
                PdfiumFormKind.RadioButton, PdfiumFormKind.RadioButton, PdfiumFormKind.RadioButton, PdfiumFormKind.RadioButton,
                PdfiumFormKind.ComboBox, PdfiumFormKind.ComboBox, PdfiumFormKind.ListBox, PdfiumFormKind.ListBox,
            ],
            fields.Select(f => f.Kind));

        // /Ff as written by tools/make-testpdfs.cs: radio groups carry Radio (1 << 15), Stufe also NoToggleToOff (1 << 14).
        Assert.Equal(
            [0u, 1u << 12, 1u << 24, 0u, 1u, 2u, 1u << 13, 0u, 0u, 1u << 15, 1u << 15, (1u << 15) | (1u << 14), (1u << 15) | (1u << 14), 1u << 17, (1u << 17) | (1u << 18), 0u, 1u << 21],
            fields.Select(f => f.Flags));
        Assert.All(fields, f => Assert.Null(f.SkipReason));

        var second = await ReadPageAsync(TestData.Pdf(FormsBasic), 1);
        Assert.Equal(["Zahlung", "Name", "Telefon", "Ort"], second.Select(f => f.Name));
        Assert.Equal([PdfiumFormKind.RadioButton, PdfiumFormKind.Text, PdfiumFormKind.Text, PdfiumFormKind.Text], second.Select(f => f.Kind));
    }

    [Fact]
    public async Task ValuesAndOptionsAreRead()
    {
        var fields = await ReadPageAsync(TestData.Pdf(FormsBasic), 0);
        var byName = fields.GroupBy(f => f.Name!).ToDictionary(g => g.Key, g => g.First());

        Assert.Equal("Alt", byName["Name"].Value);
        Assert.Equal("Fest", byName["Gesperrt"].Value);
        Assert.Equal(string.Empty, byName["Bemerkung"].Value);
        Assert.Equal(["Rot", "Gruen", "Blau"], byName["Farbe"].OptionLabels);
        Assert.Empty(byName["Farbe"].Selected);
        Assert.Equal(["Eins", "Zwei", "Drei"], byName["Frei"].OptionLabels);
        Assert.Equal(["Alpha", "Beta", "Gamma", "Delta"], byName["Liste"].OptionLabels);
        Assert.Empty(byName["Liste"].Selected);
        Assert.False(byName["Agb"].Checked);

        // Text fields have no options.
        Assert.Empty(byName["Name"].OptionLabels);

        // A form with chosen values: the combo's value is the export value of its option, the selection of a list
        // comes only from FPDFAnnot_IsOptionSelected (the field value would give only the first entry of /V).
        var chosen = await ReadPageAsync(WriteChosenForm(), 0);
        var combo = chosen.Single(f => f.Name == "Farbe");
        Assert.Equal("g", combo.Value);
        Assert.Equal(["Rot", "Gruen", "Blau"], combo.OptionLabels);
        Assert.Equal([1], combo.Selected);
        var list = chosen.Single(f => f.Name == "Mehrfach");
        Assert.Equal([1, 3], list.Selected);
        Assert.Null(list.Value);
        var check = chosen.Single(f => f.Name == "Ja");
        Assert.True(check.Checked);
        Assert.Equal("Ja", check.ExportValue);
    }

    [Fact]
    public async Task RadioGroupReadsAllThreeOptionsAcrossTwoPages()
    {
        var first = await ReadPageAsync(TestData.Pdf(FormsBasic), 0);
        var second = await ReadPageAsync(TestData.Pdf(FormsBasic), 1);

        var group = first.Where(f => f.Name == "Zahlung").Concat(second.Where(f => f.Name == "Zahlung")).ToList();
        Assert.Equal(["Bar", "Karte", "Rechnung"], group.Select(f => f.ExportValue));
        Assert.All(group, f =>
        {
            Assert.Equal(PdfiumFormKind.RadioButton, f.Kind);
            Assert.False(f.Checked);
            Assert.Null(f.SkipReason);
        });
        Assert.Equal(["1", "2"], first.Where(f => f.Name == "Stufe").Select(f => f.ExportValue));

        // Check boxes with unusual export values.
        Assert.Equal("Yes", first.Single(f => f.Name == "Agb").ExportValue);
        Assert.Equal("Ja", first.Single(f => f.Name == "Zustimmung").ExportValue);

        // One text field with a widget on each page: the same name and value on both.
        Assert.Equal("Alt", first.Single(f => f.Name == "Name").Value);
        Assert.Equal("Alt", second.Single(f => f.Name == "Name").Value);
        Assert.Null(first.Single(f => f.Name == "Name").ExportValue);
    }

    [Theory]
    [InlineData(FormsBasic, 0)]
    [InlineData(FormsBasic, 1)]
    [InlineData(FormsRotated, 0)]
    [InlineData(FormsRotated, 1)]
    [InlineData(FormsHostile, 0)]
    public async Task BoundsEqualTheBoundsOfTheWidgetAnnotation(string name, int page)
    {
        await using var document = await OpenFixtureAsync(name);
        var annotations = await document.GetAnnotationsAsync(page, Ct);
        var (fields, width, height) = await Pdfium.InvokeAsync(
            () =>
            {
                using var opened = PdfiumDocument.Open(Pdfium, TestData.Pdf(name), null);
                using var loaded = opened.LoadPage(page);
                return (PdfiumForms.ReadPage(loaded), loaded.Width, loaded.Height);
            },
            cancellationToken: Ct);

        Assert.Equal(annotations.Select(a => a.Index), fields.Select(f => f.Index));
        foreach (var field in fields)
        {
            var bounds = annotations.Single(a => a.Index == field.Index).Bounds;
            Assert.Equal((bounds.Left, bounds.Top, bounds.Right, bounds.Bottom), field.Rect);

            // On the displayed page (rotation and CropBox applied), never a raw /Rect.
            Assert.InRange(field.Rect.Left, 0, width);
            Assert.InRange(field.Rect.Right, field.Rect.Left, width);
            Assert.InRange(field.Rect.Top, 0, height);
            Assert.InRange(field.Rect.Bottom, field.Rect.Top, height);
        }
    }

    [Fact]
    public async Task RotatedPageGivesTheRectOfTheTurnedPage()
    {
        // Page 1: /Rotate 90 with the CropBox [20 10 380 290]; Text1 has /Rect [80 220 260 242]. Turned clockwise, the left
        // edge of the CropBox (x = 20) becomes the top and its bottom edge (y = 10) the left: left = 220 - 10 = 210,
        // right = 242 - 10 = 232; top = 80 - 20 = 60, bottom = 260 - 20 = 240.
        var fields = await ReadPageAsync(TestData.Pdf(FormsRotated), 0);
        var text = fields.Single(f => f.Name == "Text1");

        Assert.Equal(210, text.Rect.Left, 1);
        Assert.Equal(232, text.Rect.Right, 1);
        Assert.Equal(60, text.Rect.Top, 1);
        Assert.Equal(240, text.Rect.Bottom, 1);
        Assert.Equal("Eins", text.Value);
    }

    [Fact]
    public async Task MaxLengthIsReadWhenDirect()
    {
        var fields = await ReadPageAsync(TestData.Pdf(FormsBasic), 0);

        Assert.Equal(8, fields.Single(f => f.Name == "Kamm").MaxLength);
        Assert.Equal(5, fields.Single(f => f.Name == "Kurz").MaxLength);
        Assert.Null(fields.Single(f => f.Name == "Bemerkung").MaxLength);
        Assert.Null(fields.Single(f => f.Name == "Name").MaxLength);

        // An inherited /MaxLen (on the parent field) cannot be read through the header; writing finds it by reading back.
        var chosen = await ReadPageAsync(WriteChosenForm(), 0);
        Assert.Null(chosen.Single(f => f.Name == "Erbe").MaxLength);
    }

    [Fact]
    public async Task HostileFormIsReadWithinTheLimits()
    {
        var (fields, info) = await Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsHostile), null);
                using var page = document.LoadPage(0);
                return (PdfiumForms.ReadPage(page), PdfiumForms.ReadDocumentInfo(document));
            },
            cancellationToken: Ct).WaitAsync(HostileTimeout, Ct);

        // /Annots: Normal, Loop leaf, NoType, NoRect, NoName, Big, LongName, NulName; the widget without /Rect is not returned.
        Assert.Equal([0, 1, 2, 4, 5, 6, 7], fields.Select(f => f.Index));
        Assert.All(fields, f =>
        {
            Assert.True(f.OptionLabels.Length <= PdfiumForms.MaxOptionsPerField);
            if (f.Name is { } name)
            {
                Assert.True(name.Length <= PdfiumForms.MaxNameLength);
                Assert.DoesNotContain('\0', name);
            }
            else
            {
                Assert.NotNull(f.SkipReason);
            }
        });

        var normal = fields.Single(f => f.Index == 0);
        Assert.Equal("Normal", normal.Name);
        Assert.Null(normal.SkipReason);

        // The leaf's parent is Loop itself; the cycle runs through the inner node, which PDFium stops: the leaf reads as a
        // plain text field. A widget without /FT is no field for PDFium (type -1).
        var loop = fields.Single(f => f.Index == 1);
        Assert.Equal("Loop", loop.Name);
        Assert.Equal(PdfiumFormKind.Text, loop.Kind);
        var noType = fields.Single(f => f.Index == 2);
        Assert.Equal(PdfiumFormKind.Unknown, noType.Kind);
        Assert.NotNull(noType.SkipReason);

        var noName = fields.Single(f => f.Index == 4);
        Assert.Null(noName.Name);
        Assert.NotNull(noName.SkipReason);

        // 20,000 options: more than MaxOptionsPerField, so not supported and no option read.
        var big = fields.Single(f => f.Index == 5);
        Assert.Equal(PdfiumFormKind.ComboBox, big.Kind);
        Assert.NotNull(big.SkipReason);
        Assert.Empty(big.OptionLabels);
        Assert.Empty(big.Selected);

        // A name of 2,000 characters is longer than MaxNameLength.
        var longName = fields.Single(f => f.Index == 6);
        Assert.Null(longName.Name);
        Assert.NotNull(longName.SkipReason);

        // A NUL in the name: either PDFium ends the name there or the field is not supported; never a NUL.
        var nul = fields.Single(f => f.Index == 7);
        Assert.True(nul.SkipReason is not null || nul.Name is { Length: > 0 });

        Assert.True(info.HasFields);
        Assert.Equal(7, info.FieldCount);
        Assert.Equal([0], info.PagesWithFields);
    }

    [Fact]
    public async Task DocumentInfoDetectsCalculatedFormatXfaSignatureAndButtons()
    {
        var calc = await ReadInfoAsync(FormsCalc);
        Assert.True(calc.HasFields);
        Assert.True(calc.Calculated);
        Assert.True(calc.FormatScripts);
        Assert.False(calc.Xfa);
        Assert.False(calc.SignatureField);
        Assert.False(calc.Buttons);
        Assert.Equal(5, calc.FieldCount);
        Assert.Equal([0], calc.PagesWithFields);

        var xfa = await ReadInfoAsync(FormsXfa);
        Assert.True(xfa.HasFields);
        Assert.True(xfa.Xfa);
        Assert.False(xfa.Calculated);
        Assert.False(xfa.FormatScripts);
        Assert.Equal(1, xfa.FieldCount);

        var signed = await ReadInfoAsync(FormsSigned);
        Assert.True(signed.SignatureField);
        Assert.True(signed.Buttons);
        Assert.False(signed.Calculated);
        Assert.False(signed.FormatScripts);
        Assert.False(signed.Xfa);
        Assert.Equal(4, signed.FieldCount);

        // Check boxes and radio buttons are no push buttons; the basic form has none of the hints.
        var basic = await ReadInfoAsync(FormsBasic);
        Assert.True(basic.HasFields);
        Assert.False(basic.Calculated || basic.FormatScripts || basic.Xfa || basic.SignatureField || basic.Buttons);
        Assert.Equal(21, basic.FieldCount);
        Assert.Equal([0, 1], basic.PagesWithFields);

        // The XFA stub's text field reads like any AcroForm field.
        var xfaFields = await ReadPageAsync(TestData.Pdf(FormsXfa), 0);
        Assert.Equal("Name", Assert.Single(xfaFields).Name);
        Assert.Equal(PdfiumFormKind.Text, xfaFields[0].Kind);
    }

    [Fact]
    public async Task DocumentWithoutFormsHasNoInfo()
    {
        var (info, fields) = await Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, TestData.Pdf("text-basic.pdf"), null);
                using var page = document.LoadPage(0);
                return (PdfiumForms.ReadDocumentInfo(document), PdfiumForms.ReadPage(page));
            },
            cancellationToken: Ct);

        Assert.False(info.HasFields);
        Assert.False(info.Calculated || info.FormatScripts || info.Xfa || info.SignatureField || info.Buttons);
        Assert.Equal(0, info.FieldCount);
        Assert.Empty(info.PagesWithFields);
        Assert.Empty(fields);
    }

    [Fact]
    public async Task ReadingOffThePdfiumThreadThrows()
    {
        var (document, page) = await Pdfium.InvokeAsync(
            () =>
            {
                var opened = PdfiumDocument.Open(Pdfium, TestData.Pdf(FormsBasic), null);
                return (opened, opened.LoadPage(0));
            },
            cancellationToken: Ct);
        try
        {
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumForms.ReadPage(page));
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumForms.ReadDocumentInfo(document));
        }
        finally
        {
            page.Dispose();
            document.Dispose();
        }
    }

    /// <summary>Pdfium cannot reference Core, so its copy of the value limit is checked here.</summary>
    [Fact]
    public void MaxValueLengthEqualsTheCoreRule() => Assert.Equal(FormValueRules.MaxValueLength, PdfiumForms.MaxValueLength);

    /// <summary>The field flag constants have the bits of the Core traits, so task 6 can map the raw flags directly.</summary>
    [Fact]
    public void FieldFlagConstantsHaveTheBitsOfTheCoreTraits()
    {
        Assert.Equal((uint)FormFieldTraits.ReadOnly, (uint)FpdfConstants.FormFlagReadOnly);
        Assert.Equal((uint)FormFieldTraits.Required, (uint)FpdfConstants.FormFlagRequired);
        Assert.Equal((uint)FormFieldTraits.NoExport, (uint)FpdfConstants.FormFlagNoExport);
        Assert.Equal((uint)FormFieldTraits.Multiline, (uint)FpdfConstants.FormFlagTextMultiline);
        Assert.Equal((uint)FormFieldTraits.Password, (uint)FpdfConstants.FormFlagTextPassword);
        Assert.Equal((uint)FormFieldTraits.NoToggleToOff, (uint)FpdfConstants.FormFlagNoToggleToOff);
        Assert.Equal((uint)FormFieldTraits.Combo, (uint)FpdfConstants.FormFlagChoiceCombo);
        Assert.Equal((uint)FormFieldTraits.Edit, (uint)FpdfConstants.FormFlagChoiceEdit);
        Assert.Equal((uint)FormFieldTraits.FileSelect, (uint)FpdfConstants.FormFlagFileSelect);
        Assert.Equal((uint)FormFieldTraits.MultiSelect, (uint)FpdfConstants.FormFlagChoiceMultiSelect);
        Assert.Equal((uint)FormFieldTraits.DoNotSpellCheck, (uint)FpdfConstants.FormFlagDoNotSpellCheck);
        Assert.Equal((uint)FormFieldTraits.DoNotScroll, (uint)FpdfConstants.FormFlagDoNotScroll);
        Assert.Equal((uint)FormFieldTraits.Comb, (uint)FpdfConstants.FormFlagComb);
        Assert.Equal((uint)FormFieldTraits.RichText, (uint)FpdfConstants.FormFlagRichText);
    }

    private static Task<PdfiumFormField[]> ReadPageAsync(string path, int pageIndex) =>
        Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, path, null);
                using var page = document.LoadPage(pageIndex);
                return PdfiumForms.ReadPage(page);
            },
            cancellationToken: Ct);

    private static Task<PdfiumFormDocumentInfo> ReadInfoAsync(string name) =>
        Pdfium.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(Pdfium, TestData.Pdf(name), null);
                return PdfiumForms.ReadDocumentInfo(document);
            },
            cancellationToken: Ct);

    /// <summary>
    /// A form with chosen values: a combo with <c>/Opt</c> pairs and <c>/V (g)</c>, a multi-select list with
    /// <c>/V [(Beta) (Delta)]</c> and <c>/I [1 3]</c>, a checked check box with the export value "Ja", and a text field whose
    /// <c>/MaxLen</c> is inherited from its parent.
    /// </summary>
    private string WriteChosenForm() =>
        WritePdf(folder, "chosen.pdf", [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R 9 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R 10 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 240 200 262] /P 3 0 R /F 4 /FT /Ch /Ff 131072 /T (Farbe) /Opt [[(r) (Rot)] [(g) (Gruen)] [(b) (Blau)]] /V (g) /DA (/Helv 10 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 150 200 230] /P 3 0 R /F 4 /FT /Ch /Ff 2097152 /T (Mehrfach) /Opt [(Alpha) (Beta) (Gamma) (Delta)] /V [(Beta) (Delta)] /I [1 3] /DA (/Helv 10 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 100 38 118] /P 3 0 R /F 4 /FT /Btn /T (Ja) /V /Ja /AS /Ja /AP << /N << /Ja 8 0 R /Off 8 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 18 18] /Length 0 >>\nstream\n\nendstream",
            "<< /FT /Tx /T (Erbe) /MaxLen 4 /DA (/Helv 10 Tf 0 g) /Kids [10 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 50 200 72] /P 3 0 R /F 4 /Parent 9 0 R >>",
        ]);
}
