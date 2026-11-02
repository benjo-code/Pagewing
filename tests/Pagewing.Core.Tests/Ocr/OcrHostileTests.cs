using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>Hostile input (milestone 15, task 11): the largest page, a page of specks that look like 100,000 words, characters the font lacks, and a run that is stopped.</summary>
public sealed class OcrHostileTests : IDisposable
{
    private readonly TempFolder folder = new();
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    private async Task<PdfDocument> OpenAsync(string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return await engine.OpenAsync(path, cancellationToken: Ct);
    }

    [Fact]
    public async Task HostileFixtureEndsWithReportedSkipsAndBoundedMemory()
    {
        await using var document = await OpenAsync("ocr-hostile.pdf");
        var manyWords = new OcrRecognition([.. Enumerable.Range(0, 100_001).Select(static i => new OcrWord("w", 10 + (i % 1000), 10 + (i / 1000), 12 + (i % 1000), 12 + (i / 1000), 40))]);
        var calls = 0;
        var fake = new FakeOcrFactory(_ =>
        {
            Interlocked.Increment(ref calls);
            return manyWords;
        });
        var before = GC.GetTotalMemory(forceFullCollection: true);

        var result = await OcrRunner.RunAsync(document, new OcrOptions("eng", OcrMode.Skip, 0, null, 2), fake, null, Ct);

        // The page of 14,400 x 14,400 points is skipped before anything is rendered; the page with the specks yields more words than a page holds
        // and is skipped with the reason, not cut. Nothing is recognized, nothing is silent.
        Assert.Empty(result.Pages);
        Assert.Equal([OcrSkipReason.TooLarge, OcrSkipReason.TooLarge], result.Report.Skipped.Select(static s => s.Reason));
        Assert.Equal([0, 1], result.Report.Skipped.Select(static s => s.Page));
        Assert.Equal(1, calls);
        var after = GC.GetTotalMemory(forceFullCollection: true);
        Assert.True(after - before < 200L * 1024 * 1024, $"the run holds {(after - before) / (1024 * 1024)} MB after it ended");
    }

    [Fact]
    public async Task WordsWithCharactersOutsideTheFontEndAsLeftOutNotReplaced()
    {
        await using var document = await OpenAsync("ocr-scan.pdf");
        var fake = new FakeOcrFactory(_ => new OcrRecognition([new OcrWord("Привет", 100, 100, 300, 150, 90), new OcrWord("мир", 320, 100, 420, 150, 90)]));
        var result = await OcrRunner.RunAsync(document, new OcrOptions("eng", OcrMode.Skip, 0, new HashSet<int> { 0 }, 1), fake, null, Ct);
        using var session = new ViewerSession(engine);
        var path = Path.Combine(folder.Path, "ocr-scan.pdf");
        await session.OpenAsync(path);
        var editor = new AnnotationEditor(session);

        var applied = await editor.ApplyOcrAsync(result, replace: false);

        Assert.Null(applied);
        Assert.False(editor.IsDirty);
        Assert.False((await session.Document!.GetOcrPageInfoAsync(0, Ct)).HasOwnLayer);
    }
}
