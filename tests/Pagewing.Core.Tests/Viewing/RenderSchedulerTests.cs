using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class RenderSchedulerTests
{
    private static RenderRequest Request(int page, bool background = false) =>
        new(new TileKey(page, 1000, 0, 0), 1.0, new PixelRect(0, 0, 4, 4), true, background);

    private static RenderedImage Image() => new(1, 1, 4, new byte[4]);

    [Fact]
    public async Task RunsOneRenderAtATime()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1), Request(2), Request(3)]);

        for (var page = 1; page <= 3; page++)
        {
            var call = await renderer.NextCallAsync();
            Assert.Equal(page, call.Request.Key.Page);
            await renderer.AssertNoCallAsync();
            call.Complete(Image());
            Assert.Equal(call.Request.Key, await events.NextReadyAsync());
        }

        Assert.Equal(1, renderer.MaxActive);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task RendersInTheOrderOfTheList()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        int[] pages = [7, 3, 9, 1, 5];

        scheduler.Update(pages.Select(page => Request(page)).ToArray());

        var order = new List<int>();
        foreach (var _ in pages)
        {
            var call = await renderer.NextCallAsync();
            order.Add(call.Request.Key.Page);
            call.Complete(Image());
        }

        Assert.Equal(pages, order);
    }

    [Fact]
    public async Task ReprioritizesQueuedRequestsOnUpdate()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());

        scheduler.Update([Request(1), Request(2), Request(3), Request(4)]);
        var first = await renderer.NextCallAsync();
        Assert.Equal(1, first.Request.Key.Page);

        // Page 1 is still wanted and keeps running; the queued rest is reordered.
        scheduler.Update([Request(1), Request(4), Request(2), Request(3)]);
        first.Complete(Image());

        var order = new List<int>();
        for (var i = 0; i < 3; i++)
        {
            var call = await renderer.NextCallAsync();
            order.Add(call.Request.Key.Page);
            call.Complete(Image());
        }

        Assert.Equal([4, 2, 3], order);
        Assert.False(first.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task UpdateDropsQueuedRequestsThatAreNoLongerWanted()
    {
        var renderer = new FakeRenderer { HonorsCancellation = false };
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var events = new EventLog(scheduler);

        scheduler.Update(Enumerable.Range(0, 50).Select(page => Request(page)).ToArray());
        var running = await renderer.NextCallAsync();
        Assert.Equal(0, running.Request.Key.Page);
        Assert.Equal(50, scheduler.PendingCount);

        scheduler.Update([Request(100)]);
        Assert.Equal(1, scheduler.PendingCount);

        // The old in-flight render still finishes (the fake ignores the cancellation); nothing else of the old list runs.
        running.Complete(Image());
        var replacement = await renderer.NextCallAsync();
        Assert.Equal(100, replacement.Request.Key.Page);
        replacement.Complete(Image());
        while ((await events.NextReadyAsync()).Page != 100)
        {
        }

        await renderer.AssertNoCallAsync();
        Assert.Equal(2, renderer.CallCount);
        Assert.True(events.Ready.Count(key => key.Page < 50) <= 1);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task UpdateCancelsTheInFlightRenderWhenNoLongerWanted()
    {
        // The fake never completes the cancelled render: the scheduler must move on through the token registration.
        var renderer = new FakeRenderer { HonorsCancellation = false };
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());

        scheduler.Update([Request(1)]);
        var running = await renderer.NextCallAsync();
        Assert.False(running.Token.IsCancellationRequested);

        scheduler.Update([Request(2)]);

        Assert.True(running.Token.IsCancellationRequested);
        await running.Cancelled.WaitAsync(SchedulerWait.Due, TestContext.Current.CancellationToken);
        var next = await renderer.NextCallAsync();
        Assert.Equal(2, next.Request.Key.Page);
    }

    [Fact]
    public async Task UpdateKeepsTheInFlightRenderWhenStillWanted()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1), Request(2)]);
        var running = await renderer.NextCallAsync();

        scheduler.Update([Request(2), Request(1)]);
        scheduler.Update([Request(1)]);
        scheduler.Update([Request(3), Request(1)]);

        Assert.False(running.Token.IsCancellationRequested);
        await renderer.AssertNoCallAsync();
        running.Complete(Image());
        Assert.Equal(1, (await events.NextReadyAsync()).Page);

        var next = await renderer.NextCallAsync();
        Assert.Equal(3, next.Request.Key.Page);
        next.Complete(Image());
        await events.NextReadyAsync();

        Assert.Equal(1, renderer.Calls.Count(call => call.Request.Key.Page == 1));
        Assert.True(cache.Contains(Request(1).Key));
    }

    [Fact]
    public async Task CancelledRenderDoesNotRaiseTileReady()
    {
        // The fake delivers an image although the render was cancelled, as a render that finishes in a race would.
        var renderer = new FakeRenderer { HonorsCancellation = false };
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1)]);
        var cancelled = await renderer.NextCallAsync();
        scheduler.Update([]);
        cancelled.Complete(Image());

        scheduler.Update([Request(2)]);
        var next = await renderer.NextCallAsync();
        next.Complete(Image());
        Assert.Equal(2, (await events.NextReadyAsync()).Page);

        await events.AssertNoReadyAsync();
        Assert.DoesNotContain(Request(1).Key, events.Ready);
        Assert.False(cache.Contains(Request(1).Key));
    }

    [Fact]
    public async Task SkipsKeysAlreadyInTheCache()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        cache.Add(Request(1).Key, Image());
        using var scheduler = new RenderScheduler(renderer.Render, cache);

        scheduler.Update([Request(1), Request(2)]);

        var call = await renderer.NextCallAsync();
        Assert.Equal(2, call.Request.Key.Page);
        call.Complete(Image());
        await renderer.AssertNoCallAsync();
        Assert.Equal(1, renderer.CallCount);
    }

    [Fact]
    public async Task FailedKeyIsReportedOnceAndNotRetried()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var events = new EventLog(scheduler);
        var failure = new InvalidDataException("damaged page");

        scheduler.Update([Request(1)]);
        (await renderer.NextCallAsync()).Fail(failure);
        var (key, exception) = await events.NextFailedAsync();
        Assert.Equal(Request(1).Key, key);
        Assert.Same(failure, exception);

        scheduler.Update([Request(1), Request(2)]);
        var next = await renderer.NextCallAsync();
        Assert.Equal(2, next.Request.Key.Page);
        next.Complete(Image());
        Assert.Equal(2, (await events.NextReadyAsync()).Page);

        scheduler.Update([Request(1)]);
        await renderer.AssertNoCallAsync();
        Assert.Single(events.Failed);
        Assert.Equal(2, renderer.CallCount);
    }

    [Fact]
    public async Task ImageLargerThanTheCacheBudgetFailsOnceInsteadOfBeingRenderedAgain()
    {
        // 4 × 4 pixels cost 64 bytes; the cache holds 16.
        var renderer = new FakeRenderer();
        var cache = new RenderCache(budgetBytes: 16);
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);
        var large = new RenderedImage(4, 4, 16, new byte[64]);
        Assert.True(RenderCache.CostOf(large) > cache.BudgetBytes);

        scheduler.Update([Request(1)]);
        (await renderer.NextCallAsync()).Complete(large);
        var (key, exception) = await events.NextFailedAsync();
        Assert.Equal(Request(1).Key, key);
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("cache budget", exception.Message, StringComparison.Ordinal);
        Assert.Empty(events.Ready);
        Assert.False(cache.Contains(key));

        // Planning the same view again does not render it again.
        scheduler.Update([Request(1)]);
        await renderer.AssertNoCallAsync();
        await events.AssertNoReadyAsync();
        Assert.Single(events.Failed);
        Assert.Equal(1, renderer.CallCount);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task DisposeCancelsAndSilencesEvents()
    {
        var renderer = new FakeRenderer { HonorsCancellation = false };
        var cache = new RenderCache();
        var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1), Request(2)]);
        var running = await renderer.NextCallAsync();

        scheduler.Dispose();

        Assert.True(running.Token.IsCancellationRequested);
        running.Complete(Image());
        scheduler.Update([Request(3)]);
        scheduler.Dispose();

        await renderer.AssertNoCallAsync();
        await events.AssertNoReadyAsync();
        Assert.Empty(events.Failed);
        Assert.False(cache.Contains(Request(1).Key));
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task BackgroundFlagReachesTheRenderer()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());

        scheduler.Update([Request(1, background: false), Request(2, background: true)]);

        var interactive = await renderer.NextCallAsync();
        interactive.Complete(Image());
        var background = await renderer.NextCallAsync();
        background.Complete(Image());

        Assert.False(interactive.Request.Background);
        Assert.True(background.Request.Background);
        Assert.Equal(PdfPriority.Interactive, RenderScheduler.OptionsFor(interactive.Request).Priority);
        Assert.Equal(PdfPriority.Background, RenderScheduler.OptionsFor(background.Request).Priority);
        Assert.Equal(PdfPriority.Interactive, interactive.Options.Priority);
        Assert.Equal(PdfPriority.Background, background.Options.Priority);
    }

    [Fact]
    public async Task RendersRealTilesOfAThousandPageDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await new PdfEngine().OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);
        const int page = 499;
        var size = await document.GetPageSizeAsync(page, ct);
        var previewScale = TilePlanner.PreviewScale(size);
        var (previewWidth, previewHeight) = size.ToPixels(previewScale);
        var preview = new RenderRequest(new TileKey(page, TileKey.PreviewScaleKey, 0, 0), previewScale, new PixelRect(0, 0, previewWidth, previewHeight), true, false);

        // At scale 16 the page is 3200 × 1600 pixels, above the whole-page limit, so it is split into tiles.
        var tiles = TilePlanner.PlanSharp(page, size, 16.0, new PixelRect(600, 300, 100, 100), background: true);
        var tile = Assert.Single(tiles);
        Assert.False(tile.WholePage);

        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(document, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([preview, tile]);

        var ready = new HashSet<TileKey> { await events.NextReadyAsync(), await events.NextReadyAsync() };
        Assert.Equal(new HashSet<TileKey> { preview.Key, tile.Key }, ready);
        Assert.Empty(events.Failed);
        foreach (var request in new[] { preview, tile })
        {
            Assert.True(cache.TryGet(request.Key, out var image));
            Assert.Equal(request.Rect.Width, image.Width);
            Assert.Equal(request.Rect.Height, image.Height);
        }
    }

    [Fact]
    public async Task InvalidatedPageIsRenderedAgainAndAnOldResultIsDiscarded()
    {
        // The fake delivers its image although the scheduler cancelled it, as a render that finishes in a race would.
        var renderer = new FakeRenderer { HonorsCancellation = false };
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1)]);
        var old = await renderer.NextCallAsync();
        scheduler.InvalidatePages(new HashSet<int> { 1 });

        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(0, scheduler.PendingCount);
        old.Complete(Image());

        // Planned again, the page renders anew; the image of the old epoch reaches neither the cache nor the events.
        scheduler.Update([Request(1)]);
        var again = await renderer.NextCallAsync();
        Assert.Equal(Request(1).Key, again.Request.Key);
        var fresh = Image();
        again.Complete(fresh);

        Assert.Equal(Request(1).Key, await events.NextReadyAsync());
        Assert.True(cache.TryGet(Request(1).Key, out var stored));
        Assert.Same(fresh, stored);
        Assert.Single(events.Ready);
        Assert.Empty(events.Failed);
        Assert.Equal(2, renderer.CallCount);
    }

    [Fact]
    public async Task OtherPagesKeepTheirTiles()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        var pageOne = new TileKey(1, 2000, 1, 1);
        var pageTwo = new TileKey(2, 2000, 1, 1);
        cache.Add(Request(1).Key, Image());
        cache.Add(pageOne, Image());
        cache.Add(pageTwo, Image());
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(3), Request(4)]);
        var running = await renderer.NextCallAsync();
        Assert.Equal(3, running.Request.Key.Page);

        scheduler.InvalidatePages(new HashSet<int> { 1 });

        // Only the tiles of page 1 are gone; the running request of another page goes on, and so does the queue.
        Assert.False(cache.Contains(Request(1).Key));
        Assert.False(cache.Contains(pageOne));
        Assert.True(cache.Contains(pageTwo));
        Assert.False(running.Token.IsCancellationRequested);
        Assert.Equal(2, scheduler.PendingCount);

        running.Complete(Image());
        Assert.Equal(Request(3).Key, await events.NextReadyAsync());
        var next = await renderer.NextCallAsync();
        Assert.Equal(4, next.Request.Key.Page);
        next.Complete(Image());
        Assert.Equal(Request(4).Key, await events.NextReadyAsync());
        Assert.True(cache.Contains(Request(3).Key));
        Assert.True(cache.Contains(Request(4).Key));
        Assert.True(cache.Contains(pageTwo));
    }

    [Fact]
    public async Task InvalidatePagesForgetsTheFailuresOfThosePagesOnly()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var events = new EventLog(scheduler);
        scheduler.Update([Request(1), Request(2)]);
        (await renderer.NextCallAsync()).Fail(new InvalidDataException("damaged page"));
        (await renderer.NextCallAsync()).Fail(new InvalidDataException("damaged page"));
        Assert.Equal(1, (await events.NextFailedAsync()).Key.Page);
        Assert.Equal(2, (await events.NextFailedAsync()).Key.Page);

        scheduler.InvalidatePages(new HashSet<int> { 2 });
        scheduler.Update([Request(1), Request(2)]);

        // Page 1 is still known as failed and is skipped; page 2 changed and is tried again.
        var again = await renderer.NextCallAsync();
        Assert.Equal(2, again.Request.Key.Page);
        Assert.Equal(1, scheduler.PendingCount);
    }

    [Fact]
    public async Task InvalidatePagesDoesNotWaitForARunningHandler()
    {
        var ct = TestContext.Current.CancellationToken;
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var inHandler = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        scheduler.TileReady += _ =>
        {
            inHandler.Set();
            release.Wait(SchedulerWait.Due, ct);
        };

        scheduler.Update([Request(1)]);
        (await renderer.NextCallAsync()).Complete(Image());
        Assert.True(inHandler.Wait(SchedulerWait.Due, ct));

        // The handler of page 1 still runs; the invalidation returns at once all the same.
        await Task.Run(() => scheduler.InvalidatePages(new HashSet<int> { 1 }), ct).WaitAsync(SchedulerWait.Due, ct);
        Assert.False(cache.Contains(Request(1).Key));
        release.Set();
    }

    [Fact]
    public void InvalidatePagesAfterDisposeDoesNothing()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        cache.Add(Request(1).Key, Image());
        var scheduler = new RenderScheduler(renderer.Render, cache);
        scheduler.Dispose();

        scheduler.InvalidatePages(new HashSet<int> { 1 });

        Assert.True(cache.Contains(Request(1).Key));
        Assert.Throws<ArgumentNullException>(() => scheduler.InvalidatePages(null!));
    }
}
