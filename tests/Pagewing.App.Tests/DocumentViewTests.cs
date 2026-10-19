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
        // Windows close before their view models (and the sessions of their tabs) are disposed.
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

        // Subscribed after the view (which connects to the new tab as soon as it is the active one, before the open
        // starts): this job runs right after the view took the document (same priority, in order) and before any
        // rendered tile can arrive, because the tiles are only requested by that job.
        FramePixels? first = null;
        var bitmapsAtFirstFrame = -1;
        DipRect pageAtFirstFrame = default;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.ActiveTab) || viewModel.ActiveTab is not { } tab)
            {
                return;
            }

            tab.Session.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (first is null && view.Layout is { } layout && view.Document is not null)
                {
                    bitmapsAtFirstFrame = view.Canvas.Bitmaps.Count;
                    pageAtFirstFrame = ToViewport(view, layout.GetPageRect(0));
                    first = FramePixels.Snapshot(view.Canvas);
                }
            });
        };

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
        Assert.Equal(500, viewModel.ActiveTab!.CurrentPage);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(0.1)]
    public async Task FastScrollingThroughAThousandPagesNeverQueuesMoreThanTheCap(double zoom)
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        viewModel.ActiveTab!.Zoom = zoom;
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

            Assert.Equal(expectedZoom, viewModel.ActiveTab!.Zoom);
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
        Assert.Equal(0.75, viewModel.ActiveTab!.Zoom);
        Assert.True(view.ScrollViewer.Offset.Y > offset);
    }

    [AvaloniaFact]
    public async Task CtrlPlusAndCtrlMinusStepThroughTheZoomLevels()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(1.0, viewModel.ActiveTab!.Zoom);

        Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(1.25, viewModel.ActiveTab!.Zoom);
        Assert.Equal(1.25, view.Layout!.Zoom);

        Press(window, Key.OemMinus, PhysicalKey.Minus, RawInputModifiers.Control);
        Assert.Equal(1.0, viewModel.ActiveTab!.Zoom);

        Press(window, Key.Subtract, PhysicalKey.NumPadSubtract, RawInputModifiers.Control);
        Assert.Equal(0.75, viewModel.ActiveTab!.Zoom);
        Assert.Equal(0.75, view.Layout.Zoom);

        Press(window, Key.Add, PhysicalKey.NumPadAdd, RawInputModifiers.Control);
        Assert.Equal(1.0, viewModel.ActiveTab!.Zoom);

        // "+" on a US layout is Shift+"=".
        Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Equal(1.25, viewModel.ActiveTab!.Zoom);

        // The status bar shows the value and its buttons step the same way.
        Assert.Equal(viewModel.ActiveTab!.ZoomText, Find<TextBlock>(window, "ZoomValueText").Text);
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

        Assert.Equal(ZoomSteps.Min, viewModel.ActiveTab!.Zoom);
        Assert.Equal(ZoomSteps.Min, view.Layout!.Zoom);
        Assert.False(zoomOut.IsEffectivelyEnabled);
        Assert.True(zoomIn.IsEffectivelyEnabled);
        Assert.True(view.Canvas.Scheduler!.PendingCount < 200);

        for (var i = 0; i < 15; i++)
        {
            Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control);
        }

        Assert.Equal(ZoomSteps.Max, viewModel.ActiveTab!.Zoom);
        Assert.Equal(ZoomSteps.Max, view.Layout.Zoom);
        Assert.False(zoomIn.IsEffectivelyEnabled);
        Assert.True(zoomOut.IsEffectivelyEnabled);

        viewModel.ActiveTab!.Zoom = 100;
        Assert.Equal(ZoomSteps.Max, viewModel.ActiveTab!.Zoom);
        viewModel.ActiveTab!.Zoom = 0.001;
        Settle(window);
        Assert.Equal(ZoomSteps.Min, viewModel.ActiveTab!.Zoom);
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
        Assert.Equal(500, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal("500", field.Text);
        var offset = view.ScrollViewer.Offset.Y;
        Assert.Equal(view.Layout!.GetPageRect(499).Y - DocumentLayout.Margin, offset, 3);

        foreach (var invalid in new[] { "0", "abc", "1001", "", "-3", "2.5" })
        {
            Commit(window, field, invalid);
            Assert.Equal("500", field.Text);
            Assert.Equal(500, viewModel.ActiveTab!.CurrentPage);
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
        Assert.Equal(501, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal(view.Layout.GetPageRect(500).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);
        Find<Button>(window, "PreviousPageButton").Command!.Execute(null);
        Settle(window);
        Assert.Equal(500, viewModel.ActiveTab!.CurrentPage);

        // The last page cannot reach the top of the viewport, but it is the current page.
        Commit(window, field, "1000");
        Assert.Equal(1000, viewModel.ActiveTab!.CurrentPage);
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
        Assert.Equal(2, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal(view.Layout!.GetPageRect(1).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);

        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(3, viewModel.ActiveTab!.CurrentPage);

        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(2, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal(view.Layout.GetPageRect(1).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);

        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(1, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal(0, view.ScrollViewer.Offset.Y, 3);
    }

    [AvaloniaFact]
    public async Task ArrowKeysSpaceHomeAndEndScrollTheDocument()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var scroller = view.ScrollViewer;
        var line = scroller.SmallChange.Height;
        var height = scroller.Viewport.Height;
        var bottom = scroller.Extent.Height - height;
        Assert.True(line > 0);

        // The document has the keys as soon as it is ready, without a focus ring (no keyboard navigation).
        Assert.True(view.IsFocused);
        Assert.DoesNotContain(":focus-visible", view.Classes);

        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(line, scroller.Offset.Y, 6);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(2 * line, scroller.Offset.Y, 6);
        Press(window, Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.Equal(line, scroller.Offset.Y, 6);

        // Up at the top stays at the top.
        Press(window, Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None);
        Press(window, Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.Equal(0, scroller.Offset.Y);

        // Space and Shift+Space go a viewport height.
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(height, scroller.Offset.Y, 6);
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(2 * height, scroller.Offset.Y, 6);
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.Shift);
        Assert.Equal(height, scroller.Offset.Y, 6);

        // End and Home: the end and the start of the document; the current page follows.
        Press(window, Key.End, PhysicalKey.End, RawInputModifiers.None);
        Assert.Equal(bottom, scroller.Offset.Y, 6);
        Assert.Equal(view.Layout!.GetMostVisiblePage(bottom, bottom + height) + 1, viewModel.ActiveTab!.CurrentPage);
        Assert.True(viewModel.ActiveTab!.CurrentPage > 990);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(bottom, scroller.Offset.Y, 6);
        Press(window, Key.Home, PhysicalKey.Home, RawInputModifiers.None);
        Assert.Equal(0, scroller.Offset.Y);
        Assert.Equal(1, viewModel.ActiveTab!.CurrentPage);
        Press(window, Key.End, PhysicalKey.End, RawInputModifiers.Control);
        Assert.Equal(bottom, scroller.Offset.Y, 6);
        Press(window, Key.Home, PhysicalKey.Home, RawInputModifiers.Control);
        Assert.Equal(0, scroller.Offset.Y);

        // The page-by-page and the zoom keys of the window still work while the document has the focus.
        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(2, viewModel.ActiveTab!.CurrentPage);
        Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(1.25, viewModel.ActiveTab!.Zoom);
        Assert.True(view.IsFocused);
    }

    [AvaloniaFact]
    public async Task LeftAndRightScrollOnlyWhenThePagesAreWiderThanTheViewport()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var scroller = view.ScrollViewer;
        Assert.True(view.Layout!.ContentWidth < scroller.Viewport.Width);

        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(0, scroller.Offset.X);

        viewModel.ActiveTab!.Zoom = 4;
        Settle(window);
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width);
        var x = scroller.Offset.X;
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Assert.Equal(x + scroller.SmallChange.Width, scroller.Offset.X, 6);
        Press(window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(x, scroller.Offset.X, 6);

        // Not past the left edge.
        for (var i = 0; i < 200 && scroller.Offset.X > 0; i++)
        {
            Press(window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.None);
        }

        Assert.Equal(0, scroller.Offset.X);
        Press(window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.None);
        Assert.Equal(0, scroller.Offset.X);
    }

    [AvaloniaFact]
    public async Task ClickOnThePagesGivesTheKeysToTheDocumentWithoutAFocusRing()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var field = Find<TextBox>(window, "PageField");
        field.Focus();
        Settle(window);
        Assert.False(view.IsFocused);

        var point = view.Canvas.TranslatePoint(new Point(view.Canvas.Bounds.Width / 2, 200), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);

        Assert.True(view.IsFocused);
        Assert.DoesNotContain(":focus-visible", view.Classes);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(view.ScrollViewer.SmallChange.Height, view.ScrollViewer.Offset.Y, 6);
    }

    [AvaloniaFact]
    public async Task KeysInThePageFieldDoNotScrollTheDocument()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        viewModel.ActiveTab!.GoToPage(10);
        Settle(window);
        var offset = view.ScrollViewer.Offset;
        var field = Find<TextBox>(window, "PageField");
        field.Focus();
        Settle(window);

        foreach (var (key, physicalKey) in new[] { (Key.Down, PhysicalKey.ArrowDown), (Key.Up, PhysicalKey.ArrowUp), (Key.Home, PhysicalKey.Home), (Key.End, PhysicalKey.End), (Key.Right, PhysicalKey.ArrowRight) })
        {
            Press(window, key, physicalKey, RawInputModifiers.None);
        }

        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyTextInput(" ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Settle(window);

        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.True(field.IsFocused);
        Assert.False(view.IsFocused);
        Assert.Contains(" ", field.Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task SpaceInThePasswordFieldTypesASpaceAndTheOpenedDocumentTakesTheFocus()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        await WaitUntilAsync(window, () => viewModel.ActiveTab!.State == ViewerState.PasswordRequired);
        var field = Find<TextBox>(window, "PasswordField");
        Assert.True(field.IsFocused);

        window.KeyTextInput("a");
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyTextInput(" ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyTextInput("b");
        Settle(window);
        Assert.Equal("a b", viewModel.ActiveTab!.Password);
        Assert.True(field.IsFocused);

        // The right password opens the document, which then has the keys (the field is gone).
        field.Text = "hôtel";
        viewModel.ActiveTab!.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.ActiveTab!.State == ViewerState.Ready && view.Document is not null);
        Assert.True(view.IsFocused);
    }

    [AvaloniaFact]
    public async Task CurrentPageFollowsTheScrollPosition()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var layout = view.Layout!;
        var field = Find<TextBox>(window, "PageField");

        Scroll(window, view, layout.GetPageRect(249).Y);
        Assert.Equal(250, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal("250", field.Text);

        // The previous page just left the viewport.
        var next = layout.GetPageRect(250);
        Scroll(window, view, next.Y - (DocumentLayout.PageGap / 2));
        Assert.Equal(251, viewModel.ActiveTab!.CurrentPage);

        Scroll(window, view, 0);
        Assert.Equal(1, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal("1", field.Text);
    }

    [AvaloniaFact]
    public async Task LoadingMorePageSizesKeepsTheTopPageInPlace()
    {
        // The first batch is real; the later batches wait for the test and come with other sizes than the estimate.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var (window, viewModel, view) = Show(() => new ViewerSession(
            engine.OpenAsync,
            async (document, firstPage, count, priority, cancellationToken) =>
            {
                if (firstPage == 0)
                {
                    return await document.GetPageSizesAsync(firstPage, count, priority, cancellationToken).ConfigureAwait(false);
                }

                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return Enumerable.Repeat(new PdfPageSize(400, 300), count).ToArray();
            }));
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
        Assert.Equal(layout.GetMostVisiblePage(top, top + view.ScrollViewer.Viewport.Height) + 1, viewModel.ActiveTab!.CurrentPage);
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
        Assert.True(viewModel.ActiveTab!.Session.Cache!.Contains(sharp));
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
            (request, _, _) =>
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
        viewModel.ActiveTab!.Zoom = 2;
        Settle(window);
        viewModel.ActiveTab!.GoToPage(400);
        Settle(window);
        Assert.Equal(400, viewModel.ActiveTab!.CurrentPage);
        Assert.True(view.ScrollViewer.Offset.Y > 0);

        // Replaced in the same tab ("Open another file"); a new tab starts like this anyway (TabsTests).
        var tab = viewModel.ActiveTab!;
        await tab.OpenAsync(TestData.Pdf("text-basic.pdf"));
        await WaitUntilAsync(window, () => view.Document is { } document && ReferenceEquals(document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);

        Assert.Same(tab, Assert.Single(viewModel.Tabs));
        Assert.Equal(2, view.Layout!.PageCount);
        Assert.Equal(1.0, viewModel.ActiveTab!.Zoom);
        Assert.Equal(1.0, view.Layout.Zoom);
        Assert.Equal(default, view.ScrollViewer.Offset);
        Assert.Equal(1, viewModel.ActiveTab!.CurrentPage);
        Assert.Equal("1", Find<TextBox>(window, "PageField").Text);
        Assert.All(view.Canvas.LastRequests, request => Assert.InRange(request.Key.Page, 0, 1));
    }

    [AvaloniaFact]
    public async Task SwitchingTabsRestoresPageZoomAndScrollPosition()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.Zoom = 2;
        Settle(window);
        a.GoToPage(3);
        Settle(window);
        view.ScrollViewer.Offset = new Vector(0, view.ScrollViewer.Offset.Y + 37);
        Settle(window);
        var offset = view.ScrollViewer.Offset;
        var pageBefore = a.CurrentPage;
        var anchor = view.Layout!.GetAnchor(offset.Y);
        Assert.Equal(2, anchor.Page);

        var b = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal(anchor, a.SavedView!.Anchor);

        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);

        Assert.Equal(2.0, a.Zoom);
        Assert.Equal(2.0, view.Layout!.Zoom);
        var restored = view.Layout.GetAnchor(view.ScrollViewer.Offset.Y);
        Assert.Equal(anchor.Page, restored.Page);
        Assert.Equal(offset.Y, view.ScrollViewer.Offset.Y, 1);
        Assert.Equal(offset.X, view.ScrollViewer.Offset.X, 1);
        Assert.Equal(pageBefore, a.CurrentPage);
        Assert.Equal(1.0, b.Zoom);
    }

    [AvaloniaFact]
    public async Task SwitchingTabsRestoresTheHorizontalPositionOfWidePages()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "wide-page.pdf");
        a.Zoom = 4;
        Settle(window);
        Assert.True(view.ScrollViewer.Extent.Width > view.ScrollViewer.Viewport.Width + 300);
        view.ScrollViewer.Offset = new Vector(300, 10);
        Settle(window);
        var offset = view.ScrollViewer.Offset;
        Assert.Equal(300, offset.X);

        await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);

        Assert.Equal(4.0, view.Layout!.Zoom);
        Assert.Equal(offset.X, view.ScrollViewer.Offset.X, 1);
        Assert.Equal(offset.Y, view.ScrollViewer.Offset.Y, 1);
    }

    [AvaloniaFact]
    public async Task ResizeWhileTheTabIsInactiveKeepsTheCentreOfTheViewport()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "wide-page.pdf");
        a.Zoom = 4;
        Settle(window);
        view.ScrollViewer.Offset = new Vector(300, 0);
        Settle(window);
        var centre = view.ScrollViewer.Offset.X + (view.ScrollViewer.Viewport.Width / 2);
        var widthBefore = view.ScrollViewer.Viewport.Width;

        await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        window.Width = 800;
        Settle(window);
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);

        // The same spot of the page stays in the middle of the narrower viewport (not the left edge).
        var scroller = view.ScrollViewer;
        Assert.True(scroller.Viewport.Width < widthBefore - 100);
        Assert.Equal(centre, scroller.Offset.X + (scroller.Viewport.Width / 2), 1);

        // Content narrower than the viewport stays centred in a wider window.
        var b = viewModel.Tabs[1];
        viewModel.ActivateTabCommand.Execute(b);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, b.Session.Document) && view.Canvas.LastRequests.Count > 0);
        Assert.Equal(0, scroller.Offset.X);
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);
        window.Width = 1100;
        Settle(window);
        viewModel.ActivateTabCommand.Execute(b);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, b.Session.Document) && view.Canvas.LastRequests.Count > 0);
        Assert.Equal(0, scroller.Offset.X);
    }

    [AvaloniaFact]
    public async Task SwitchingAwayWhileTheTabLoadsAReplacementKeepsNoStalePosition()
    {
        // The open of the replacement waits for the test: the session stays in Loading with the old document.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var replacementPath = TestData.Pdf("hello_world.pdf");
        var (window, viewModel, view) = Show(() => new ViewerSession(async (path, password, cancellationToken) =>
        {
            if (path == replacementPath)
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        }));
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.Zoom = 2;
        Settle(window);
        a.GoToPage(40);
        Settle(window);
        var b = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);
        var oldDocument = a.Session.Document;

        // The replacement is loading (the session still holds the old document), and the tab is left before the
        // posted job of the view, which would close the old document, ran.
        var replacement = a.OpenAsync(replacementPath);
        Assert.Equal(ViewerState.Loading, a.Session.State);
        Assert.Same(oldDocument, a.Session.Document);
        viewModel.ActivateTabCommand.Execute(b);
        Assert.Null(a.SavedView);

        gate.SetResult();
        await replacement;
        Settle(window);
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);

        // The new document starts at the top with the default zoom, not at the position of the old one.
        Assert.NotSame(oldDocument, view.Document);
        Assert.Equal(1.0, a.Zoom);
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal(default, view.ScrollViewer.Offset);
        Assert.Equal(1, a.CurrentPage);
    }

    [AvaloniaFact]
    public async Task SyncingAnUnchangedDocumentRaisesNothingOnTheTab()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        await WaitUntilAsync(window, () => a.Session.LoadedSizes.Count == 1000);
        Settle(window);
        var raised = new List<string?>();
        a.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        // What the view does for every batch of page sizes: the document is the one the tab already has.
        a.SyncDocumentFromSession();
        Assert.Empty(raised);
        Assert.Same(a.Session.Document, view.Document);

        // A document the tab has not seen yet is taken at once.
        a.Zoom = 2;
        raised.Clear();
        var replacement = a.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var deadline = DateTime.UtcNow + Timeout;
        while (a.Session.State != ViewerState.Ready || a.Session.Path != TestData.Pdf("hello_world.pdf"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the replacement did not open");
            Thread.Sleep(5);
        }

        a.SyncDocumentFromSession();
        Assert.Equal(1.0, a.Zoom);
        Assert.Equal(a.Session.Document!.PageCount, a.PageCount);
        await replacement;
    }

    [AvaloniaFact]
    public async Task ANewTabStartsAtTheTopWithDefaultZoom()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.Zoom = 2;
        Settle(window);
        a.GoToPage(40);
        Settle(window);
        Assert.True(view.ScrollViewer.Offset.Y > 0);

        var b = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");

        Assert.Equal(1.0, b.Zoom);
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal(default, view.ScrollViewer.Offset);
        Assert.Equal(1, b.CurrentPage);
        Assert.Null(b.SavedView);
        Assert.Equal(2.0, a.Zoom);
    }

    [AvaloniaFact]
    public async Task ADocumentShownBeforeItsTabRefreshedStillStartsAtTheTopWithDefaultZoom()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.Zoom = 2;
        Settle(window);
        a.GoToPage(40);
        Settle(window);
        var b = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        Assert.NotNull(a.SavedView);

        // A new document arrives in the inactive tab; the jobs of the UI thread (the refresh of the tab) do not run
        // before the tab becomes the active one again.
        var replacement = a.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var deadline = DateTime.UtcNow + Timeout;
        while (a.Session.State != ViewerState.Ready || a.Session.Path != TestData.Pdf("hello_world.pdf"))
        {
            Assert.True(DateTime.UtcNow < deadline, "the replacement did not open");
            Thread.Sleep(5);
        }

        Assert.Equal(2.0, a.Zoom);
        viewModel.ActivateTabCommand.Execute(a);

        // Without any further job: the view already shows the new document at the default zoom, at the top.
        Assert.Same(a.Session.Document, view.Document);
        Assert.Equal(1.0, a.Zoom);
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal(default, view.ScrollViewer.Offset);
        Assert.Null(a.SavedView);
        await replacement;
        Settle(window);
        Assert.Equal(1.0, view.Layout.Zoom);
        Assert.Equal(default, view.ScrollViewer.Offset);
        Assert.Equal(1, a.CurrentPage);
        Assert.Equal(1.0, b.Zoom);
    }

    [AvaloniaFact]
    public async Task InactiveTabsRequestNoRendering()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.Zoom = 0.5;
        Settle(window);
        view.ScrollViewer.Offset = new Vector(0, view.Layout!.GetPageRect(300).Y);
        Settle(window);
        Assert.NotEmpty(view.Canvas.LastRequests);

        var b = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");

        // The old tab has no wish list any more, whatever it was rendering; the canvas serves the new tab.
        Assert.Equal(0, a.Session.Scheduler!.PendingCount);
        Assert.Same(b.Session.Scheduler, view.Canvas.Scheduler);
        await Task.Delay(100);
        Settle(window);
        Assert.Equal(0, a.Session.Scheduler.PendingCount);
        Assert.All(view.Canvas.LastRequests, request => Assert.InRange(request.Key.Page, 0, 40));

        // And back: the other tab is the quiet one.
        viewModel.ActivateTabCommand.Execute(a);
        Assert.Equal(0, b.Session.Scheduler!.PendingCount);
        Assert.Same(a.Session.Scheduler, view.Canvas.Scheduler);
    }

    [AvaloniaFact]
    public async Task SwitchingBackShowsCachedTilesWithoutRendering()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var preview = new TileKey(0, TileKey.PreviewScaleKey, 0, 0);
        await WaitUntilAsync(window, () => HasSharpTile(view, 0) && view.Canvas.Bitmaps.Contains(preview));
        await OpenTabAsync(window, viewModel, view, "hello_world.pdf");

        viewModel.ActivateTabCommand.Execute(a);

        // No job ran since: the cache of the tab delivers the images in the same call that shows the document.
        Assert.Same(a.Session.Document, view.Document);
        Assert.True(view.Canvas.Bitmaps.Contains(preview));
        Assert.True(HasSharpTile(view, 0));
        Assert.Equal(0, a.Session.Scheduler!.PendingCount);
    }

    [AvaloniaFact]
    public async Task ClosingTheActiveTabShowsTheNeighbourAtItsSavedPosition()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var b = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        b.Zoom = 1.5;
        Settle(window);
        b.GoToPage(10);
        Settle(window);
        view.ScrollViewer.Offset = new Vector(view.ScrollViewer.Offset.X, view.ScrollViewer.Offset.Y + 50);
        Settle(window);
        var offset = view.ScrollViewer.Offset;
        var pageBefore = b.CurrentPage;
        var c = await OpenTabAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal([a, b, c], viewModel.Tabs);

        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, b.Session.Document) && view.Canvas.LastRequests.Count > 0);

        Assert.Same(b, viewModel.ActiveTab);
        Assert.Equal(1.5, view.Layout!.Zoom);
        Assert.Equal(offset.Y, view.ScrollViewer.Offset.Y, 1);
        Assert.Equal(offset.X, view.ScrollViewer.Offset.X, 1);
        Assert.Equal(pageBefore, b.CurrentPage);
        Assert.Equal(pageBefore.ToString(CultureInfo.InvariantCulture), Find<TextBox>(window, "PageField").Text);
    }

    [AvaloniaFact]
    public async Task ResizeWhileAnotherTabIsActiveKeepsTheSavedAnchor()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.Zoom = 2;
        Settle(window);
        a.GoToPage(5);
        Settle(window);
        view.ScrollViewer.Offset = new Vector(view.ScrollViewer.Offset.X, view.ScrollViewer.Offset.Y + 90);
        Settle(window);
        var anchor = view.Layout!.GetAnchor(view.ScrollViewer.Offset.Y);
        await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(anchor, a.SavedView!.Anchor);

        window.Width = 700;
        window.Height = 500;
        Settle(window);
        Assert.Equal(anchor, a.SavedView.Anchor);

        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);

        // Page and fraction, not pixels: the same spot of the page is at the top of the smaller window.
        var restored = view.Layout!.GetAnchor(view.ScrollViewer.Offset.Y);
        Assert.Equal(anchor.Page, restored.Page);
        Assert.Equal(anchor.Fraction, restored.Fraction, 3);
        Assert.Equal(2.0, view.Layout.Zoom);
        Assert.Equal(5, a.CurrentPage);
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

    [AvaloniaTheory]
    [InlineData(1.0, 1333)]
    [InlineData(1.25, 1667)]
    public async Task SharpImagesAreDrawnOneToOneOnDevicePixels(double scaling, int scaleKey)
    {
        // At 100 % the scale keys are rounded (1.3333… → 1.333, 1.6666… → 1.667): the image must still map 1:1.
        var (window, viewModel, view) = Show();
        window.SetRenderScaling(scaling);
        Settle(window);
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var key = new TileKey(0, scaleKey, 0, 0);
        Assert.Contains(view.Canvas.LastRequests, request => request.Key == key);

        await WaitUntilAsync(window, () => view.Canvas.LastSharpDestinations.Any(drawn => drawn.Key == key));
        Assert.True(view.Canvas.Bitmaps.TryGet(key, out var bitmap));
        var destination = view.Canvas.LastSharpDestinations.First(drawn => drawn.Key == key).Destination;

        Assert.Equal(bitmap.PixelSize.Width / scaling, destination.Width, 9);
        Assert.Equal(bitmap.PixelSize.Height / scaling, destination.Height, 9);
        AssertOnDevicePixel(destination.X, scaling);
        AssertOnDevicePixel(destination.Y, scaling);

        // The canvas itself sits on the device grid, so canvas coordinates on it are device pixels of the window.
        var origin = view.Canvas.TranslatePoint(default, window)!.Value;
        AssertOnDevicePixel(origin.X, scaling);
        AssertOnDevicePixel(origin.Y, scaling);
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public async Task TilesOfALargePageAbutExactlyOnDevicePixels(double scaling)
    {
        var (window, viewModel, view) = Show();
        window.SetRenderScaling(scaling);
        Settle(window);
        await OpenAsync(window, viewModel, view, "text-basic.pdf");

        // At 800 % the page has more than 4 Mpx at every scaling, so it is rendered in 512 px tiles.
        viewModel.ActiveTab!.Zoom = 8;
        Settle(window);
        view.ScrollViewer.Offset = new Vector(300, 300);
        Settle(window);
        var tiles = view.Canvas.LastRequests.Where(request => !request.Key.IsPreview && request.Key.Page == 0).Select(request => request.Key).ToList();
        Assert.True(tiles.Count >= 4, $"{tiles.Count} tiles");
        Assert.All(tiles, key => Assert.Equal(TileKey.ToScaleKey(DocumentLayout.DipsPerPoint * 8 * scaling), key.ScaleKey));
        await WaitUntilAsync(window, () => tiles.All(key => view.Canvas.LastSharpDestinations.Any(drawn => drawn.Key == key)));

        var drawn = view.Canvas.LastSharpDestinations.Where(item => tiles.Contains(item.Key)).ToDictionary(item => item.Key, item => item.Destination);
        var neighbours = 0;
        foreach (var (key, rect) in drawn)
        {
            AssertOnDevicePixel(rect.X, scaling);
            AssertOnDevicePixel(rect.Y, scaling);
            Assert.True(view.Canvas.Bitmaps.TryGet(key, out var bitmap));
            Assert.Equal(bitmap.PixelSize.Width / scaling, rect.Width, 9);
            Assert.Equal(bitmap.PixelSize.Height / scaling, rect.Height, 9);

            if (drawn.TryGetValue(key with { Column = key.Column + 1 }, out var right))
            {
                Assert.Equal(rect.Right, right.X, 9);
                Assert.Equal(rect.Y, right.Y, 9);
                neighbours++;
            }

            if (drawn.TryGetValue(key with { Row = key.Row + 1 }, out var below))
            {
                Assert.Equal(rect.Bottom, below.Y, 9);
                Assert.Equal(rect.X, below.X, 9);
                neighbours++;
            }
        }

        Assert.True(neighbours >= 2, $"{neighbours} neighbouring pairs");
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
        viewModel.ActiveTab!.Zoom = 2;
        await WaitUntilAsync(window, () => view.Canvas.Bitmaps.Keys.Any(key => !key.IsPreview && key.ScaleKey == TileKey.ToScaleKey(DocumentLayout.DipsPerPoint * 2)));
        Settle(window);
        Assert.Equal(1, raised);

        await OpenAsync(window, viewModel, view, "hello_world.pdf");
        await WaitUntilAsync(window, () => raised == 2);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(Func<ViewerSession>? factory = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(factory ?? (() => new ViewerSession(new PdfEngine())), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    private static async Task OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        await WaitUntilAsync(window, () => view.Document is { } document && ReferenceEquals(document, viewModel.ActiveTab?.Session.Document) && view.Canvas.LastRequests.Count > 0);

        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
    }

    /// <summary>Opens a test file in a new tab and waits until the view shows it; returns that tab.</summary>
    private static async Task<TabViewModel> OpenTabAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);

        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        return tab;
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

    private static void AssertOnDevicePixel(double dips, double scaling)
    {
        var pixels = dips * scaling;
        Assert.True(Math.Abs(pixels - Math.Round(pixels)) < 1e-6, $"{dips} DIP is {pixels} device pixels, not on the grid");
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
