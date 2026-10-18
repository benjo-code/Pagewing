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
    public async Task DisposedSourceThrowsAndDoesNotCacheALoadThatWasRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-dense.pdf"), cancellationToken: ct);
        var source = new PageTextSource(document);
        var raised = 0;
        source.Loaded += _ => Interlocked.Increment(ref raised);
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
        Assert.Equal(1, (await running.WaitAsync(Due, ct)).PageIndex);
        Assert.Equal(0, source.Count);
        Assert.Equal(1, Volatile.Read(ref raised));
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
