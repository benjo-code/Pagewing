using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class TextSelectionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly System.Globalization.CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public TextSelectionTests() => Strings.Culture = new System.Globalization.CultureInfo("en");

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
    public async Task DraggingOverTextSelectsIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        Assert.Equal("Pagewing", text.GetText(0, 8));

        // From the left half of "P" to the right half of "g": the caret before the first and after the last letter.
        var from = ScreenPoint(view, window, 0, LeftHalf(text.Chars[0].Box));
        var to = ScreenPoint(view, window, 0, RightHalf(text.Chars[7].Box));
        Drag(window, from, to);

        var selection = Assert.IsType<TextSelection>(tab.TextSelection);
        Assert.True(tab.HasTextSelection);
        Assert.Equal(new TextPosition(0, 0), selection.Start);
        Assert.Equal(new TextPosition(0, 8), selection.End);

        var marks = view.Canvas.LastHighlightDestinations.Where(entry => entry.Highlight.Kind == HighlightKind.Selection).ToArray();
        var mark = Assert.Single(marks);
        Assert.Equal(0, mark.Highlight.Page);
        Assert.Equal(text.RectsOf(0, 8)[0], mark.Highlight.Rect);
    }

    [AvaloniaFact]
    public async Task DoubleClickSelectsAWord()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        Assert.Equal("Handbuch", text.GetText(9, 17));

        var point = ScreenPoint(view, window, 0, Centre(text.Chars[12].Box));
        Click(window, point, clicks: 2);

        var selection = Assert.IsType<TextSelection>(tab.TextSelection);
        Assert.Equal(new TextPosition(0, 9), selection.Start);
        Assert.Equal(new TextPosition(0, 17), selection.End);
        Assert.Single(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);

        // A double click where there is no character selects no word: its first click clears the selection, the second
        // finds no character. Far from the word, so the clicks do not continue the double click above.
        var empty = ScreenPoint(view, window, 0, new Point(200, 380));
        Click(window, empty, clicks: 2);
        Assert.Null(tab.TextSelection);
        Assert.DoesNotContain(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);
    }

    [AvaloniaFact]
    public async Task TripleClickSelectsALine()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);

        var point = ScreenPoint(view, window, 0, Centre(text.Chars[12].Box));
        Click(window, point, clicks: 3);

        var selection = Assert.IsType<TextSelection>(tab.TextSelection);
        var (start, end) = text.LineAt(12);
        Assert.Equal(new TextPosition(0, start), selection.Start);
        Assert.Equal(new TextPosition(0, end), selection.End);
        Assert.Equal("Pagewing Handbuch", text.GetText(selection.Start.Caret, selection.End.Caret));
    }

    [AvaloniaFact]
    public async Task ClickingEmptySpaceClearsTheSelection()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);

        // An empty part of the page.
        await SelectAsync(window, view, 0, 0, 8);
        Assert.True(tab.HasTextSelection);
        Click(window, ScreenPoint(view, window, 0, new Point(200, 380)));
        Assert.Null(tab.TextSelection);
        Assert.DoesNotContain(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);

        // Next to the page, in the document area.
        await SelectAsync(window, view, 0, 0, 8);
        Assert.True(tab.HasTextSelection);
        Click(window, ScreenPoint(view, window, 0, new Point(-10, 100)));
        Assert.Null(tab.TextSelection);
        Assert.False(tab.HasTextSelection);
    }

    [AvaloniaFact]
    public async Task DraggingAcrossPagesSelectsBothPages()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");

        // Both pages in the viewport.
        tab.Zoom = 0.5;
        Settle(window);
        var first = await TextAsync(window, tab, 0);
        var second = await TextAsync(window, tab, 1);
        Assert.Equal("Seite zwei", second.GetText(0, 10));

        var from = ScreenPoint(view, window, 0, LeftHalf(first.Chars[9].Box));
        var to = ScreenPoint(view, window, 1, RightHalf(second.Chars[4].Box));
        Drag(window, from, to);

        var selection = Assert.IsType<TextSelection>(tab.TextSelection);
        Assert.Equal(new TextPosition(0, 9), selection.Start);
        Assert.Equal(new TextPosition(1, 5), selection.End);
        var marks = view.Canvas.LastHighlightDestinations.Where(entry => entry.Highlight.Kind == HighlightKind.Selection).ToArray();
        Assert.Contains(marks, entry => entry.Highlight.Page == 0);
        Assert.Contains(marks, entry => entry.Highlight.Page == 1);

        // On page 1 everything from "Handbuch" to the end, on page 2 only "Seite".
        Assert.Equal(first.RectsOf(9, first.Chars.Count).Count, marks.Count(entry => entry.Highlight.Page == 0));
        Assert.Equal(second.RectsOf(0, 5)[0], marks.Single(entry => entry.Highlight.Page == 1).Highlight.Rect);
    }

    [AvaloniaFact]
    public async Task SelectionSurvivesZoomAndTabSwitch()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, first, 0);
        var selection = await SelectAsync(window, view, 0, 0, 8);
        Assert.NotNull(selection);
        var before = view.Canvas.LastHighlightDestinations.Single(entry => entry.Highlight.Kind == HighlightKind.Selection);

        first.Zoom = 2;
        Settle(window);
        Assert.Equal(selection, first.TextSelection);
        var after = view.Canvas.LastHighlightDestinations.Single(entry => entry.Highlight.Kind == HighlightKind.Selection);
        Assert.Equal(before.Destination.Width * 2, after.Destination.Width, 1.5);

        // Another tab shows nothing selected and has no selection of its own.
        var second = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Settle(window);
        Assert.Null(second.TextSelection);
        Assert.DoesNotContain(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);
        Assert.Equal(selection, first.TextSelection);

        // Back on the first tab the selection is drawn again.
        viewModel.ActivateTabCommand.Execute(first);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, first.Session.Document)
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.Selection));
        Assert.Equal(selection, first.TextSelection);
    }

    [AvaloniaFact]
    public async Task SelectionClearsOnANewDocument()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);
        await SelectAsync(window, view, 0, 0, 8);
        Assert.True(tab.HasTextSelection);

        var old = tab.Session.Document;
        await tab.OpenAsync(TestData.Pdf("text-basic.pdf"));
        await WaitUntilAsync(window, () => view.Document is not null && !ReferenceEquals(view.Document, old));
        Assert.Null(tab.TextSelection);
        Assert.False(tab.HasTextSelection);
        Assert.DoesNotContain(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);
    }

    [AvaloniaFact]
    public async Task CursorIsIBeamOverTextAndArrowElsewhere()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);

        Move(window, ScreenPoint(view, window, 0, Centre(text.Chars[3].Box)));
        Assert.Same(view.TextCursor, view.ScrollViewer.Cursor);

        Move(window, ScreenPoint(view, window, 0, new Point(200, 380)));
        Assert.Null(view.ScrollViewer.Cursor);

        // The link annotations of the page: an address and a jump to page 2.
        var web = text.Links.First(link => link.Target is UriTarget { Url: "https://example.org/docs" });
        Move(window, ScreenPoint(view, window, 0, Centre(web.Rects[0])));
        Assert.Same(view.LinkCursor, view.ScrollViewer.Cursor);
        var jump = text.Links.First(link => link.Target is PageTarget);
        Move(window, ScreenPoint(view, window, 0, Centre(jump.Rects[0])));
        Assert.Same(view.LinkCursor, view.ScrollViewer.Cursor);

        // Next to the page.
        Move(window, ScreenPoint(view, window, 0, new Point(-10, 100)));
        Assert.Null(view.ScrollViewer.Cursor);

        // Only links that can be followed get the hand: never a script or a file.
        var area = new[] { new PageRect(0, 0, 10, 10) };
        Assert.True(DocumentView.IsClickable(new PageLink(area, new PageTarget(1, null))));
        Assert.True(DocumentView.IsClickable(new PageLink(area, new UriTarget("https://example.org"))));
        Assert.True(DocumentView.IsClickable(new PageLink(area, new UriTarget("mailto:info@example.org"))));
        Assert.False(DocumentView.IsClickable(new PageLink(area, new UriTarget("javascript:alert(1)"))));
        Assert.False(DocumentView.IsClickable(new PageLink(area, new UriTarget("file:///C:/Windows/notepad.exe"))));
    }

    [AvaloniaFact]
    public async Task ClickingAScrollBarDoesNotSelect()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);

        // Zoomed in, so the pages need the scroll bars.
        tab.Zoom = 2;
        Settle(window);
        var selection = await SelectAsync(window, view, 0, 0, 8);
        var bar = view.ScrollViewer.GetVisualDescendants().OfType<ScrollBar>()
            .First(candidate => candidate.Orientation == Avalonia.Layout.Orientation.Vertical && candidate.IsEffectivelyVisible);
        var top = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height / 3), window)!.Value;
        var bottom = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height * 2 / 3), window)!.Value;

        Click(window, top);
        Assert.Equal(selection, tab.TextSelection);
        Drag(window, top, bottom);
        Assert.Equal(selection, tab.TextSelection);
        Assert.Null(view.PressedLink);
    }

    [AvaloniaFact]
    public async Task PagesWithoutTextShowTheHint()
    {
        var (window, viewModel, view) = Show();
        view.NoTextHintDuration = TimeSpan.FromMilliseconds(300);
        var tab = await OpenAsync(window, viewModel, view, "vector-shapes.pdf");
        var text = await TextAsync(window, tab, 0);
        Assert.False(text.HasText);

        // Nothing at startup: the hint is made the first time it is needed.
        Assert.Null(FindOrNull<Border>(window, "NoTextHint"));

        Click(window, ScreenPoint(view, window, 0, new Point(150, 150)));
        var hint = FindOrNull<Border>(window, "NoTextHint") ?? throw new InvalidOperationException("No hint.");
        Assert.True(hint.IsVisible);
        Assert.False(hint.IsHitTestVisible);
        Assert.False(hint.Focusable);
        var line = Assert.IsType<TextBlock>(hint.Child);
        Assert.Equal(Strings.NoTextOnPage, line.Text);
        Assert.Equal(Avalonia.Automation.AutomationLiveSetting.Polite, Avalonia.Automation.AutomationProperties.GetLiveSetting(line));
        Assert.Null(tab.TextSelection);

        // At the bottom of the document area, in the colours of a raised surface.
        var bounds = hint.TranslatePoint(default, view)!.Value;
        Assert.True(bounds.Y > view.Bounds.Height / 2, $"the hint is at {bounds.Y} of {view.Bounds.Height}");
        Assert.Equal(TokenTests.Token("Surface.Raised", ThemeVariant.Light), ((Avalonia.Media.ISolidColorBrush)hint.Background!).Color);
        Assert.Equal(TokenTests.Token("Border.Subtle", ThemeVariant.Light), ((Avalonia.Media.ISolidColorBrush)hint.BorderBrush!).Color);

        // It goes by itself.
        await WaitUntilAsync(window, () => !hint.IsVisible);

        // Shown again, its text is set anew, so screen readers announce it again.
        var changes = 0;
        line.PropertyChanged += (_, e) => changes += e.Property == TextBlock.TextProperty ? 1 : 0;
        Click(window, ScreenPoint(view, window, 0, new Point(150, 150)));
        Assert.True(hint.IsVisible);
        Assert.True(changes > 0);
        Assert.Equal(Strings.NoTextOnPage, line.Text);
    }

    [AvaloniaFact]
    public async Task TextIsNotLoadedBeforeTheFirstSharpPage()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(TestData.Pdf("text-rich.pdf"));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        bool? atFirstTile = null;
        view.FirstSharpTileDrawn += () => atFirstTile = tab.Session.HasPageTexts;

        // Until the first sharp page nobody asks for the text, not even a pointer over the page.
        var moved = false;
        await WaitUntilAsync(window, () =>
        {
            if (!tab.HasShownSharpPage)
            {
                Assert.False(tab.Session.HasPageTexts);
                if (!moved && view.Layout is not null)
                {
                    moved = true;
                    Move(window, view.Canvas.TranslatePoint(new Point(view.Canvas.Bounds.Width / 2, 100), window)!.Value);
                }
            }

            return tab.HasShownSharpPage;
        });
        Assert.False(atFirstTile);

        // After it the visible pages are read without anyone asking.
        await WaitUntilAsync(window, () => tab.Session.HasPageTexts && tab.Session.EnsurePageTexts()!.TryGet(0, out _));
    }

    [AvaloniaFact]
    public async Task SelectionOnARotatedPageSitsOnTheText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "rotated-page.pdf");
        tab.Zoom = 2;
        Settle(window);
        var text = await TextAsync(window, tab, 0);
        Assert.Equal("Rotated", text.GetText(0, 7));

        // The word runs along the long side of the page: from before the middle of its first letter to beyond the middle
        // of its last, in its reading direction.
        var first = Centre(text.Chars[0].Box);
        var last = Centre(text.Chars[6].Box);
        Assert.True(Math.Abs(last.Y - first.Y) > Math.Abs(last.X - first.X) * 4, "the word does not run vertically");
        var from = ScreenPoint(view, window, 0, first - ((Centre(text.Chars[1].Box) - first) * 0.4));
        var to = ScreenPoint(view, window, 0, last + ((last - Centre(text.Chars[5].Box)) * 0.4));
        Drag(window, from, to);

        var selection = Assert.IsType<TextSelection>(tab.TextSelection);
        Assert.Equal(new TextPosition(0, 0), selection.Start);
        Assert.Equal(new TextPosition(0, 7), selection.End);

        var page = ToCanvas(view, view.Layout!.GetPageRect(0));
        var mark = Assert.Single(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);
        Assert.True(page.Contains(mark.Destination.TopLeft) && page.Contains(mark.Destination.BottomRight), $"{mark.Destination} is outside {page}");
        Assert.True(mark.Destination.Height > mark.Destination.Width * 2, $"{mark.Destination} does not lie along the rotated text");
    }

    [AvaloniaFact]
    public async Task EscapeClearsTheSelectionThenEndsTheSearch()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);
        var search = tab.Search;
        search.Query = "Pagewing";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        await SelectAsync(window, view, 0, 0, 8);
        view.Focus();
        Settle(window);

        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(tab.TextSelection);
        Assert.True(search.IsActive);

        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(search.IsActive);
        Assert.Empty(view.Canvas.LastHighlightDestinations);

        // Without either, Esc changes nothing.
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.Null(tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task ClosingTheTabWhileTheTextLoadsDoesNotThrow()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");

        // Far away, where no text is loaded yet, and pressed at once: the press waits for the text of its page.
        var gate = ScrollToPageHoldingTheText(view, 700);
        var point = ScreenPoint(view, window, 700, new Point(5, 50));
        window.MouseDown(point, MouseButton.Left);
        Assert.True(view.IsPressWaitingForText);
        window.MouseMove(point + new Point(30, 5), RawInputModifiers.LeftMouseButton);
        viewModel.CloseTabCommand.Execute(tab);

        // The loads end after the session is gone; nothing may throw on the UI thread.
        gate.SetResult();
        for (var i = 0; i < 30; i++)
        {
            Settle(window);
            await Task.Delay(10);
        }

        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        Assert.Null(viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task APressOnAPageWithoutReadTextSelectsOnceItArrives()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        const int page = 700;

        // Far away and pressed at once: the text of that page is not read yet, the press waits for it.
        var gate = ScrollToPageHoldingTheText(view, page);
        var start = new Point(5, 50);
        var end = new Point(195, 50);
        window.MouseDown(ScreenPoint(view, window, page, start), MouseButton.Left);
        Assert.True(view.IsPressWaitingForText);
        window.MouseMove(ScreenPoint(view, window, page, end), RawInputModifiers.LeftMouseButton);

        // Once it is there the selection begins at the press and ends at the latest pointer position.
        Assert.Null(tab.TextSelection);
        gate.SetResult();
        var text = await TextAsync(window, tab, page);
        await WaitUntilAsync(window, () => tab.TextSelection is not null);
        var selection = tab.TextSelection!.Value;
        Assert.Equal(new TextPosition(page, text.CaretAt(start.X, start.Y)), selection.Anchor);
        Assert.Equal(new TextPosition(page, text.CaretAt(end.X, end.Y)), selection.Focus);
        Assert.Equal("Page 701", text.GetText(selection.Start.Caret, selection.End.Caret));
        window.MouseUp(ScreenPoint(view, window, page, end), MouseButton.Left);
        Settle(window);
        Assert.Equal(selection, tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task AClickOnAPageWithoutReadTextSelectsNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        const int page = 700;

        var gate = ScrollToPageHoldingTheText(view, page);
        var point = ScreenPoint(view, window, page, new Point(5, 50));
        window.MouseDown(point, MouseButton.Left);
        Assert.True(view.IsPressWaitingForText);
        window.MouseUp(point, MouseButton.Left);

        // The text arrives after the release: the click stays a click, nothing is selected.
        gate.SetResult();
        await TextAsync(window, tab, page);
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        Assert.Null(tab.TextSelection);
        Assert.False(view.IsPressWaitingForText);
    }

    [AvaloniaFact]
    public async Task AWaitingPressDoesNotBeginAfterATabSwitch()
    {
        var (window, viewModel, view) = Show();
        var other = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        await TextAsync(window, tab, 0);
        var selection = await SelectAsync(window, view, 0, 0, 4);
        Assert.NotNull(selection);

        // Pressed where the text is not read yet; the tab goes and comes back (with the same text source) before the
        // text arrives.
        const int page = 700;
        var gate = ScrollToPageHoldingTheText(view, page);
        var point = ScreenPoint(view, window, page, new Point(5, 50));
        window.MouseDown(point, MouseButton.Left);
        Assert.True(view.IsPressWaitingForText);
        viewModel.ActivateTabCommand.Execute(other);
        viewModel.ActivateTabCommand.Execute(tab);
        Assert.False(view.IsPressWaitingForText);

        // The press that went with the switch never begins: the selection of the tab stays as it was.
        gate.SetResult();
        await TextAsync(window, tab, page);
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        window.MouseMove(point + new Point(40, 0), RawInputModifiers.LeftMouseButton);
        window.MouseUp(point + new Point(40, 0), MouseButton.Left);
        Settle(window);
        Assert.Equal(selection, tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task APageWhoseTextCannotBeReadIsNotReadOnEveryScroll()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "thousand-pages.pdf");

        // Every page far away is damaged.
        var reads = 0;
        view.ReadText = (_, _, _) =>
        {
            reads++;
            return Task.FromException<PageText>(new InvalidDataException("damaged"));
        };
        view.ScrollViewer.Offset = new Vector(0, view.Layout!.GetPageRect(500).Y);
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        var first = reads;
        Assert.True(first > 0);

        // Scrolling over the same pages does not try them again.
        for (var step = 1; step <= 5; step++)
        {
            view.ScrollViewer.Offset = new Vector(0, view.Layout.GetPageRect(500).Y + step);
            Settle(window);
        }

        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        Assert.Equal(first, reads);
    }

    [AvaloniaFact]
    public async Task ASelectionOverMorePagesThanTheTextCacheDoesNotReadThemAgainAndAgain()
    {
        var (window, viewModel, view) = Show(height: 1000);
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");

        // Far zoomed out in two columns: more pages visible than the text source keeps.
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        tab.Zoom = ZoomSteps.Min;
        Settle(window);
        var layout = view.Layout!;
        var offset = view.ScrollViewer.Offset.Y;
        var (first, last) = layout.GetVisiblePages(offset, offset + view.ScrollViewer.Viewport.Height);
        Assert.True(last - first + 1 > PageTextSource.MaxCachedPages, $"only pages {first} to {last} are visible");

        var source = tab.Session.EnsurePageTexts()!;
        var loads = 0;
        source.Loaded += _ => Interlocked.Increment(ref loads);
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);

        // Selected from the first to the last page: the view reads the text of a bounded number of pages and then stops.
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(layout.PageCount - 1, 0));
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        var settled = Volatile.Read(ref loads);
        Assert.True(settled <= DocumentView.MaxTextPages, $"{settled} pages were read");
        for (var i = 0; i < 30; i++)
        {
            Settle(window);
            await Task.Delay(10);
        }

        Assert.Equal(settled, Volatile.Read(ref loads));
        Assert.Equal(0, view.PendingTextRequests);
        Assert.Contains(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);
    }

    /// <summary>Selects characters through the view as Task 15's copy tests do; waits until the selection is drawn.</summary>
    private static async Task<TextSelection?> SelectAsync(Window window, DocumentView view, int page, int first, int last)
    {
        var task = view.SelectAsync(page, first, last);
        await WaitUntilAsync(window, () => task.IsCompleted);
        Settle(window);
        return await task;
    }

    /// <summary>Waits until the text of a page is there (read through the session, as the view does after the first sharp page).</summary>
    private static async Task<PageText> TextAsync(Window window, TabViewModel tab, int page)
    {
        var source = tab.Session.EnsurePageTexts() ?? throw new InvalidOperationException("No document.");
        var task = source.GetAsync(page);
        await WaitUntilAsync(window, () => task.IsCompleted);
        return await task;
    }

    /// <summary>
    /// Holds back every text read of the view until the returned gate is opened, then scrolls so that a page is at the top
    /// of the viewport: its text is certainly not read when the test presses on it.
    /// </summary>
    private static TaskCompletionSource ScrollToPageHoldingTheText(DocumentView view, int page)
    {
        var gate = new TaskCompletionSource();
        view.ReadText = async (texts, index, priority) =>
        {
            await gate.Task;
            return await texts.GetAsync(index, priority);
        };
        view.ScrollViewer.Offset = new Vector(0, view.Layout!.GetPageRect(page).Y - DocumentLayout.Margin);
        return gate;
    }

    private static Point LeftHalf(PageRect box) => new(box.Left + (box.Width / 4), (box.Top + box.Bottom) / 2);

    private static Point RightHalf(PageRect box) => new(box.Right - (box.Width / 4), (box.Top + box.Bottom) / 2);

    private static Point Centre(PageRect box) => new((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);

    /// <summary>The window point of a point on a page (PDF points from the top left of the page as displayed).</summary>
    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = ToCanvas(view, layout.GetPageRect(page));
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var canvasPoint = new Point(rect.X + (pagePoint.X * scale), rect.Y + (pagePoint.Y * scale));
        return view.Canvas.TranslatePoint(canvasPoint, window)!.Value;
    }

    private static Rect ToCanvas(DocumentView view, DipRect rect) =>
        new(rect.X - view.Canvas.Viewport.X, rect.Y - view.Canvas.Viewport.Y, rect.Width, rect.Height);

    private static void Move(Window window, Point point)
    {
        window.MouseMove(point);
        Settle(window);
    }

    private static void Click(Window window, Point point, int clicks = 1, bool settleFirst = true)
    {
        if (settleFirst)
        {
            Settle(window);
        }

        // Without settling in between, so the clicks come within the double-click time.
        for (var i = 0; i < clicks; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        Settle(window);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Settle(window);
        window.MouseMove(from + ((to - from) / 2), RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseUp(to, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
        Settle(window);
    }

    private (Window Window, MainViewModel ViewModel, DocumentView View) Show(double height = 720)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = height, RequestedThemeVariant = ThemeVariant.Light };
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
