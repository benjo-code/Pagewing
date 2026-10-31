using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Viewing;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

/// <summary>
/// <see cref="ViewerSession.AdoptRewrittenDocumentAsync"/>: after a full rewrite the document has other pages, so the
/// session starts over with a new cache, new sizes and new thumbnails, without a moment outside <see cref="ViewerState.Ready"/>.
/// </summary>
public sealed class ViewerSessionRewriteTests
{
    private static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    private readonly PdfEngine engine = new();

    [Fact]
    public async Task AdoptRewrittenReplacesDocumentCacheAndSizes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        var rewritten = CopyTo(folder, "hello_world.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        session.SetActive(false);
        session.SetPageTheme(PageTheme.Dark);
        var old = session.Document!;
        var oldCache = session.Cache!;
        var oldScheduler = session.Scheduler!;
        var oldCount = old.PageCount;
        var key = new TileKey(0, 1000, 0, 0);
        oldCache.Add(key, new RenderedImage(1, 1, 4, new byte[4]));
        var pageTexts = session.EnsurePageTexts()!;
        await pageTexts.GetAsync(0, cancellationToken: ct);
        var reopened = await engine.OpenAsync(rewritten, cancellationToken: ct);
        Assert.NotEqual(oldCount, reopened.PageCount);
        var expectedSizes = await reopened.GetPageSizesAsync(0, reopened.PageCount, PdfPriority.Interactive, ct);

        Assert.True(await session.AdoptRewrittenDocumentAsync(reopened, rewritten, replacing: old));

        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Same(reopened, session.Document);
        Assert.NotSame(oldCache, session.Cache);
        Assert.NotSame(oldScheduler, session.Scheduler);
        Assert.False(session.Cache!.Contains(key));
        Assert.Equal(0, oldCache.Count);
        Assert.Equal(TabBudget.For(active: false), session.Cache.BudgetBytes);
        Assert.Equal(PageTheme.Dark, session.Scheduler!.PageTheme);
        Assert.Equal(expectedSizes, session.LoadedSizes);
        Assert.Equal(reopened.PageCount, session.LoadedSizes.Count);
        Assert.Equal(Path.GetFullPath(rewritten), session.Path);
        Assert.Equal(FileStamp.Of(Path.GetFullPath(rewritten)), session.OpenedStamp);
        Assert.Null(session.Error);
        Assert.Null(session.BackgroundLoadFailure);
        Assert.Throws<ObjectDisposedException>(() => old.PageCount);

        // The texts belong to the old pages: the source is replaced, not rebound.
        Assert.True(pageTexts.IsDisposed);
        Assert.NotSame(pageTexts, session.EnsurePageTexts());

        // The budget follows the tab from now on.
        session.SetActive(true);
        Assert.Equal(TabBudget.For(active: true), session.Cache.BudgetBytes);
    }

    [Fact]
    public async Task AdoptRewrittenReadsTheRemainingSizesInBatchesFromTheNewDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "hello_world.pdf");
        var rewritten = CopyTo(folder, "thousand-pages.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var reopened = await engine.OpenAsync(rewritten, cancellationToken: ct);
        var batches = new List<int>();
        session.Changed += () =>
        {
            lock (batches)
            {
                batches.Add(session.LoadedSizes.Count);
            }
        };

        Assert.True(await session.AdoptRewrittenDocumentAsync(reopened, rewritten));

        // Ready at once with the first batch only; the rest follows and the prefix only grows.
        Assert.True(session.LoadedSizes.Count >= ViewerSession.FirstBatchSize);
        await WaitForAsync(() => session.LoadedSizes.Count == 1000);
        Assert.All(session.LoadedSizes, size => Assert.Equal(new PdfPageSize(200, 100), size));
        lock (batches)
        {
            Assert.Equal(batches.Order(), batches);
            Assert.True(batches.Count > 1);
        }

        Assert.Null(session.BackgroundLoadFailure);
    }

    [Fact]
    public async Task AdoptRewrittenIgnoresASupersededDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        var other = CopyTo(folder, "hello_world.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var old = session.Document!;
        await session.OpenAsync(other);
        var current = session.Document!;
        var cache = session.Cache!;
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);
        var raised = 0;
        session.Changed += () => Interlocked.Increment(ref raised);

        Assert.False(await session.AdoptRewrittenDocumentAsync(reopened, path, replacing: old));

        Assert.Throws<ObjectDisposedException>(() => reopened.PageCount);
        Assert.Same(current, session.Document);
        Assert.Same(cache, session.Cache);
        Assert.Equal(Path.GetFullPath(other), session.Path);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.Equal(0, Volatile.Read(ref raised));

        // A closed session has nothing to replace either.
        session.Close();
        var again = await engine.OpenAsync(path, cancellationToken: ct);
        Assert.False(await session.AdoptRewrittenDocumentAsync(again, path));
        Assert.Throws<ObjectDisposedException>(() => again.PageCount);
        Assert.Equal(ViewerState.Empty, session.State);
    }

    [Fact]
    public async Task AdoptRewrittenAfterDisposeDoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var reopened = await engine.OpenAsync(path, cancellationToken: ct);
        var raised = 0;
        session.Changed += () => Interlocked.Increment(ref raised);
        session.Dispose();

        Assert.False(await session.AdoptRewrittenDocumentAsync(reopened, path));

        Assert.Throws<ObjectDisposedException>(() => reopened.PageCount);
        Assert.Null(session.Document);
        Assert.Null(session.Cache);
        Assert.Equal(0, Volatile.Read(ref raised));
    }

    [Fact]
    public async Task AdoptRewrittenRaisesChangedOnceAndNeverLeavesReady()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        var rewritten = CopyTo(folder, "hello_world.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        var reopened = await engine.OpenAsync(rewritten, cancellationToken: ct);
        var events = new List<(ViewerState State, int Sizes, bool HasDocument)>();
        session.Changed += () =>
        {
            lock (events)
            {
                events.Add((session.State, session.LoadedSizes.Count, session.Document is not null));
            }
        };

        // Another thread reads the session all the time: it must always see a ready session with everything in place.
        var stop = 0;
        var gaps = 0;
        var reads = 0;
        var reader = Task.Run(
            () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    if (session.State != ViewerState.Ready || session.Document is null || session.Cache is null || session.Scheduler is null)
                    {
                        Interlocked.Increment(ref gaps);
                    }

                    Interlocked.Increment(ref reads);
                }
            },
            ct);
        await WaitForAsync(() => Volatile.Read(ref reads) > 100);

        await session.AdoptRewrittenDocumentAsync(reopened, rewritten);
        Volatile.Write(ref stop, 1);
        await reader;

        Assert.Equal(0, gaps);
        lock (events)
        {
            Assert.Equal([(ViewerState.Ready, 1, true)], events);
        }
    }

    [Fact]
    public async Task ThumbnailsRestartForTheNewDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        var path = CopyTo(folder, "text-basic.pdf");
        var rewritten = CopyTo(folder, "hello_world.pdf");
        using var session = new ViewerSession(engine);
        await session.OpenAsync(path);
        session.SetPageTheme(PageTheme.Dark);
        var thumbnails = session.EnsureThumbnails()!;
        thumbnails.Cache.Add(new TileKey(0, TileKey.PreviewScaleKey, 0, 0), new RenderedImage(1, 1, 4, new byte[4]));
        var reopened = await engine.OpenAsync(rewritten, cancellationToken: ct);

        Assert.True(await session.AdoptRewrittenDocumentAsync(reopened, rewritten));

        Assert.True(thumbnails.IsDisposed);
        Assert.Equal(0, thumbnails.Cache.Count);
        var restarted = session.EnsureThumbnails();
        Assert.NotNull(restarted);
        Assert.NotSame(thumbnails, restarted);
        Assert.Equal(PageTheme.Dark, restarted.PageTheme);
        Assert.Equal(0, restarted.Cache.Count);

        // The new thumbnails render the new document.
        var ready = new TaskCompletionSource<TileKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.Scheduler.TileReady += key => ready.TrySetResult(key);
        restarted.Scheduler.Update(ThumbnailPlanner.Plan(session.LoadedSizes, [0], 1.0));
        var done = await ready.Task.WaitAsync(Due, ct);
        Assert.True(restarted.Cache.Contains(done));
    }

    private static string CopyTo(TempFolder folder, string fixture)
    {
        var path = Path.Combine(folder.Path, fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
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
}
