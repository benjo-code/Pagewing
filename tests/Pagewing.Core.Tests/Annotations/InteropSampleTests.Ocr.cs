using System.Globalization;
using System.Text;
using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Annotations;

/// <summary>The text recognition samples of milestone 15 for the check in other viewers (<c>packaging/README.md</c>, "OCR prüfen (M15)").</summary>
public sealed partial class InteropSampleTests
{
    /// <summary>
    /// The OCR samples: the scan, the turned scan and the mixed document, each with the layer from the known words of the fixture
    /// (so the samples do not depend on the native library), saved through the editor, then read back. Writes <c>ocr-*.pdf</c> and
    /// <c>README-ocr.txt</c> into <c>PAGEWING_INTEROP_OUT</c>.
    /// </summary>
    [Fact]
    public async Task WritesOcrSamplesForOtherViewers()
    {
        var output = Environment.GetEnvironmentVariable(OutputVariable);
        Assert.SkipWhen(string.IsNullOrEmpty(output), $"Interop samples only: set {OutputVariable}=<folder>.");
        output = Directory.CreateDirectory(output).FullName;

        var engine = new PdfEngine();
        using var work = new TempFolder();
        var readme = new StringBuilder();
        readme.AppendLine("Text recognition of Pagewing (milestone 15): the page image stays, the recognized words are invisible text (render mode 3, marked content /PagewingOcr)")
            .AppendLine("laid over the words of the image. Written by tests/Pagewing.Core.Tests/Annotations/InteropSampleTests.Ocr.cs (PAGEWING_INTEROP_OUT).")
            .AppendLine("Check in Chrome and Acrobat Reader: the page looks as before (no visible text, no shift); search for a word below and the hit lies on the word")
            .AppendLine("in the image; select text and copy it (the words come in reading order); the page of the turned scan is the same.")
            .AppendLine("Save the file once in Acrobat and open it in Pagewing: is the layer still found as the layer of Pagewing (see the checklist)?")
            .AppendLine();

        await WriteAsync("ocr-scan-recognized.pdf", "ocr-scan.pdf", [0, 1]);
        await WriteAsync("ocr-scan-rotated-recognized.pdf", "ocr-scan-rotated.pdf", [0]);
        await WriteAsync("ocr-mixed-recognized.pdf", "ocr-mixed.pdf", [1]);

        await File.WriteAllTextAsync(Path.Combine(output, "README-ocr.txt"), readme.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), Ct);
        Assert.Empty(Directory.GetFiles(output, "*.tmp"));

        async Task WriteAsync(string name, string source, int[] pages)
        {
            var copy = Path.Combine(work.Path, name);
            File.Copy(TestData.Pdf(source), copy);
            var target = Path.Combine(output, name);
            File.Delete(target);
            var known = pages.Select(page => (Page: page, Words: OcrFixtures.WordsOf(source, page))).ToList();
            using (var session = new ViewerSession(engine))
            {
                await session.OpenAsync(copy);
                Assert.Equal(ViewerState.Ready, session.State);
                var editor = new AnnotationEditor(session);
                var result = new OcrRunResult(
                    [.. known.Select(static k => new OcrPageResult(k.Page, [.. k.Words.Select(static w => new OcrPageWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, 90))], 200, false, 90))],
                    OcrReport.Build([], [], []));
                await editor.ApplyOcrAsync(result, replace: false);
                await editor.SaveAsync(engine, target, overwriteChangedOriginal: false, progress: null, Ct);
            }

            await using var reopened = await OpenAsync(target);
            readme.AppendLine(CultureInfo.InvariantCulture, $"{name} (from {source}):");
            foreach (var (page, words) in known)
            {
                var layer = await reopened.ReadOcrLayerAsync(page, Ct);
                Assert.Equal(words.Count, layer.Count);
                readme.AppendLine(CultureInfo.InvariantCulture, $"  - page {page + 1}: {layer.Count} words, e.g. \"{words[0].Text}\" at {words[0].Left:0.#} {words[0].Top:0.#} {words[0].Right:0.#} {words[0].Bottom:0.#} (pt from the top left)");
            }
        }
    }
}
