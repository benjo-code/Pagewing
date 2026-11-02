using System.Collections.Concurrent;
using Pagewing.Core.Pdf;
using Pagewing.Ocr;

namespace Pagewing.Core.Ocr;

/// <summary>Limits and seams of a run; the production values are the defaults, tests change them.</summary>
internal sealed class OcrRunnerSettings
{
    /// <summary>The clock the time limit runs on.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>The time limit for one page.</summary>
    public TimeSpan PageTimeout { get; init; } = OcrEngine.DefaultTimeout;

    /// <summary>The most words one page may have; a page with more is skipped, never cut.</summary>
    public int MaxWordsPerPage { get; init; } = 20_000;

    /// <summary>The most characters one page may have in its words; a page with more is skipped, never cut.</summary>
    public long MaxCharactersPerPage { get; init; } = 400_000;

    /// <summary>Called (on the thread that renders) when a page has been rendered and is held in memory, for the tests of the backpressure.</summary>
    public Action<int>? PageRendered { get; init; }
}

/// <summary>
/// The run of a text recognition over the pages of a document. Rendering goes one page after the other as short jobs on the PDFium thread; the
/// recognition runs on up to <see cref="OcrOptions.EffectiveJobs"/> threads with an engine of their own each, so the PDFium thread and the
/// interface never wait for the engine. At most workers + 1 rendered pages are held in memory (backpressure), a page that takes longer than the limit
/// is skipped and its engine abandoned, and nothing is returned half: a canceled run throws. Every page that is not recognized is in the report with
/// its reason. The result is not written into the document (see <c>AnnotationEditor.ApplyOcrAsync</c>).
/// </summary>
public static class OcrRunner
{
    /// <summary>Runs the recognition.</summary>
    /// <param name="document">The document.</param>
    /// <param name="options">What to do.</param>
    /// <param name="factory">Makes the engines.</param>
    /// <param name="progress">Told after every page that is decided; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the run; in-flight pages finish first (a running native call cannot be interrupted), then it throws.</param>
    /// <exception cref="ArgumentException">The options are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page of the options does not exist, or a number is out of range.</exception>
    /// <exception cref="OcrEngineException">The library or the language data is missing or damaged; no page was recognized.</exception>
    /// <exception cref="OcrRunCanceledException">The run was canceled; nothing is kept, but the pages that were done are in the exception.</exception>
    public static Task<OcrRunResult> RunAsync(PdfDocument document, OcrOptions options, IOcrEngineFactory factory, IProgress<OcrProgress>? progress, CancellationToken cancellationToken) =>
        RunAsync(document, options, factory, progress, new OcrRunnerSettings(), cancellationToken);

    internal static async Task<OcrRunResult> RunAsync(PdfDocument document, OcrOptions options, IOcrEngineFactory factory, IProgress<OcrProgress>? progress, OcrRunnerSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(factory);
        options.Validate();
        var pages = options.Pages is null
            ? Enumerable.Range(0, document.PageCount).ToList()
            : [.. options.Pages.Order()];
        if (pages.Any(page => page < 0 || page >= document.PageCount))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A page of the options is not a page of the document.");
        }

        using var run = new Run(document, options, factory, progress, settings, pages, cancellationToken);
        return await run.ExecuteAsync().ConfigureAwait(false);
    }

    private sealed class Run : IDisposable
    {
        private readonly PdfDocument document;
        private readonly OcrOptions options;
        private readonly string languages;
        private readonly IOcrEngineFactory factory;
        private readonly IProgress<OcrProgress>? progress;
        private readonly OcrRunnerSettings settings;
        private readonly List<int> pages;
        private readonly CancellationToken outer;
        private readonly CancellationTokenSource inner;
        private readonly SemaphoreSlim memorySlots;
        private readonly SemaphoreSlim workSlots;
        private readonly ConcurrentBag<IOcrEngine> idle = [];
        private readonly ConcurrentBag<OcrPageResult> results = [];
        private readonly ConcurrentBag<OcrSkippedPage> skipped = [];
        private readonly ConcurrentBag<int> littleText = [];
        private readonly List<Task> pending = [];
        private readonly object gate = new();
        private int done;
        private volatile bool closed;
        private OcrEngineException? fatal;

        public Run(PdfDocument document, OcrOptions options, IOcrEngineFactory factory, IProgress<OcrProgress>? progress, OcrRunnerSettings settings, List<int> pages, CancellationToken outer)
        {
            this.document = document;
            this.options = options;
            languages = OcrLanguages.Normalize(options.Languages)!;
            this.factory = factory;
            this.progress = progress;
            this.settings = settings;
            this.pages = pages;
            this.outer = outer;
            inner = CancellationTokenSource.CreateLinkedTokenSource(outer);
            var jobs = options.EffectiveJobs;
            memorySlots = new SemaphoreSlim(jobs + 1);
            workSlots = new SemaphoreSlim(jobs);
        }

        private CancellationToken Token => inner.Token;

        public async Task<OcrRunResult> ExecuteAsync()
        {
            try
            {
                // The first engine is made at once: a missing library or language file fails the run before any page is touched.
                idle.Add(factory.Create(languages));
                try
                {
                    foreach (var page in pages)
                    {
                        Token.ThrowIfCancellationRequested();
                        await PrepareAsync(page).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (Token.IsCancellationRequested)
                {
                    // Fall through: the pages in flight finish, then the run reports the cancel (or the fatal error that caused it).
                }

                await FinishAsync().ConfigureAwait(false);
                if (fatal is not null)
                {
                    throw fatal;
                }

                var ordered = results.OrderBy(static r => r.Page).ToList();
                var finished = new OcrRunResult(ordered, OcrReport.Build(ordered, [.. skipped], [.. littleText]));
                if (outer.IsCancellationRequested)
                {
                    // The pages that were done before the cancel are handed on: the user may choose to use them (never written without a question).
                    throw new OcrRunCanceledException(finished, outer);
                }

                return finished;
            }
            catch
            {
                await FinishAsync().ConfigureAwait(false);
                throw;
            }
        }

        public void Dispose()
        {
            closed = true;
            DisposeIdle();

            inner.Dispose();
            memorySlots.Dispose();
            workSlots.Dispose();
        }

        private void DisposeIdle()
        {
            while (idle.TryTake(out var engine))
            {
                engine.Dispose();
            }
        }

        private async Task FinishAsync()
        {
            Task[] open;
            lock (gate)
            {
                open = [.. pending];
            }

            foreach (var task in open)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or OcrEngineException)
                {
                    // Recorded by the page itself; the run decides at its end.
                }
            }
        }

        // Decides, resolves and renders one page (one short job after the other on the PDFium thread), then hands it to a worker.
        private async Task PrepareAsync(int page)
        {
            PageOcrInfo info;
            PdfPageSize size;
            try
            {
                info = await document.GetOcrPageInfoAsync(page, Token).ConfigureAwait(false);
                var decision = OcrPlanner.Decide(info, options.Mode);
                if (!decision.Run)
                {
                    Skip(page, decision.Reason!.Value, null);
                    return;
                }

                size = await document.GetPageSizeAsync(page, Token).ConfigureAwait(false);
                var raster = OcrResolution.Resolve(size.Width, size.Height, info.ImageDpi, options.Dpi);
                if (raster.TooLow)
                {
                    // The pixel limit pushed the page below the lowest resolution (a poster): "too large"; a resolution asked for below it: "too low".
                    Skip(page, raster.Lowered ? OcrSkipReason.TooLarge : OcrSkipReason.ResolutionTooLow, $"{raster.Dpi} dpi");
                    return;
                }

                await memorySlots.WaitAsync(Token).ConfigureAwait(false);
                byte[] gray;
                try
                {
                    var image = await document.RenderPageForOcrAsync(page, raster.Dpi, Token).ConfigureAwait(false);
                    gray = ToGray(image);
                }
                catch
                {
                    memorySlots.Release();
                    throw;
                }

                settings.PageRendered?.Invoke(page);
                var task = RecognizeAsync(page, gray, raster, size, decision.LittleText);
                lock (gate)
                {
                    pending.Add(task);
                }
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not ObjectDisposedException and not OutOfMemoryException)
            {
                Skip(page, OcrSkipReason.Failed, null);
            }
        }

        private async Task RecognizeAsync(int page, byte[] gray, OcrRaster raster, PdfPageSize size, bool little)
        {
            var slotHeld = false;
            try
            {
                await workSlots.WaitAsync(Token).ConfigureAwait(false);
                slotHeld = true;
                var work = Task.Factory.StartNew(() => RecognizeOnThread(gray, raster), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                using var stopWatch = new CancellationTokenSource();
                var limit = Task.Delay(settings.PageTimeout, settings.Clock, stopWatch.Token);
                var finished = await Task.WhenAny(work, limit).ConfigureAwait(false);
                await stopWatch.CancelAsync().ConfigureAwait(false);
                _ = limit.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

                if (finished != work)
                {
                    // The page took longer than the limit: skip it and let the engine of the stuck call go; its thread ends when the call does.
                    slotHeld = false;
                    workSlots.Release();
                    _ = work.ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
                    Skip(page, OcrSkipReason.Timeout, null);
                    return;
                }

                Accept(page, await work.ConfigureAwait(false), raster, size, little);
            }
            catch (OperationCanceledException) when (Token.IsCancellationRequested)
            {
                // The run is canceled: nothing of this page is kept.
            }
            catch (OcrEngineException e) when (e.Error is OcrEngineError.Timeout)
            {
                Skip(page, OcrSkipReason.Timeout, null);
            }
            catch (OcrEngineException e) when (IsFatal(e))
            {
                lock (gate)
                {
                    fatal ??= e;
                }

                await inner.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Skip(page, OcrSkipReason.Failed, null);
            }
            finally
            {
                if (slotHeld)
                {
                    workSlots.Release();
                }

                memorySlots.Release();
            }
        }

        private OcrRecognition RecognizeOnThread(byte[] gray, OcrRaster raster)
        {
            if (!idle.TryTake(out var engine))
            {
                engine = factory.Create(languages);
            }

            try
            {
                var recognition = engine.Recognize(gray, raster.WidthPx, raster.HeightPx, raster.Dpi, settings.PageTimeout, Token);
                idle.Add(engine);
                if (closed)
                {
                    // The run is over (this was a page that had timed out): nobody will take the engine again.
                    DisposeIdle();
                }

                return recognition;
            }
            catch
            {
                // The state of an engine after a failure is unknown: it is not used again.
                engine.Dispose();
                throw;
            }
        }

        private void Accept(int page, OcrRecognition recognition, OcrRaster raster, PdfPageSize size, bool little)
        {
            var words = new List<OcrPageWord>(recognition.Words.Count);
            long characters = 0;
            foreach (var word in recognition.Words)
            {
                var mapped = OcrWordMapper.ToPagePoints(word, raster, size.Width, size.Height);
                if (!mapped.IsUsable)
                {
                    continue;
                }

                words.Add(mapped);
                characters += mapped.Text.Length;
            }

            if (words.Count > settings.MaxWordsPerPage || characters > settings.MaxCharactersPerPage)
            {
                Skip(page, OcrSkipReason.TooLarge, null);
                return;
            }

            var mean = words.Count == 0 ? 0 : words.Average(static w => (double)w.Confidence);
            results.Add(new OcrPageResult(page, words, raster.Dpi, raster.Lowered, mean));
            if (little)
            {
                littleText.Add(page);
            }

            Report();
        }

        private void Skip(int page, OcrSkipReason reason, string? detail)
        {
            skipped.Add(new OcrSkippedPage(page, reason, detail));
            Report();
        }

        private void Report()
        {
            var count = Interlocked.Increment(ref done);
            progress?.Report(new OcrProgress(count, pages.Count, results.Count, skipped.Count));
        }

        private static bool IsFatal(OcrEngineException e) =>
            e.Error is OcrEngineError.LibraryMissing or OcrEngineError.LibraryUnusable or OcrEngineError.LanguageInvalid or OcrEngineError.LanguageMissing or OcrEngineError.DataCorrupt;

        // BGRA from the renderer to the gray the engine takes; the colour image is dropped as soon as this returns.
        private static byte[] ToGray(RenderedImage image)
        {
            var gray = new byte[image.Width * image.Height];
            var pixels = image.Pixels.Span;
            for (var y = 0; y < image.Height; y++)
            {
                var row = y * image.Stride;
                var target = y * image.Width;
                for (var x = 0; x < image.Width; x++)
                {
                    var i = row + (x * 4);
                    gray[target + x] = (byte)(((pixels[i] * 29) + (pixels[i + 1] * 150) + (pixels[i + 2] * 77)) >> 8);
                }
            }

            return gray;
        }
    }
}
