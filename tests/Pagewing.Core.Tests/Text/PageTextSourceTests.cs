using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Text;

public sealed class PageTextSourceTests
{
    private static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    private readonly PdfEngine engine = new();

    [Fact]
    public async Task PageTextSourceCachesAndJoinsLoads()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        var loaded = new List<int>();
        source.Loaded += page =>
        {
            lock (loaded)
            {
                loaded.Add(page);
            }
        };
        Assert.False(source.TryGet(0, out _));

        // Both calls start before the first load can have finished, so the second joins it.
        var first = source.GetAsync(0, cancellationToken: ct);
        var second = source.GetAsync(0, cancellationToken: ct);
        var texts = await Task.WhenAll(first, second).WaitAsync(Due, ct);

        Assert.Same(texts[0], texts[1]);
        Assert.Equal("Pagewing Handbuch", texts[0].GetText(0, 17));
        Assert.True(source.TryGet(0, out var cached));
        Assert.Same(texts[0], cached);

        // Later calls come from the cache.
        Assert.Same(texts[0], await source.GetAsync(0, cancellationToken: ct));
        Assert.Equal([0], loaded);
    }

    [Fact]
    public async Task ManyParallelCallsForOnePageLoadItOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        var count = 0;
        source.Loaded += _ => Interlocked.Increment(ref count);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => source.GetAsync(5, cancellationToken: ct), ct)).ToArray();
        var texts = await Task.WhenAll(tasks).WaitAsync(Due, ct);

        Assert.All(texts, t => Assert.Same(texts[0], t));
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task PageTextSourceKeepsAtMostThirtyTwoPages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);

        for (var page = 0; page < 40; page++)
        {
            await source.GetAsync(page, PdfPriority.Background, ct);
        }

        Assert.Equal(PageTextSource.MaxCachedPages, source.Count);
        Assert.False(source.TryGet(0, out _));
        Assert.False(source.TryGet(7, out _));
        Assert.True(source.TryGet(8, out _));
        Assert.True(source.TryGet(39, out var last));
        Assert.Equal(39, last.PageIndex);
    }

    [Fact]
    public async Task UsingAPageKeepsItInTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        for (var page = 0; page < PageTextSource.MaxCachedPages; page++)
        {
            await source.GetAsync(page, PdfPriority.Background, ct);
        }

        Assert.True(source.TryGet(0, out _)); // page 0 was the oldest; now page 1 is
        await source.GetAsync(PageTextSource.MaxCachedPages, PdfPriority.Background, ct);

        Assert.True(source.TryGet(0, out _));
        Assert.False(source.TryGet(1, out _));
        Assert.True(source.TryGet(PageTextSource.MaxCachedPages, out _));
    }

    [Fact]
    public async Task LoadedIsRaisedOnAPoolThreadAfterTheTextIsCached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        var raised = new TaskCompletionSource<(int Page, bool Cached, bool Pool)>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Loaded += page => raised.TrySetResult((page, source.TryGet(page, out _), Thread.CurrentThread.IsThreadPoolThread));

        await source.GetAsync(1, cancellationToken: ct);
        var (page, cached, pool) = await raised.Task.WaitAsync(Due, ct);

        Assert.Equal(1, page);
        Assert.True(cached);
        Assert.True(pool);
    }

    [Fact]
    public async Task ExceptionsOfLoadedHandlersAreSwallowed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Loaded += _ => throw new InvalidOperationException("handler failure");
        source.Loaded += _ => second.TrySetResult();

        var text = await source.GetAsync(0, cancellationToken: ct);

        await second.Task.WaitAsync(Due, ct);
        Assert.Equal(0, text.PageIndex);
        Assert.True(source.TryGet(0, out _));
    }

    [Fact]
    public async Task AFailedLoadIsNotCachedAndTheNextCallReadsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        var raised = 0;
        source.Loaded += _ => Interlocked.Increment(ref raised);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GetAsync(99, cancellationToken: ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GetAsync(99, cancellationToken: ct));
        Assert.False(source.TryGet(99, out _));
        Assert.Equal(0, source.Count);

        // The source still works for valid pages.
        await source.GetAsync(0, cancellationToken: ct);
        Assert.Equal(1, source.Count);
        Assert.Equal(1, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task ACanceledCallerDoesNotCancelTheSharedLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        using var quitter = new CancellationTokenSource();

        var impatient = source.GetAsync(3, cancellationToken: quitter.Token);
        var patient = source.GetAsync(3, cancellationToken: ct);
        await quitter.CancelAsync();

        var text = await patient.WaitAsync(Due, ct);
        Assert.Equal(3, text.PageIndex);
        Assert.True(source.TryGet(3, out _));

        // The impatient caller either stopped waiting or, if the load was quicker than the cancellation, got the text.
        try
        {
            Assert.Same(text, await impatient.WaitAsync(Due, ct));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.True(impatient.IsCanceled);
        }

        // A token that is canceled before the call starts no load.
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAsync(4, cancellationToken: canceled.Token));
        Assert.False(source.TryGet(4, out _));
    }

    [Fact]
    public async Task ClearForgetsEveryPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        await source.GetAsync(0, cancellationToken: ct);
        await source.GetAsync(1, cancellationToken: ct);

        source.Clear();

        Assert.Equal(0, source.Count);
        Assert.False(source.TryGet(0, out _));
        Assert.False(source.TryGet(1, out _));
        Assert.NotNull(await source.GetAsync(0, cancellationToken: ct));
        Assert.True(source.TryGet(0, out _));
    }

    [Fact]
    public async Task RemovePagesForgetsThosePagesAndALoadThatWasRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-rich.pdf"), cancellationToken: ct);
        var stale = await document.GetPageTextAsync(1, ct);

        // The first load of page 1 is held back until the test lets it go, so the removal comes while it runs.
        var gate = new TaskCompletionSource<PageText>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readsOfPageOne = 0;
        using var source = new PageTextSource(document, (page, priority) =>
            page == 1 && Interlocked.Increment(ref readsOfPageOne) == 1 ? gate.Task : document.GetPageTextAsync(page, priority, CancellationToken.None));
        var raised = new List<int>();
        source.Loaded += page =>
        {
            lock (raised)
            {
                raised.Add(page);
            }
        };
        await source.GetAsync(0, cancellationToken: ct);
        var running = source.GetAsync(1, cancellationToken: ct);

        source.RemovePages(new HashSet<int> { 0, 1 });

        Assert.Equal(0, source.Count);
        Assert.False(source.TryGet(0, out _));

        // A call after the removal does not join the old load but reads the page again.
        var fresh = await source.GetAsync(1, cancellationToken: ct).WaitAsync(Due, ct);
        Assert.NotSame(stale, fresh);
        Assert.Equal(2, Volatile.Read(ref readsOfPageOne));

        // The old load still answers the caller that waited for it, but its text is not cached over the new one.
        gate.SetResult(stale);
        Assert.Same(stale, await running.WaitAsync(Due, ct));
        Assert.True(source.TryGet(1, out var kept));
        Assert.Same(fresh, kept);
        Assert.Equal(1, source.Count);
        lock (raised)
        {
            Assert.Equal([0, 1], raised);
        }

        // An empty set changes nothing; after Dispose the call does nothing either.
        source.RemovePages(new HashSet<int>());
        Assert.Equal(1, source.Count);
        source.Dispose();
        source.RemovePages(new HashSet<int> { 1 });
    }

    [Fact]
    public async Task DisposedSourceThrowsAndDoesNotCacheALoadThatWasRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);
        var pageOne = await document.GetPageTextAsync(1, ct);

        // Page 1 is held back until the test lets it go, so Dispose always comes while that load is running.
        var gate = new TaskCompletionSource<PageText>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new PageTextSource(document, (page, priority) => page == 1 ? gate.Task : document.GetPageTextAsync(page, priority, CancellationToken.None));
        var raised = new List<int>();
        source.Loaded += page =>
        {
            lock (raised)
            {
                raised.Add(page);
            }
        };
        await source.GetAsync(0, cancellationToken: ct);

        var running = source.GetAsync(1, cancellationToken: ct);
        source.Dispose();
        source.Dispose();

        Assert.Throws<ObjectDisposedException>(() => source.TryGet(0, out _));
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = source.GetAsync(0, cancellationToken: ct);
        });
        Assert.Equal(0, source.Count);

        // The caller that already waits still gets its text; it is not cached and raises nothing.
        gate.SetResult(pageOne);
        Assert.Same(pageOne, await running.WaitAsync(Due, ct));
        Assert.Equal(0, source.Count);
        lock (raised)
        {
            Assert.Equal([0], raised);
        }
    }

    [Fact]
    public async Task ADisposedDocumentFailsTheLoadWithoutCachingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new PageTextSource(document);
        document.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetAsync(0, cancellationToken: ct));
        Assert.Equal(0, source.Count);
    }
}
