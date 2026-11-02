using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Tests.Cli;
using Pagewing.Core.Tests.Redaction;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>
/// The text layer next to the other features (milestone 15, task 11): redacting a word of a recognized page, organizing pages, a second run, a run over
/// pages that have text, and the real engine on the turned scan.
/// </summary>
public sealed class OcrInteractionTests : CliTestBase
{
    private static OcrPageWord[] Known(string file, int page) =>
        [.. OcrFixtures.WordsOf(file, page).Select(static w => new OcrPageWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, 90))];

    private static OcrRunResult Run(params (int Page, OcrPageWord[] Words)[] pages) =>
        new([.. pages.Select(static p => new OcrPageResult(p.Page, p.Words, 200, false, 90))], OcrReport.Build([], [], []));

    private async Task<ViewerSession> OpenAsync(string path)
    {
        var session = new ViewerSession(Engine);
        await session.OpenAsync(path);
        Assert.Equal(ViewerState.Ready, session.State);
        return session;
    }

    [Fact]
    public async Task OcrThenRedactingAWordRemovesPixelsAndLayerAndTheCheckPasses()
    {
        // Acceptance 6: the word of the layer under a mark is part of what the redaction removes, and the check of the written file finds nothing of it.
        var path = Copy("ocr-scan.pdf");
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        await editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0))), replace: false);
        await editor.SaveAsync(Engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        var word = OcrFixtures.WordsOf("ocr-scan.pdf", 0).Single(static w => w.Text == "Invoice");
        var area = new PageRect(word.Left - 1, word.Top - 1, word.Right + 1, word.Bottom + 1);
        var plan = RedactionPlan.Empty.With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [area], null, false));
        var target = Path.Combine(WorkDirectory.FullName, "redacted.pdf");

        var result = await RedactionApplier.ApplyAsync(session, Engine, new RedactionApplyRequest(plan, target), null, Ct);

        Assert.True(result.Check.IsClean, RedactionRefusalText.Of(result.Check));
        var bytes = await File.ReadAllBytesAsync(target, Ct);
        Assert.False(RawPdf.ContainsAnywhere(bytes, "Invoice"));
        await using var document = await Engine.OpenAsync(target, cancellationToken: Ct);
        var text = await document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        Assert.DoesNotContain("Invoice", all, StringComparison.Ordinal);
        Assert.Contains("Pagewing", all, StringComparison.Ordinal);
        Assert.Contains("lazy", all, StringComparison.Ordinal);

        // The pixels under the mark are the fill, not the word.
        var image = await document.RenderPageAsync(0, 1, cancellationToken: Ct);
        var dark = 0;
        var total = 0;
        for (var y = (int)(word.Top + 2); y < (int)(word.Bottom - 2); y++)
        {
            for (var x = (int)(word.Left + 2); x < (int)(word.Right - 2); x++)
            {
                total++;
                if (image.Pixels.Span[(y * image.Stride) + (x * 4)] < 40)
                {
                    dark++;
                }
            }
        }

        Assert.True(dark > total * 0.95, $"{dark} of {total} pixels under the mark are dark");
    }

    [Fact]
    public async Task OrganizingPagesKeepsTheLayerOfTheTurnedPage()
    {
        var path = Copy("ocr-scan.pdf");
        using (var session = await OpenAsync(path))
        {
            var editor = new AnnotationEditor(session);
            await editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0)), (1, Known("ocr-scan.pdf", 1))), replace: false);
            await editor.SaveAsync(Engine, path, overwriteChangedOriginal: false, progress: null, Ct);
        }

        var output = Path.Combine(WorkDirectory.FullName, "turned.pdf");
        var result = await RunAsync("rotate", path, "--pages", "1", "--angle", "90", "--output", output);

        Assert.Equal(0, result.ExitCode);
        await using var document = await Engine.OpenAsync(output, cancellationToken: Ct);
        var first = await document.GetOcrPageInfoAsync(0, Ct);
        var second = await document.GetOcrPageInfoAsync(1, Ct);
        Assert.True(first.HasOwnLayer);
        Assert.True(second.HasOwnLayer);
        var text = await document.GetPageTextAsync(0, Ct);
        Assert.Contains("Invoice", text.GetText(0, text.Chars.Count), StringComparison.Ordinal);
        Assert.Equal(Known("ocr-scan.pdf", 0).Length, (await document.ReadOcrLayerAsync(0, Ct)).Count);
    }

    [Fact]
    public async Task SecondRunInRedoModeLeavesExactlyOneLayer()
    {
        using var session = await OpenAsync(Copy("ocr-scan.pdf"));
        var editor = new AnnotationEditor(session);
        await editor.ApplyOcrAsync(Run((0, Known("ocr-scan.pdf", 0))), replace: false);
        OcrPageWord[] second = [new("Second", 40, 40, 120, 64, 80), new("Run", 140, 40, 200, 64, 80)];

        await editor.ApplyOcrAsync(Run((0, second)), replace: true);

        var layer = await session.Document!.ReadOcrLayerAsync(0, Ct);
        Assert.Equal(["Second", "Run"], layer.Select(static w => w.Text));
        var text = await session.Document.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        Assert.Contains("Second", all, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoice", all, StringComparison.Ordinal);
        Assert.Equal(1, (await session.Document.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer ? 1 : 0);
    }

    [Fact]
    public async Task ForceModeKeepsExistingTextUntouched()
    {
        var path = Copy("ocr-mixed.pdf");
        using var session = await OpenAsync(path);
        var editor = new AnnotationEditor(session);
        var before = await session.Document!.GetPageTextAsync(0, Ct);
        var original = before.GetText(0, before.Chars.Count);

        await editor.ApplyOcrAsync(Run((0, [new OcrPageWord("Recognized", 40, 300, 160, 324, 85)])), replace: true);

        var after = await session.Document.GetPageTextAsync(0, Ct);
        var all = after.GetText(0, after.Chars.Count);
        Assert.Contains(original.Trim(), all, StringComparison.Ordinal);
        Assert.Contains("Recognized", all, StringComparison.Ordinal);
        var info = await session.Document.GetOcrPageInfoAsync(0, Ct);
        Assert.True(info.HasOwnLayer);
        Assert.True(info.TextCharacters >= OcrPlanner.TextThreshold, "the text of its own is still counted without the layer");
    }

    [Fact]
    public async Task RotatedFixtureSearchHitsAreOnTheWord()
    {
        OcrTestLibrary.Require();
        var path = Copy("ocr-scan-rotated.pdf");
        using var session = await OpenAsync(path);
        var factory = new TesseractEngineFactory(OcrTestLibrary.Tessdata);
        var result = await OcrRunner.RunAsync(session.Document!, new OcrOptions("eng", OcrMode.Skip, 0, null, 2), factory, null, Ct);
        var editor = new AnnotationEditor(session);

        var applied = await editor.ApplyOcrAsync(result, replace: false);

        Assert.NotNull(applied);
        var text = await session.Document!.GetPageTextAsync(0, Ct);
        var all = text.GetText(0, text.Chars.Count);
        var known = OcrFixtures.WordsOf("ocr-scan-rotated.pdf", 0).Single(static w => w.Text == "Pagewing");
        var start = all.IndexOf("Pagewing", StringComparison.Ordinal);
        Assert.True(start >= 0, "the recognized layer does not hold 'Pagewing'");
        var rects = text.RectsOf(start, start + "Pagewing".Length);
        var tolerance = (known.Bottom - known.Top) / 2;
        Assert.InRange(rects.Min(static r => r.Left), known.Left - tolerance, known.Left + tolerance);
        Assert.InRange(rects.Max(static r => r.Right), known.Right - tolerance, known.Right + tolerance);
        Assert.InRange(rects.Min(static r => r.Top), known.Top - tolerance, known.Top + tolerance);
        Assert.InRange(rects.Max(static r => r.Bottom), known.Bottom - tolerance, known.Bottom + tolerance);
    }
}
