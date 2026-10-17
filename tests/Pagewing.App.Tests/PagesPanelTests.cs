using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
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

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PagesPanelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

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
    public async Task PanelShowsOnePlaceholderPerPageInThePageAspectRatio()
    {
        Strings.Culture = new CultureInfo("en");
        var (window, viewModel, _) = Show(800);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await OpenPagesPanelAsync(window, viewModel);

        // One entry per page, 136 wide and as high as the page format asks (outline.pdf: 300 x 400 pt), the number below.
        Assert.Equal(3, list.Thumbnails.Count);
        Assert.Equal(3, list.ItemCount);
        for (var index = 0; index < 3; index++)
        {
            list.ScrollIntoView(index);
            Settle(window);
            var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
            var frame = Find<Control>(container, "ThumbnailFrame");
            Assert.Equal(ThumbnailPlanner.Width, frame.Bounds.Width, 0.5);
            Assert.Equal(ThumbnailPlanner.HeightFor(new PdfPageSize(300, 400)), frame.Bounds.Height, 1.0);
            var number = Find<TextBlock>(container, "PageNumber");
            Assert.Equal((index + 1).ToString(CultureInfo.InvariantCulture), number.Text);
            Assert.Contains("mono", number.Classes);
            Assert.True(number.TranslatePoint(default, frame)!.Value.Y >= frame.Bounds.Height, "the number lies below the frame");
            Assert.Equal($"Page {index + 1}", AutomationName(container).Split(',')[0]);
        }

        // A long document: one entry per page, in its format (200 x 100 pt). Entries that were never shown are empty
        // frames: surface.raised with a border.subtle edge, no image.
        var thousand = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        list = await WaitForListAsync(window, thousand);
        Assert.Equal(1000, list.Thumbnails.Count);
        list.ScrollIntoView(700);
        list.UpdateLayout();
        var fresh = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(700));
        Assert.Null(list.Thumbnails[700].Bitmap);
        var placeholder = Find<Border>(fresh, "Placeholder");
        var freshFrame = Find<Control>(fresh, "ThumbnailFrame");
        Assert.Equal(ThumbnailPlanner.Width, freshFrame.Bounds.Width, 0.5);
        Assert.Equal(ThumbnailPlanner.HeightFor(new PdfPageSize(200, 100)), freshFrame.Bounds.Height, 1.0);
        Assert.Equal(TokenTests.Token("Surface.Raised", ThemeVariant.Light), SolidColor(placeholder.Background));
        Assert.Equal(TokenTests.Token("Border.Subtle", ThemeVariant.Light), SolidColor(placeholder.BorderBrush));
        Assert.Equal(new Thickness(1), placeholder.BorderThickness);
        Assert.Equal("701", Find<TextBlock>(fresh, "PageNumber").Text);
        Assert.NotEqual(tab, thousand);
    }

    [AvaloniaFact]
    public async Task OnlyRealizedItemsAreRequested()
    {
        var (window, viewModel, _) = Show(800);
        await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await OpenPagesPanelAsync(window, viewModel);
        await WaitUntilAsync(window, () => list.LastRequests.Count > 0);

        // Virtualized: a handful of the 1000 entries exist, and only those are planned, at most MaxRequests.
        var realized = RealizedIndexes(list);
        Assert.Equal(1000, list.Thumbnails.Count);
        Assert.InRange(realized.Count, 1, 100);
        Assert.InRange(list.LastRequests.Count, 1, ThumbnailPlanner.MaxRequests);
        Assert.All(list.LastRequests, request => Assert.Contains(request.Key.Page, realized));
        Assert.All(list.LastRequests, request => Assert.Equal(new TileKey(request.Key.Page, TileKey.PreviewScaleKey, 0, 0), request.Key));
        Assert.All(list.LastRequests, request => Assert.True(request.Background));

        // Scrolling the list plans the entries that came into view, and none of those that left it.
        list.ScrollIntoView(600);
        await WaitUntilAsync(window, () => list.LastRequests.Any(request => request.Key.Page == 600));
        realized = RealizedIndexes(list);
        Assert.InRange(list.LastRequests.Count, 1, ThumbnailPlanner.MaxRequests);
        Assert.All(list.LastRequests, request => Assert.Contains(request.Key.Page, realized));
        Assert.DoesNotContain(list.LastRequests, request => request.Key.Page < 100);

        // The bitmaps follow the entries: none for an entry that is not realized.
        await WaitUntilAsync(window, () => list.Thumbnails[600].Bitmap is not null);
        realized = RealizedIndexes(list);
        for (var index = 0; index < list.Thumbnails.Count; index++)
        {
            if (!realized.Contains(index))
            {
                Assert.Null(list.Thumbnails[index].Bitmap);
            }
        }
    }

    [AvaloniaFact]
    public async Task ThumbnailsRenderAfterTheFirstSharpPage()
    {
        // The file opens only when the test lets it, so the panel is there before the document.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var (window, viewModel, view) = Show(800, factory: () => new ViewerSession(async (path, password, cancellationToken) =>
        {
            await gate.Task.ConfigureAwait(false);
            return await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        }));

        var opening = viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        Settle(window);
        var tab = viewModel.ActiveTab!;
        Assert.Equal(ViewerState.Loading, tab.State);
        var list = await OpenPagesPanelAsync(window, viewModel);
        Assert.Empty(list.Thumbnails);
        Assert.False(tab.HasShownSharpPage);

        // When the tab reports its first sharp page, the empty frames are there, and the thumbnails have not asked for
        // anything yet (not even for their scheduler).
        bool? sharpAtReady = null;
        int? plansAtSharp = null;
        int? itemsAtSharp = null;
        bool? bitmapsAtSharp = null;
        bool? sourceAtSharp = null;
        bool? viewSharpAtSharp = null;
        void OnTabChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TabViewModel.State) && tab.State == ViewerState.Ready)
            {
                sharpAtReady ??= tab.HasShownSharpPage;
            }
            else if (e.PropertyName == nameof(TabViewModel.HasShownSharpPage) && tab.HasShownSharpPage && plansAtSharp is null)
            {
                plansAtSharp = list.PlanCount;
                itemsAtSharp = list.Thumbnails.Count;
                bitmapsAtSharp = list.Thumbnails.Any(item => item.Bitmap is not null);
                sourceAtSharp = list.Source is not null;
                viewSharpAtSharp = view.Canvas.Bitmaps.Keys.Any(key => !key.IsPreview);
            }
        }

        tab.PropertyChanged += OnTabChanged;
        gate.SetResult();
        await opening.WaitAsync(Timeout);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        tab.PropertyChanged -= OnTabChanged;

        Assert.False(sharpAtReady);
        Assert.Equal(0, plansAtSharp);
        Assert.Equal(3, itemsAtSharp);
        Assert.False(bitmapsAtSharp);
        Assert.False(sourceAtSharp);
        Assert.True(viewSharpAtSharp);

        // After it, the thumbnails render.
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.True(list.PlanCount > 0);
    }

    [AvaloniaFact]
    public async Task ThumbnailAppearsWhenRendered()
    {
        var (window, viewModel, _) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");

        // The wide window opened "Pages" after the first sharp page; the thumbnail of page 1 replaces its empty frame.
        var list = await WaitForListAsync(window, tab);
        var received = new List<TileKey>();
        list.ThumbnailReceived += received.Add;
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        var bitmap = list.Thumbnails[0].Bitmap!;
        Assert.Equal(ThumbnailPlanner.Width, bitmap.PixelSize.Width);
        Assert.Equal(ThumbnailPlanner.HeightFor(new PdfPageSize(300, 400)), bitmap.PixelSize.Height, 1.0);

        var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
        var image = Find<Image>(container, "ThumbnailImage");
        Assert.Same(bitmap, image.Source);
        Assert.True(image.IsEffectivelyVisible);
        var frame = Find<Control>(container, "ThumbnailFrame");
        var centre = frame.TranslatePoint(new Point(frame.Bounds.Width / 2, frame.Bounds.Height / 2), window)!.Value;
        using (var pixels = new FramePixels(window.CaptureRenderedFrame()!))
        {
            // outline.pdf is a gray area: no longer the white (surface.raised) frame.
            var color = pixels.GetColor((int)centre.X, (int)centre.Y);
            Assert.NotEqual(TokenTests.Token("Surface.Raised", ThemeVariant.Light), color);
            Assert.Equal(color.R, color.G);
            Assert.Equal(color.G, color.B);
        }

        // The thumbnails live in their own cache, under the preview key of the page.
        Assert.True(list.Source!.Cache.Contains(new TileKey(0, TileKey.PreviewScaleKey, 0, 0)));
    }

    [AvaloniaFact]
    public async Task ClickingAThumbnailJumpsToThePage()
    {
        var (window, viewModel, _) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await WaitForListAsync(window, tab);
        Assert.Equal(1, tab.CurrentPage);

        // A click on an entry shows that page; beside the document the panel stays open.
        list.ScrollIntoView(2);
        Settle(window);
        Click(window, Assert.IsType<ListBoxItem>(list.ContainerFromIndex(2)));
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);

        // The arrow keys move the focus without jumping; Enter and Space jump to the focused entry.
        list.ScrollIntoView(0);
        Settle(window);
        Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0)).Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Down);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Same(list.ContainerFromIndex(1), window.FocusManager!.GetFocusedElement());
        Press(window, Key.Enter);
        Assert.Equal(2, tab.CurrentPage);

        Press(window, Key.Up);
        Assert.Equal(2, tab.CurrentPage);
        Press(window, Key.Space);
        Assert.Equal(1, tab.CurrentPage);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task CurrentPageIsMarkedWithAFrameAndABoldNumber(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel, _) = Show(1024, variant);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await WaitForListAsync(window, tab);

        tab.GoToPage(2);
        list.ScrollIntoView(1);
        Settle(window);
        Assert.Equal([false, true, false], list.Thumbnails.Select(item => item.IsCurrent));

        var subtle = TokenTests.Token("Accent.Subtle", variant);
        var accent = TokenTests.Token("Accent.Default", variant);
        var current = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
        var other = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));

        // Design system 7.3 and 7.10: an accent.subtle surface, a 2 px accent.default frame and a bold number, so the
        // current page is not marked by colour alone.
        Assert.Equal(subtle, SolidColor(Find<Border>(current, "ThumbnailCell").Background));
        var ring = Find<Border>(current, "CurrentRing");
        Assert.Equal(new Thickness(2), ring.BorderThickness);
        Assert.Equal(accent, SolidColor(ring.BorderBrush));
        Assert.True(Find<TextBlock>(current, "PageNumber").FontWeight >= FontWeight.SemiBold);
        Assert.Equal(TokenTests.Token("Text.Primary", variant), SolidColor(Find<TextBlock>(current, "PageNumber").Foreground));
        Assert.EndsWith(Strings.ThumbnailCurrentSuffix, AutomationName(current), StringComparison.Ordinal);

        Assert.True(Find<Border>(other, "ThumbnailCell").Background is null or ISolidColorBrush { Color.A: 0 }, "no surface behind another page");
        Assert.True(Find<Border>(other, "CurrentRing").BorderBrush is null or ISolidColorBrush { Color.A: 0 }, "no frame around another page");
        Assert.Equal(FontWeight.Normal, Find<TextBlock>(other, "PageNumber").FontWeight);
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), SolidColor(Find<TextBlock>(other, "PageNumber").Foreground));

        using (var pixels = new FramePixels(window.CaptureRenderedFrame()!))
        {
            var cell = Find<Border>(current, "ThumbnailCell");
            var inCell = cell.TranslatePoint(new Point(3, cell.Bounds.Height / 2), window)!.Value;
            var onRing = ring.TranslatePoint(new Point(1, ring.Bounds.Height / 2), window)!.Value;
            Assert.Equal(subtle, pixels.GetColor((int)inCell.X, (int)inCell.Y));
            Assert.Equal(accent, pixels.GetColor((int)onRing.X, (int)onRing.Y));
        }

        Assert.True(Wcag.Contrast(accent, subtle) >= 3, "accent.default on accent.subtle");
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Primary", variant), subtle) >= 4.5, "text.primary on accent.subtle");
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Secondary", variant), TokenTests.Token("Surface.Sidebar", variant)) >= 4.5, "text.secondary on surface.sidebar");
    }

    [AvaloniaFact]
    public async Task BothPagesOfARowAreMarkedInTwoPageMode()
    {
        var (window, viewModel, _) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await WaitForListAsync(window, tab);

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);
        tab.GoToPage(1);
        Settle(window);
        Assert.Equal((1, 2), (tab.CurrentPage, tab.CurrentPageLast));
        Assert.Equal([true, true, false], list.Thumbnails.Select(item => item.IsCurrent));

        tab.GoToPage(3);
        Settle(window);
        Assert.Equal([false, false, true], list.Thumbnails.Select(item => item.IsCurrent));

        // With a cover page the rows are 1, then 2 and 3.
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPagesWithCover);
        Settle(window);
        tab.GoToPage(2);
        Settle(window);
        Assert.Equal([false, true, true], list.Thumbnails.Select(item => item.IsCurrent));
        tab.GoToPage(1);
        Settle(window);
        Assert.Equal([true, false, false], list.Thumbnails.Select(item => item.IsCurrent));
    }

    [AvaloniaFact]
    public async Task SelectionFollowsTheDocumentScroll()
    {
        var (window, viewModel, view) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await WaitForListAsync(window, tab);
        Assert.Equal(0, list.SelectedIndex);

        // Scrolling the document to page 500 marks that page in the list and brings it into view.
        view.ScrollViewer.Offset = new Vector(view.ScrollViewer.Offset.X, view.Layout!.GetPageRect(499).Y);
        Settle(window);
        var page = tab.CurrentPage;
        Assert.InRange(page, 495, 505);
        Assert.Equal(page - 1, list.SelectedIndex);
        Assert.True(list.Thumbnails[page - 1].IsCurrent);
        Assert.False(list.Thumbnails[0].IsCurrent);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(page - 1));
        var top = container.TranslatePoint(default, list)!.Value.Y;
        Assert.InRange(top, 0, list.Bounds.Height - container.Bounds.Height);

        // The thumbnails plan from the current page outward.
        await WaitUntilAsync(window, () => list.LastRequests.Count > 0 && list.LastRequests[0].Key.Page == page - 1);

        // A jump from the page field does the same.
        tab.GoToPage(20);
        Settle(window);
        Assert.Equal(19, list.SelectedIndex);
        Assert.NotNull(list.ContainerFromIndex(19));
        Assert.Equal(1, list.Thumbnails.Count(item => item.IsCurrent));
    }

    [AvaloniaFact]
    public async Task ClosingThePanelCancelsThumbnailWork()
    {
        var (window, viewModel, _) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        var source = list.Source!;
        var items = list.Thumbnails.ToArray();

        // Closing stops the work at once (also while the panel fades out) and releases the bitmaps with the content.
        viewModel.CloseSidePanelCommand.Execute(null);
        Settle(window);
        Assert.Equal(0, source.Scheduler.PendingCount);
        Assert.Empty(list.LastRequests);
        await WaitUntilAsync(window, () => !Find<Control>(window, AutoOpenedPanel.HostName).IsVisible);
        Assert.All(items, item => Assert.Null(item.Bitmap));
        Assert.Null(FindOrNull<ThumbnailList>(window, "ThumbnailList"));

        // The active tab keeps its rendered thumbnails: opening the panel again shows them from the cache.
        Assert.True(source.Cache.Count > 0);
        Assert.Equal(0, source.Scheduler.PendingCount);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        var reopened = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => reopened.Thumbnails[0].Bitmap is not null);
        Assert.Same(source, reopened.Source);
    }

    [AvaloniaFact]
    public async Task SwitchingTabsReplacesTheThumbnails()
    {
        var (window, viewModel, _) = Show(1024);
        var first = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await WaitForListAsync(window, first);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        var firstSource = list.Source!;

        // The second tab opens without a panel; its own "Pages" shows its own pages.
        var second = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        list = await WaitForListAsync(window, second);
        Assert.Equal(1000, list.Thumbnails.Count);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        var secondSource = list.Source!;
        Assert.NotSame(firstSource, secondSource);
        Assert.Equal(0, firstSource.Scheduler.PendingCount);
        Assert.Equal(0, firstSource.Cache.Count);

        // Back to the first tab, which shows "Pages" too: the same list now shows its three pages, and the bitmaps of
        // the second document are gone.
        var secondItems = list.Thumbnails.ToArray();
        viewModel.ActivateTabCommand.Execute(first);
        Settle(window);
        Assert.Same(list, Find<ThumbnailList>(window, "ThumbnailList"));
        Assert.Equal(3, list.Thumbnails.Count);
        Assert.Equal(ThumbnailPlanner.HeightFor(new PdfPageSize(300, 400)), list.Thumbnails[0].FrameHeight, 9);
        Assert.All(secondItems, item => Assert.Null(item.Bitmap));
        Assert.Equal(0, secondSource.Scheduler.PendingCount);
        Assert.Equal(0, secondSource.Cache.Count);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.Same(firstSource, list.Source);
    }

    [AvaloniaFact]
    public async Task ThumbnailsUseTheirOwnCacheBudget()
    {
        // Narrow: the panel lies over the document, so the document view keeps its viewport and plans nothing new.
        var (window, viewModel, view) = Show(800);
        var tab = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        await WaitUntilAsync(window, () => view.Layout!.KnownPageCount == 1000 && tab.HasShownSharpPage && tab.Session.Scheduler!.PendingCount == 0);
        Settle(window);
        var mainCache = tab.Session.Cache!;
        var used = mainCache.UsedBytes;
        var count = mainCache.Count;

        var list = await OpenPagesPanelAsync(window, viewModel);
        await WaitUntilAsync(window, () => list.Source is { } source && source.Cache.Count >= 3 && source.Scheduler.PendingCount == 0);

        Assert.Equal(ThumbnailSource.BudgetBytes, list.Source!.Cache.BudgetBytes);
        Assert.True(list.Source.Cache.UsedBytes > 0);
        Assert.Same(list.Source, tab.EnsureThumbnails());
        Assert.Equal(used, mainCache.UsedBytes);
        Assert.Equal(count, mainCache.Count);
    }

    [AvaloniaFact]
    public async Task ThumbnailsAreCrispAtScaling150()
    {
        var (window, viewModel, _) = Show(1024);
        window.SetRenderScaling(1.5);
        Settle(window);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var list = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);

        // 136 DIP at 150 % are 204 device pixels: one image pixel per device pixel.
        Assert.Equal(204, list.Thumbnails[0].Bitmap!.PixelSize.Width);
        Assert.All(list.LastRequests, request => Assert.Equal(204, request.Rect.Width));
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
        Assert.Equal(136, Find<Image>(container, "ThumbnailImage").Bounds.Width, 0.5);
        var origin = Find<Control>(container, "ThumbnailFrame").TranslatePoint(default, window)!.Value;
        Assert.Equal(Math.Round(origin.X * 1.5), origin.X * 1.5, 6);
        Assert.Equal(Math.Round(origin.Y * 1.5), origin.Y * 1.5, 6);
    }

    [AvaloniaFact]
    public async Task NoThumbnailWorkForInactiveTabs()
    {
        var (window, viewModel, _) = Show(1024);
        var first = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await WaitForListAsync(window, first);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        var source = list.Source!;

        // Another tab becomes active: the inactive one renders nothing and keeps no thumbnails.
        var second = await OpenAsync(window, viewModel, "hello_world.pdf");
        Assert.False(first.IsActive);
        Assert.Equal(0, source.Scheduler.PendingCount);
        Assert.Equal(0, source.Cache.Count);
        for (var i = 0; i < 5; i++)
        {
            Settle(window);
        }

        Assert.Equal(0, source.Scheduler.PendingCount);
        Assert.Equal(0, source.Cache.Count);

        // Also when its panel was closed before the tab was left.
        viewModel.ActivateTabCommand.Execute(first);
        list = await WaitForListAsync(window, first);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.True(source.Cache.Count > 0);
        viewModel.CloseSidePanelCommand.Execute(null);
        await WaitUntilAsync(window, () => !Find<Control>(window, AutoOpenedPanel.HostName).IsVisible);
        Assert.True(source.Cache.Count > 0);
        viewModel.ActivateTabCommand.Execute(second);
        Settle(window);
        Assert.Equal(0, source.Cache.Count);
        Assert.Equal(0, source.Scheduler.PendingCount);
    }

    [AvaloniaFact]
    public async Task UnknownPageSizesTakeTheFirstPageAndAreRefreshed()
    {
        // The sizes after the first batch arrive when the test lets them, and they differ from the first page.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new PdfPageSize(100, 200);
        var (window, viewModel, _) = Show(800, factory: () => new ViewerSession(
            new PdfEngine().OpenAsync,
            async (document, firstPage, count, priority, cancellationToken) =>
            {
                if (firstPage == 0)
                {
                    return await document.GetPageSizesAsync(firstPage, count, priority, cancellationToken).ConfigureAwait(false);
                }

                await gate.Task.ConfigureAwait(false);
                return Enumerable.Repeat(later, count).ToArray();
            }));
        var tab = await OpenAsync(window, viewModel, "thousand-pages.pdf");
        var list = await OpenPagesPanelAsync(window, viewModel);

        // One entry per page from the start; the unknown ones in the format of the first page.
        Assert.Equal(1000, list.Thumbnails.Count);
        var first = ThumbnailPlanner.HeightFor(new PdfPageSize(200, 100));
        Assert.Equal(first, list.Thumbnails[0].FrameHeight, 9);
        Assert.Equal(first, list.Thumbnails[999].FrameHeight, 9);

        gate.SetResult();
        await WaitUntilAsync(window, () => tab.Session.LoadedSizes.Count == 1000);
        Settle(window);
        Assert.Equal(first, list.Thumbnails[0].FrameHeight, 9);
        Assert.Equal(ThumbnailPlanner.HeightFor(later), list.Thumbnails[999].FrameHeight, 9);
        list.ScrollIntoView(999);
        Settle(window);
        var frame = Find<Control>(Assert.IsType<ListBoxItem>(list.ContainerFromIndex(999)), "ThumbnailFrame");
        Assert.Equal(ThumbnailPlanner.HeightFor(later), frame.Bounds.Height, 1.0);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(double width, ThemeVariant? theme = null, Func<ViewerSession>? factory = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(factory ?? (() => new ViewerSession(new PdfEngine())), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = theme ?? ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a test file in a new tab and waits until the tab has shown its first sharp page; returns that tab.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        return tab;
    }

    /// <summary>Opens "Pages" in the active tab (it must not be open) and waits for its list.</summary>
    private static async Task<ThumbnailList> OpenPagesPanelAsync(Window window, MainViewModel viewModel)
    {
        Assert.NotEqual(SidePanelKind.Pages, viewModel.ActivePanel);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        return await WaitForListAsync(window, viewModel.ActiveTab!);
    }

    /// <summary>Waits until the window shows the list of thumbnails of <paramref name="tab"/>, fully faded in.</summary>
    private static async Task<ThumbnailList> WaitForListAsync(Window window, TabViewModel tab)
    {
        ThumbnailList? list = null;
        await WaitUntilAsync(window, () =>
        {
            list = FindOrNull<ThumbnailList>(window, "ThumbnailList");
            return list is { IsEffectivelyVisible: true }
                && ReferenceEquals(list.DataContext, tab)
                && list.Thumbnails.Count == tab.PageCount
                && list.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);
        });
        return list!;
    }

    private static HashSet<int> RealizedIndexes(ThumbnailList list) =>
        list.GetRealizedContainers().Select(list.IndexFromContainer).Where(index => index >= 0).ToHashSet();

    private static string AutomationName(Control control) => Avalonia.Automation.AutomationProperties.GetName(control) ?? string.Empty;

    private static Color SolidColor(IBrush? brush) => Assert.IsType<ISolidColorBrush>(brush, exactMatch: false).Color;

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control)
    {
        var point = CenterOf(window, control);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key)
    {
        window.KeyPress(key, RawInputModifiers.None, PhysicalKeyOf(key), null);
        window.KeyRelease(key, RawInputModifiers.None, PhysicalKeyOf(key), null);
        Settle(window);
    }

    private static PhysicalKey PhysicalKeyOf(Key key) => key switch
    {
        Key.Down => PhysicalKey.ArrowDown,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Enter => PhysicalKey.Enter,
        Key.Space => PhysicalKey.Space,
        _ => PhysicalKey.None,
    };

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
                throw new TimeoutException("The window did not reach the expected state.");
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
