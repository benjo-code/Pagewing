using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class ViewerSessionTests
{
    /// <summary>User password of the encrypted PDFium test files (same value as in <c>OpenTests</c>).</summary>
    private const string UserPassword = "hôtel";

    // How long a test waits for something that must happen.
    private static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    // How long a test waits to show that something does not happen.
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(300);

    private readonly PdfEngine engine = new();

    [Fact]
    public async Task OpenReachesReadyAndLoadsAllPageSizes()
    {
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));

        var ready = recorder.Events.First(e => e.State == ViewerState.Ready);
        Assert.True(ready.Sizes >= 32, $"Ready came with only {ready.Sizes} page sizes.");
        Assert.Equal(ViewerState.Loading, recorder.Events[0].State);
        Assert.NotNull(session.Document);
        Assert.NotNull(session.Cache);
        Assert.NotNull(session.Scheduler);
        Assert.Equal(Path.GetFullPath(TestData.Pdf("thousand-pages.pdf")), session.Path);
        Assert.Null(session.Error);

        await recorder.WaitUntilAsync(() => session.LoadedSizes.Count == 1000);
        Assert.All(session.LoadedSizes, size => Assert.Equal(new PdfPageSize(200, 100), size));

        // Every Changed after Ready shows a growing prefix, never a shrinking or reset one.
        var sizes = recorder.Events.Select(e => e.Sizes).ToArray();
        Assert.Equal(sizes.Order(), sizes);
    }

    [Fact]
    public async Task EncryptedFileAsksForAPasswordThenAcceptsIt()
    {
        var path = TestData.Pdf("encrypted_hello_world_r3.pdf");
        using var session = new ViewerSession(engine);

        await session.OpenAsync(path);
        Assert.Equal(ViewerState.PasswordRequired, session.State);
        Assert.Equal(PdfOpenError.PasswordRequired, session.Error);
        Assert.Equal(Path.GetFullPath(path), session.Path);
        Assert.Null(session.Document);

        await session.OpenAsync(session.Path!, "wrong");
        Assert.Equal(ViewerState.PasswordIncorrect, session.State);
        Assert.Equal(PdfOpenError.PasswordIncorrect, session.Error);
        Assert.Equal(Path.GetFullPath(path), session.Path);
        Assert.Null(session.Document);

        await session.OpenAsync(session.Path!, UserPassword);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Null(session.Error);
        Assert.Equal(1, session.Document!.PageCount);
        Assert.Equal(new PdfPageSize(200, 200), session.LoadedSizes.Single());
    }

    [Fact]
    public async Task MissingFileFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(TestData.Pdf("does-not-exist.pdf"));

        AssertFailed(session, PdfOpenError.FileNotFound);
    }

    [Fact]
    public async Task NotAPdfFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));

        AssertFailed(session, PdfOpenError.NotAPdf);
    }

    [Fact]
    public async Task EmptyFileFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(TestData.Pdf("empty.pdf"));

        AssertFailed(session, PdfOpenError.NotAPdf);
    }

    [Fact]
    public async Task DirectoryInsteadOfFileFails()
    {
        using var session = new ViewerSession(engine);

        await session.OpenAsync(Path.Combine(TestData.Root, "testdata", "pdfs"));

        AssertFailed(session, PdfOpenError.CannotRead);
    }

    [Fact]
    public async Task InvalidPathCharactersMapToCannotRead()
    {
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        await session.OpenAsync("bad\0name.pdf");

        AssertFailed(session, PdfOpenError.CannotRead);
        Assert.Equal([ViewerState.Loading, ViewerState.Failed], recorder.Events.Select(e => e.State));
    }

    [Fact]
    public async Task TooLongPathMapsToCannotRead()
    {
        using var session = new ViewerSession(engine);

        // Longer than any supported path; Path.GetFullPath throws PathTooLongException (Windows) or the open fails.
        await session.OpenAsync(Path.Combine(TestData.Root, new string('a', 70_000), "x.pdf"));

        Assert.Equal(ViewerState.Failed, session.State);
        Assert.NotNull(session.Error);
        Assert.Null(session.Document);
    }

    [Fact]
    public async Task EmptyPathIsAnArgumentError()
    {
        using var session = new ViewerSession(engine);

        await Assert.ThrowsAsync<ArgumentException>(() => session.OpenAsync(string.Empty));
        Assert.Equal(ViewerState.Empty, session.State);
    }

    [Fact]
    public async Task PathWithUnicodeAndSpacesOpens()
    {
        var directory = Directory.CreateTempSubdirectory("pagewing-tests-");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory.FullName, "Ünï cödé"));
            var copy = Path.Combine(folder.FullName, "hello world ß.pdf");
            File.Copy(TestData.Pdf("hello_world.pdf"), copy);
            using var session = new ViewerSession(engine);

            await session.OpenAsync(copy);

            Assert.Equal(ViewerState.Ready, session.State);
            Assert.Equal(copy, session.Path);
            Assert.Equal(1, session.Document!.PageCount);

            // The file is closed again, so the folder can be removed.
            session.Close();
            await WaitForAsync(() => TryDelete(directory));
        }
        finally
        {
            if (directory.Exists)
            {
                TryDelete(directory);
            }
        }
    }

    [Fact]
    public async Task LatestOpenWins()
    {
        var first = TestData.Pdf("thousand-pages.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        var firstOpen = session.OpenAsync(first);
        var secondOpen = session.OpenAsync(second);
        await firstOpen;
        await secondOpen;

        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(Path.GetFullPath(second), session.Path);
        Assert.Equal(2, session.Document!.PageCount);

        // Nothing of the first file leaks in afterwards, not even from its background batches.
        var before = recorder.Count;
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.Equal(before, recorder.Count);
        Assert.Equal(2, session.LoadedSizes.Count);
        var lastSecondReady = recorder.Events.FindLastIndex(e => e.State == ViewerState.Ready);
        Assert.DoesNotContain(recorder.Events.Skip(lastSecondReady), e => e.Path == Path.GetFullPath(first));
    }

    [Fact]
    public async Task SupersededRequestThatFinishesLastChangesNothing()
    {
        var first = TestData.Pdf("hello_world.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(first);
        using var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var firstOpen = session.OpenAsync(first);
        await session.OpenAsync(second);
        await recorder.WaitUntilAsync(() => session.LoadedSizes.Count == 2);
        var events = recorder.Count;
        var currentDocument = session.Document;

        // The older open only now completes with a document. It must be closed, and nothing may change.
        gate.SetResult();
        await firstOpen;

        Assert.Equal(events, recorder.Count);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(Path.GetFullPath(second), session.Path);
        Assert.Same(currentDocument, session.Document);
        Assert.Throws<ObjectDisposedException>(() => gates.Opened(first).PageCount);
        Assert.Equal(2, currentDocument!.PageCount);
    }

    [Fact]
    public async Task SupersededFailureIsSilent()
    {
        var missing = TestData.Pdf("does-not-exist.pdf");
        var good = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(missing);
        using var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var failingOpen = session.OpenAsync(missing);
        await session.OpenAsync(good);
        var events = recorder.Count;

        gate.SetResult();
        await failingOpen;

        Assert.Equal(events, recorder.Count);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Null(session.Error);
    }

    [Fact]
    public async Task PreviousDocumentStaysOpenUntilTheNewOneIsAccepted()
    {
        var first = TestData.Pdf("hello_world.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(second);
        using var session = new ViewerSession(gates.Open);
        await session.OpenAsync(first);
        var firstDocument = session.Document!;
        var firstScheduler = session.Scheduler;

        var secondOpen = session.OpenAsync(second);

        Assert.Equal(ViewerState.Loading, session.State);
        Assert.Equal(Path.GetFullPath(second), session.Path);
        Assert.Same(firstDocument, session.Document);
        Assert.Equal(1, firstDocument.PageCount);

        gate.SetResult();
        await secondOpen;

        Assert.Equal(ViewerState.Ready, session.State);
        Assert.NotSame(firstDocument, session.Document);
        Assert.NotSame(firstScheduler, session.Scheduler);
        Assert.Throws<ObjectDisposedException>(() => firstDocument.PageCount);
    }

    [Fact]
    public async Task FailedOpenKeepsNothingOfTheOldDocument()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var oldDocument = session.Document!;
        Assert.Equal(ViewerState.Ready, session.State);

        await session.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));

        AssertFailed(session, PdfOpenError.NotAPdf);
        Assert.Empty(session.LoadedSizes);
        Assert.Throws<ObjectDisposedException>(() => oldDocument.PageCount);
    }

    [Fact]
    public async Task PasswordPromptKeepsNothingOfTheOldDocument()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var oldDocument = session.Document!;

        await session.OpenAsync(TestData.Pdf("encrypted_hello_world_r6.pdf"));

        Assert.Equal(ViewerState.PasswordRequired, session.State);
        Assert.Null(session.Document);
        Assert.Null(session.Scheduler);
        Assert.Null(session.Cache);
        Assert.Throws<ObjectDisposedException>(() => oldDocument.PageCount);
    }

    [Fact]
    public async Task CloseReturnsToEmptyAndReleasesTheDocument()
    {
        using var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var document = session.Document!;

        session.Close();

        Assert.Equal(ViewerState.Empty, session.State);
        Assert.Null(session.Path);
        Assert.Null(session.Error);
        Assert.Null(session.Document);
        Assert.Null(session.Cache);
        Assert.Null(session.Scheduler);
        Assert.Empty(session.LoadedSizes);
        Assert.Throws<ObjectDisposedException>(() => document.PageCount);
        Assert.Equal(ViewerState.Empty, recorder.Events[^1].State);

        // Closing an empty session is not a change.
        var events = recorder.Count;
        session.Close();
        Assert.Equal(events, recorder.Count);
    }

    [Fact]
    public async Task CloseCancelsAnOpenThatIsStillRunning()
    {
        var path = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(path);
        using var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var open = session.OpenAsync(path);
        session.Close();
        var events = recorder.Count;
        gate.SetResult();
        await open;

        Assert.Equal(events, recorder.Count);
        Assert.Equal(ViewerState.Empty, session.State);
        Assert.Throws<ObjectDisposedException>(() => gates.Opened(path).PageCount);
    }

    [Fact]
    public async Task NoChangedEventsAfterDispose()
    {
        var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);
        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        var document = session.Document!;

        // The remaining page sizes are probably still being loaded.
        session.Dispose();
        var events = recorder.Count;
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        Assert.Equal(events, recorder.Count);
        Assert.Throws<ObjectDisposedException>(() => document.PageCount);

        // A disposed session ignores further requests and can be disposed again.
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        session.Close();
        session.Dispose();
        Assert.Equal(events, recorder.Count);
        Assert.Null(session.Document);
    }

    [Fact]
    public async Task DisposeDuringAnOpenClosesTheDocumentItGets()
    {
        var path = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(path);
        var session = new ViewerSession(gates.Open);
        using var recorder = new Recorder(session);

        var open = session.OpenAsync(path);
        session.Dispose();
        var events = recorder.Count;
        gate.SetResult();
        await open;

        Assert.Equal(events, recorder.Count);
        Assert.Throws<ObjectDisposedException>(() => gates.Opened(path).PageCount);
    }

    [Fact]
    public async Task HandlerMayDisposeTheSession()
    {
        var session = new ViewerSession(engine);
        session.Changed += () =>
        {
            if (session.State == ViewerState.Ready)
            {
                session.Dispose();
            }
        };

        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));

        Assert.Null(session.Document);
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(PdfiumThreadViolationException))]
    [InlineData(typeof(IOException))]
    public async Task FailedBackgroundBatchFillsTheRestWithTheStandInAndReportsIt(Type exceptionType)
    {
        var failure = (Exception)Activator.CreateInstance(exceptionType, "batch failed")!;
        using var session = new ViewerSession(
            engine.OpenAsync,
            (document, firstPage, count, priority, cancellationToken) =>
                firstPage >= ViewerSession.FirstBatchSize + ViewerSession.BackgroundBatchSize
                    ? Task.FromException<IReadOnlyList<PdfPageSize>>(failure)
                    : document.GetPageSizesAsync(firstPage, count, priority, cancellationToken));
        using var recorder = new Recorder(session);

        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        await recorder.WaitUntilAsync(() => session.BackgroundLoadFailure is not null && session.LoadedSizes.Count == 1000);

        // 32 + 256 sizes were read, the pages after the failed batch report the A4 stand-in.
        var sizes = session.LoadedSizes;
        Assert.Same(failure, session.BackgroundLoadFailure);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.All(sizes.Take(288), size => Assert.Equal(new PdfPageSize(200, 100), size));
        Assert.All(sizes.Skip(288), size => Assert.Equal(new PdfPageSize(595.28, 841.89), size));

        // The completed list is announced exactly once, and nothing follows.
        var events = recorder.Events;
        Assert.Equal(1, events.Count(e => e.Sizes == 1000));
        Assert.Equal(1000, events[^1].Sizes);
        var count = recorder.Count;
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);
        Assert.Equal(count, recorder.Count);
    }

    [Theory]
    [InlineData(typeof(OperationCanceledException))]
    [InlineData(typeof(ObjectDisposedException))]
    public async Task CanceledOrDisposedBackgroundBatchStaysSilent(Type exceptionType)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new ViewerSession(
            engine.OpenAsync,
            async (document, firstPage, count, priority, cancellationToken) =>
            {
                if (firstPage < ViewerSession.FirstBatchSize)
                {
                    return await document.GetPageSizesAsync(firstPage, count, priority, cancellationToken);
                }

                entered.TrySetResult();
                await release.Task;
                throw (Exception)Activator.CreateInstance(exceptionType, "gone")!;
            });
        using var recorder = new Recorder(session);

        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        await entered.Task.WaitAsync(Due, TestContext.Current.CancellationToken);
        var count = recorder.Count;
        release.SetResult();
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        Assert.Equal(count, recorder.Count);
        Assert.Null(session.BackgroundLoadFailure);
        Assert.Equal(ViewerSession.FirstBatchSize, session.LoadedSizes.Count);
    }

    [Fact]
    public async Task FailedBackgroundBatchOfASupersededRequestStaysSilent()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new ViewerSession(
            engine.OpenAsync,
            async (document, firstPage, count, priority, cancellationToken) =>
            {
                if (document.PageCount != 1000 || firstPage < ViewerSession.FirstBatchSize)
                {
                    return await document.GetPageSizesAsync(firstPage, count, priority, cancellationToken);
                }

                entered.TrySetResult();
                await release.Task;
                throw new InvalidOperationException("late failure");
            });
        using var recorder = new Recorder(session);

        await session.OpenAsync(TestData.Pdf("thousand-pages.pdf"));
        await entered.Task.WaitAsync(Due, TestContext.Current.CancellationToken);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var count = recorder.Count;
        release.SetResult();
        await Task.Delay(Quiet, TestContext.Current.CancellationToken);

        Assert.Equal(count, recorder.Count);
        Assert.Null(session.BackgroundLoadFailure);
        Assert.Equal(2, session.LoadedSizes.Count);
    }

    [Fact]
    public async Task InactiveSessionKeepsAtMostTheInactiveBudget()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var cache = session.Cache!;
        Assert.Equal(TabBudget.ActiveBytes, cache.BudgetBytes);
        FillCache(cache, 4);
        Assert.True(cache.UsedBytes > TabBudget.InactiveBytes);

        session.SetActive(false);

        Assert.Equal(TabBudget.InactiveBytes, cache.BudgetBytes);
        Assert.True(cache.UsedBytes <= TabBudget.InactiveBytes);
        Assert.True(cache.UsedBytes > 0);
    }

    [Fact]
    public async Task ActivatingRestoresTheFullBudget()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        session.SetActive(false);

        session.SetActive(true);

        Assert.Equal(TabBudget.ActiveBytes, session.Cache!.BudgetBytes);
        FillCache(session.Cache, 4);
        Assert.True(session.Cache.UsedBytes > TabBudget.InactiveBytes);
    }

    [Fact]
    public async Task CacheOfADocumentOpenedWhileInactiveStartsSmall()
    {
        using var session = new ViewerSession(engine);
        session.SetActive(false);

        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));

        Assert.Equal(TabBudget.InactiveBytes, session.Cache!.BudgetBytes);

        // A document opened later while active gets the full budget again.
        session.SetActive(true);
        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));
        Assert.Equal(TabBudget.ActiveBytes, session.Cache!.BudgetBytes);
    }

    [Fact]
    public async Task SetActiveAfterDisposeDoesNothing()
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var cache = session.Cache!;
        session.Dispose();

        session.SetActive(false);

        // Dispose released the session's cache, so the budget of the captured cache cannot show the guard;
        // the flag can.
        Assert.True(session.IsActive);
        Assert.Equal(TabBudget.ActiveBytes, cache.BudgetBytes);
    }

    [Fact]
    public async Task EnsureThumbnailsIsNullBeforeReady()
    {
        using var session = new ViewerSession(engine);
        Assert.Null(session.EnsureThumbnails());

        await session.OpenAsync(TestData.Pdf("does-not-exist.pdf"));
        Assert.Null(session.EnsureThumbnails());

        await session.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        Assert.Equal(ViewerState.PasswordRequired, session.State);
        Assert.Null(session.EnsureThumbnails());

        session.Dispose();
        Assert.Null(session.EnsureThumbnails());
    }

    [Fact]
    public async Task EnsureThumbnailsReturnsTheSameSourceUntilTheDocumentChanges()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));

        var first = session.EnsureThumbnails();
        var second = session.EnsureThumbnails();

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(ThumbnailSource.BudgetBytes, first.Cache.BudgetBytes);
        Assert.NotSame(session.Cache, first.Cache);
        Assert.NotSame(session.Scheduler, first.Scheduler);

        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var third = session.EnsureThumbnails();

        Assert.NotNull(third);
        Assert.NotSame(first, third);
        Assert.True(first.IsDisposed);
        Assert.False(third.IsDisposed);
    }

    [Fact]
    public async Task ThumbnailsAreDisposedWithTheDocument()
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var beforeClose = session.EnsureThumbnails()!;

        session.Close();

        Assert.True(beforeClose.IsDisposed);
        Assert.Null(session.EnsureThumbnails());

        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var beforeDispose = session.EnsureThumbnails()!;
        Assert.False(beforeDispose.IsDisposed);

        session.Dispose();

        Assert.True(beforeDispose.IsDisposed);
        Assert.Null(session.EnsureThumbnails());
    }

    [Fact]
    public async Task EnsurePageTextsIsNullBeforeReady()
    {
        using var session = new ViewerSession(engine);
        Assert.Null(session.EnsurePageTexts());

        await session.OpenAsync(TestData.Pdf("does-not-exist.pdf"));
        Assert.Null(session.EnsurePageTexts());

        await session.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        Assert.Null(session.EnsurePageTexts());

        session.Dispose();
        Assert.Null(session.EnsurePageTexts());
    }

    [Fact]
    public async Task EnsurePageTextsReturnsTheSameSourceUntilTheDocumentChanges()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));

        var first = session.EnsurePageTexts();
        var second = session.EnsurePageTexts();

        Assert.NotNull(first);
        Assert.Same(first, second);
        var text = await first.GetAsync(0, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("Hello", text.GetText(0, text.Chars.Count), StringComparison.Ordinal);

        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var third = session.EnsurePageTexts();

        Assert.NotNull(third);
        Assert.NotSame(first, third);
        Assert.True(first.IsDisposed);
        Assert.False(third.IsDisposed);
    }

    [Fact]
    public async Task EnsurePageTextsIsDisposedWithTheDocument()
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var beforeClose = session.EnsurePageTexts()!;

        session.Close();

        Assert.True(beforeClose.IsDisposed);
        Assert.Null(session.EnsurePageTexts());

        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var beforeDispose = session.EnsurePageTexts()!;
        Assert.False(beforeDispose.IsDisposed);

        session.Dispose();

        Assert.True(beforeDispose.IsDisposed);
        Assert.Null(session.EnsurePageTexts());
    }

    [Fact]
    public async Task ThumbnailsRenderIntoTheirOwnCache()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var thumbnails = session.EnsureThumbnails()!;
        var ready = new TaskCompletionSource<TileKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        thumbnails.Scheduler.TileReady += key => ready.TrySetResult(key);
        var plan = ThumbnailPlanner.Plan(session.LoadedSizes, [0], 1.0);

        thumbnails.Scheduler.Update(plan);
        var key = await ready.Task.WaitAsync(Due, TestContext.Current.CancellationToken);

        Assert.Equal(new TileKey(0, TileKey.PreviewScaleKey, 0, 0), key);
        Assert.True(thumbnails.Cache.TryGet(key, out var image));
        Assert.Equal(plan[0].Rect.Width, image.Width);
        Assert.False(session.Cache!.Contains(key));
        Assert.Equal(0, session.Cache.Count);
    }

    [Fact]
    public async Task SessionInvalidatePagesDropsThumbnailsAndPageText()
    {
        var ct = TestContext.Current.CancellationToken;
        using var session = new ViewerSession(engine);
        session.SetActive(false);
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var cache = session.Cache!;
        var thumbnails = session.EnsureThumbnails()!;
        var pageTexts = session.EnsurePageTexts()!;
        for (var page = 0; page < 2; page++)
        {
            cache.Add(new TileKey(page, TileKey.PreviewScaleKey, 0, 0), SmallImage());
            cache.Add(new TileKey(page, 1000, 0, 0), SmallImage());
            thumbnails.Cache.Add(new TileKey(page, TileKey.PreviewScaleKey, 0, 0), SmallImage());
            await pageTexts.GetAsync(page, cancellationToken: ct);
        }

        session.InvalidatePages(new HashSet<int> { 0 });

        // Page 0 is gone from the view, the thumbnails and the page texts; page 1 keeps everything.
        Assert.False(cache.Contains(new TileKey(0, TileKey.PreviewScaleKey, 0, 0)));
        Assert.False(cache.Contains(new TileKey(0, 1000, 0, 0)));
        Assert.True(cache.Contains(new TileKey(1, TileKey.PreviewScaleKey, 0, 0)));
        Assert.True(cache.Contains(new TileKey(1, 1000, 0, 0)));
        Assert.False(thumbnails.Cache.Contains(new TileKey(0, TileKey.PreviewScaleKey, 0, 0)));
        Assert.True(thumbnails.Cache.Contains(new TileKey(1, TileKey.PreviewScaleKey, 0, 0)));
        Assert.False(pageTexts.TryGet(0, out _));
        Assert.True(pageTexts.TryGet(1, out _));

        // The sources stay what they were, with their budgets, so a running search or the pages panel goes on with them.
        Assert.Same(cache, session.Cache);
        Assert.Same(thumbnails, session.EnsureThumbnails());
        Assert.True(session.HasPageTexts);
        Assert.Same(pageTexts, session.EnsurePageTexts());
        Assert.False(thumbnails.IsDisposed);
        Assert.False(pageTexts.IsDisposed);
        Assert.Equal(TabBudget.InactiveBytes, cache.BudgetBytes);
        Assert.Equal(ThumbnailSource.BudgetBytes, thumbnails.Cache.BudgetBytes);

        // The text of page 0 is read again on the next request.
        var again = await pageTexts.GetAsync(0, cancellationToken: ct);
        Assert.Equal(0, again.PageIndex);
        Assert.True(pageTexts.TryGet(0, out _));
    }

    [Fact]
    public async Task InvalidatePagesRaisesChangedOnce()
    {
        var session = new ViewerSession(engine);
        using var recorder = new Recorder(session);

        // Without a document there is nothing to drop and nothing to report.
        session.InvalidatePages(new HashSet<int> { 0 });
        Assert.Equal(0, recorder.Count);

        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var before = recorder.Count;

        session.InvalidatePages(new HashSet<int> { 0, 1 });

        Assert.Equal(before + 1, recorder.Count);
        Assert.Equal(new Snapshot(ViewerState.Ready, session.Path, 2), recorder.Events[^1]);

        // An empty set changes nothing, a missing one is an error, and after Dispose nothing happens.
        session.InvalidatePages(new HashSet<int>());
        Assert.Equal(before + 1, recorder.Count);
        Assert.Throws<ArgumentNullException>(() => session.InvalidatePages(null!));
        session.Dispose();
        session.InvalidatePages(new HashSet<int> { 0 });
        Assert.Equal(before + 1, recorder.Count);
    }

    [Fact]
    public async Task OpenRemembersThePathStampAndCloseForgetsIt()
    {
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        using var session = new ViewerSession(engine);

        await session.OpenAsync(path);
        Assert.Equal(FileStamp.Of(path), session.OpenedStamp);
        Assert.Equal(session.OpenedStamp, session.Document!.PathStampAtOpen);
        Assert.Null(session.OpenPassword);
        var snapshot = session.SnapshotForSave()!;
        Assert.Same(session.Document, snapshot.Document);
        Assert.Equal(session.Path, snapshot.Path);
        Assert.Equal(session.OpenedStamp, snapshot.OpenedStamp);

        session.Close();
        Assert.Null(session.OpenedStamp);
        Assert.Null(session.SnapshotForSave());

        await session.OpenAsync(TestData.Pdf("does-not-exist.pdf"));
        Assert.Null(session.OpenedStamp);
    }

    [Fact]
    public async Task AdoptSavedDocumentKeepsTheCacheAndNeverShowsAnEmptyState()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        session.SetPageTheme(PageTheme.Dark);
        var cache = session.Cache!;
        var oldScheduler = session.Scheduler!;
        var sizes = session.LoadedSizes;
        var pageTexts = session.EnsurePageTexts()!;
        await pageTexts.GetAsync(0, cancellationToken: ct);
        var thumbnails = session.EnsureThumbnails()!;
        cache.Add(new TileKey(0, 1000, 0, 0), SmallImage());
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);
        using var recorder = new Recorder(session);

        // Another thread reads the session all the time: it must always see a ready session with a document.
        var stop = 0;
        var gaps = 0;
        var reads = 0;
        var reader = Task.Run(
            () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    var state = session.State;
                    var document = session.Document;
                    if (state != ViewerState.Ready || document is null || session.Cache is null || session.Scheduler is null)
                    {
                        Interlocked.Increment(ref gaps);
                    }

                    Interlocked.Increment(ref reads);
                }
            },
            ct);
        await WaitForAsync(() => Volatile.Read(ref reads) > 100);

        await session.AdoptSavedDocumentAsync(reopened, path);
        Volatile.Write(ref stop, 1);
        await reader;

        Assert.Equal(0, gaps);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Same(reopened, session.Document);
        Assert.Same(cache, session.Cache);
        Assert.True(cache.Contains(new TileKey(0, 1000, 0, 0)));
        Assert.NotSame(oldScheduler, session.Scheduler);
        Assert.Equal(PageTheme.Dark, session.Scheduler!.PageTheme);
        Assert.Equal(sizes, session.LoadedSizes);
        Assert.Equal(Path.GetFullPath(path), session.Path);
        Assert.Null(session.Error);

        // Changed exactly once, with the ready state.
        Assert.Equal([new Snapshot(ViewerState.Ready, Path.GetFullPath(path), 2)], recorder.Events);

        // The texts stay (a search may be using them) and now read the new document; the thumbnails are made anew.
        Assert.Same(pageTexts, session.EnsurePageTexts());
        Assert.True(pageTexts.TryGet(0, out _));
        var second = await pageTexts.GetAsync(1, cancellationToken: ct);
        Assert.Equal(1, second.PageIndex);
        Assert.True(thumbnails.IsDisposed);
        var newThumbnails = session.EnsureThumbnails();
        Assert.NotNull(newThumbnails);
        Assert.NotSame(thumbnails, newThumbnails);
        Assert.Equal(PageTheme.Dark, newThumbnails.PageTheme);
    }

    [Fact]
    public async Task AdoptSavedDocumentClosesTheOldDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "hello_world.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var old = session.Document!;
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);

        Assert.True(await session.AdoptSavedDocumentAsync(reopened, path, replacing: old));

        Assert.Throws<ObjectDisposedException>(() => old.PageCount);
        Assert.Equal(1, reopened.PageCount);

        // The new scheduler renders the new document into the same cache.
        var ready = new TaskCompletionSource<TileKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Scheduler!.TileReady += key => ready.TrySetResult(key);
        var plan = ThumbnailPlanner.Plan(session.LoadedSizes, [0], 1.0);
        session.Scheduler.Update(plan);
        var key = await ready.Task.WaitAsync(Due, ct);
        Assert.True(session.Cache!.Contains(key));

        // Closing the session closes the adopted document too.
        session.Close();
        Assert.Throws<ObjectDisposedException>(() => reopened.PageCount);
    }

    [Fact]
    public async Task PasswordProtectedDocumentIsAdoptedWithoutAskingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "encrypted_hello_world_r6.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path, UserPassword);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(UserPassword, session.OpenPassword);
        Assert.Equal(UserPassword, session.SnapshotForSave()!.Password);
        Assert.DoesNotContain(UserPassword, session.SnapshotForSave()!.ToString(), StringComparison.Ordinal);
        var editor = new AnnotationEditor(session);
        using var recorder = new Recorder(session);

        // Two saves in a row: each reopens the file with the password the session keeps in memory.
        for (var round = 1; round <= 2; round++)
        {
            await editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(20, 20 * round, 120, (20 * round) + 15)], new AnnotationColor(255, 230, 0), "Saved", null);
            await editor.SaveAsync(engine, path, overwriteChangedOriginal: false, progress: null, ct);

            Assert.Equal(ViewerState.Ready, session.State);
            Assert.Equal(round, (await session.Document!.GetAnnotationsAsync(0, ct)).Count);
            Assert.True((await session.Document.GetPrintInfoAsync(ct)).IsEncrypted);
        }

        Assert.All(recorder.Events, e => Assert.Equal(ViewerState.Ready, e.State));
        Assert.Equal(UserPassword, session.OpenPassword);
        Assert.Null(session.Error);

        // Close forgets the password.
        session.Close();
        Assert.Null(session.OpenPassword);
    }

    [Fact]
    public async Task AdoptSavedDocumentAfterDisposeClosesTheReopenedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "hello_world.pdf");
        var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);
        session.Dispose();

        Assert.False(await session.AdoptSavedDocumentAsync(reopened, path));

        Assert.Throws<ObjectDisposedException>(() => reopened.PageCount);
        Assert.Null(session.Document);
    }

    [Fact]
    public async Task AdoptSavedDocumentDuringANewerOpenClosesTheReopenedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "hello_world.pdf");
        var other = TestData.Pdf("text-basic.pdf");
        var gates = new GatedOpen(engine);
        var gate = gates.Gate(other);
        using var session = new ViewerSession(gates.Open);
        await session.OpenAsync(path);
        var old = session.Document!;
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);
        var newer = session.OpenAsync(other);

        Assert.False(await session.AdoptSavedDocumentAsync(reopened, path));
        Assert.Throws<ObjectDisposedException>(() => reopened.PageCount);
        Assert.Same(old, session.Document);
        Assert.Equal(ViewerState.Loading, session.State);

        gate.SetResult();
        await newer;
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(Path.GetFullPath(other), session.Path);

        // A document that is no longer the one that was saved is not replaced either.
        var again = await engine.OpenAsync(path, cancellationToken: ct);
        Assert.False(await session.AdoptSavedDocumentAsync(again, path, replacing: old));
        Assert.Throws<ObjectDisposedException>(() => again.PageCount);
        Assert.Equal(Path.GetFullPath(other), session.Path);
    }

    [Fact]
    public async Task AdoptSavedDocumentWithAnotherPageCountIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var old = session.Document!;
        var wrong = await engine.OpenAsync(TestData.Pdf("hello_world.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<ArgumentException>(() => session.AdoptSavedDocumentAsync(wrong, path));

        Assert.Throws<ObjectDisposedException>(() => wrong.PageCount);
        Assert.Same(old, session.Document);
        Assert.Equal(ViewerState.Ready, session.State);
    }

    [Fact]
    public async Task AdoptDuringBackgroundLoadingReadsTheRestFromTheNewDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readFrom = new List<PdfDocument>();
        using var session = new ViewerSession(
            engine.OpenAsync,
            async (document, firstPage, count, priority, cancellationToken) =>
            {
                if (firstPage > 0)
                {
                    lock (readFrom)
                    {
                        readFrom.Add(document);
                    }

                    await release.Task.WaitAsync(cancellationToken);
                }

                return await document.GetPageSizesAsync(firstPage, count, priority, cancellationToken);
            });
        using var recorder = new Recorder(session);
        var path = TestData.Pdf("thousand-pages.pdf");
        await session.OpenAsync(path);
        var old = session.Document!;
        await WaitForAsync(() =>
        {
            lock (readFrom)
            {
                return readFrom.Count == 1;
            }
        });
        Assert.Equal(ViewerSession.FirstBatchSize, session.LoadedSizes.Count);
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);

        await session.AdoptSavedDocumentAsync(reopened, path);
        release.SetResult();
        await recorder.WaitUntilAsync(() => session.LoadedSizes.Count == 1000);

        Assert.All(session.LoadedSizes, size => Assert.Equal(new PdfPageSize(200, 100), size));
        lock (readFrom)
        {
            Assert.Same(old, readFrom[0]);
            Assert.All(readFrom.Skip(1), document => Assert.Same(reopened, document));
            Assert.True(readFrom.Count > 1);
        }

        Assert.Null(session.BackgroundLoadFailure);
    }

    private static string CopyTo(TempFolder folder, string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private static RenderedImage SmallImage() => new(1, 1, 4, new byte[4]);

    // Fills the cache with images of 8 MiB each (they are not rendered, only stored).
    private static void FillCache(RenderCache cache, int count)
    {
        for (var i = 0; i < count; i++)
        {
            cache.Add(new TileKey(i, 1000, 0, 0), new RenderedImage(1, 1, 4, new byte[8 * 1024 * 1024]));
        }
    }

    private static void AssertFailed(ViewerSession session, PdfOpenError error)
    {
        Assert.Equal(ViewerState.Failed, session.State);
        Assert.Equal(error, session.Error);
        Assert.Null(session.Document);
        Assert.Null(session.Cache);
        Assert.Null(session.Scheduler);
        Assert.Empty(session.LoadedSizes);
        Assert.NotNull(session.Path);
    }

    private static bool TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Due;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private readonly record struct Snapshot(ViewerState State, string? Path, int Sizes);

    /// <summary>Records what a handler of <see cref="ViewerSession.Changed"/> sees, and waits for conditions.</summary>
    private sealed class Recorder : IDisposable
    {
        private readonly ViewerSession session;
        private readonly object gate = new();
        private readonly List<Snapshot> events = [];
        private readonly SemaphoreSlim signal = new(0);

        public Recorder(ViewerSession session)
        {
            this.session = session;
            session.Changed += OnChanged;
        }

        public List<Snapshot> Events
        {
            get
            {
                lock (gate)
                {
                    return [.. events];
                }
            }
        }

        public int Count
        {
            get
            {
                lock (gate)
                {
                    return events.Count;
                }
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Due;
            while (!condition())
            {
                var remaining = deadline - DateTime.UtcNow;
                Assert.True(
                    remaining > TimeSpan.Zero && await signal.WaitAsync(remaining, TestContext.Current.CancellationToken),
                    $"The condition did not become true in time (state {session.State}, {session.LoadedSizes.Count} sizes).");
            }
        }

        public void Dispose()
        {
            session.Changed -= OnChanged;
            signal.Dispose();
        }

        private void OnChanged()
        {
            lock (gate)
            {
                events.Add(new Snapshot(session.State, session.Path, session.LoadedSizes.Count));
            }

            try
            {
                signal.Release();
            }
            catch (ObjectDisposedException)
            {
                // The test is over.
            }
        }
    }

    /// <summary>
    /// Wraps the real engine so that a test can hold back the result of one open. The held open ignores the
    /// cancellation token, like an open that has already started on the PDFium thread.
    /// </summary>
    private sealed class GatedOpen(PdfEngine engine)
    {
        private readonly object gate = new();
        private readonly Dictionary<string, TaskCompletionSource> gates = [];
        private readonly Dictionary<string, PdfDocument> opened = [];

        public TaskCompletionSource Gate(string path)
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                gates[Path.GetFullPath(path)] = source;
            }

            return source;
        }

        public PdfDocument Opened(string path)
        {
            lock (gate)
            {
                return opened[Path.GetFullPath(path)];
            }
        }

        public async Task<PdfDocument> Open(string path, string? password, CancellationToken cancellationToken)
        {
            TaskCompletionSource? held;
            lock (gate)
            {
                gates.TryGetValue(path, out held);
            }

            if (held is not null)
            {
                await held.Task;
            }

            var document = await engine.OpenAsync(path, password, held is null ? cancellationToken : CancellationToken.None);
            lock (gate)
            {
                opened[path] = document;
            }

            return document;
        }
    }
}
