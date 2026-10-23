using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class PageThemeSchedulerTests
{
    private readonly PdfEngine engine = new();

    private static RenderRequest Request(int page, bool background = false) =>
        new(new TileKey(page, 1000, 0, 0), 1.0, new PixelRect(0, 0, 4, 4), true, background);

    private static RenderedImage Image() => new(1, 1, 4, new byte[4]);

    [Fact]
    public async Task SettingTheThemeClearsTheCacheAndTheQueue()
    {
        var renderer = new FakeRenderer { HonorsCancellation = false };
        var cache = new RenderCache();
        cache.Add(Request(7).Key, Image());
        using var scheduler = new RenderScheduler(renderer.Render, cache);

        scheduler.Update([Request(1), Request(2), Request(3)]);
        var running = await renderer.NextCallAsync();
        Assert.Equal(PageTheme.Light, scheduler.PageTheme);

        scheduler.SetPageTheme(PageTheme.Dark);

        Assert.Equal(PageTheme.Dark, scheduler.PageTheme);
        Assert.True(running.Token.IsCancellationRequested);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, scheduler.PendingCount);

        // The rest of the old list never runs; the caller plans again after the switch.
        await renderer.AssertNoCallAsync();
        Assert.Equal(1, renderer.CallCount);
    }

    [Fact]
    public async Task AResultRenderedInTheOldThemeIsDiscarded()
    {
        // The fake delivers its image although the scheduler cancelled it, as a render that finishes in a race would.
        var renderer = new FakeRenderer { HonorsCancellation = false };
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1)]);
        var old = await renderer.NextCallAsync();
        Assert.Equal(PageTheme.Light, old.Options.PageTheme);
        scheduler.SetPageTheme(PageTheme.Dark);
        old.Complete(Image());

        await events.AssertNoReadyAsync();
        Assert.Empty(events.Failed);
        Assert.False(cache.Contains(Request(1).Key));
        Assert.Equal(0, cache.Count);

        // The same key is rendered again, now in the new theme.
        scheduler.Update([Request(1)]);
        var again = await renderer.NextCallAsync();
        Assert.Equal(PageTheme.Dark, again.Options.PageTheme);
        again.Complete(Image());
        Assert.Equal(Request(1).Key, await events.NextReadyAsync());
        Assert.True(cache.Contains(Request(1).Key));
    }

    [Fact]
    public async Task AFailureInTheOldThemeIsDiscarded()
    {
        var renderer = new FakeRenderer { HonorsCancellation = false };
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1)]);
        var old = await renderer.NextCallAsync();
        scheduler.SetPageTheme(PageTheme.Dark);
        old.Fail(new InvalidDataException("damaged page"));

        // No TileFailed, and the key is not marked as failed: it is tried again in the new theme.
        scheduler.Update([Request(1)]);
        var again = await renderer.NextCallAsync();
        Assert.Equal(PageTheme.Dark, again.Options.PageTheme);
        again.Complete(Image());
        Assert.Equal(Request(1).Key, await events.NextReadyAsync());
        Assert.Empty(events.Failed);
    }

    [Fact]
    public async Task SwitchingForgetsTheFailuresOfTheOldTheme()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1)]);
        (await renderer.NextCallAsync()).Fail(new InvalidDataException("damaged page"));
        Assert.Equal(Request(1).Key, (await events.NextFailedAsync()).Key);
        scheduler.Update([Request(1)]);
        await renderer.AssertNoCallAsync();

        scheduler.SetPageTheme(PageTheme.Dark);
        scheduler.Update([Request(1)]);

        var again = await renderer.NextCallAsync();
        Assert.Equal(1, again.Request.Key.Page);
        Assert.Equal(PageTheme.Dark, again.Options.PageTheme);
    }

    [Fact]
    public async Task RequestsAfterTheSwitchRenderInTheNewTheme()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.SetPageTheme(PageTheme.Dark);
        scheduler.Update([Request(1), Request(2, background: true)]);

        var interactive = await renderer.NextCallAsync();
        interactive.Complete(Image());
        var background = await renderer.NextCallAsync();
        background.Complete(Image());
        await events.NextReadyAsync();
        await events.NextReadyAsync();

        Assert.Equal((PageTheme.Dark, PdfPriority.Interactive), (interactive.Options.PageTheme, interactive.Options.Priority));
        Assert.Equal((PageTheme.Dark, PdfPriority.Background), (background.Options.PageTheme, background.Options.Priority));
        Assert.True(cache.Contains(Request(1).Key));
        Assert.True(cache.Contains(Request(2).Key));
        Assert.Equal(PageTheme.Dark, RenderScheduler.OptionsFor(Request(1), PageTheme.Dark).PageTheme);
        Assert.Equal(PageTheme.Light, RenderScheduler.OptionsFor(Request(1)).PageTheme);
    }

    [Fact]
    public async Task SameThemeDoesNothing()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        cache.Add(Request(7).Key, Image());
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);

        scheduler.Update([Request(1), Request(2)]);
        var running = await renderer.NextCallAsync();

        scheduler.SetPageTheme(PageTheme.Light);

        Assert.False(running.Token.IsCancellationRequested);
        Assert.True(cache.Contains(Request(7).Key));
        Assert.Equal(2, scheduler.PendingCount);
        running.Complete(Image());
        Assert.Equal(1, (await events.NextReadyAsync()).Page);
        var next = await renderer.NextCallAsync();
        Assert.Equal(2, next.Request.Key.Page);
        Assert.Equal(PageTheme.Light, next.Options.PageTheme);
    }

    [Fact]
    public async Task TheSwitchWaitsForARunningHandlerSoNoOldEventStartsAfterIt()
    {
        // Like Dispose: once SetPageTheme has returned, no TileReady of the old theme starts. A handler that is
        // already running when the theme changes is waited for.
        var ct = TestContext.Current.CancellationToken;
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
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

        var switching = Task.Run(() => scheduler.SetPageTheme(PageTheme.Dark), ct);
        await Task.Delay(SchedulerWait.Quiet, ct);
        Assert.False(switching.IsCompleted);

        release.Set();
        await switching.WaitAsync(SchedulerWait.Due, ct);
        Assert.Equal(PageTheme.Dark, scheduler.PageTheme);
    }

    [Fact]
    public async Task SetPageThemeAfterDisposeDoesNothing()
    {
        var renderer = new FakeRenderer();
        var cache = new RenderCache();
        cache.Add(Request(7).Key, Image());
        var scheduler = new RenderScheduler(renderer.Render, cache);
        scheduler.Dispose();

        scheduler.SetPageTheme(PageTheme.Dark);

        Assert.Equal(PageTheme.Light, scheduler.PageTheme);
        Assert.True(cache.Contains(Request(7).Key));
        await renderer.AssertNoCallAsync();
    }

    [Fact]
    public void AnUndefinedThemeIsRejected()
    {
        var renderer = new FakeRenderer();
        using var scheduler = new RenderScheduler(renderer.Render, new RenderCache());
        using var session = new ViewerSession(engine);

        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.SetPageTheme((PageTheme)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetPageTheme((PageTheme)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RenderScheduler(renderer.Render, new RenderCache(), (PageTheme)7));
        Assert.Equal(PageTheme.Light, scheduler.PageTheme);
        Assert.Equal(PageTheme.Light, session.PageTheme);
    }

    [Fact]
    public async Task ANewDocumentOpensInTheSessionsTheme()
    {
        using var session = new ViewerSession(engine);
        Assert.Equal(PageTheme.Light, session.PageTheme);

        session.SetPageTheme(PageTheme.Dark);
        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));

        Assert.Equal(PageTheme.Dark, session.PageTheme);
        Assert.Equal(PageTheme.Dark, session.Scheduler!.PageTheme);
        Assert.Equal(PageTheme.Dark, session.EnsureThumbnails()!.PageTheme);

        // And so does the next one.
        await session.OpenAsync(TestData.Pdf("text-basic.pdf"));
        Assert.Equal(PageTheme.Dark, session.Scheduler!.PageTheme);
        Assert.Equal(PageTheme.Dark, session.EnsureThumbnails()!.PageTheme);
    }

    [Fact]
    public async Task TheSessionSwitchesTheOpenDocumentAndItsThumbnails()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var scheduler = session.Scheduler!;
        var thumbnails = session.EnsureThumbnails()!;
        session.Cache!.Add(Request(0).Key, Image());
        thumbnails.Cache.Add(Request(0).Key, Image());

        session.SetPageTheme(PageTheme.Dark);

        Assert.Same(scheduler, session.Scheduler);
        Assert.Equal(PageTheme.Dark, scheduler.PageTheme);
        Assert.Equal(PageTheme.Dark, thumbnails.PageTheme);
        Assert.Equal(0, session.Cache.Count);
        Assert.Equal(0, thumbnails.Cache.Count);
    }

    [Fact]
    public async Task SetPageThemeOfTheSessionAfterDisposeDoesNothing()
    {
        var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("hello_world.pdf"));
        session.Dispose();

        session.SetPageTheme(PageTheme.Dark);

        Assert.Equal(PageTheme.Light, session.PageTheme);
    }

    [Fact]
    public async Task ThumbnailSourceSwitchClearsItsCacheAndRendersInTheTheme()
    {
        using var session = new ViewerSession(engine);
        await session.OpenAsync(TestData.Pdf("dark-sample.pdf"));
        var thumbnails = session.EnsureThumbnails()!;
        thumbnails.Cache.Add(Request(5).Key, Image());

        thumbnails.SetPageTheme(PageTheme.Dark);

        Assert.Equal(PageTheme.Dark, thumbnails.PageTheme);
        Assert.Equal(0, thumbnails.Cache.Count);

        var ready = new TaskCompletionSource<TileKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        thumbnails.Scheduler.TileReady += key => ready.TrySetResult(key);
        thumbnails.Scheduler.Update(ThumbnailPlanner.Plan(session.LoadedSizes, [0], 1.0));
        var key = await ready.Task.WaitAsync(SchedulerWait.Due, TestContext.Current.CancellationToken);

        // The top left corner of the page is empty: the dark ground, blue, green, red, opaque.
        Assert.True(thumbnails.Cache.TryGet(key, out var image));
        Assert.Equal([0x27, 0x21, 0x1E, 0xFF], image.Pixels.Span[..4].ToArray());
    }

    [Fact]
    public async Task TheMainViewOfADarkSessionRendersDarkPages()
    {
        using var session = new ViewerSession(engine);
        session.SetPageTheme(PageTheme.Dark);
        await session.OpenAsync(TestData.Pdf("dark-sample.pdf"));
        var size = session.LoadedSizes[0];
        var scale = TilePlanner.PreviewScale(size);
        var (width, height) = size.ToPixels(scale);
        var preview = new RenderRequest(new TileKey(0, TileKey.PreviewScaleKey, 0, 0), scale, new PixelRect(0, 0, width, height), true, false);
        var ready = new TaskCompletionSource<TileKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Scheduler!.TileReady += key => ready.TrySetResult(key);

        session.Scheduler.Update([preview]);
        var key = await ready.Task.WaitAsync(SchedulerWait.Due, TestContext.Current.CancellationToken);

        Assert.True(session.Cache!.TryGet(key, out var image));
        Assert.Equal([0x27, 0x21, 0x1E, 0xFF], image.Pixels.Span[..4].ToArray());
    }
}
