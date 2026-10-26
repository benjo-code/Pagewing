using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Writing form values through PDFium's form engine (milestone 11, task 7): every value is written in one short job, read back
/// and kept only when the engine wrote exactly what was asked for; it survives saving (through <see cref="DocumentSaver"/>, as
/// the app saves) and reopening. The widgets are drawn with <c>FPDF_FFLDraw</c> on a white bitmap (the page render draws no
/// widget before task 9) and compared with the 8 x 8 block means of the render regression's tolerant mode.
/// </summary>
public sealed partial class FormRoundTripTests : IDisposable
{
    private const double Scale = 2;

    /// <summary>The largest channel difference of a block mean that still counts as the same picture (tolerant mode).</summary>
    private const double MaxBlockDelta = 40;

    private const string Greeting = "Grüße aus Köln, 5 €";

    private readonly TempFolder folder = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task TextValueSurvivesSaveAndReload()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            var change = await document.SetFormValueAsync("Telefon", FormFieldState.OfText(Greeting), Ct);

            Assert.Equal(FormFieldState.OfText(string.Empty), change.Before);
            Assert.Equal(FormFieldState.OfText(Greeting), change.After);
            Assert.Equal([1], change.Pages.Order());
            Assert.Equal(FormFieldState.OfText(Greeting), (await FieldAsync(document, "Telefon")).State);
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            var field = await FieldAsync(reopened, "Telefon");
            Assert.Equal(FormFieldState.OfText(Greeting), field.State);
            Assert.Equal(FormLock.None, field.Lock);

            // Still fillable after the reload.
            var again = await reopened.SetFormValueAsync("Telefon", FormFieldState.OfText("Neu"), Ct);
            Assert.Equal(FormFieldState.OfText(Greeting), again.Before);
            Assert.Equal(FormFieldState.OfText("Neu"), again.After);
        }
    }

    [Fact]
    public async Task AppearanceShowsTheValue()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        Rendered empty, written;
        PageRect bounds;
        PdfDocument reopened;
        await using (document)
        {
            bounds = (await FieldAsync(document, "Ort")).Bounds;
            empty = await RenderWidgetsAsync(document, 1);
            await document.SetFormValueAsync("Ort", FormFieldState.OfText(Greeting), Ct);
            written = await RenderWidgetsAsync(document, 1);
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            var saved = await RenderWidgetsAsync(reopened, 1);
            Assert.True(BlockDelta(empty, bounds, written, bounds) > MaxBlockDelta, "The written value does not show in the field.");
            AssertSamePicture(written, bounds, saved, bounds);
        }
    }

    [Fact]
    public async Task CheckBoxAndRadioGroupSurvive()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            var agree = await document.SetFormValueAsync("Agb", FormFieldState.OfChecked(true), Ct);
            Assert.Equal(FormFieldState.OfChecked(false), agree.Before);
            Assert.Equal(FormFieldState.OfChecked(true), agree.After);
            Assert.Equal([0], agree.Pages.Order());

            var payment = await document.SetFormValueAsync("Zahlung", FormFieldState.OfText("Karte"), Ct);
            Assert.Equal(FormFieldState.OfText(null), payment.Before);
            Assert.Equal(FormFieldState.OfText("Karte"), payment.After);
            Assert.Equal([0, 1], payment.Pages.Order());

            var level = await document.SetFormValueAsync("Stufe", FormFieldState.OfText("1"), Ct);
            Assert.Equal(FormFieldState.OfText("1"), level.After);
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            Assert.Equal(FormFieldState.OfChecked(true), (await FieldAsync(reopened, "Agb")).State);
            Assert.Equal(FormFieldState.OfChecked(false), (await FieldAsync(reopened, "Zustimmung")).State);
            Assert.Equal(FormFieldState.OfText("Karte"), (await FieldAsync(reopened, "Zahlung")).State);
            Assert.Equal(FormFieldState.OfText("1"), (await FieldAsync(reopened, "Stufe")).State);
            Assert.Equal("-x-", await CheckedWidgetsAsync(reopened, "Zahlung"));
            Assert.Equal("x-", await CheckedWidgetsAsync(reopened, "Stufe"));

            // A check box goes off by a click as well.
            var off = await reopened.SetFormValueAsync("Agb", FormFieldState.OfChecked(false), Ct);
            Assert.Equal(FormFieldState.OfChecked(true), off.Before);
            Assert.Equal(FormFieldState.OfChecked(false), off.After);
        }
    }

    [Fact]
    public async Task RadioGroupAcrossTwoPagesKeepsOneChoice()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            await document.SetFormValueAsync("Zahlung", FormFieldState.OfText("Bar"), Ct);
            Assert.Equal("x--", await CheckedWidgetsAsync(document, "Zahlung"));

            var change = await document.SetFormValueAsync("Zahlung", FormFieldState.OfText("Rechnung"), Ct);

            Assert.Equal(FormFieldState.OfText("Bar"), change.Before);
            Assert.Equal(FormFieldState.OfText("Rechnung"), change.After);
            Assert.Equal([0, 1], change.Pages.Order());
            Assert.Equal("--x", await CheckedWidgetsAsync(document, "Zahlung"));
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            Assert.Equal(FormFieldState.OfText("Rechnung"), (await FieldAsync(reopened, "Zahlung")).State);
            Assert.Equal("--x", await CheckedWidgetsAsync(reopened, "Zahlung"));
            Assert.All(await WidgetsAsync(reopened, "Zahlung"), f => Assert.Equal(FormFieldState.OfText("Rechnung"), f.State));
        }
    }

    /// <summary>
    /// The second widget of <c>Name</c> (page 2) is drawn after the first write, so it has an appearance of its own; the second
    /// write must renew it although the value is written through the first widget.
    /// </summary>
    [Fact]
    public async Task FieldWithTwoWidgetsShowsTheValueOnBothPages()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        Rendered first;
        PdfDocument reopened;
        await using (document)
        {
            var change = await document.SetFormValueAsync("Name", FormFieldState.OfText("Erste"), Ct);
            Assert.Equal([0, 1], change.Pages.Order());
            first = await RenderWidgetsAsync(document, 1);
            await document.SetFormValueAsync("Name", FormFieldState.OfText("Zweite Fassung"), Ct);
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            var widgets = await WidgetsAsync(reopened, "Name");
            Assert.Equal(2, widgets.Count);
            Assert.All(widgets, f => Assert.Equal(FormFieldState.OfText("Zweite Fassung"), f.State));
            var pageOne = await RenderWidgetsAsync(reopened, 0);
            var pageTwo = await RenderWidgetsAsync(reopened, 1);

            // Both widgets have the same size: both show the same text.
            AssertSamePicture(pageOne, widgets[0].Bounds, pageTwo, widgets[1].Bounds);
            Assert.True(BlockDelta(first, widgets[1].Bounds, pageTwo, widgets[1].Bounds) > MaxBlockDelta, "Page 2 still shows the first value.");
        }
    }

    [Fact]
    public async Task ComboAndEditableComboSurvive()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            var colour = await document.SetFormValueAsync("Farbe", FormFieldState.OfSelection([1]), Ct);
            Assert.Equal(FormFieldState.OfSelection([]), colour.Before);

            // The engine keeps the export value of the option in /V ("g" for "Gruen").
            Assert.Equal(new FormFieldState { Text = "g", SelectedOptions = [1] }, colour.After);

            var free = await document.SetFormValueAsync("Frei", FormFieldState.OfText("Eigener Wert"), Ct);
            Assert.Equal("Eigener Wert", free.After.Text);
            Assert.Empty(free.After.SelectedOptions);

            await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Farbe", FormFieldState.OfSelection([0, 1]), Ct));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.SetFormValueAsync("Farbe", FormFieldState.OfSelection([3]), Ct));
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            Assert.Equal(new FormFieldState { Text = "g", SelectedOptions = [1] }, (await FieldAsync(reopened, "Farbe")).State);
            Assert.Equal("Eigener Wert", (await FieldAsync(reopened, "Frei")).State.Text);

            // The editable combo also takes one of its options.
            var option = await reopened.SetFormValueAsync("Frei", FormFieldState.OfSelection([2]), Ct);
            Assert.Equal([2], option.After.SelectedOptions);
        }
    }

    [Fact]
    public async Task MultiSelectListSurvives()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            var many = await document.SetFormValueAsync("Mehrfach", FormFieldState.OfSelection([2, 0]), Ct);
            Assert.Equal(FormFieldState.OfSelection([]), many.Before);
            Assert.Equal(FormFieldState.OfSelection([0, 2]), many.After);

            var single = await document.SetFormValueAsync("Liste", FormFieldState.OfSelection([3]), Ct);
            Assert.Equal(FormFieldState.OfSelection([3]), single.After);

            // A list without multiple selection takes one option only.
            await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Liste", FormFieldState.OfSelection([0, 1]), Ct));
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            Assert.Equal(FormFieldState.OfSelection([0, 2]), (await FieldAsync(reopened, "Mehrfach")).State);
            Assert.Equal(FormFieldState.OfSelection([3]), (await FieldAsync(reopened, "Liste")).State);

            // The options not wanted any more are deselected.
            var fewer = await reopened.SetFormValueAsync("Mehrfach", FormFieldState.OfSelection([1]), Ct);
            Assert.Equal(FormFieldState.OfSelection([1]), fewer.After);
            var none = await reopened.SetFormValueAsync("Mehrfach", FormFieldState.OfSelection([]), Ct);
            Assert.Equal(FormFieldState.OfSelection([]), none.After);
        }
    }

    [Fact]
    public async Task MultilineBreakSurvives()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            var change = await document.SetFormValueAsync("Bemerkung", FormFieldState.OfText("Zeile eins\nZeile zwei"), Ct);
            Assert.Equal(FormFieldState.OfText("Zeile eins\r\nZeile zwei"), change.After);

            // The same text with another line break is the same value.
            var same = await document.SetFormValueAsync("Bemerkung", FormFieldState.OfText("Zeile eins\rZeile zwei"), Ct);
            Assert.Equal(same.Before, same.After);

            // A line break in a single-line field is refused.
            await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Telefon", FormFieldState.OfText("a\nb"), Ct));
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            Assert.Equal(FormFieldState.OfText("Zeile eins\r\nZeile zwei"), (await FieldAsync(reopened, "Bemerkung")).State);
        }
    }

    [Fact]
    public async Task CombFieldKeepsEightCells()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        PdfDocument reopened;
        await using (document)
        {
            var change = await document.SetFormValueAsync("Kamm", FormFieldState.OfText("12345678"), Ct);
            Assert.Equal(FormFieldState.OfText("12345678"), change.After);
            await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Kamm", FormFieldState.OfText("123456789"), Ct));
            Assert.Equal(FormFieldState.OfText("12345678"), (await FieldAsync(document, "Kamm")).State);
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            var field = await FieldAsync(reopened, "Kamm");
            Assert.Equal(FormFieldState.OfText("12345678"), field.State);
            Assert.Equal(8, field.MaxLength);
        }
    }

    [Fact]
    public async Task RotatedPageAndCropBox()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsRotated);
        Rendered writtenOne, writtenTwo;
        PdfDocument reopened;
        await using (document)
        {
            foreach (var (text, check, combo) in new[] { ("Text1", "Check1", "Combo1"), ("Text2", "Check2", "Combo2") })
            {
                await document.SetFormValueAsync(text, FormFieldState.OfText(Greeting), Ct);
                await document.SetFormValueAsync(check, FormFieldState.OfChecked(true), Ct);
                await document.SetFormValueAsync(combo, FormFieldState.OfSelection([2]), Ct);
            }

            writtenOne = await RenderWidgetsAsync(document, 0);
            writtenTwo = await RenderWidgetsAsync(document, 1);
            reopened = await SaveAsync(engine, document, path, stamp);
        }

        await using (reopened)
        {
            foreach (var (page, rendered) in new[] { (0, writtenOne), (1, writtenTwo) })
            {
                var fields = await reopened.GetFormFieldsAsync(page, Ct);
                Assert.Equal(FormFieldState.OfText(Greeting), fields[0].State);
                Assert.Equal(FormFieldState.OfChecked(true), fields[1].State);
                Assert.Equal([2], fields[2].State.SelectedOptions);
                var saved = await RenderWidgetsAsync(reopened, page);
                foreach (var field in fields)
                {
                    AssertSamePicture(rendered, field.Bounds, saved, field.Bounds);
                }
            }
        }
    }

    [Fact]
    public async Task ExistingBytesStayThePrefix()
    {
        var (engine, path, stamp, document) = await OpenCopyAsync(FormsBasic);
        var before = await File.ReadAllBytesAsync(path, Ct);
        await using (document)
        {
            await document.SetFormValueAsync("Name", FormFieldState.OfText(Greeting), Ct);
            await document.SetFormValueAsync("Agb", FormFieldState.OfChecked(true), Ct);
            await document.SetFormValueAsync("Zahlung", FormFieldState.OfText("Rechnung"), Ct);
            await document.SetFormValueAsync("Mehrfach", FormFieldState.OfSelection([1, 3]), Ct);
            await using var reopened = await SaveAsync(engine, document, path, stamp);
            var after = await File.ReadAllBytesAsync(path, Ct);
            Assert.True(after.Length > before.Length);
            Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before), "The original bytes were changed.");
            Assert.Equal(FormFieldState.OfText(Greeting), (await FieldAsync(reopened, "Name")).State);
        }
    }

    [Fact]
    public async Task ReadOnlyFieldIsRefused()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Gesperrt", FormFieldState.OfText("Neu"), Ct));
        Assert.Contains("read-only", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(FormFieldState.OfText("Fest"), (await FieldAsync(document, "Gesperrt")).State);

        // Unsupported fields are refused the same way: a signature and a push button.
        await using var signed = await OpenFixtureAsync(FormsSigned);
        await Assert.ThrowsAsync<InvalidOperationException>(() => signed.SetFormValueAsync("Signature2", FormFieldState.OfText("x"), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => signed.SetFormValueAsync("Senden", FormFieldState.OfChecked(true), Ct));
    }

    [Fact]
    public async Task EmojiIsRefusedAndTheFieldStaysAsItWas()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);

        var emoji = await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Name", FormFieldState.OfText("Hallo 😀"), Ct));
        Assert.Contains("😀", emoji.Message, StringComparison.Ordinal);

        // Typographic quotes, dash and ellipsis are outside the character set as well (Docs/DECISIONS.md, spike point 8).
        await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Name", FormFieldState.OfText("„Zitat“ – und …"), Ct));
        Assert.All(await WidgetsAsync(document, "Name"), f => Assert.Equal(FormFieldState.OfText("Alt"), f.State));

        // The supported set is written as it is.
        var accepted = await document.SetFormValueAsync("Name", FormFieldState.OfText(Greeting), Ct);
        Assert.Equal(FormFieldState.OfText(Greeting), accepted.After);
    }

    [Fact]
    public async Task TextAboveMaxLengthIsRefusedOrRevertedByTheReadBack()
    {
        // A direct /MaxLen is checked before writing.
        await using (var document = await OpenFixtureAsync(FormsBasic))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Kurz", FormFieldState.OfText("123456"), Ct));
            Assert.Equal(FormFieldState.OfText(string.Empty), (await FieldAsync(document, "Kurz")).State);
            Assert.Equal(FormFieldState.OfText("12345"), (await document.SetFormValueAsync("Kurz", FormFieldState.OfText("12345"), Ct)).After);
        }

        // An inherited /MaxLen cannot be read: the engine shortens the text, the read-back notices and writes the old value back.
        await using (var inherited = await OpenAsync(WriteInheritedMaxLengthForm()))
        {
            Assert.Null((await FieldAsync(inherited, "Erbe")).MaxLength);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => inherited.SetFormValueAsync("Erbe", FormFieldState.OfText("1234567"), Ct));
            Assert.Contains("not accepted", error.Message, StringComparison.Ordinal);
            Assert.Contains("different text", error.Message, StringComparison.Ordinal);
            Assert.Equal(FormFieldState.OfText("abc"), (await FieldAsync(inherited, "Erbe")).State);
            Assert.Equal(FormFieldState.OfText("12345"), (await inherited.SetFormValueAsync("Erbe", FormFieldState.OfText("12345"), Ct)).After);
        }
    }

    /// <summary>
    /// A text the engine draws with a substitute font (a glyph the field's font lacks) keeps the full text in <c>/V</c>, so only
    /// the fonts of the new appearance show it; the write is undone. The character check is skipped to reach the engine. Windows
    /// only: which system font PDFium picks (or whether it drops the glyph) depends on the fonts of the machine.
    /// </summary>
    [Fact]
    public async Task SubstituteFontInTheAppearanceIsRevertedByTheReadBack()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The substitute fonts are only known on Windows (Arial, Tahoma).");
        await using var document = await OpenFixtureAsync(FormsBasic);
        await document.SetFormValueAsync("Telefon", FormFieldState.OfText("vorher"), Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => document.SetFormValueAsync("Telefon", FormFieldState.OfText("Šárka"), checkCharacters: false, Ct));

        Assert.Contains("not accepted", error.Message, StringComparison.Ordinal);
        Assert.Contains("font", error.Message, StringComparison.Ordinal);
        Assert.Equal(FormFieldState.OfText("vorher"), (await FieldAsync(document, "Telefon")).State);

        // Name has no /DA of its own (inherited from the field): then the appearance may use one font only.
        var inherited = await Assert.ThrowsAsync<InvalidOperationException>(
            () => document.SetFormValueAsync("Name", FormFieldState.OfText("Šárka"), checkCharacters: false, Ct));
        Assert.Contains("font", inherited.Message, StringComparison.Ordinal);
        Assert.All(await WidgetsAsync(document, "Name"), f => Assert.Equal(FormFieldState.OfText("Alt"), f.State));
    }

    [Fact]
    public async Task SettingTheSameValueAgainChangesNothing()
    {
        var plain = Copy(FormsBasic, "plain.pdf");
        var touched = Copy(FormsBasic, "touched.pdf");
        string expected;
        await using (var document = await OpenAsync(plain))
        {
            _ = await document.GetFormFieldsAsync(0, Ct);
            _ = await document.GetFormFieldsAsync(1, Ct);
            expected = await SaveIncrementalAsync(document);
            Assert.Contains("/ID[<>]", expected, StringComparison.Ordinal);
        }

        await using (var document = await OpenAsync(touched))
        {
            _ = await document.GetFormFieldsAsync(0, Ct);
            _ = await document.GetFormFieldsAsync(1, Ct);
            foreach (var (name, state) in new[]
            {
                ("Name", FormFieldState.OfText("Alt")),
                ("Agb", FormFieldState.OfChecked(false)),
                ("Liste", FormFieldState.OfSelection([])),
                ("Farbe", FormFieldState.OfSelection([])),
                ("Bemerkung", FormFieldState.OfText(string.Empty)),
                ("Bemerkung", FormFieldState.OfText(null)),
            })
            {
                var change = await document.SetFormValueAsync(name, state, Ct);
                Assert.Equal(change.Before, change.After);
                Assert.NotEmpty(change.Pages);
            }

            Assert.Equal([0, 1], (await document.SetFormValueAsync("Name", FormFieldState.OfText("Alt"), Ct)).Pages.Order());

            // Nothing was written: the saved bytes are those of a document that was only read.
            Assert.Equal(expected, await SaveIncrementalAsync(document));
        }
    }

    [Fact]
    public async Task UnknownFieldThrowsFormFieldNotFound()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);
        var error = await Assert.ThrowsAsync<FormFieldNotFoundException>(() => document.SetFormValueAsync("Gibt es nicht", FormFieldState.OfText("x"), Ct));
        Assert.Contains("Gibt es nicht", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync(string.Empty, FormFieldState.OfText("x"), Ct));

        await using var plain = await OpenFixtureAsync("text-basic.pdf");
        await Assert.ThrowsAsync<FormFieldNotFoundException>(() => plain.SetFormValueAsync("Name", FormFieldState.OfText("x"), Ct));
        Assert.Equal(0, plain.FormEnvironmentsCreated);
    }

    [Fact]
    public async Task RadioToNoSelectionIsAlwaysRefused()
    {
        await using var document = await OpenFixtureAsync(FormsBasic);

        // Without NoToggleToOff (Zahlung) as with it (Stufe): the engine never undoes a choice.
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Stufe", FormFieldState.OfText(null), Ct));
        await document.SetFormValueAsync("Zahlung", FormFieldState.OfText("Bar"), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Zahlung", FormFieldState.OfText(null), Ct));
        Assert.Equal(FormFieldState.OfText("Bar"), (await FieldAsync(document, "Zahlung")).State);

        // An export value no button has is no state of the group.
        await Assert.ThrowsAsync<ArgumentException>(() => document.SetFormValueAsync("Zahlung", FormFieldState.OfText("Scheck"), Ct));
    }

    [Fact]
    public async Task HostileComboWithManyOptionsIsNeverFocused()
    {
        await using var document = await OpenFixtureAsync(FormsHostile);
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.SetFormValueAsync("Big", FormFieldState.OfSelection([1]), Ct));

        // Focusing it would take about 7 s (Docs/DECISIONS.md, spike point 12).
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"Took {watch.Elapsed}.");
        var normal = await document.SetFormValueAsync("Normal", FormFieldState.OfText("geht"), Ct);
        Assert.Equal(FormFieldState.OfText("geht"), normal.After);
    }

    private static async Task<FormField> FieldAsync(PdfDocument document, string name) => (await WidgetsAsync(document, name))[0];

    /// <summary>All widgets of a field, page by page in <c>/Annots</c> order.</summary>
    private static async Task<IReadOnlyList<FormField>> WidgetsAsync(PdfDocument document, string name)
    {
        var result = new List<FormField>();
        for (var page = 0; page < document.PageCount; page++)
        {
            result.AddRange((await document.GetFormFieldsAsync(page, Ct)).Where(f => f.Key.FieldName == name));
        }

        Assert.NotEmpty(result);
        return result;
    }

    /// <summary>Whether each widget of a field is checked ("x") or not ("-"), read per widget through PDFium.</summary>
    private static async Task<string> CheckedWidgetsAsync(PdfDocument document, string name)
    {
        var native = PdfiumOf(document);
        var pages = document.PageCount;
        return await native.Thread.InvokeAsync(
            () =>
            {
                var result = new List<bool>();
                for (var index = 0; index < pages; index++)
                {
                    using var page = native.LoadPage(index);
                    result.AddRange(PdfiumForms.ReadPage(page).Where(w => w.Name == name).Select(w => w.Checked));
                }

                return string.Concat(result.Select(c => c ? "x" : "-"));
            },
            cancellationToken: Ct);
    }

    private static PdfiumDocument PdfiumOf(PdfDocument document) =>
        (PdfiumDocument)typeof(PdfDocument).GetField("document", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(document)!;

    /// <summary>Draws only the widgets of a page (<c>FPDF_FFLDraw</c>) on white, from a page loaded for this draw alone.</summary>
    private static async Task<Rendered> RenderWidgetsAsync(PdfDocument document, int pageIndex)
    {
        var native = PdfiumOf(document);
        return await native.Thread.InvokeAsync(
            () =>
            {
                using var page = native.LoadPage(pageIndex);
                var (width, height) = PdfiumRenderWork.PagePixelSize(page.Width, page.Height, Scale);
                var pixels = new byte[width * height * 4];
                pixels.AsSpan().Fill(0xFF);
                DrawWidgets(native.FormEnvironment!.Handle, page, pixels, width, height);
                return new Rendered(pixels, width, height);
            },
            cancellationToken: Ct);
    }

    private static unsafe void DrawWidgets(nint form, PdfiumPage page, byte[] pixels, int width, int height)
    {
        fixed (byte* buffer = pixels)
        {
            var bitmap = Fpdf.FPDFBitmap_CreateEx(width, height, FpdfConstants.BitmapBgrx, buffer, width * 4);
            Assert.NotEqual(0, bitmap);
            try
            {
                Fpdf.FPDF_FFLDraw(form, bitmap, page.Handle.DangerousGetHandle(), 0, 0, width, height, 0, FpdfConstants.RenderAnnot);
            }
            finally
            {
                Fpdf.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    private static void AssertSamePicture(Rendered expected, PageRect expectedRect, Rendered actual, PageRect actualRect)
    {
        var delta = BlockDelta(expected, expectedRect, actual, actualRect);
        Assert.True(delta <= MaxBlockDelta, $"The field pictures differ: largest block delta {delta:0.#} at {actualRect}.");
    }

    /// <summary>
    /// The largest difference of a channel mean between the 8 x 8 blocks of two field rectangles of the same size (smaller blocks
    /// at the right and bottom edge), as the tolerant mode of the render regression compares.
    /// </summary>
    private static double BlockDelta(Rendered a, PageRect rectA, Rendered b, PageRect rectB)
    {
        var (ax, ay, width, height) = PixelsOf(a, rectA);
        var (bx, by, widthB, heightB) = PixelsOf(b, rectB);
        Assert.Equal((width, height), (widthB, heightB));
        double max = 0;
        for (var y = 0; y < height; y += 8)
        {
            for (var x = 0; x < width; x += 8)
            {
                var w = Math.Min(8, width - x);
                var h = Math.Min(8, height - y);
                for (var channel = 0; channel < 3; channel++)
                {
                    var delta = Math.Abs(Mean(a, ax + x, ay + y, w, h, channel) - Mean(b, bx + x, by + y, w, h, channel));
                    max = Math.Max(max, delta);
                }
            }
        }

        return max;
    }

    private static (int X, int Y, int Width, int Height) PixelsOf(Rendered image, PageRect rect)
    {
        var x0 = Math.Clamp((int)Math.Round(rect.Left * Scale), 0, image.Width);
        var y0 = Math.Clamp((int)Math.Round(rect.Top * Scale), 0, image.Height);
        var x1 = Math.Clamp((int)Math.Round(rect.Right * Scale), 0, image.Width);
        var y1 = Math.Clamp((int)Math.Round(rect.Bottom * Scale), 0, image.Height);
        Assert.True(x1 > x0 && y1 > y0, $"The rectangle {rect} has no pixels.");
        return (x0, y0, x1 - x0, y1 - y0);
    }

    private static double Mean(Rendered image, int x0, int y0, int width, int height, int channel)
    {
        double sum = 0;
        for (var y = y0; y < y0 + height; y++)
        {
            for (var x = x0; x < x0 + width; x++)
            {
                sum += image.Pixels[(((y * image.Width) + x) * 4) + channel];
            }
        }

        return sum / (width * height);
    }

    private static async Task<PdfDocument> SaveAsync(PdfEngine engine, PdfDocument document, string path, FileStamp? stamp)
    {
        var counts = new Dictionary<int, int>();
        for (var page = 0; page < document.PageCount; page++)
        {
            counts[page] = (await document.GetAnnotationsAsync(page, Ct)).Count;
        }

        var request = new SaveRequest(path, path, stamp, null, document.PageCount, counts, OverwriteChangedOriginal: false);
        return (await DocumentSaver.SaveAsync(document, engine, request, cancellationToken: Ct)).Reopened;
    }

    /// <summary>
    /// The incremental save as text, with the file identifier blanked: PDFium writes a new random <c>/ID</c> on every save. The
    /// increment is not empty even for a document that was only read: loading a page with the form environment gives every
    /// widget without an appearance one.
    /// </summary>
    private static async Task<string> SaveIncrementalAsync(PdfDocument document)
    {
        using var stream = new MemoryStream();
        await document.SaveIncrementalAsync(stream, Ct);
        return FileIdentifier().Replace(Encoding.Latin1.GetString(stream.ToArray()), "/ID[<>]");
    }

    [GeneratedRegex(@"/ID\s*\[\s*<[0-9A-Fa-f]*>\s*<[0-9A-Fa-f]*>\s*\]")]
    private static partial Regex FileIdentifier();

    private async Task<(PdfEngine Engine, string Path, FileStamp? Stamp, PdfDocument Document)> OpenCopyAsync(string name)
    {
        var path = Copy(name, name);
        var engine = new PdfEngine();
        var document = await engine.OpenAsync(path, cancellationToken: Ct);
        return (engine, path, FileStamp.Of(path), document);
    }

    private string Copy(string fixture, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>A text field whose <c>/MaxLen 5</c> sits on the parent field, so the widget cannot report it; value <c>abc</c>.</summary>
    private string WriteInheritedMaxLengthForm() => WritePdf(
        folder,
        "inherited-maxlen.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Annots [5 0 R] >>",
            "<< /FT /Tx /T (Erbe) /MaxLen 5 /DA (/Helv 10 Tf 0 g) /V (abc) /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 100 280 122] /P 3 0 R /Parent 4 0 R /F 4 /MK << /BC [0 0 0] /BG [1 1 1] >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ]);

    /// <summary>A BGRx picture of the widgets of a page at <see cref="Scale"/>.</summary>
    private sealed record Rendered(byte[] Pixels, int Width, int Height);
}
