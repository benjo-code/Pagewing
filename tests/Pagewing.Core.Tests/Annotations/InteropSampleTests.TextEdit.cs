using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.TextEdit;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The text edit samples of milestone 16 for the check in other viewers (<c>packaging/README.md</c>, "Text bearbeiten prüfen (M16)").</summary>
public sealed partial class InteropSampleTests
{
    /// <summary>
    /// The text edit samples: a line of a standard font, of an embedded subset (once with all characters in the font, once with one in Geist), of a turned
    /// page, each edited through the editor and saved. Writes <c>text-edit-*-edited.pdf</c> and <c>README-text-edit.txt</c> into <c>PAGEWING_INTEROP_OUT</c>.
    /// </summary>
    [Fact]
    public async Task WritesTextEditSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var engine = new PdfEngine();
        using var work = new TempFolder();
        var readme = new StringBuilder();
        readme.AppendLine("Text editing of Pagewing (milestone 16): one line of a page is replaced as a whole; the unchanged characters keep their spacing, the rest of the")
            .AppendLine("line moves by the difference; a character the font lacks is set in Geist Regular. Written by")
            .AppendLine("tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.TextEdit.cs (PAGEWING_INTEROP_OUT).")
            .AppendLine("Check in Chrome and Acrobat Reader: the page looks as described below (new text where the old one stood, nothing else moved); search for the new text")
            .AppendLine("and for the old one (only the new is found); select and copy the line. Then open the file in Acrobat's own editor: can the line be edited")
            .AppendLine("further (an honest answer, not a goal)? Save once in Acrobat and open the file in Pagewing again.")
            .AppendLine();

        await WriteAsync("text-edit-standard-edited.pdf", "text-edit-standard.pdf", 0, "Quarterly report 2026", "Quarterly report 2027", "Helvetica line, correction");
        await WriteAsync("text-edit-subset-edited.pdf", "text-edit-subset.pdf", 0, "Invoice total: 1,250 EUR", "Invoice total: 1,205 EUR", "embedded subset, all characters in the font");
        await WriteAsync("text-edit-subset-geist-edited.pdf", "text-edit-subset.pdf", 0, "Invoice total: 1,250 EUR", "Invoice total: 1,259 EUR", "embedded subset, the 9 in Geist Regular");
        await WriteAsync("text-edit-rotated-edited.pdf", "text-edit-rotated.pdf", 0, "Turned page line", "Turned page lines of text", "page turned by 90 degrees with a crop box, longer text");

        await File.WriteAllTextAsync(Path.Combine(output, "README-text-edit.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));

        async Task WriteAsync(string name, string source, int page, string oldText, string newText, string what)
        {
            var copy = Path.Combine(work.Path, name);
            File.Copy(TestData.Pdf(source), copy);
            var target = Path.Combine(output, name);
            File.Delete(target);
            using (var session = new ViewerSession(engine))
            {
                await session.OpenAsync(copy);
                Assert.Equal(ViewerState.Ready, session.State);
                var editor = new AnnotationEditor(session);
                var run = (await session.Document!.GetTextRunsAsync(page, Ct)).Single(r => r.Text.TrimEnd() == oldText);
                var result = await editor.ApplyTextEditAsync(run, newText, TextEditMode.KeepFont);
                Assert.NotNull(result);
                await editor.SaveAsync(engine, target, overwriteChangedOriginal: false, progress: null, Ct);
            }

            await using var reopened = await engine.OpenAsync(target, cancellationToken: Ct);
            var text = await reopened.GetPageTextAsync(page, Ct);
            var all = text.GetText(0, text.Chars.Count);
            Assert.Contains(newText, all, StringComparison.Ordinal);
            if (!newText.Contains(oldText, StringComparison.Ordinal))
            {
                Assert.DoesNotContain(oldText, all, StringComparison.Ordinal);
            }

            readme.AppendLine(CultureInfo.InvariantCulture, $"{name} (from {source}, {what}): \"{oldText}\" -> \"{newText}\" on page {page + 1}.");
        }
    }
}
