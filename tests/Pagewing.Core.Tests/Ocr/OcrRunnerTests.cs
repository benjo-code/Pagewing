using System.Diagnostics;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>The run of a recognition over a document, with fake engines: which pages, in what order, how much memory, what a stuck or failing page does.</summary>
public sealed class OcrRunnerTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OcrRecognition One(string text = "word", float confidence = 90) =>
        new([new OcrWord(text, 100, 100, 300, 150, confidence)]);

    private static OcrRecognition None() => new([]);

    private static OcrOptions Options(OcrMode mode = OcrMode.Skip, int jobs = 1, IReadOnlySet<int>? pages = null) =>
        new("eng", mode, 0, pages, jobs);

    private async Task<PdfDocument> OpenAsync(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return await engine.OpenAsync(path, cancellationToken: Ct);
    }

    [Fact]
    public async Task SkipsPagesWithTextAndReportsWhy()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-mixed.pdf");
        var fake = new FakeOcrFactory(_ => One());

        var result = await OcrRunner.RunAsync(document, Options(), fake, null, Ct);

        Assert.Equal([1, 2], result.Pages.Select(static p => p.Page));
        var skipped = Assert.Single(result.Report.Skipped);
        Assert.Equal(0, skipped.Page);
        Assert.Equal(OcrSkipReason.HasText, skipped.Reason);
        Assert.Equal([1], result.Report.LittleTextPages);
        Assert.Equal(2, fake.Calls);
    }

    [Fact]
    public async Task PagesAreReturnedInPageOrderWhateverTheWorkersFinishFirst()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var delays = new[] { 400, 200, 0 };
        var fake = new FakeOcrFactory(call =>
        {
            Thread.Sleep(delays[call.Index % 3]);
            return One("page" + call.Index);
        });

        var result = await OcrRunner.RunAsync(document, Options(jobs: 3), fake, null, Ct);

        Assert.Equal([0, 1, 2], result.Pages.Select(static p => p.Page));
        Assert.Equal(["page0", "page1", "page2"], result.Pages.Select(static p => p.Words[0].Text).OrderBy(static t => t));
    }

    [Fact]
    public async Task MemoryStaysBoundedBySlowWorkers()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "thousand-pages.pdf");
        using var release = new ManualResetEventSlim();
        var rendered = 0;
        var finished = 0;
        var peak = 0;
        var settings = new OcrRunnerSettings
        {
            PageRendered = _ =>
            {
                var held = Interlocked.Increment(ref rendered) - Volatile.Read(ref finished);
                int seen;
                while (held > (seen = Volatile.Read(ref peak)) && Interlocked.CompareExchange(ref peak, held, seen) != seen)
                {
                }
            },
        };
        var fake = new FakeOcrFactory(call =>
        {
            release.Wait(call.Cancellation);
            Interlocked.Increment(ref finished);
            return None();
        });
        var run = OcrRunner.RunAsync(document, Options(OcrMode.Force, jobs: 2, pages: Enumerable.Range(0, 12).ToHashSet()), fake, null, settings, Ct);

        // Two workers are stuck, a third page waits for a slot: no fourth page is rendered however long the workers take.
        await WaitUntilAsync(() => Volatile.Read(ref rendered) >= 3);
        await Task.Delay(400, Ct);
        Assert.Equal(3, Volatile.Read(ref rendered));
        Assert.False(run.IsCompleted);

        release.Set();
        var result = await run;

        Assert.Equal(12, result.Pages.Count);
        Assert.InRange(Volatile.Read(ref peak), 1, 3);
        Assert.Equal(2, fake.MaxActive);
    }

    [Fact]
    public async Task CancelStopsWorkersAndKeepsNothingHalf()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "thousand-pages.pdf");
        var started = new SemaphoreSlim(0);
        var fake = new FakeOcrFactory(call =>
        {
            started.Release();
            call.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            call.Cancellation.ThrowIfCancellationRequested();
            return One();
        });
        using var cancel = new CancellationTokenSource();
        var run = OcrRunner.RunAsync(document, Options(OcrMode.Force, jobs: 2, pages: Enumerable.Range(0, 20).ToHashSet()), fake, null, cancel.Token);
        await started.WaitAsync(Ct);

        await cancel.CancelAsync();

        var canceled = await Assert.ThrowsAsync<OcrRunCanceledException>(() => run);
        Assert.Empty(canceled.Partial.Pages);
        Assert.Equal(0, fake.Active);
        Assert.Equal(fake.Created, fake.Disposed);
        Assert.True(fake.Calls <= 3, $"{fake.Calls} pages were recognized after the cancel");
    }

    [Fact]
    public async Task TimeoutSkipsThePageAndKeepsTheRun()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var clock = new ManualClock();
        using var stuck = new ManualResetEventSlim();
        var fake = new FakeOcrFactory(call =>
        {
            if (call.Index == 1)
            {
                stuck.Wait();
            }

            return One("page" + call.Index);
        });
        var settings = new OcrRunnerSettings { Clock = clock, PageTimeout = TimeSpan.FromSeconds(300) };
        var run = OcrRunner.RunAsync(document, Options(jobs: 2), fake, null, settings, Ct);
        await WaitUntilAsync(() => fake.Calls >= 2 && clock.Waiting >= 1);

        clock.Advance(TimeSpan.FromSeconds(301));
        var result = await run;
        stuck.Set();

        var timeout = Assert.Single(result.Report.Skipped);
        Assert.Equal(OcrSkipReason.Timeout, timeout.Reason);
        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(2, result.Report.PagesRecognized);
        await WaitUntilAsync(() => fake.Active == 0);
    }

    [Fact]
    public async Task BlankPageGivesNoWordsAndNoError()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-mixed.pdf");
        var fake = new FakeOcrFactory(_ => None());

        var result = await OcrRunner.RunAsync(document, Options(pages: new HashSet<int> { 2 }), fake, null, Ct);

        var page = Assert.Single(result.Pages);
        Assert.Empty(page.Words);
        Assert.Equal([2], result.Report.PagesWithoutWords);
        Assert.Empty(result.Report.Skipped);
    }

    [Fact]
    public async Task ATooLargePageIsSkippedWithTheReason()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-hostile.pdf");
        var fake = new FakeOcrFactory(_ => None());

        var result = await OcrRunner.RunAsync(document, Options(), fake, null, Ct);

        var skipped = Assert.Single(result.Report.Skipped);
        Assert.Equal(0, skipped.Page);
        Assert.Equal(OcrSkipReason.TooLarge, skipped.Reason);
        Assert.Contains("dpi", skipped.Detail, StringComparison.Ordinal);
        Assert.Equal([1], result.Pages.Select(static p => p.Page));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public async Task AWorkerFailureSkipsThePageWithFailed()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(call => call.Index switch
        {
            0 => throw new OcrEngineException(OcrEngineError.Failed, "boom"),
            1 => throw new InvalidOperationException("boom"),
            _ => One(),
        });

        var result = await OcrRunner.RunAsync(document, Options(), fake, null, Ct);

        Assert.Equal(2, result.Report.Skipped.Count);
        Assert.All(result.Report.Skipped, static s => Assert.Equal(OcrSkipReason.Failed, s.Reason));
        Assert.Single(result.Pages);
        Assert.Equal(fake.Created, fake.Disposed);
    }

    [Fact]
    public async Task AnEngineThatFailedIsNotUsedAgain()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(call => call.Index == 0 ? throw new InvalidOperationException("boom") : One());

        await OcrRunner.RunAsync(document, Options(), fake, null, Ct);

        // The first engine failed and was thrown away; a second one did the other two pages, and both are closed at the end.
        Assert.Equal(2, fake.Created);
        Assert.Equal(2, fake.Disposed);
    }

    [Fact]
    public async Task TooManyWordsOnAPageSkipsIt()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(call => call.Index == 0
            ? new OcrRecognition([.. Enumerable.Range(0, 6).Select(i => new OcrWord("w" + i, i * 10, 10, (i * 10) + 8, 20, 90))])
            : One());
        var settings = new OcrRunnerSettings { MaxWordsPerPage = 5 };

        var result = await OcrRunner.RunAsync(document, Options(), fake, null, settings, Ct);

        var skipped = Assert.Single(result.Report.Skipped);
        Assert.Equal(OcrSkipReason.TooLarge, skipped.Reason);
        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public async Task ReportCountsWordsConfidenceAndLowConfidencePages()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(call => call.Index switch
        {
            0 => new OcrRecognition([new OcrWord("a", 10, 10, 50, 40, 95), new OcrWord("b", 60, 10, 100, 40, 85)]),
            1 => new OcrRecognition([new OcrWord("c", 10, 10, 50, 40, 30)]),
            _ => None(),
        });

        var result = await OcrRunner.RunAsync(document, Options(), fake, null, Ct);

        Assert.Equal(3, result.Report.PagesRecognized);
        Assert.Equal(3, result.Report.Words);
        Assert.Equal(70, result.Report.MeanConfidence, 0.01);
        Assert.Equal([1], result.Report.LowConfidencePages);
        Assert.Equal([2], result.Report.PagesWithoutWords);
        Assert.Equal(90, result.Pages[0].MeanConfidence, 0.01);
    }

    [Fact]
    public async Task WordsAreMappedToPointsOfTheDisplayedPage()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        FakeCall? seen = null;
        var fake = new FakeOcrFactory(call =>
        {
            seen ??= call;
            return new OcrRecognition([new OcrWord("x", 100, 100, 300, 150, 90)]);
        });

        var result = await OcrRunner.RunAsync(document, Options(pages: new HashSet<int> { 0 }), fake, null, Ct);

        // The picture of the page is 200 dpi: the page is rendered at 200 dpi, 1000 x 1400 pixels, 0.36 points each.
        Assert.Equal(200, seen!.Dpi);
        Assert.Equal(1000, seen.Width);
        Assert.Equal(1400, seen.Height);
        var word = Assert.Single(Assert.Single(result.Pages).Words);
        Assert.Equal(36, word.Left, 0.01);
        Assert.Equal(36, word.Top, 0.01);
        Assert.Equal(108, word.Right, 0.01);
        Assert.Equal(54, word.Bottom, 0.01);
        Assert.Equal(200, result.Pages[0].Dpi);
    }

    [Fact]
    public async Task PdfiumThreadIsNeverBlockedByRecognition()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(_ =>
        {
            Thread.Sleep(2000);
            return None();
        });
        var run = OcrRunner.RunAsync(document, Options(pages: new HashSet<int> { 0 }), fake, null, Ct);
        await WaitUntilAsync(() => fake.Active == 1);

        var clock = Stopwatch.StartNew();
        var size = await document.GetPageSizeAsync(1, Ct);

        Assert.True(clock.ElapsedMilliseconds < 200, $"the PDFium thread answered after {clock.ElapsedMilliseconds} ms while an engine was busy");
        Assert.Equal(360, size.Width, 0.5);
        await run;
    }

    [Fact]
    public async Task AMissingLanguageFailsTheRunBeforeAnyPage()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(_ => One()) { CreateFailure = new OcrEngineException(OcrEngineError.LanguageMissing, "no data", ["deu", "eng"]) };

        var failure = await Assert.ThrowsAsync<OcrEngineException>(() => OcrRunner.RunAsync(document, Options(), fake, null, Ct));

        Assert.Equal(OcrEngineError.LanguageMissing, failure.Error);
        Assert.Equal(0, fake.Calls);
    }

    [Fact]
    public async Task PagesOutsideTheDocumentAreRefused()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(_ => One());

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => OcrRunner.RunAsync(document, Options(pages: new HashSet<int> { 3 }), fake, null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => OcrRunner.RunAsync(document, new OcrOptions("../eng"), fake, null, Ct));
        Assert.Equal(0, fake.Created);
    }

    [Fact]
    public async Task OnlyTheChosenPagesAreLookedAt()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-scan.pdf");
        var fake = new FakeOcrFactory(_ => One());

        var result = await OcrRunner.RunAsync(document, Options(pages: new HashSet<int> { 0, 2 }), fake, null, Ct);

        Assert.Equal([0, 2], result.Pages.Select(static p => p.Page));
        Assert.Empty(result.Report.Skipped);
    }

    [Fact]
    public async Task ProgressCountsEveryDecidedPage()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "ocr-mixed.pdf");
        var seen = new List<OcrProgress>();
        var progress = new SynchronousProgress(seen.Add);
        var fake = new FakeOcrFactory(_ => One());

        await OcrRunner.RunAsync(document, Options(), fake, progress, Ct);

        Assert.Equal([1, 2, 3], seen.Select(static p => p.PagesDone).Order());
        Assert.All(seen, static p => Assert.Equal(3, p.PagesTotal));
        var last = seen.OrderBy(static p => p.PagesDone).Last();
        Assert.Equal(2, last.Recognized);
        Assert.Equal(1, last.Skipped);
    }

    [Fact]
    public async Task EnginesAreNeverSharedBetweenThreadsAndAreDisposedAtTheEnd()
    {
        using var folder = new TempFolder();
        await using var document = await OpenAsync(folder, "thousand-pages.pdf");
        var fake = new FakeOcrFactory(_ =>
        {
            Thread.Sleep(5);
            return One();
        });

        var result = await OcrRunner.RunAsync(document, Options(OcrMode.Force, jobs: 3, pages: Enumerable.Range(0, 30).ToHashSet()), fake, null, Ct);

        Assert.Equal(30, result.Pages.Count);
        Assert.Equal(0, fake.Violations);
        Assert.True(fake.Created <= 4, $"{fake.Created} engines for 3 workers");
        Assert.Equal(fake.Created, fake.Disposed);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "the condition did not come true in time");
            await Task.Delay(10, Ct);
        }
    }

    private sealed class SynchronousProgress(Action<OcrProgress> report) : IProgress<OcrProgress>
    {
        private readonly object gate = new();

        public void Report(OcrProgress value)
        {
            lock (gate)
            {
                report(value);
            }
        }
    }
}
