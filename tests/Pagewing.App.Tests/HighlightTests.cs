using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class HighlightTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly System.Globalization.CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public HighlightTests() => Strings.Culture = new System.Globalization.CultureInfo("en");

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
    public async Task AllHitsAreDrawnAndTheCurrentOneDiffers()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        // Nothing was searched: no search exists and nothing is marked.
        Assert.False(tab.IsSearchCreated);
        Assert.Empty(view.Canvas.LastHighlightDestinations);

        var search = await SearchAsync(window, tab, "Pagewing");
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Count > 0);
        var onPage = search.Highlights.Where(highlight => highlight.Page == 0).ToArray();
        Assert.True(onPage.Length >= 3, "text-rich.pdf has several matches on its first page");

        var drawn = view.Canvas.LastHighlightDestinations.Where(entry => entry.Highlight.Page == 0).ToArray();
        Assert.Equal(onPage.Length, drawn.Length);

        // One match is the current one, drawn last so its frame stays on top; all others are plain hits.
        Assert.Equal(1, drawn.Count(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent));
        Assert.Equal(onPage.Length - 1, drawn.Count(entry => entry.Highlight.Kind == HighlightKind.SearchHit));
        Assert.Equal(HighlightKind.SearchCurrent, drawn[^1].Highlight.Kind);
        Assert.Equal(search.Current!.Hit.Rects[0], drawn[^1].Highlight.Rect);

        // The rectangle lies at the page origin plus its page-point rectangle scaled to DIPs.
        var layout = view.Layout!;
        var page = ToCanvas(view, layout.GetPageRect(0));
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var rect = drawn[0].Highlight.Rect;
        Assert.Equal(page.X + (rect.Left * scale), drawn[0].Destination.X, 1.0);
        Assert.Equal(page.Y + (rect.Top * scale), drawn[0].Destination.Y, 1.0);
        Assert.Equal(rect.Width * scale, drawn[0].Destination.Width, 1.0);
        Assert.Equal(rect.Height * scale, drawn[0].Destination.Height, 1.0);
    }

    [AvaloniaFact]
    public async Task HitsAreDrawnBehindTheTextAndInTheSearchColours()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await SearchAsync(window, tab, "Pagewing");
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Count > 0);
        var drawn = view.Canvas.LastHighlightDestinations.Where(entry => entry.Highlight.Page == 0).ToArray();
        var hit = drawn.First(entry => entry.Highlight.Kind == HighlightKind.SearchHit).Destination;
        var current = drawn.First(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent).Destination;

        using var frame = FramePixels.Snapshot(view.Canvas);
        var yellow = TokenTests.Token("Search.Hit", ThemeVariant.Light);
        var orange = TokenTests.Token("Search.Current", ThemeVariant.Light);

        // The match is on the token colour, and the page text stays dark on it (the fill multiplies with the page).
        Assert.True(Count(frame, hit, color => Near(color, yellow)) > 50, "no search.hit colour in the hit");
        Assert.True(Count(frame, hit, color => Math.Max(color.R, Math.Max(color.G, color.B)) < 110) > 5, "the text under the hit is gone");
        Assert.True(Count(frame, current, color => Near(color, orange)) > 50, "no search.current colour in the current hit");
        Assert.True(Count(frame, current, color => Math.Max(color.R, Math.Max(color.G, color.B)) < 110) > 5, "the text under the current hit is gone");

        // The current hit has a 1.5 px frame in search.on, which the others do not have.
        var on = TokenTests.Token("Search.On", ThemeVariant.Light);
        var frameBand = new Rect(current.X - 1, current.Y - 1, current.Width + 2, 2);
        Assert.True(Count(frame, frameBand, color => Near(color, on, 40)) > current.Width / 2, "no frame on the current hit");
        var hitBand = new Rect(hit.X + 2, hit.Y - 2, hit.Width - 4, 1);
        Assert.Equal(0, Count(frame, hitBand, color => Near(color, on, 40)));
    }

    [AvaloniaFact]
    public async Task HighlightsFollowTheZoom()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await SearchAsync(window, tab, "Pagewing");
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Count > 0);
        var before = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Page == 0);

        tab.Zoom = 2;
        Settle(window);
        var after = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight == before.Highlight);

        Assert.Equal(before.Destination.Width * 2, after.Destination.Width, 1.5);
        Assert.Equal(before.Destination.Height * 2, after.Destination.Height, 1.5);

        // Still on the text: the rectangle keeps its place relative to the page.
        var page = ToCanvas(view, view.Layout!.GetPageRect(0));
        Assert.Equal(page.X + (before.Highlight.Rect.Left * DocumentLayout.DipsPerPoint * 2), after.Destination.X, 1.0);
    }

    [AvaloniaFact]
    public async Task HighlightsAreGoneAfterTheSearchEnds()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var search = await SearchAsync(window, tab, "Pagewing");
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Count > 0);

        search.EndCommand.Execute(null);
        Settle(window);
        Assert.Empty(search.Highlights);
        Assert.Empty(view.Canvas.LastHighlightDestinations);

        // A new query marks again.
        search.Query = "Handbuch";
        await WaitUntilAsync(window, () => search.State == SearchState.Done && view.Canvas.LastHighlightDestinations.Count > 0);
        Assert.Single(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Page == 0);
    }

    [AvaloniaFact]
    public async Task HighlightsBelongToTheActiveTab()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await SearchAsync(window, first, "Pagewing");
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Count > 0);

        // Another tab has no search and no marks; nothing is created for it.
        var second = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Settle(window);
        Assert.False(second.IsSearchCreated);
        Assert.Empty(view.Canvas.LastHighlightDestinations);

        // Back on the first one the marks are there again.
        viewModel.ActivateTabCommand.Execute(first);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, first.Session.Document) && view.Canvas.LastHighlightDestinations.Count > 0);
        Assert.Contains(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.SearchCurrent);

        // The search of a tab that is not shown does not draw into the view.
        viewModel.ActivateTabCommand.Execute(second);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, second.Session.Document));
        first.Search.Query = "Handbuch";
        await WaitUntilAsync(window, () => first.Search.State == SearchState.Done);
        Settle(window);
        Assert.Empty(view.Canvas.LastHighlightDestinations);
        Assert.False(second.IsSearchCreated);
    }

    [AvaloniaFact]
    public async Task NextHitOnAnotherPageScrollsThere()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        var search = await SearchAsync(window, tab, "Needle");
        Assert.Equal(10, tab.CurrentPage);
        AssertInViewport(view, search.Current!.Hit);

        search.NextCommand.Execute(null);
        Settle(window);
        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(20, tab.CurrentPage);
        Assert.Equal("20", Find<TextBox>(window, "PageField").Text);
        AssertInViewport(view, search.Current!.Hit);

        // The mark is drawn there, too.
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Page == 19 && entry.Highlight.Kind == HighlightKind.SearchCurrent));

        // The way back, and round the start of the list.
        search.PreviousCommand.Execute(null);
        Settle(window);
        Assert.Equal(10, tab.CurrentPage);
        AssertInViewport(view, search.Current!.Hit);
        search.PreviousCommand.Execute(null);
        Settle(window);
        Assert.Equal(200, tab.CurrentPage);
        AssertInViewport(view, search.Current!.Hit);
    }

    [AvaloniaFact]
    public async Task AJumpToAnotherPageFitsAgain()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        var fitted = view.Layout!.Zoom;

        // The pages of text-dense.pdf all have the same size: a zoom off the fit (as after a page of another size)
        // stands in for the row of another width. The jump to the match on page 10 fits that row again (design
        // system 7.5: the modes fit again when the view jumps to a page).
        view.Layout.Zoom = fitted / 2;
        var search = await SearchAsync(window, tab, "Needle");
        Assert.Equal(10, tab.CurrentPage);
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        Assert.Equal(fitted, view.Layout.Zoom, 3);
        Assert.Equal(fitted, tab.Zoom, 3);
        AssertInViewport(view, search.Current!.Hit);
    }

    [AvaloniaFact]
    public async Task AVisibleHitDoesNotScroll()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var search = await SearchAsync(window, tab, "Pagewing");
        Assert.True(search.Hits.Count >= 3);
        var offset = view.ScrollViewer.Offset;
        Assert.Equal(new Vector(0, 0), offset);

        // All matches of page 1 are in the viewport: stepping through them leaves the view where it is.
        var reveals = 0;
        tab.RevealRequested += (_, _) => reveals++;
        search.NextCommand.Execute(null);
        Settle(window);
        search.NextCommand.Execute(null);
        Settle(window);
        Assert.Equal(2, reveals);
        Assert.Equal(2, search.CurrentIndex);
        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.Equal(1, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task AHiddenHitIsCentredAndTheViewScrollsSidewaysOnlyAsFarAsNeeded()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");

        // Zoomed in so that the pages are wider than the viewport.
        tab.Zoom = 4;
        Settle(window);
        var search = await SearchAsync(window, tab, "Needle");
        AssertInViewport(view, search.Current!.Hit);
        Assert.True(view.ScrollViewer.Extent.Width > view.ScrollViewer.Viewport.Width);

        // Scrolled away to the top left, the next match is brought back: the middle of its rectangle in the middle of
        // the viewport vertically.
        view.ScrollViewer.Offset = new Vector(0, 0);
        Settle(window);
        search.NextCommand.Execute(null);
        Settle(window);
        var next = search.Current!.Hit;
        var rect = ContentRect(view, next.PageIndex, Union(next));
        var viewport = view.Canvas.Viewport;
        Assert.Equal(rect.Y + (rect.Height / 2), viewport.Y + (viewport.Height / 2), 1.5);
        AssertInViewport(view, next);

        // Sideways only as far as needed: moved so that the rectangle is just inside, not centred.
        var sideways = view.ScrollViewer.Offset.X;
        Assert.True(Math.Abs(sideways - Math.Max(0, rect.Right - viewport.Width)) < 1.5 || Math.Abs(sideways) < 1.5, $"offset {sideways}, rectangle {rect}, viewport {viewport.Width}");
    }

    [AvaloniaFact]
    public async Task RotatedPageHighlightsLieOnTheText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "rotated-page.pdf");
        await SearchAsync(window, tab, "Rotated");
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Count > 0);

        // The page is 100 x 200 pt after the rotation; the word runs upwards: a tall, narrow rectangle inside the page.
        var page = ToCanvas(view, view.Layout!.GetPageRect(0));
        var entry = Assert.Single(view.Canvas.LastHighlightDestinations);
        Assert.True(page.Contains(entry.Destination.TopLeft) && page.Contains(entry.Destination.BottomRight), $"{entry.Destination} is outside {page}");
        Assert.True(entry.Destination.Height > entry.Destination.Width * 2, $"{entry.Destination} does not lie along the rotated text");

        // Drawn on the text pixels: the search colour and the dark glyphs are both inside.
        using var frame = FramePixels.Snapshot(view.Canvas);
        Assert.True(Count(frame, entry.Destination, color => Near(color, TokenTests.Token("Search.Current", ThemeVariant.Light))) > 20);
        Assert.True(Count(frame, entry.Destination, color => Math.Max(color.R, Math.Max(color.G, color.B)) < 110) > 5);
    }

    [AvaloniaFact]
    public async Task SinglePageLayoutShowsThePageOfTheHit()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.SinglePage);
        Settle(window);
        Assert.True(view.Layout!.IsShown(0));

        var search = await SearchAsync(window, tab, "Needle");
        Assert.Equal(10, tab.CurrentPage);
        Assert.True(view.Layout!.IsShown(9));
        Assert.False(view.Layout.IsShown(0));
        AssertInViewport(view, search.Current!.Hit);

        search.NextCommand.Execute(null);
        Settle(window);
        Assert.Equal(20, tab.CurrentPage);
        Assert.True(view.Layout.IsShown(19));
        Assert.False(view.Layout.IsShown(9));
        AssertInViewport(view, search.Current!.Hit);
        await WaitUntilAsync(window, () => view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Page == 19));
    }

    [AvaloniaFact]
    public async Task ASelectionIsDrawnTranslucentOverThePage()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-basic.pdf");

        // A rectangle on an empty part of the page (300 x 200 pt; the text and the line are in its upper half).
        var rect = new PageRect(20, 140, 120, 190);
        view.Canvas.SetHighlights([new PageHighlight(0, rect, HighlightKind.Selection)]);
        Settle(window);
        var entry = Assert.Single(view.Canvas.LastHighlightDestinations);
        Assert.Equal(HighlightKind.Selection, entry.Highlight.Kind);

        using var frame = FramePixels.Snapshot(view.Canvas);
        var middle = entry.Destination.Center;
        var expected = Blend(TokenTests.Token("Selection.Page", ThemeVariant.Light), Colors.White);
        var actual = frame.GetColor((int)middle.X, (int)middle.Y);
        Assert.True(Near(actual, expected, 3), $"{actual} is not {expected}");

        // Replaced all at once.
        view.Canvas.SetHighlights([]);
        Settle(window);
        Assert.Empty(view.Canvas.LastHighlightDestinations);
    }

    [AvaloniaFact]
    public void SelectionOverlayKeepsPageTextReadable()
    {
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            // Pages stay white in both themes, so the overlay is the same: 30 % indigo on white against black text.
            var overlay = TokenTests.Token("Selection.Page", variant);
            Assert.Equal(Color.FromArgb(0x4D, 0x3B, 0x48, 0xC4), overlay);
            var blended = Blend(overlay, Colors.White);
            Assert.True(Wcag.Contrast(Colors.Black, blended) >= 4.5, $"{variant}: black text on the selection is {Wcag.Contrast(Colors.Black, blended):F2}:1");

            // The search colours keep black text readable, too (they are multiplied with the page).
            foreach (var name in new[] { "Search.Hit", "Search.Current" })
            {
                var contrast = Wcag.Contrast(Colors.Black, TokenTests.Token(name, variant));
                Assert.True(contrast >= 4.5, $"{variant}: black text on {name} is {contrast:F2}:1");
            }

            // The selection is not yellow (yellow is for search matches only).
            Assert.False(new HsvColor(blended).H is >= 40 and <= 70);
        }
    }

    private static Color Blend(Color overlay, Color under)
    {
        var alpha = overlay.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round((overlay.R * alpha) + (under.R * (1 - alpha))),
            (byte)Math.Round((overlay.G * alpha) + (under.G * (1 - alpha))),
            (byte)Math.Round((overlay.B * alpha) + (under.B * (1 - alpha))));
    }

    private static bool Near(Color color, Color expected, int tolerance = 12) =>
        Math.Abs(color.R - expected.R) <= tolerance && Math.Abs(color.G - expected.G) <= tolerance && Math.Abs(color.B - expected.B) <= tolerance;

    private static int Count(FramePixels frame, Rect area, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = Math.Max(0, (int)Math.Floor(area.Y)); y < Math.Min(frame.Height, (int)Math.Ceiling(area.Bottom)); y++)
        {
            for (var x = Math.Max(0, (int)Math.Floor(area.X)); x < Math.Min(frame.Width, (int)Math.Ceiling(area.Right)); x++)
            {
                if (predicate(frame.GetColor(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static PageRect Union(SearchHit hit)
    {
        var rect = hit.Rects[0];
        for (var i = 1; i < hit.Rects.Count; i++)
        {
            rect = rect.Union(hit.Rects[i]);
        }

        return rect;
    }

    /// <summary>The rectangle of a page-point rectangle in content coordinates of the layout.</summary>
    private static Rect ContentRect(DocumentView view, int page, PageRect rect)
    {
        var layout = view.Layout!;
        var pageRect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        return new Rect(pageRect.X + (rect.Left * scale), pageRect.Y + (rect.Top * scale), rect.Width * scale, rect.Height * scale);
    }

    private static void AssertInViewport(DocumentView view, SearchHit hit)
    {
        var rect = ContentRect(view, hit.PageIndex, Union(hit));
        var viewport = view.Canvas.Viewport;
        Assert.True(
            rect.X >= viewport.X - 0.5 && rect.Y >= viewport.Y - 0.5 && rect.Right <= viewport.Right + 0.5 && rect.Bottom <= viewport.Bottom + 0.5,
            $"{rect} lies outside the viewport {viewport.X},{viewport.Y} {viewport.Width}x{viewport.Height}");
    }

    private static Rect ToCanvas(DocumentView view, DipRect rect) =>
        new(rect.X - view.Canvas.Viewport.X, rect.Y - view.Canvas.Viewport.Y, rect.Width, rect.Height);

    private static async Task<SearchViewModel> SearchAsync(Window window, TabViewModel tab, string query)
    {
        var search = tab.Search;
        search.Query = query;
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        return search;
    }

    private (Window Window, MainViewModel ViewModel, DocumentView View) Show()
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a file in a new tab, waits until the view shows it and closes the panel the window opens by itself.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
