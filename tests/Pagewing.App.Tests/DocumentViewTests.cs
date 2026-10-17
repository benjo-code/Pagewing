using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using PdfPixelRect = Pagewing.Core.Pdf.PixelRect;
using PixelRect = Avalonia.PixelRect;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DocumentViewTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public void Dispose()
    {
        // Windows close before their sessions are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task ShowsPlaceholdersImmediatelyAndPageImagesAfterRendering()
    {
        var (window, viewModel, view) = Show();
        var path = TestData.Pdf("text-basic.pdf");

        // Subscribed after the view: this job runs right after the view took the document (same priority, in order)
        // and before any rendered tile can arrive, because the tiles are only requested by that job.
        FramePixels? first = null;
        var bitmapsAtFirstFrame = -1;
        DipRect pageAtFirstFrame = default;
        viewModel.Session.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (first is null && view.Layout is { } layout && view.Document is not null)
            {
                bitmapsAtFirstFrame = view.Canvas.Bitmaps.Count;
                pageAtFirstFrame = ToViewport(view, layout.GetPageRect(0));
                first = FramePixels.Snapshot(view.Canvas);
            }
        });

        await viewModel.OpenAsync(path);
        await WaitUntilAsync(window, () => first is not null);
        using (first)
        {
            // Placeholder: no image yet, the page number in the middle, nothing of the page content.
            Assert.Equal(0, bitmapsAtFirstFrame);
            Assert.True(CountPixels(first!, Center(pageAtFirstFrame, 24, 12), IsDark) > 0, "no page number on the placeholder");
            Assert.Equal(0, CountPixels(first!, TextBand(pageAtFirstFrame), IsDark));
        }

        // Then the rendered page: the dark text of "Hello, Pagewing" in its band.
        await WaitUntilAsync(window, () => HasSharpTile(view, 0));
        var page = ToViewport(view, view.Layout!.GetPageRect(0));
        var origin = view.Canvas.TranslatePoint(default, window)!.Value;
        using var frame = new FramePixels(window.CaptureRenderedFrame()!);
        var band = TextBand(page);
        Assert.True(CountPixels(frame, new PixelRect(band.X + (int)origin.X, band.Y + (int)origin.Y, band.Width, band.Height), IsDark) > 100);
    }

    [AvaloniaFact]
    public async Task ScrollingRequestsTheTilesOfTheVisiblePages()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");

        var layout = view.Layout!;
        var top = layout.GetPageRect(499).Y;
        Scroll(window, view, top);

        // The visible pages and a buffer of one viewport height above and below, nothing else.
        var height = view.ScrollViewer.Viewport.Height;
        var (first, last) = layout.GetVisiblePages(top - height, top + (2 * height));
        Assert.InRange(first, 480, 499);
        Assert.InRange(last, 499, 520);
        var requests = view.Canvas.LastRequests;
        Assert.NotEmpty(requests);
        Assert.All(requests, request => Assert.InRange(request.Key.Page, first, last));
        Assert.Contains(requests, request => request.Key.Page == 499 && request.Key.IsPreview && !request.Background);
        Assert.Contains(requests, request => request.Key.Page == 499 && !request.Key.IsPreview);
        Assert.Equal(500, viewModel.CurrentPage);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(0.1)]
    public async Task FastScrollingThroughAThousandPagesNeverQueuesMoreThanTheCap(double zoom)
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        viewModel.Zoom = zoom;
        var scheduler = view.Canvas.Scheduler!;
        var height = view.Layout!.ContentHeight;

        for (var i = 0; i < 100; i++)
        {
            view.ScrollViewer.Offset = new Vector(0, height * i / 100);
            Assert.True(scheduler.PendingCount < 200, $"{scheduler.PendingCount} jobs pending after step {i}");
        }

        // The last position decides what is wanted.
        var last = view.Layout.GetMostVisiblePage(view.ScrollViewer.Offset.Y, view.ScrollViewer.Offset.Y + view.ScrollViewer.Viewport.Height);
        Assert.Contains(view.Canvas.LastRequests, request => request.Key.Page == last && !request.Background);
    }

    [AvaloniaFact]
    public async Task CtrlWheelZoomsAroundThePointer()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        Scroll(window, view, view.Layout!.GetPageRect(299).Y);
        var canvas = view.Canvas;
        var inCanvas = new Point(canvas.Bounds.Width / 2 + 40, canvas.Bounds.Height / 3);
        var inWindow = canvas.TranslatePoint(inCanvas, window)!.Value;

        foreach (var (delta, expectedZoom) in new[] { (1.0, 1.25), (1.0, 1.5), (-1.0, 1.25), (-1.0, 1.0), (-1.0, 0.75) })
        {
            var before = canvas.PagePointAt(inCanvas);
            Assert.NotNull(before);

            window.MouseWheel(inWindow, new Vector(0, delta), RawInputModifiers.Control);
            Settle(window);

            Assert.Equal(expectedZoom, viewModel.Zoom);
            Assert.Equal(expectedZoom, view.Layout!.Zoom);
            var after = canvas.PagePointAt(inCanvas);
            Assert.NotNull(after);
            Assert.Equal(before.Value.Page, after.Value.Page);

            // The same spot of the page stays under the pointer (within a DIP at the new zoom). Horizontally only
            // while the pages are wider than the viewport: narrower pages are centred.
            var tolerance = 1 / (DocumentLayout.DipsPerPoint * expectedZoom);
            Assert.InRange(after.Value.Position.Y, before.Value.Position.Y - tolerance, before.Value.Position.Y + tolerance);
            if (view.Layout.ContentWidth > view.ScrollViewer.Viewport.Width)
            {
                Assert.InRange(after.Value.Position.X, before.Value.Position.X - tolerance, before.Value.Position.X + tolerance);
            }
        }

        // Without Ctrl the wheel scrolls and leaves the zoom alone.
        var offset = view.ScrollViewer.Offset.Y;
        window.MouseWheel(inWindow, new Vector(0, -1), RawInputModifiers.None);
        Settle(window);
        Assert.Equal(0.75, viewModel.Zoom);
        Assert.True(view.ScrollViewer.Offset.Y > offset);
    }

    [AvaloniaFact]
    public async Task CtrlPlusAndCtrlMinusStepThroughTheZoomLevels()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(1.0, viewModel.Zoom);

        Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(1.25, viewModel.Zoom);
        Assert.Equal(1.25, view.Layout!.Zoom);

        Press(window, Key.OemMinus, PhysicalKey.Minus, RawInputModifiers.Control);
        Assert.Equal(1.0, viewModel.Zoom);

        Press(window, Key.Subtract, PhysicalKey.NumPadSubtract, RawInputModifiers.Control);
        Assert.Equal(0.75, viewModel.Zoom);
        Assert.Equal(0.75, view.Layout.Zoom);

        Press(window, Key.Add, PhysicalKey.NumPadAdd, RawInputModifiers.Control);
        Assert.Equal(1.0, viewModel.Zoom);

        // "+" on a US layout is Shift+"=".
        Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(1.25, viewModel.Zoom);

        // The status bar shows the value and its buttons step the same way.
        Assert.Equal(viewModel.ZoomText, Find<TextBlock>(window, "ZoomValueText").Text);
        Find<Button>(window, "ZoomOutButton").Command!.Execute(null);
        Settle(window);
        Assert.Equal(1.0, view.Layout.Zoom);
    }

    [AvaloniaFact]
    public async Task ZoomIsClampedTo10And800Percent()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var zoomOut = Find<Button>(window, "ZoomOutButton");
        var zoomIn = Find<Button>(window, "ZoomInButton");

        for (var i = 0; i < 15; i++)
        {
            Press(window, Key.OemMinus, PhysicalKey.Minus, RawInputModifiers.Control);
        }

        Assert.Equal(ZoomSteps.Min, viewModel.Zoom);
        Assert.Equal(ZoomSteps.Min, view.Layout!.Zoom);
        Assert.False(zoomOut.IsEffectivelyEnabled);
        Assert.True(zoomIn.IsEffectivelyEnabled);
        Assert.True(view.Canvas.Scheduler!.PendingCount < 200);

        for (var i = 0; i < 15; i++)
        {
            Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control);
        }

        Assert.Equal(ZoomSteps.Max, viewModel.Zoom);
        Assert.Equal(ZoomSteps.Max, view.Layout.Zoom);
        Assert.False(zoomIn.IsEffectivelyEnabled);
        Assert.True(zoomOut.IsEffectivelyEnabled);

        viewModel.Zoom = 100;
        Assert.Equal(ZoomSteps.Max, viewModel.Zoom);
        viewModel.Zoom = 0.001;
        Settle(window);
        Assert.Equal(ZoomSteps.Min, viewModel.Zoom);
        Assert.Equal(ZoomSteps.Min, view.Layout.Zoom);
    }

    [AvaloniaFact]
    public async Task PageFieldJumpsToThePageAndResetsOnInvalidInput()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var field = Find<TextBox>(window, "PageField");
        field.Focus();

        Commit(window, field, "500");
        Assert.Equal(500, viewModel.CurrentPage);
        Assert.Equal("500", field.Text);
        var offset = view.ScrollViewer.Offset.Y;
        Assert.Equal(view.Layout!.GetPageRect(499).Y - DocumentLayout.Margin, offset, 3);

        foreach (var invalid in new[] { "0", "abc", "1001", "", "-3", "2.5" })
        {
            Commit(window, field, invalid);
            Assert.Equal("500", field.Text);
            Assert.Equal(500, viewModel.CurrentPage);
            Assert.Equal(offset, view.ScrollViewer.Offset.Y);
        }

        // An entry that is left without Enter goes back as well.
        field.Text = "17";
        Find<Button>(window, "ZoomInButton").Focus();
        Settle(window);
        Assert.Equal("500", field.Text);

        // The arrows go one page on.
        Find<Button>(window, "NextPageButton").Command!.Execute(null);
        Settle(window);
        Assert.Equal(501, viewModel.CurrentPage);
        Assert.Equal(view.Layout.GetPageRect(500).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);
        Find<Button>(window, "PreviousPageButton").Command!.Execute(null);
        Settle(window);
        Assert.Equal(500, viewModel.CurrentPage);

        // The last page cannot reach the top of the viewport, but it is the current page.
        Commit(window, field, "1000");
        Assert.Equal(1000, viewModel.CurrentPage);
        Assert.False(Find<Button>(window, "NextPageButton").IsEffectivelyEnabled);
    }

    [AvaloniaFact]
    public async Task PageUpAndPageDownGoPageByPage()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");

        // A click into the document first: the scroll viewer must not swallow the keys.
        var point = view.Canvas.TranslatePoint(new Point(view.Canvas.Bounds.Width / 2, 200), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);

        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal(view.Layout!.GetPageRect(1).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);

        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(3, viewModel.CurrentPage);

        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(2, viewModel.CurrentPage);
        Assert.Equal(view.Layout.GetPageRect(1).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);

        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal(0, view.ScrollViewer.Offset.Y, 3);
    }

    [AvaloniaFact]
    public async Task CurrentPageFollowsTheScrollPosition()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var layout = view.Layout!;
        var field = Find<TextBox>(window, "PageField");

        Scroll(window, view, layout.GetPageRect(249).Y);
        Assert.Equal(250, viewModel.CurrentPage);
        Assert.Equal("250", field.Text);

        // The previous page just left the viewport.
        var next = layout.GetPageRect(250);
        Scroll(window, view, next.Y - (DocumentLayout.PageGap / 2));
        Assert.Equal(251, viewModel.CurrentPage);

        Scroll(window, view, 0);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal("1", field.Text);
    }

    [AvaloniaFact]
    public async Task LoadingMorePageSizesKeepsTheTopPageInPlace()
    {
        // The first batch is real; the later batches wait for the test and come with other sizes than the estimate.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var session = new ViewerSession(
            engine.OpenAsync,
            async (document, firstPage, count, priority, cancellationToken) =>
            {
                if (firstPage == 0)
                {
                    return await document.GetPageSizesAsync(firstPage, count, priority, cancellationToken).ConfigureAwait(false);
                }

                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return Enumerable.Repeat(new PdfPageSize(400, 300), count).ToArray();
            });
        _cleanup.Add(session);
        var (window, viewModel, view) = Show(session);
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var layout = view.Layout!;
        Assert.Equal(32, layout.KnownPageCount);

        Scroll(window, view, layout.GetPageRect(599).Y + 100);
        var anchor = layout.GetAnchor(view.ScrollViewer.Offset.Y);
        var offset = view.ScrollViewer.Offset.Y;
        Assert.Equal(599, anchor.Page);

        gate.SetResult();
        await WaitUntilAsync(window, () => layout.KnownPageCount == 1000);

        // The pages above got smaller, so the offset moved; the spot at the top of the viewport did not.
        Assert.NotEqual(offset, view.ScrollViewer.Offset.Y);
        var after = layout.GetAnchor(view.ScrollViewer.Offset.Y);
        Assert.Equal(anchor.Page, after.Page);
        Assert.Equal(anchor.Fraction, after.Fraction, 6);
        var top = view.ScrollViewer.Offset.Y;
        Assert.Equal(layout.GetMostVisiblePage(top, top + view.ScrollViewer.Viewport.Height) + 1, viewModel.CurrentPage);
    }

    [AvaloniaFact]
    public async Task UnwantedTilesAreNotTurnedIntoBitmaps()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var scheduler = view.Canvas.Scheduler!;

        // Subscribed after the canvas: when this handler runs, the canvas has already posted the tile to the UI thread.
        // Page 300 was never wanted before, so its sharp tile cannot have been rendered yet.
        var sharp = default(TileKey);
        using var ready = new ManualResetEventSlim();
        scheduler.TileReady += key =>
        {
            if (key.Page == 299 && !key.IsPreview)
            {
                sharp = key;
                ready.Set();
            }
        };
        view.ScrollViewer.Offset = new Vector(0, view.Layout!.GetPageRect(299).Y);
        Assert.Contains(view.Canvas.LastRequests, request => request.Key.Page == 299 && !request.Key.IsPreview);

        // Blocks the UI thread, so the posted tile waits; meanwhile the view scrolls far away.
        Assert.True(ready.Wait(Timeout), "the sharp tile of page 300 was not rendered");
        view.ScrollViewer.Offset = new Vector(0, view.Layout.GetPageRect(800).Y);
        Assert.DoesNotContain(view.Canvas.LastRequests, request => request.Key.Page == 299);
        var received = false;
        view.Canvas.TileReceived += key => received |= key == sharp;

        await WaitUntilAsync(window, () => received);
        Assert.False(view.Canvas.Bitmaps.Contains(sharp));
        Assert.DoesNotContain(view.Canvas.Bitmaps.Keys, key => key.Page == 299);
        Assert.True(viewModel.Session.Cache!.Contains(sharp));
    }

    [AvaloniaFact]
    public async Task ScrollingBackShowsCachedPagesAtOnce()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        await WaitUntilAsync(window, () => HasSharpTile(view, 0) && view.Canvas.Bitmaps.Contains(new TileKey(0, TileKey.PreviewScaleKey, 0, 0)));

        view.ScrollViewer.Offset = new Vector(0, view.Layout!.GetPageRect(500).Y);
        Assert.DoesNotContain(view.Canvas.Bitmaps.Keys, key => key.Page == 0);

        // Back at the top, the cache delivers both images in the same call: no render job, no waiting for a frame.
        view.ScrollViewer.Offset = default;
        Assert.True(HasSharpTile(view, 0));
        Assert.True(view.Canvas.Bitmaps.Contains(new TileKey(0, TileKey.PreviewScaleKey, 0, 0)));
    }

    [AvaloniaFact]
    public void FailedTileShowsAnEmptyPageAndIsNotRetried()
    {
        // A scheduler whose renderer fails for page 1 and works for page 2 (a real session cannot be made to fail).
        Strings.Culture = new CultureInfo("en");
        var calls = new List<TileKey>();
        var cache = new RenderCache();
        using var scheduler = new RenderScheduler(
            (request, _) =>
            {
                lock (calls)
                {
                    calls.Add(request.Key);
                }

                return request.Key.Page == 0
                    ? Task.FromException<RenderedImage>(new PdfRenderException("broken page"))
                    : Task.FromResult(Gray(request.Rect.Width, request.Rect.Height));
            },
            cache);
        var layout = new DocumentLayout(2, new PdfPageSize(300, 200));
        var canvas = new DocumentCanvas
        {
            PlaceholderBrush = Avalonia.Media.Brushes.White,
            PlaceholderBorderBrush = Avalonia.Media.Brushes.LightGray,
            PlaceholderTextBrush = Avalonia.Media.Brushes.Black,
        };
        var window = new Window { Content = canvas, Width = 600, Height = 700, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);

        var viewport = new DipRect(-(600 - layout.ContentWidth) / 2, 0, 600, 700);
        canvas.Attach(layout, scheduler, cache);
        canvas.SetViewport(viewport, 1);
        var expected = canvas.LastRequests.Select(request => request.Key).ToHashSet();
        var deadline = DateTime.UtcNow + Timeout;
        while (canvas.Bitmaps.Count < expected.Count(key => key.Page == 1) || CallCount(calls) < expected.Count || scheduler.PendingCount > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the requests did not finish");
            Settle(window);
            Thread.Sleep(5);
        }

        Settle(window);
        using (var frame = FramePixels.Snapshot(canvas))
        {
            // Page 1 stays an empty page with the message; page 2 shows its image.
            var failed = ToViewport(canvas, layout.GetPageRect(0));
            Assert.True(CountPixels(frame, Center(failed, 100, 10), IsDark) > 0, "no message on the failed page");
            Assert.Equal(0, CountPixels(frame, Inset(failed, 8), IsGray));
            Assert.True(CountPixels(frame, Inset(ToViewport(canvas, layout.GetPageRect(1)), 8), IsGray) > 1000);
        }

        // Planning the same view again does not try the failed keys again (nor the cached ones).
        var count = CallCount(calls);
        canvas.SetViewport(viewport, 1);
        Assert.Equal(0, scheduler.PendingCount);
        Settle(window);
        Assert.Equal(count, CallCount(calls));
        canvas.Detach();
    }

    [AvaloniaFact]
    public async Task OpeningAnotherDocumentResetsScrollAndZoom()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        viewModel.Zoom = 2;
        Settle(window);
        viewModel.GoToPage(400);
        Settle(window);
        Assert.Equal(400, viewModel.CurrentPage);
        Assert.True(view.ScrollViewer.Offset.Y > 0);

        await OpenAsync(window, viewModel, view, "text-basic.pdf");

        Assert.Equal(2, view.Layout!.PageCount);
        Assert.Equal(1.0, viewModel.Zoom);
        Assert.Equal(1.0, view.Layout.Zoom);
        Assert.Equal(default, view.ScrollViewer.Offset);
        Assert.Equal(1, viewModel.CurrentPage);
        Assert.Equal("1", Find<TextBox>(window, "PageField").Text);
        Assert.All(view.Canvas.LastRequests, request => Assert.InRange(request.Key.Page, 0, 1));
    }

    [AvaloniaFact]
    public async Task RenderScalingOf150PercentRendersAtTheDevicePixelSize()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Contains(view.Canvas.LastRequests, request => request.Key.ScaleKey == TileKey.ToScaleKey(DocumentLayout.DipsPerPoint));

        window.SetRenderScaling(1.5);
        Settle(window);

        // 96/72 DIPs per point × 1.5 device pixels per DIP = 2 pixels per point.
        var sharp = view.Canvas.LastRequests.Where(request => !request.Key.IsPreview).ToList();
        Assert.NotEmpty(sharp);
        Assert.All(sharp, request => Assert.Equal(2000, request.Key.ScaleKey));
        var page = sharp.First(request => request.Key.Page == 0);
        var size = view.Layout!.GetPageSize(0).ToPixels(2.0);
        Assert.Equal(new PdfPixelRect(0, 0, size.Width, size.Height), page.Rect);

        await WaitUntilAsync(window, () => view.Canvas.Bitmaps.Contains(page.Key));
        Assert.True(view.Canvas.Bitmaps.TryGet(page.Key, out var bitmap));
        Assert.Equal(new PixelSize(size.Width, size.Height), bitmap.PixelSize);
        Assert.DoesNotContain(view.Canvas.Bitmaps.Keys, key => !key.IsPreview && key.ScaleKey != 2000);
    }

    [AvaloniaFact]
    public async Task FirstSharpTileDrawnIsRaisedOncePerDocument()
    {
        var (window, viewModel, view) = Show();
        var raised = 0;
        var sharpWhenRaised = false;
        view.FirstSharpTileDrawn += () =>
        {
            raised++;
            sharpWhenRaised = HasSharpTile(view, 0);
        };

        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        await WaitUntilAsync(window, () => raised == 1);
        Assert.True(sharpWhenRaised);

        // Zooming and scrolling draw new sharp tiles, but the first one of this document was reported.
        viewModel.Zoom = 2;
        await WaitUntilAsync(window, () => view.Canvas.Bitmaps.Keys.Any(key => !key.IsPreview && key.ScaleKey == TileKey.ToScaleKey(DocumentLayout.DipsPerPoint * 2)));
        Settle(window);
        Assert.Equal(1, raised);

        await OpenAsync(window, viewModel, view, "hello_world.pdf");
        await WaitUntilAsync(window, () => raised == 2);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(ViewerSession? session = null)
    {
        if (session is null)
        {
            session = new ViewerSession(new PdfEngine());
            _cleanup.Add(session);
        }

        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(session, new NoFilePicker()) { IsWindowsTitleBar = true };
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    private static async Task OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        await WaitUntilAsync(window, () => view.Document is { } document && ReferenceEquals(document, viewModel.Session.Document) && view.Canvas.LastRequests.Count > 0);
    }

    private static void Scroll(Window window, DocumentView view, double y)
    {
        view.ScrollViewer.Offset = new Vector(view.ScrollViewer.Offset.X, y);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    private static void Commit(Window window, TextBox field, string text)
    {
        field.Focus();
        field.Text = text;
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
    }

    private static bool HasSharpTile(DocumentView view, int page) =>
        view.Canvas.Bitmaps.Keys.Any(key => key.Page == page && !key.IsPreview);

    /// <summary>A page rectangle in content coordinates, moved into the canvas (viewport) coordinates.</summary>
    private static DipRect ToViewport(DocumentView view, DipRect rect) => ToViewport(view.Canvas, rect);

    private static DipRect ToViewport(DocumentCanvas canvas, DipRect rect) =>
        new(rect.X - canvas.Viewport.X, rect.Y - canvas.Viewport.Y, rect.Width, rect.Height);

    /// <summary>The band of the text line of text-basic.pdf (see testdata/references/text-basic).</summary>
    private static PixelRect TextBand(DipRect page) =>
        new((int)(page.X + (page.Width * 0.08)), (int)(page.Y + (page.Height * 0.28)), (int)(page.Width * 0.5), (int)(page.Height * 0.14));

    private static PixelRect Center(DipRect page, int halfWidth, int halfHeight) =>
        new((int)(page.X + (page.Width / 2)) - halfWidth, (int)(page.Y + (page.Height / 2)) - halfHeight, 2 * halfWidth, 2 * halfHeight);

    private static PixelRect Inset(DipRect page, int inset) =>
        new((int)page.X + inset, (int)page.Y + inset, (int)page.Width - (2 * inset), (int)page.Height - (2 * inset));

    private static bool IsDark(Avalonia.Media.Color color) => Math.Max(color.R, Math.Max(color.G, color.B)) < 128;

    private static bool IsGray(Avalonia.Media.Color color) => color.R is > 120 and < 136 && color.G == color.R && color.B == color.R;

    private static int CountPixels(FramePixels frame, PixelRect area, Func<Avalonia.Media.Color, bool> predicate)
    {
        var count = 0;
        for (var y = Math.Max(0, area.Y); y < Math.Min(frame.Height, area.Y + area.Height); y++)
        {
            for (var x = Math.Max(0, area.X); x < Math.Min(frame.Width, area.X + area.Width); x++)
            {
                if (predicate(frame.GetColor(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static int CallCount(List<TileKey> calls)
    {
        lock (calls)
        {
            return calls.Count;
        }
    }

    /// <summary>An opaque mid-gray image (128, 128, 128) as a renderer would deliver it.</summary>
    private static RenderedImage Gray(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 128;
            pixels[i + 1] = 128;
            pixels[i + 2] = 128;
            pixels[i + 3] = 255;
        }

        return new RenderedImage(width, height, width * 4, pixels);
    }

    private static async Task WaitUntilAsync(Window window, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The view did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
