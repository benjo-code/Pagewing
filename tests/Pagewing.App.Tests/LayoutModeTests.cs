using System.Globalization;
using System.Text;
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

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class LayoutModeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public LayoutModeTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task SwitchingToTwoPagesKeepsTheCurrentPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        tab.GoToPage(5);
        Settle(window);
        var scroller = view.ScrollViewer;

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);

        // Page 5 starts the row 5–6, which is where page 5 was: at the margin below the top of the viewport.
        var layout = view.Layout!;
        Assert.Equal(PageLayoutMode.TwoPages, tab.LayoutMode);
        Assert.Equal(PageLayoutMode.TwoPages, layout.Mode);
        Assert.Equal(5, tab.CurrentPage);
        Assert.Equal(6, tab.CurrentPageLast);
        Assert.Equal(layout.GetPageRect(4).Y - DocumentLayout.Margin, scroller.Offset.Y, 0.5);
        Assert.Equal(layout.GetPageRect(4).Y, layout.GetPageRect(5).Y);

        // Back to continuous and on to a single page: still page 5.
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.Continuous);
        Settle(window);
        Assert.Equal(5, tab.CurrentPage);
        Assert.Equal(5, tab.CurrentPageLast);
        Assert.Equal(layout.GetPageRect(4).Y - DocumentLayout.Margin, scroller.Offset.Y, 0.5);

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        Assert.Equal(5, tab.CurrentPage);
        Assert.True(layout.IsShown(4));
        Assert.False(layout.IsShown(3));
        Assert.Equal(0, scroller.Offset.Y);
    }

    [AvaloniaFact]
    public async Task TwoPageModeShowsBothPagesOfARowAtTheSameHeight()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        await WaitUntilAsync(window, () => DrawsPage(view, 0) && DrawsPage(view, 1));

        // The top tiles of both pages are drawn at the same height, page 2 to the right of page 1.
        var first = TopTile(view, 0);
        var second = TopTile(view, 1);
        Assert.Equal(first.Y, second.Y, 6);
        Assert.True(second.X > first.Right - 0.5, $"page 1 at {first}, page 2 at {second}");
        var axis = view.Canvas.Bounds.Width / 2;
        Assert.True(first.Right <= axis + 0.5 && second.X >= axis - 0.5, $"page 1 at {first}, page 2 at {second}, axis {axis}");
    }

    [AvaloniaFact]
    public async Task CoverModeShowsThePageAloneOnTheRight()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPagesWithCover);
        await WaitUntilAsync(window, () => DrawsPage(view, 0));

        var layout = view.Layout!;
        var axis = layout.ContentWidth / 2;
        Assert.True(layout.GetPageRect(0).X >= axis, $"cover at {layout.GetPageRect(0)}, axis {axis}");

        // Pages 2 and 3 share the next row, left and right of the axis.
        Assert.Equal(layout.GetPageRect(1).Y, layout.GetPageRect(2).Y);
        Assert.True(layout.GetPageRect(1).Right <= axis && layout.GetPageRect(2).X >= axis);
        Assert.True(layout.GetPageRect(1).Y > layout.GetPageRect(0).Bottom);

        // On the canvas the cover is drawn right of the middle.
        Assert.True(TopTile(view, 0).X >= (view.Canvas.Bounds.Width / 2) - 0.5, $"cover drawn at {TopTile(view, 0)}");
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(1, tab.CurrentPageLast);
    }

    [AvaloniaFact]
    public async Task NextPageInTwoPageModeMovesByARow()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        var scroller = view.ScrollViewer;
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(2, tab.CurrentPageLast);

        var next = Find<Button>(window, "NextPageButton");
        var previous = Find<Button>(window, "PreviousPageButton");
        next.Command!.Execute(null);
        Settle(window);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(4, tab.CurrentPageLast);
        Assert.Equal(view.Layout!.GetPageRect(2).Y - DocumentLayout.Margin, scroller.Offset.Y, 0.5);

        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(5, tab.CurrentPage);
        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(3, tab.CurrentPage);
        previous.Command!.Execute(null);
        Settle(window);
        Assert.Equal(1, tab.CurrentPage);
        Assert.False(previous.Command.CanExecute(null));

        // With the cover the first row has one page: 1, 2–3, 4–5.
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPagesWithCover);
        Settle(window);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(1, tab.CurrentPageLast);
        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(3, tab.CurrentPageLast);
        Press(window, Key.PageDown, PhysicalKey.PageDown, RawInputModifiers.None);
        Assert.Equal(4, tab.CurrentPage);
        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(2, tab.CurrentPage);
        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(1, tab.CurrentPage);

        // The last row: 1000 is alone in its row and the arrow stops there.
        tab.GoToPage(999);
        Settle(window);
        Assert.Equal(998, tab.CurrentPage);
        Assert.Equal(999, tab.CurrentPageLast);
        next.Command!.Execute(null);
        Settle(window);
        Assert.Equal(1000, tab.CurrentPage);
        Assert.Equal(1000, tab.CurrentPageLast);
        Assert.False(next.Command.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task PageFieldShowsTheFirstPageOfTheRow()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);
        var field = Find<TextBox>(window, "PageField");

        // Page 4 is the right page of the row 3–4.
        field.Focus();
        field.Text = "4";
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(4, tab.CurrentPageLast);
        Assert.Equal("3", field.Text);
        Assert.Equal(view.Layout!.GetPageRect(2).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 0.5);

        // Scrolling: the row that fills most of the viewport.
        Scroll(window, view, view.Layout.GetPageRect(7).Y - DocumentLayout.Margin);
        Assert.Equal(7, tab.CurrentPage);
        Assert.Equal(8, tab.CurrentPageLast);
        Assert.Equal("7", field.Text);
    }

    [AvaloniaFact]
    public async Task SinglePageModeShowsOnlyOnePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        tab.GoToPage(10);
        Settle(window);

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        await WaitUntilAsync(window, () => view.Canvas.LastRequests.All(request => request.Key.Page == 9));

        // Layout rounds the extent up to whole device pixels.
        var layout = view.Layout!;
        var scroller = view.ScrollViewer;
        Assert.Equal(layout.GetPageRect(9).Height + (2 * DocumentLayout.Margin), scroller.Extent.Height, 1.0);
        Assert.Equal(10, tab.CurrentPage);
        Assert.Equal(10, tab.CurrentPageLast);
        Assert.NotEmpty(view.Canvas.LastRequests);
        Assert.Equal([9], Enumerable.Range(0, layout.PageCount).Where(layout.IsShown));

        // The status bar arrow shows the next page alone.
        Find<Button>(window, "NextPageButton").Command!.Execute(null);
        await WaitUntilAsync(window, () => view.Canvas.LastRequests.Count > 0 && view.Canvas.LastRequests.All(request => request.Key.Page == 10));
        Assert.Equal(11, tab.CurrentPage);
        Assert.True(layout.IsShown(10));
        Assert.False(layout.IsShown(9));
        Assert.Equal(0, scroller.Offset.Y);
        Assert.Equal(layout.GetPageRect(10).Height + (2 * DocumentLayout.Margin), scroller.Extent.Height, 1.0);

        // Page Up and Page Down as well.
        Press(window, Key.PageUp, PhysicalKey.PageUp, RawInputModifiers.None);
        Assert.Equal(10, tab.CurrentPage);
        Assert.True(layout.IsShown(9));
    }

    [AvaloniaFact]
    public async Task SpaceAtTheEndOfASinglePageGoesToTheNextPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        tab.Zoom = 2;
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        var scroller = view.ScrollViewer;
        var bottom = scroller.Extent.Height - scroller.Viewport.Height;
        Assert.True(bottom > scroller.Viewport.Height / 2, $"the page must be taller than the viewport: extent {scroller.Extent}, viewport {scroller.Viewport}");

        // Space scrolls within the page until its end; arrow keys never leave the page.
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(scroller.Offset.Y > 0);
        for (var i = 0; i < 5 && scroller.Offset.Y < bottom - 0.5; i++)
        {
            Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        }

        Assert.Equal(bottom, scroller.Offset.Y, 0.5);
        Assert.Equal(1, tab.CurrentPage);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(bottom, scroller.Offset.Y, 0.5);

        // At the end Space shows the next page from its top.
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(view.Layout!.IsShown(1));
        Assert.Equal(0, scroller.Offset.Y);

        // Shift+Space at the top goes back to the end of the page before; Up at the top stays.
        Press(window, Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.Equal(2, tab.CurrentPage);
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.Shift);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(view.Layout.IsShown(0));
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 0.5);

        // The last page has no next one: Space at its end stays.
        tab.GoToPage(3);
        Settle(window);
        Press(window, Key.End, PhysicalKey.End, RawInputModifiers.None);
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Equal(3, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task WheelAtTheEdgeFlipsTheSinglePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        tab.Zoom = 2;
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        var scroller = view.ScrollViewer;
        var point = Middle(window, view);

        // The wheel scrolls to the end of the page; the step that reaches it does not flip.
        var steps = 0;
        while (scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 0.5)
        {
            Assert.True(++steps < 100, "the wheel does not reach the end of the page");
            window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
            Settle(window);
            Assert.Equal(1, tab.CurrentPage);
        }

        Assert.True(steps > 1, $"{steps} wheel steps");

        // Ctrl+wheel at the edge zooms and never flips.
        var zoom = tab.Zoom;
        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control);
        Settle(window);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(ZoomSteps.Previous(zoom), tab.Zoom);
        tab.Zoom = zoom;
        Settle(window);
        view.ScrollViewer.Offset = new Vector(0, scroller.Extent.Height);
        Settle(window);

        // One more step down shows the next page from its top.
        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
        Settle(window);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(view.Layout!.IsShown(1));
        Assert.Equal(0, scroller.Offset.Y);

        // A step up at the top shows the page before from its end.
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.None);
        Settle(window);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(view.Layout.IsShown(0));
        Assert.Equal(scroller.Extent.Height - scroller.Viewport.Height, scroller.Offset.Y, 0.5);

        // Up again scrolls within the page.
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.None);
        Settle(window);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(scroller.Offset.Y < scroller.Extent.Height - scroller.Viewport.Height - 0.5);
    }

    [AvaloniaFact]
    public async Task WheelInTheMiddleOfASinglePageOnlyScrolls()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        tab.Zoom = 2;
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        tab.GoToPage(2);
        Settle(window);
        var scroller = view.ScrollViewer;
        var middle = (scroller.Extent.Height - scroller.Viewport.Height) / 2;
        Scroll(window, view, middle);
        var point = Middle(window, view);

        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.None);
        Settle(window);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(scroller.Offset.Y > middle, $"offset {scroller.Offset.Y}");

        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.None);
        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.None);
        Settle(window);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(scroller.Offset.Y < middle && scroller.Offset.Y > 0, $"offset {scroller.Offset.Y}");
        Assert.True(view.Layout!.IsShown(1));
    }

    [AvaloniaFact]
    public async Task LayoutModeIsKeptPerTab()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        a.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);

        // A new tab starts continuous.
        var b = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        Assert.Equal(PageLayoutMode.Continuous, b.LayoutMode);
        Assert.Equal(PageLayoutMode.Continuous, view.Layout!.Mode);
        b.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        b.GoToPage(3);
        Settle(window);
        Assert.True(view.Layout.IsShown(2));

        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);
        Assert.Equal(PageLayoutMode.TwoPages, view.Layout!.Mode);
        Assert.Equal(PageLayoutMode.TwoPages, a.LayoutMode);

        // The single page comes back with the page it showed.
        viewModel.ActivateTabCommand.Execute(b);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, b.Session.Document) && view.Canvas.LastRequests.Count > 0);
        Assert.Equal(PageLayoutMode.SinglePage, view.Layout!.Mode);
        Assert.True(view.Layout.IsShown(2));
        Assert.False(view.Layout.IsShown(0));
        Assert.Equal(3, b.CurrentPage);
        Assert.Equal(view.Layout.GetPageRect(2).Height + (2 * DocumentLayout.Margin), view.ScrollViewer.Extent.Height, 1.0);
    }

    [AvaloniaFact]
    public async Task LayoutButtonShowsTheCheckmarkOnTheCurrentLayout()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        var button = Find<Button>(window, "LayoutButton");
        var icon = Find<StrokeIcon>(window, "LayoutIcon");
        var text = Find<TextBlock>(window, "LayoutText");

        // Right of the zoom, with the icon and the name of the current layout; the tooltip names it as well.
        var zoomIn = Find<Button>(window, "ZoomInButton");
        Assert.True(button.TranslatePoint(default, window)!.Value.X > zoomIn.TranslatePoint(default, window)!.Value.X);
        Assert.Same(Resource("Pw.Icon.LayoutRows"), icon.Data);
        Assert.Equal("Continuous", text.Text);
        Assert.True(text.IsEffectivelyVisible);
        Assert.Contains("Continuous", ToolTip.GetTip(button) as string, StringComparison.Ordinal);
        Assert.Contains("Continuous", Avalonia.Automation.AutomationProperties.GetName(button), StringComparison.Ordinal);

        // Built when it opens: four entries, the current one with the check mark.
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.Empty(menu.Items);
        var entries = Open(window, button);
        Assert.Equal(
            [PageLayoutMode.SinglePage, PageLayoutMode.Continuous, PageLayoutMode.TwoPages, PageLayoutMode.TwoPagesWithCover],
            entries.Select(entry => (PageLayoutMode)entry.CommandParameter!));
        Assert.Equal(["Single page", "Continuous", "Two pages", "Two pages with cover"], entries.Select(entry => entry.Header as string));
        Assert.Equal([PageLayoutMode.Continuous], entries.Where(HasCheckmark).Select(entry => (PageLayoutMode)entry.CommandParameter!));

        Choose(window, menu, entries[2]);
        Assert.Equal(PageLayoutMode.TwoPages, tab.LayoutMode);
        Assert.Same(Resource("Pw.Icon.LayoutColumns"), icon.Data);
        Assert.Equal("Two pages", text.Text);
        Assert.Contains("Two pages", ToolTip.GetTip(button) as string, StringComparison.Ordinal);
        entries = Open(window, button);
        Assert.Equal([PageLayoutMode.TwoPages], entries.Where(HasCheckmark).Select(entry => (PageLayoutMode)entry.CommandParameter!));

        Choose(window, menu, entries[3]);
        Assert.Same(Resource("Pw.Icon.Book"), icon.Data);
        entries = Open(window, button);
        Choose(window, menu, entries[0]);
        Assert.Same(Resource("Pw.Icon.File"), icon.Data);
        Assert.Equal("Single page", text.Text);

        // Below 760 px only the icon stays, with the tooltip.
        window.Width = 700;
        Settle(window);
        Assert.False(text.IsEffectivelyVisible);
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Contains("Single page", ToolTip.GetTip(button) as string, StringComparison.Ordinal);

        // German texts.
        Strings.Culture = new CultureInfo("de");
        entries = Open(window, button);
        Assert.Equal(["Einzelseite", "Fortlaufend", "Doppelseitig", "Doppelseitig mit Deckblatt"], entries.Select(entry => entry.Header as string));
        menu.Hide();
        Settle(window);
    }

    [AvaloniaFact]
    public async Task OddPageCountLeavesTheLastPageAloneOnTheLeft()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);

        // Three pages: 1–2, then 3 alone left of the axis.
        var layout = view.Layout!;
        var axis = layout.ContentWidth / 2;
        Assert.Equal(2, layout.RowCount);
        Assert.True(layout.GetPageRect(2).Right <= axis, $"page 3 at {layout.GetPageRect(2)}, axis {axis}");
        Assert.True(layout.GetPageRect(2).Y > layout.GetPageRect(0).Bottom);

        // The last row shows page 3 alone: the arrow stops there.
        tab.GoToPage(3);
        Settle(window);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(3, tab.CurrentPageLast);
        Assert.False(tab.NextPageCommand.CanExecute(null));
        await WaitUntilAsync(window, () => DrawsPage(view, 2));
        Assert.True(TopTile(view, 2).Right <= (view.Canvas.Bounds.Width / 2) + 0.5, $"page 3 drawn at {TopTile(view, 2)}");
    }

    [AvaloniaFact]
    public async Task MixedPageSizesDoNotOverlapInTwoPageMode()
    {
        // Portrait and landscape pages of different sizes in the same spreads.
        var path = WriteMixedSizePdf([(300, 400), (600, 150), (200, 300), (500, 250), (250, 500)]);
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, path);
        await WaitUntilAsync(window, () => view.Layout!.KnownPageCount == 5);

        foreach (var mode in new[] { PageLayoutMode.TwoPages, PageLayoutMode.TwoPagesWithCover })
        {
            tab.SetLayoutModeCommand.Execute(mode);
            Settle(window);
            var layout = view.Layout!;
            var rects = Enumerable.Range(0, layout.PageCount).Select(layout.GetPageRect).ToList();
            for (var i = 0; i < rects.Count; i++)
            {
                // Inside the content with its margins.
                Assert.True(rects[i].X >= DocumentLayout.Margin - 0.001 && rects[i].Right <= layout.ContentWidth - DocumentLayout.Margin + 0.001, $"{mode}: page {i + 1} at {rects[i]}, content width {layout.ContentWidth}");
                for (var j = i + 1; j < rects.Count; j++)
                {
                    Assert.False(Intersects(rects[i], rects[j]), $"{mode}: page {i + 1} at {rects[i]} overlaps page {j + 1} at {rects[j]}");
                }
            }

            // Every row starts below the tallest page of the row above, with the gap between them.
            for (var row = 1; row < layout.RowCount; row++)
            {
                var (first, last) = layout.PagesOfRow(row - 1);
                var bottom = Enumerable.Range(first, last - first + 1).Max(page => rects[page].Bottom);
                Assert.Equal(bottom + DocumentLayout.PageGap, rects[layout.PagesOfRow(row).First].Y, 6);
            }

            // Drawn on the canvas: the tiles of different visible pages do not overlap either.
            var scroller = view.ScrollViewer;
            var (firstVisible, lastVisible) = layout.GetVisiblePages(scroller.Offset.Y, scroller.Offset.Y + scroller.Viewport.Height);
            Assert.True(lastVisible > firstVisible, $"{mode}: pages {firstVisible + 1} to {lastVisible + 1} visible");
            await WaitUntilAsync(window, () => DrawnPages(view).Count == lastVisible - firstVisible + 1);
            var drawn = DrawnPages(view);
            foreach (var (page, area) in drawn)
            {
                foreach (var (other, otherArea) in drawn.Where(entry => entry.Key > page))
                {
                    Assert.False(Intersects(area, otherArea), $"{mode}: page {page + 1} drawn at {area} overlaps page {other + 1} at {otherArea}");
                }
            }
        }
    }

    [AvaloniaFact]
    public async Task FitWidthAdaptsToTheLayoutMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var scroller = view.ScrollViewer;
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        var single = tab.Zoom;

        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);

        // The spread fills the width: two pages, the gap between them and the margins.
        var layout = view.Layout!;
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        Assert.Equal(layout.Zoom, tab.Zoom);
        Assert.True(tab.Zoom < single * 0.6, $"zoom {tab.Zoom} after {single}");
        Assert.Equal(scroller.Viewport.Width, layout.GetPageRect(1).Right - layout.GetPageRect(0).X + (2 * DocumentLayout.Margin), 0.5);
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width, $"extent {scroller.Extent}, viewport {scroller.Viewport}");

        // Back to continuous: one page fills the width again.
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.Continuous);
        Settle(window);
        Assert.Equal(single, tab.Zoom, 3);
        Assert.Equal(scroller.Viewport.Width, layout.GetPageRect(0).Width + (2 * DocumentLayout.Margin), 0.5);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(double width = 1024)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a test file (a name in testdata, or a full path) in a new tab and waits until the view shows it.</summary>
    private static async Task<TabViewModel> OpenTabAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(Path.IsPathRooted(name) ? name : TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        return tab;
    }

    /// <summary>
    /// Writes an uncompressed PDF with one page per size (points) to a temporary file, deleted after the test. Each
    /// page has a frame inset by 10 points, so an overlap would show in a frame capture.
    /// </summary>
    private string WriteMixedSizePdf(IReadOnlyList<(int Width, int Height)> sizes)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', sizes.Select((_, i) => $"{3 + (2 * i)} 0 R"))}] /Count {sizes.Count} >>",
        };
        foreach (var (width, height) in sizes)
        {
            var content = FormattableString.Invariant($"0.23 0.28 0.77 RG 4 w 10 10 {width - 20} {height - 20} re S");
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents {objects.Count + 2} 0 R >>"));
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(Path.GetTempPath(), $"pagewing-mixed-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);

        // Disposed last: after the windows and sessions that have the file open.
        _cleanup.Insert(0, new FileDeleter(path));
        return path;
    }

    private static bool Intersects(DipRect a, DipRect b) =>
        a.X < b.Right - 0.001 && b.X < a.Right - 0.001 && a.Y < b.Bottom - 0.001 && b.Y < a.Bottom - 0.001;

    private static bool Intersects(Rect a, Rect b) =>
        a.X < b.Right - 0.5 && b.X < a.Right - 0.5 && a.Y < b.Bottom - 0.5 && b.Y < a.Bottom - 0.5;

    private static bool DrawsPage(DocumentView view, int page) =>
        view.Canvas.LastSharpDestinations.Any(drawn => drawn.Key.Page == page);

    /// <summary>The area of the top-left sharp tile of <paramref name="page"/> in the last frame, in canvas coordinates.</summary>
    private static Rect TopTile(DocumentView view, int page)
    {
        var tiles = view.Canvas.LastSharpDestinations.Where(drawn => drawn.Key.Page == page).ToList();
        var top = tiles.Min(drawn => drawn.Key.Row);
        return tiles.Where(drawn => drawn.Key.Row == top).MinBy(drawn => drawn.Key.Column).Destination;
    }

    /// <summary>The area the sharp tiles of each drawn page cover in the last frame, in canvas coordinates.</summary>
    private static Dictionary<int, Rect> DrawnPages(DocumentView view) =>
        view.Canvas.LastSharpDestinations
            .GroupBy(drawn => drawn.Key.Page)
            .ToDictionary(group => group.Key, group => group.Select(drawn => drawn.Destination).Aggregate((a, b) => a.Union(b)));

    private static Point Middle(Window window, DocumentView view)
    {
        var canvas = view.Canvas;
        return canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;
    }

    private static object? Resource(string key) => Application.Current!.FindResource(key);

    /// <summary>Clicks the button and returns the entries of the menu that opened.</summary>
    private static List<MenuItem> Open(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        var menu = (MenuFlyout)button.Flyout!;
        Assert.True(menu.IsOpen, "the menu did not open");
        return menu.Items.OfType<MenuItem>().ToList();
    }

    private static void Choose(Window window, MenuFlyout menu, MenuItem entry)
    {
        entry.Command!.Execute(entry.CommandParameter);
        menu.Hide();
        Settle(window);
    }

    private static bool HasCheckmark(MenuItem entry) =>
        entry.Icon is StrokeIcon icon && ReferenceEquals(icon.Data, Resource("Pw.Icon.Check"));

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

    private sealed class FileDeleter(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still open somewhere: the temporary folder keeps it.
            }
        }
    }
}
