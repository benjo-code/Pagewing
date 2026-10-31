using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The text box samples of milestone 10 for the check in other viewers (<c>packaging/README.md</c>, "Annotationen im Fremdviewer prüfen").</summary>
public sealed partial class InteropSampleTests
{
    private static readonly AnnotationColor TextBlue = new(30, 80, 220);

    /// <summary>
    /// The text box samples: plain, on a rotated page, inside a CropBox, with umlauts and "€", moved, and with its text
    /// changed; each through the editor and its save, then read back. Writes <c>textbox-*.pdf</c> and
    /// <c>README-textboxes.txt</c> into <c>PAGEWING_INTEROP_OUT</c>.
    /// </summary>
    [Fact]
    public async Task WritesTextBoxSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;
        UseTextBoxFont();

        var engine = new PdfEngine();
        using var work = new TempFolder();
        var readme = new StringBuilder();
        readme.AppendLine("Text boxes of Pagewing (milestone 10, variant b2: FreeText with an appearance of Geist glyph outlines, no font in the file).")
            .AppendLine("Written by tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.TextBoxes.cs (PAGEWING_INTEROP_OUT).")
            .AppendLine("Check in Chrome and Acrobat Reader: every line and character is shown (ü ö ß – € ©), upright, inside the box, no message")
            .AppendLine("about a missing font; Acrobat lists each box in the comment list with its text; a double click edits the text on the page")
            .AppendLine("(Acrobat then draws it anew in Helvetica; on the rotated page the edited text turns with the page: known, accepted).")
            .AppendLine();

        await WriteAsync("textbox-plain.pdf", "text-basic.pdf", 0, "Two boxes, 12 pt black and 20 pt blue in two lines.", async (editor, size) =>
        {
            await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, size.Height - 90, 220, size.Height - 70), "Textfeld von Pagewing", 12, new AnnotationColor(0, 0, 0)), "Pagewing Interop");
            await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, size.Height - 60, size.Width - 20, size.Height - 40), "Grüße aus Köln – 5 €\nÄÖÜ äöü ß © ½ « » µ", 20, TextBlue), "Pagewing Interop");
        });
        await WriteAsync("textbox-rotated.pdf", "rotated-page.pdf", 0, "Page with /Rotate 90; the text stands upright on screen.", async (editor, size) =>
            await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(10, 10, size.Width - 10, 30), "Aufrecht 5 €", 14, TextBlue), "Pagewing Interop"));
        await WriteAsync("textbox-cropbox.pdf", "cropbox.pdf", 0, "Page with a CropBox offset; the box lies at the top left of what is shown.", async (editor, size) =>
            await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(10, 10, size.Width - 10, 30), "Im Zuschnitt – Köln", 14, TextBlue), "Pagewing Interop"));
        await WriteAsync("textbox-rotated-cropbox.pdf", "drawings-foreign.pdf", 1, "Page 2: /Rotate 90 and a CropBox offset.", async (editor, size) =>
            await editor.ApplyTextBoxAsync(1, new TextBoxSpec(new PageRect(20, 20, size.Width - 20, 40), "Gedreht und zugeschnitten – 5 €", 16, TextBlue), "Pagewing Interop"));
        await WriteAsync("textbox-moved.pdf", "text-basic.pdf", 0, "A box created at the top and moved down by 60 pt (one replacement under the same /NM).", async (editor, size) =>
        {
            var created = (await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 220, 40), "Verschoben", 16, TextBlue), "Pagewing Interop"))!;
            await editor.ReplaceTextBoxAsync(new AnnotationLocator(0, created.Index, created.Name), created.TextBox! with { Box = created.TextBox.Box with { Top = 80, Bottom = 80 + created.TextBox.Box.Height } });
        });
        await WriteAsync("textbox-edited.pdf", "text-basic.pdf", 0, "A box whose text was changed from \"Vorher\" to two lines and 20 pt.", async (editor, size) =>
        {
            var created = (await editor.ApplyTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 20, 260, 40), "Vorher", 12, TextBlue), "Pagewing Interop"))!;
            await editor.ReplaceTextBoxAsync(new AnnotationLocator(0, created.Index, created.Name), created.TextBox! with { Text = "Nachher geändert\nzweite Zeile – 5 €", FontSize = 20 });
        });

        await File.WriteAllTextAsync(Path.Combine(output, "README-textboxes.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));

        async Task WriteAsync(string name, string source, int pageIndex, string what, Func<AnnotationEditor, PdfPageSize, Task> annotate)
        {
            var copy = Path.Combine(work.Path, name);
            File.Copy(TestData.Pdf(source), copy);
            var target = Path.Combine(output, name);
            File.Delete(target);
            using (var session = new ViewerSession(engine))
            {
                await session.OpenAsync(copy);
                Assert.Equal(ViewerState.Ready, session.State);
                session.Document!.Clock = new FixedClock(Now);
                var editor = new AnnotationEditor(session);
                await annotate(editor, await session.Document.GetPageSizeAsync(pageIndex, Ct));
                await editor.SaveAsync(engine, target, overwriteChangedOriginal: false, progress: null, Ct);
            }

            // Read back: every text box is Pagewing's own and unchanged.
            await using var reopened = await OpenAsync(target);
            var boxes = (await reopened.GetAnnotationsAsync(pageIndex, Ct)).Where(a => a.Kind == AnnotationKind.TextBox).ToList();
            Assert.NotEmpty(boxes);
            Assert.All(boxes, b => Assert.True(b.GeometryEditable, $"{name}: {b.Name} is not recognised as Pagewing's."));
            readme.AppendLine(CultureInfo.InvariantCulture, $"{name} (from {source}, page {pageIndex + 1}): {what}");
            foreach (var box in boxes)
            {
                readme.AppendLine(CultureInfo.InvariantCulture, $"  - \"{box.TextBox!.Text.Replace("\n", " / ", StringComparison.Ordinal)}\", {box.TextBox.FontSize} pt, box {box.TextBox.Box.Left:0.#} {box.TextBox.Box.Top:0.#} {box.TextBox.Box.Right:0.#} {box.TextBox.Box.Bottom:0.#} (pt from the top left)");
            }
        }
    }
}
