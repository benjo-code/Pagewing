using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The form samples of milestone 11 for the check in other viewers (<c>packaging/README.md</c>, "Formulare im Fremdviewer prüfen").</summary>
public sealed partial class InteropSampleTests
{
    /// <summary>
    /// The form samples: each is a fixture of <c>testdata/pdfs</c> filled through <see cref="AnnotationEditor"/> (the engine writes
    /// <c>/V</c> and <c>/AP</c>) and saved through the editor and <c>DocumentSaver</c>, as the app does it.
    /// </summary>
    private static IReadOnlyList<FormSample> FormSamples { get; } =
    [
        new("forms-basic.pdf", "form-text.pdf",
            "Text fields: single line, multi line (two lines), comb field (8 cells), a field with a maximum length of 5, a field on page 2 and the second widget of 'Name' on page 2 (one field, two widgets: both show the value).",
            [
                new("Name", FormFieldState.OfText("Erika Mustermann")),
                new("Bemerkung", FormFieldState.OfText("Erste Zeile\nZweite Zeile")),
                new("Kamm", FormFieldState.OfText("AB123456")),
                new("Kurz", FormFieldState.OfText("12345")),
                new("Telefon", FormFieldState.OfText("0221 123456")),
                new("Ort", FormFieldState.OfText("Koeln")),
            ]),
        new("forms-basic.pdf", "form-choices.pdf",
            "Check box 'Agb' on, 'Zustimmung' stays off; radio group 'Zahlung' = 'Rechnung' (its third button is on page 2, the buttons on page 1 stay off), group 'Stufe' = '2'; combo 'Farbe' = 'Gruen'; editable combo 'Frei' with its own text; list 'Liste' = 'Gamma'; multi-select list 'Mehrfach' = 'Alpha' and 'Gamma'.",
            [
                new("Agb", FormFieldState.OfChecked(true)),
                new("Zahlung", FormFieldState.OfText("Rechnung")),
                new("Stufe", FormFieldState.OfText("2")),
                new("Farbe", FormFieldState.OfSelection([1])),
                new("Frei", FormFieldState.OfText("Eigener Wert")),
                new("Liste", FormFieldState.OfSelection([2])),
                new("Mehrfach", FormFieldState.OfSelection([0, 2])),
            ]),
        new("forms-rotated.pdf", "form-rotated.pdf",
            "Page 1: /Rotate 90 with a CropBox [20 10 380 290] offset inside the MediaBox; page 2: /Rotate 270. A text field, a check box and a combo on each page; the values must stand where the fields are, upright (text in the field of the rotated page: see the note in packaging/README.md).",
            [
                new("Text1", FormFieldState.OfText("Seite eins")),
                new("Check1", FormFieldState.OfChecked(true)),
                new("Combo1", FormFieldState.OfSelection([1])),
                new("Text2", FormFieldState.OfText("Seite zwei")),
                new("Check2", FormFieldState.OfChecked(true)),
                new("Combo2", FormFieldState.OfSelection([2])),
            ]),
        new("forms-basic.pdf", "form-umlauts.pdf",
            "Umlauts, sharp s and the euro sign in a single-line and a multi-line field. Typographic quotes and the en dash are not in Pagewing's character set (the sample writer checks that they are refused as a whole).",
            [
                new("Name", FormFieldState.OfText("Grüße aus Köln, 5 €")),
                new("Bemerkung", FormFieldState.OfText("Äpfel, Öl und Übermut\nStraße 5 € ½ « » µ")),
                new("Ort", FormFieldState.OfText("München, 5 €")),
            ],
            RefusedText: "„Köln“ – 5 €"),
        new("forms-basic.pdf", "form-with-annotations.pdf",
            "A highlight (yellow, comment 'Interop: highlight next to form values') on the label 'Name' on page 1 and three form values, all written in one save: the highlight must be there and the fields must show their values.",
            [
                new("Name", FormFieldState.OfText("Erika Mustermann")),
                new("Agb", FormFieldState.OfChecked(true)),
                new("Ort", FormFieldState.OfText("Köln")),
            ],
            Markup: new Mark(0, "Name", AnnotationKind.Highlight, MarkupColor.Yellow, "Interop: highlight next to form values")),
        new("forms-signed.pdf", "form-signed.pdf",
            "Signature dictionary with DocMDP level 2 (forms and signing allowed), a structure dummy without cryptography: only the text field 'Name' is filled; the signature fields and the 'Senden' button are not touched (the button runs nothing). Whatever Acrobat reports about the signature is a hand check; Pagewing never calls it valid.",
            [
                new("Name", FormFieldState.OfText("Erika Mustermann")),
            ]),
    ];

    /// <summary>
    /// Writes the form samples (<c>form-*.pdf</c>, the unchanged sources next to them, and <c>README-forms.txt</c>) into
    /// <c>PAGEWING_INTEROP_OUT</c>; runs only with that variable. Every sample is read back (values, fields still fillable)
    /// and keeps the original bytes as its beginning.
    /// </summary>
    [Fact]
    public async Task WritesFormSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        await WriteFormSamplesAsync(output);

        foreach (var name in FormSamples.SelectMany(s => new[] { s.Source, s.Name }).Append("README-forms.txt"))
        {
            Assert.True(File.Exists(Path.Combine(output, name)), $"{name} is missing in {output}.");
        }

        Assert.Empty(Directory.GetFiles(output, "*.tmp"));
    }

    /// <summary>The same generation into a temporary folder, every run: each sample is the original plus an appended revision.</summary>
    [Fact]
    public async Task FormSamplesKeepTheOriginalAsPrefix()
    {
        using var output = new TempFolder();
        var report = await WriteFormSamplesAsync(output.Path);

        Assert.Equal(6, FormSamples.Count);
        foreach (var sample in FormSamples)
        {
            var original = await File.ReadAllBytesAsync(TestData.Pdf(sample.Source), Ct);
            var written = await File.ReadAllBytesAsync(Path.Combine(output.Path, sample.Name), Ct);
            Assert.True(written.Length > original.Length, $"{sample.Name}: nothing was appended.");
            Assert.True(written.AsSpan(0, original.Length).SequenceEqual(original), $"{sample.Name}: the original bytes were changed.");
            Assert.Contains(sample.Name, report, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.GetFiles(output.Path, "*.tmp"));
    }

    private static async Task<string> WriteFormSamplesAsync(string output)
    {
        var engine = new PdfEngine();
        var readme = new StringBuilder();
        readme.AppendLine("Pagewing - form samples for other viewers (milestone 11)");
        readme.AppendLine("=========================================================");
        readme.AppendLine();
        readme.AppendLine("Written by tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.Forms.cs (PAGEWING_INTEROP_OUT).");
        readme.AppendLine("Every <source>.pdf is the unchanged fixture from testdata/pdfs, every form-*.pdf the same file filled by Pagewing's");
        readme.AppendLine("own pipeline (AnnotationEditor over PDFium's form engine, then Save through DocumentSaver: incremental update,");
        readme.AppendLine("checked before it replaced the file; the first bytes are the original). The checklist is in packaging/README.md,");
        readme.AppendLine("section \"Formulare im Fremdviewer prüfen\".");
        readme.AppendLine();
        readme.AppendLine("For every sample a viewer must show:");
        readme.AppendLine("  - every value named below in its field (text, check mark, chosen radio button, chosen options), nothing empty, nothing shifted;");
        readme.AppendLine("  - the fields still fillable (click into a text field and type; tick a box; change the choice);");
        readme.AppendLine("  - after the viewer saves the file itself and it is opened again: the same values.");
        readme.AppendLine("Pagewing never claims that a signature is valid; for form-signed.pdf only report what the viewer says.");
        readme.AppendLine();

        using var work = new TempFolder();
        foreach (var sample in FormSamples)
        {
            readme.Append(await WriteFormSampleAsync(engine, sample, work.Path, output));
        }

        await File.WriteAllTextAsync(Path.Combine(output, "README-forms.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
        return readme.ToString();
    }

    private static async Task<string> WriteFormSampleAsync(PdfEngine engine, FormSample sample, string workFolder, string output)
    {
        var source = TestData.Pdf(sample.Source);
        var original = await File.ReadAllBytesAsync(source, Ct);
        var working = Path.Combine(workFolder, sample.Name);
        File.Copy(source, working, overwrite: true);

        using (var session = new ViewerSession(engine))
        {
            await session.OpenAsync(working);
            Assert.Equal(ViewerState.Ready, session.State);
            session.Document!.Clock = new FixedClock(Now);
            var editor = new AnnotationEditor(session);
            if (sample.RefusedText is { } refused)
            {
                // Characters outside the character set are refused as a whole, nothing is written.
                await Assert.ThrowsAnyAsync<ArgumentException>(() => editor.SetFormValueAsync("Ort", FormFieldState.OfText(refused)));
                Assert.False(editor.IsDirty, $"{sample.Name}: a refused value must leave the editor clean.");
            }

            foreach (var (field, state) in sample.Values)
            {
                Assert.NotNull(await editor.SetFormValueAsync(field, state));
            }

            if (sample.Markup is { } mark)
            {
                var rects = await RectsOfAsync(session.Document!, mark);
                Assert.NotNull(await editor.ApplyMarkupAsync(mark.Page, mark.Kind, rects, AnnotationPalette.ColorOf(mark.Color), mark.Comment, Author));
            }

            Assert.True(editor.IsDirty);
            var saved = await editor.SaveAsync(engine, working, overwriteChangedOriginal: false, progress: null, Ct);
            Assert.True(saved.Adopted);
            Assert.False(editor.IsDirty);
        }

        var bytes = await File.ReadAllBytesAsync(working, Ct);
        Assert.True(bytes.Length > original.Length, $"{sample.Name}: nothing was appended.");
        Assert.True(bytes.AsSpan(0, original.Length).SequenceEqual(original), $"{sample.Name}: the original bytes were changed.");

        // Read back: the values are there and every filled field can be filled again (it is not locked).
        await using (var reopened = await engine.OpenAsync(working, cancellationToken: Ct))
        {
            for (var page = 0; page < reopened.PageCount; page++)
            {
                _ = await reopened.RenderPageAsync(page, 1, cancellationToken: Ct);
            }

            foreach (var (field, state) in sample.Values)
            {
                var widgets = new List<FormField>();
                for (var page = 0; page < reopened.PageCount; page++)
                {
                    widgets.AddRange((await reopened.GetFormFieldsAsync(page, Ct)).Where(f => f.Key.FieldName == field));
                }

                Assert.True(widgets.Count > 0, $"{sample.Name}: the field {field} is missing after saving.");
                foreach (var widget in widgets)
                {
                    Assert.Equal(FormLock.None, widget.Lock);
                    Assert.True(SameState(state, widget.State), $"{sample.Name}: {field} reads back as text '{widget.State.Text}', checked {widget.State.Checked}, options [{string.Join(',', widget.State.SelectedOptions)}].");
                }
            }

            if (sample.Markup is { } mark)
            {
                Assert.Contains(await reopened.GetAnnotationsAsync(mark.Page, Ct), a => a.Kind == mark.Kind && a.Contents == mark.Comment);
            }
        }

        File.Copy(source, Path.Combine(output, sample.Source), overwrite: true);
        File.Copy(working, Path.Combine(output, sample.Name), overwrite: true);

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{sample.Source} -> {sample.Name}");
        text.AppendLine(new string('-', sample.Source.Length + sample.Name.Length + 4));
        text.AppendLine(CultureInfo.InvariantCulture, $"  {sample.About}");
        text.AppendLine(CultureInfo.InvariantCulture, $"  Original {original.Length} bytes, filled {bytes.Length} bytes; the first {original.Length} bytes are identical.");
        foreach (var (field, state) in sample.Values)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - {field}: {Show(state)}");
        }

        if (sample.Markup is { } shown)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  - page {shown.Page + 1}: {KindName(shown.Kind)} {shown.Color} on \"{shown.Text}\"{CommentOf(shown.Comment)}");
        }

        text.AppendLine();
        return text.ToString();
    }

    /// <summary>Whether a field reads back as written; the line break of a multi-line field comes back as CR LF.</summary>
    private static bool SameState(FormFieldState want, FormFieldState got) =>
        want.Checked == got.Checked
        && want.SelectedOptions.SequenceEqual(got.SelectedOptions)

        // A combo box reads back the export value of its option as text as well; only a text is compared as a text.
        && (want.SelectedOptions.Count > 0 || string.Equals(Normalize(want.Text), Normalize(got.Text), StringComparison.Ordinal));

    private static string? Normalize(string? text) => text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string Show(FormFieldState state)
    {
        if (state.Checked is { } isChecked)
        {
            return isChecked ? "checked" : "not checked";
        }

        if (state.SelectedOptions.Count > 0)
        {
            return "options " + string.Join(", ", state.SelectedOptions.Select(i => (i + 1).ToString(CultureInfo.InvariantCulture))) + " (counted from 1, in the order of the list)";
        }

        return state.Text is null ? "no choice" : "\"" + state.Text.Replace("\n", " / ", StringComparison.Ordinal) + "\"";
    }

    /// <summary>One sample: a fixture, the file written, the values to fill (in this order), an optional markup and a text that must be refused.</summary>
    private sealed record FormSample(string Source, string Name, string About, IReadOnlyList<FormValue> Values, Mark? Markup = null, string? RefusedText = null);

    private sealed record FormValue(string Field, FormFieldState State);
}
