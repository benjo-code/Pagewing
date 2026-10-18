using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
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
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class SearchPanelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // The 0-based pages of text-dense.pdf with "Needle": every tenth page.
    private static readonly int[] NeedlePages = Enumerable.Range(1, 20).Select(i => (i * 10) - 1).ToArray();

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
    public async Task SearchPanelIsNotBuiltBeforeItOpens()
    {
        Strings.Culture = new CultureInfo("en");
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");

        // The document is open, its first sharp page shown and "Pages" opened by the wide window: no search exists yet.
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages);
        Settle(window);
        Assert.False(tab.IsSearchCreated);
        Assert.Null(FindOrNull<SearchPanel>(window, "SearchContent"));

        // The action bar has "Search" below "Contents": 40 px, the Tabler icon "search" in 20 px, tooltip with the shortcut.
        var contents = Find<Button>(window, "ContentsButton");
        var button = Find<Button>(window, "SearchButton");
        Assert.Equal(new Size(40, 40), button.Bounds.Size);
        var icon = Assert.Single(button.GetVisualDescendants().OfType<StrokeIcon>());
        Assert.Equal(new Size(20, 20), icon.Bounds.Size);
        Assert.Same(Resource("Pw.Icon.Search"), icon.Data);
        Assert.True(contents.TranslatePoint(default, window)!.Value.Y < button.TranslatePoint(default, window)!.Value.Y);
        Assert.Equal("Search (Ctrl+F)", ToolTip.GetTip(button));
        Assert.Equal(Strings.SearchTooltip, AutomationProperties.GetName(button));
        Assert.DoesNotContain("active", button.Classes);
        Assert.False(tab.IsSearchCreated);

        // The click builds the panel and the search of the tab.
        Click(window, button);
        await WaitUntilAsync(window, () => IsShown(window, "SearchContent"));
        Assert.True(tab.IsSearchCreated);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        Assert.True(viewModel.IsSearchPanelOpen);
        Assert.False(viewModel.IsPagesPanelOpen);
        Assert.Contains("active", button.Classes);
        Assert.Equal("Search", Find<TextBlock>(window, "PanelTitle").Text);
        Assert.Equal(Strings.SearchPlaceholder, Find<TextBox>(window, "SearchField").PlaceholderText);
        Assert.Equal(Strings.SearchMatchCase, Find<CheckBox>(window, "MatchCaseBox").Content);
        Assert.Equal(Strings.SearchWholeWord, Find<CheckBox>(window, "WholeWordBox").Content);
        Assert.Equal(Strings.SearchPrevious, ToolTip.GetTip(Find<Button>(window, "PreviousHitButton")));
        Assert.Equal(Strings.SearchNext, ToolTip.GetTip(Find<Button>(window, "NextHitButton")));
        Assert.Same(Resource("Pw.Icon.ChevronUp"), Assert.Single(Find<Button>(window, "PreviousHitButton").GetVisualDescendants().OfType<StrokeIcon>()).Data);
        Assert.Same(Resource("Pw.Icon.ChevronDown"), Assert.Single(Find<Button>(window, "NextHitButton").GetVisualDescendants().OfType<StrokeIcon>()).Data);
        Assert.Equal(SearchState.Idle, tab.Search.State);
        Assert.False(tab.Search.IsActive);

        // A second click closes it again.
        Click(window, button);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task CtrlFOpensThePanelWithTheFocusInTheField()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");

        Press(window, Key.F, RawInputModifiers.Control);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        await WaitUntilAsync(window, () => window.FocusManager!.GetFocusedElement() is TextBox { Name: "SearchField" });

        // Ctrl+F again while the panel is open: the focus goes back to the field with its content selected.
        Type(window, "Needle");
        var field = Find<TextBox>(window, "SearchField");
        Assert.Equal("Needle", tab.Search.Query);
        Find<CheckBox>(window, "WholeWordBox").Focus();
        Settle(window);
        Assert.NotSame(field, window.FocusManager!.GetFocusedElement());
        Press(window, Key.F, RawInputModifiers.Control);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        await WaitUntilAsync(window, () => ReferenceEquals(window.FocusManager!.GetFocusedElement(), field));
        Assert.Equal(0, field.SelectionStart);
        Assert.Equal(6, field.SelectionEnd);
    }

    [AvaloniaTheory]
    [InlineData("en", "20 matches on 20 pages")]
    [InlineData("de", "20 Treffer auf 20 Seiten")]
    public async Task TypingFillsTheListAndTheSummary(string language, string summary)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        var reveals = new List<(int Page, PageRect Rect)>();
        tab.RevealRequested += (page, rect) => reveals.Add((page, rect));

        Press(window, Key.F, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => window.FocusManager!.GetFocusedElement() is TextBox { Name: "SearchField" });
        var search = tab.Search;
        Type(window, "Needle");

        // While it runs (also during the 200 ms before it starts) the summary says so.
        Assert.Equal(SearchState.Searching, search.State);
        Assert.Equal(Strings.SearchRunning, search.Summary);
        Assert.True(search.IsActive);
        await WaitUntilAsync(window, () => search.State == SearchState.Done);

        Assert.Equal(summary, search.Summary);
        Assert.Equal(summary, Find<TextBlock>(window, "SearchSummary").Text);
        Assert.Equal(NeedlePages, search.Hits.Select(hit => hit.Hit.PageIndex));
        Assert.True(search.IsActive);

        // The first hit at or after the current page (1) became the current one and was revealed once.
        Assert.Equal(0, search.CurrentIndex);
        Assert.Same(search.Hits[0], search.Current);
        Assert.True(search.Current!.IsCurrent);
        Assert.Equal(1, search.Hits.Count(hit => hit.IsCurrent));
        var reveal = Assert.Single(reveals);
        Assert.Equal(9, reveal.Page);
        Assert.Equal(search.Current.Hit.Rects[0].Left, reveal.Rect.Left, 3);
        Assert.Equal(10, tab.CurrentPage);

        // Highlights: every hit, the current one marked.
        Assert.Equal(20, search.Highlights.Count);
        Assert.Equal([9], search.Highlights.Where(highlight => highlight.IsCurrent).Select(highlight => highlight.Page));

        // The list: page mark in type.mono, the snippet with the match on search.hit in search.on.
        var list = Find<ListBox>(window, "HitList");
        Assert.Same(search.Hits, list.ItemsSource);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });
        var first = list.ContainerFromIndex(0)!;
        var page = Find<TextBlock>(first, "HitPage");
        Assert.Equal("10", page.Text);
        Assert.Contains("mono", page.Classes);
        Assert.Equal(11.5, page.FontSize);
        var snippet = Find<TextBlock>(first, "HitSnippet");
        var runs = snippet.Inlines!.OfType<Run>().ToArray();
        Assert.Equal(3, runs.Length);
        Assert.Equal("Needle", runs[1].Text);
        Assert.Equal(string.Empty, runs[0].Text);
        Assert.StartsWith(" in Seite 10", runs[2].Text, StringComparison.Ordinal);
        Assert.Equal(TokenTests.Token("Search.Hit", ThemeVariant.Light), SolidColor(runs[1].Background));
        Assert.Equal(TokenTests.Token("Search.On", ThemeVariant.Light), SolidColor(runs[1].Foreground));
        var name = Format(Strings.SearchHitName, 10, search.Hits[0].Before + search.Hits[0].Match + search.Hits[0].After);
        Assert.Equal(name, AutomationProperties.GetName(first));

        // The current hit: accent.subtle with a 2 px accent.default edge on the left (not by colour alone); the others not.
        var row = Find<Border>(first, "HitRow");
        Assert.Contains("current", row.Classes);
        Assert.Equal(TokenTests.Token("Accent.Subtle", ThemeVariant.Light), SolidColor(row.Background));
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), SolidColor(row.BorderBrush));
        Assert.Equal(new Thickness(2, 0, 0, 0), row.BorderThickness);
        var other = Find<Border>(list.ContainerFromIndex(1)!, "HitRow");
        Assert.DoesNotContain("current", other.Classes);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void CurrentHitColoursMeetTheContrast(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var subtle = TokenTests.Token("Accent.Subtle", variant);
        var sidebar = TokenTests.Token("Surface.Sidebar", variant);

        // Snippet and page mark on the current hit and on the panel; the match on yellow; the edge of the current hit.
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Primary", variant), subtle) >= 4.5, "text.primary on accent.subtle");
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Secondary", variant), subtle) >= 4.5, "text.secondary on accent.subtle");
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Secondary", variant), sidebar) >= 4.5, "text.secondary on surface.sidebar");
        Assert.True(Wcag.Contrast(TokenTests.Token("Search.On", variant), TokenTests.Token("Search.Hit", variant)) >= 4.5, "search.on on search.hit");
        Assert.True(Wcag.Contrast(TokenTests.Token("Accent.Default", variant), subtle) >= 3, "accent.default on accent.subtle");
    }

    [AvaloniaFact]
    public async Task EnterAndShiftEnterStepThroughTheHitsAndWrap()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        var search = await SearchWithKeyboardAsync(window, tab, "Needle");
        var reveals = new List<int>();
        tab.RevealRequested += (page, _) => reveals.Add(page);
        Assert.Equal(0, search.CurrentIndex);

        Press(window, Key.Enter);
        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(20, tab.CurrentPage);
        Press(window, Key.Enter);
        Assert.Equal(2, search.CurrentIndex);
        Press(window, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal(1, search.CurrentIndex);
        Press(window, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal(0, search.CurrentIndex);

        // Shift+Enter on the first hit goes round to the last one, Enter on the last one to the first.
        Press(window, Key.Enter, RawInputModifiers.Shift);
        Assert.Equal(19, search.CurrentIndex);
        Assert.Equal(200, tab.CurrentPage);
        Assert.True(search.Hits[19].IsCurrent);
        Assert.False(search.Hits[0].IsCurrent);
        Press(window, Key.Enter);
        Assert.Equal(0, search.CurrentIndex);
        Assert.Equal([19, 29, 19, 9, 199, 9], reveals);

        // The field keeps the focus: Enter does not leave it.
        Assert.IsType<TextBox>(window.FocusManager!.GetFocusedElement());
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);

        // The arrow buttons do the same.
        Click(window, Find<Button>(window, "NextHitButton"));
        Assert.Equal(1, search.CurrentIndex);
        Click(window, Find<Button>(window, "PreviousHitButton"));
        Assert.Equal(0, search.CurrentIndex);
    }

    [AvaloniaFact]
    public async Task F3WorksWithThePanelClosed()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");

        // Without a search F3 does nothing and creates nothing.
        Press(window, Key.F3);
        Press(window, Key.F3, RawInputModifiers.Shift);
        Assert.False(tab.IsSearchCreated);

        var search = await SearchWithKeyboardAsync(window, tab, "Needle");
        viewModel.CloseSidePanelCommand.Execute(null);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.True(search.IsActive);

        Press(window, Key.F3);
        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(20, tab.CurrentPage);
        Press(window, Key.F3, RawInputModifiers.Shift);
        Assert.Equal(0, search.CurrentIndex);
        Press(window, Key.F3, RawInputModifiers.Shift);
        Assert.Equal(19, search.CurrentIndex);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task EscapeInTheFieldEndsTheSearchAndRestoresThePanelShownBefore()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages);

        var search = await SearchWithKeyboardAsync(window, tab, "Needle");
        var highlightChanges = 0;
        search.HighlightsChanged += () => highlightChanges++;
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        Assert.NotEmpty(search.Highlights);

        Press(window, Key.Escape);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);

        // The field went with the panel: the keys go back to the document.
        Assert.Same(Find<DocumentView>(window, "DocumentView"), window.FocusManager!.GetFocusedElement());
        Assert.Empty(search.Hits);
        Assert.Empty(search.Highlights);
        Assert.True(highlightChanges > 0);
        Assert.False(search.IsActive);
        Assert.Equal(string.Empty, search.Query);
        Assert.Equal(SearchState.Idle, search.State);
        Assert.Null(search.Current);
        Assert.Equal(-1, search.CurrentIndex);

        // Without a panel before, Esc closes the panel.
        viewModel.CloseSidePanelCommand.Execute(null);
        Settle(window);
        await SearchWithKeyboardAsync(window, tab, "Needle");
        Press(window, Key.Escape);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.False(search.IsActive);
    }

    [AvaloniaFact]
    public async Task CtrlFCountsAsTheUserChoosingAPanel()
    {
        var (window, viewModel) = Show(1024);

        // A file that waits for its password: no sharp page before the password is in.
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.State == ViewerState.PasswordRequired);

        // Ctrl+F before the first sharp page, then Esc: the user closed the panel, so "Pages" does not open by itself.
        Press(window, Key.F, RawInputModifiers.Control);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        await WaitUntilAsync(window, () => window.FocusManager!.GetFocusedElement() is TextBox { Name: "SearchField" });
        Press(window, Key.Escape);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);

        tab.Password = "hôtel";
        tab.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        await PumpAsync(window, TimeSpan.FromMilliseconds(200));
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task SwitchingTabsDoesNotCreateTheSearchOfAnotherTab()
    {
        var (window, viewModel) = Show(1024);
        var first = await OpenAsync(window, viewModel, "text-dense.pdf");
        await OpenSearchAsync(window, viewModel, first);
        var second = await OpenAsync(window, viewModel, "outline.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        var third = await OpenAsync(window, viewModel, "text-basic.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Settle(window);

        // From the tab with "Search" to tabs with other panels and back: only the first tab has a search.
        foreach (var tab in new[] { first, second, first, third, first })
        {
            viewModel.ActivateTabCommand.Execute(tab);
            Settle(window);
        }

        Assert.Equal(SidePanelKind.Search, first.SidePanel);
        Assert.True(first.IsSearchCreated);
        Assert.False(second.IsSearchCreated);
        Assert.False(third.IsSearchCreated);
        Assert.Same(first.Search, Find<DockPanel>(window, "SearchRoot").DataContext);
    }

    [AvaloniaFact]
    public async Task AQueryTypedBeforeTheDocumentIsOpenIsSearchedOnceItIs()
    {
        var (window, viewModel) = Show(1024);
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.State == ViewerState.PasswordRequired);

        // Typed while the document waits for its password: nothing to search yet, the query stays.
        var search = await OpenSearchAsync(window, viewModel, tab);
        search.Query = "world";
        await PumpAsync(window, TimeSpan.FromMilliseconds(300));
        Assert.Equal("world", search.Query);
        Assert.Empty(search.Hits);

        // Once the document is open, it is searched.
        tab.Password = "hôtel";
        tab.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && search.State == SearchState.Done);
        Assert.Equal(2, search.Hits.Count);
        Assert.Equal("world", search.Query);

        // Another file in the tab clears the search.
        await tab.OpenAsync(TestData.Pdf("text-basic.pdf"));
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        Assert.Equal(string.Empty, search.Query);
        Assert.Empty(search.Hits);
        Assert.Equal(SearchState.Idle, search.State);
    }

    [AvaloniaFact]
    public async Task TheSearchStartsAtTheCurrentPage()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        tab.GoToPage(100);
        Settle(window);
        Assert.Equal(100, tab.CurrentPage);

        var search = tab.Search;
        var searched = new List<int>();
        search.PageSearched += searched.Add;
        search.Query = "Needle";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);

        // The pages from the current one to the end, then from the start: page 100 (index 99) first.
        Assert.Equal(99, searched[0]);
        Assert.Equal(200, searched.Count);
        Assert.Equal(Enumerable.Range(99, 101).Concat(Enumerable.Range(0, 99)), searched);
        Assert.Equal(NeedlePages, search.Hits.Select(hit => hit.Hit.PageIndex));
        Assert.Equal(99, search.Current!.Hit.PageIndex);
        Assert.Equal(9, search.CurrentIndex);
        Assert.Equal(100, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task AShortQueryEndsAtTheHitLimit()
    {
        Strings.Culture = new CultureInfo("de");
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages);
        var search = await OpenSearchAsync(window, viewModel, tab);
        var list = Find<ListBox>(window, "HitList");

        // Near the end of the document, so most matches come after the search wrapped round and go in at the front of
        // the list, before the current one (the dearest case for the list bound to the panel).
        tab.GoToPage(196);
        Settle(window);
        var jobs = new List<(int Hits, TimeSpan Duration)>();
        search.BatchApplied += (hits, duration) => jobs.Add((hits, duration));
        search.Query = "a";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Equal(DocumentSearch.MaxHits, search.Hits.Count);
        Assert.Equal(195, search.Current!.Hit.PageIndex);

        // The UI thread stays responsive while the matches come in: they arrive in small jobs, none with more than
        // SearchRun.HitsPerJob matches, so the 10 000 matches need at least 40 of them. The durations are informational
        // only (wall time on a slow runner says little); the bound is generous.
        Assert.Equal(DocumentSearch.MaxHits, jobs.Sum(job => job.Hits));
        Assert.All(jobs, job => Assert.InRange(job.Hits, 0, SearchRun.HitsPerJob));
        Assert.True(jobs.Count >= DocumentSearch.MaxHits / SearchRun.HitsPerJob, $"{jobs.Count} jobs");
        var longest = jobs.Max(job => job.Duration);
        Assert.True(longest < TimeSpan.FromSeconds(1), $"the longest job applying matches took {longest.TotalMilliseconds:F1} ms ({jobs.Count} jobs)");

        // The limit with the number grouped as the language writes it.
        Assert.Equal("Mehr als 10.000 Treffer", search.Summary);
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("More than 10,000 matches", search.Summary);

        // Sorted by page and character, and the list builds only what it shows.
        var hits = search.Hits.Select(hit => (hit.Hit.PageIndex, hit.Hit.FirstChar)).ToArray();
        Assert.Equal(hits.Order(), hits);
        Assert.InRange(list.GetVisualDescendants().OfType<ListBoxItem>().Count(), 1, 200);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task ADocumentWithoutTextShowsTheHint(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "vector-shapes.pdf");
        var search = await OpenSearchAsync(window, viewModel, tab);

        search.Query = "a";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Empty(search.Hits);
        Assert.Equal(Strings.SearchNoText, search.Summary);
        var text = Find<TextBlock>(window, "SearchSummary");
        Assert.Equal(Strings.SearchNoText, text.Text);
        Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
        Assert.False(search.IsActive);

        // A document with text but without the word says "No matches".
        var other = await OpenAsync(window, viewModel, "text-dense.pdf");
        other.Search.Query = "zzzz";
        await WaitUntilAsync(window, () => other.Search.State == SearchState.Done);
        Assert.Equal(Strings.SearchNone, other.Search.Summary);
    }

    [AvaloniaFact]
    public async Task SummaryNamesOneHitAndHitsOnOnePage()
    {
        Strings.Culture = new CultureInfo("en");
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        var search = tab.Search;

        search.Query = "Seite 100";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Equal("1 match on page 100", search.Summary);

        // Match case and whole words restart the search: "seite" is on no page, "Needle in" on all twenty.
        search.MatchCase = true;
        search.Query = "seite";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Equal(Strings.SearchNone, search.Summary);
        search.MatchCase = false;
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Equal("20 matches on 20 pages", search.Summary);

        // Whole words: "Seit" is part of "Seite" only.
        search.Query = "Seit";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Equal(20, search.Hits.Count);
        search.WholeWord = true;
        Assert.Equal(SearchState.Searching, search.State);
        Assert.Empty(search.Hits);
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Empty(search.Hits);

        // "Seite 1" is the start of pages 10 and 100 to 190.
        search.WholeWord = false;
        search.Query = "Needle in Seite 1";
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        Assert.Equal("11 matches on 11 pages", search.Summary);

        // An empty query ends the search without a summary.
        search.Query = string.Empty;
        Assert.Equal(SearchState.Idle, search.State);
        Assert.Equal(string.Empty, search.Summary);
        Assert.Empty(search.Hits);
        Assert.False(search.IsActive);

        // Several hits on one page: the two "l" of "Hello, Pagewing".
        var basic = await OpenAsync(window, viewModel, "text-basic.pdf");
        basic.Search.Query = "l";
        await WaitUntilAsync(window, () => basic.Search.State == SearchState.Done);
        Assert.Equal("2 matches, all on page 1", basic.Search.Summary);
    }

    [AvaloniaFact]
    public async Task ReplacingTheDocumentOrClosingTheTabCancelsTheSearch()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        var search = tab.Search;
        var tokens = new List<CancellationToken>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Searcher = Gated(gate, tokens);

        // A search that hangs after its first page.
        search.Query = "alpha";
        await WaitUntilAsync(window, () => search.Hits.Count > 0);
        Assert.Equal(SearchState.Searching, search.State);

        // Another document in the tab: the search is cancelled and everything is cleared; a late result is dropped.
        await tab.OpenAsync(TestData.Pdf("text-basic.pdf"));
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        Assert.True(tokens[0].IsCancellationRequested);
        gate.SetResult();
        await PumpAsync(window, TimeSpan.FromMilliseconds(300));
        Assert.Empty(search.Hits);
        Assert.Equal(SearchState.Idle, search.State);
        Assert.Equal(string.Empty, search.Query);
        Assert.False(search.IsActive);
        Assert.Empty(search.Highlights);

        // Closing the tab while it searches: cancelled, nothing arrives later, nothing throws.
        var second = await OpenAsync(window, viewModel, "text-dense.pdf");
        var secondSearch = second.Search;
        var secondGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        secondSearch.Searcher = Gated(secondGate, tokens);
        secondSearch.Query = "alpha";
        await WaitUntilAsync(window, () => secondSearch.Hits.Count > 0);
        viewModel.CloseTabCommand.Execute(second);
        Settle(window);
        Assert.True(tokens[1].IsCancellationRequested);
        secondGate.SetResult();
        await PumpAsync(window, TimeSpan.FromMilliseconds(300));
        Assert.Empty(secondSearch.Hits);
        Assert.Equal(SearchState.Idle, secondSearch.State);
        secondSearch.Dispose();
    }

    [AvaloniaFact]
    public async Task TheFirstHitOfATabLeftBeforeItArrivedDoesNotMoveThatTab()
    {
        var (window, viewModel) = Show(1024);
        var first = await OpenAsync(window, viewModel, "text-dense.pdf");
        var search = first.Search;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        search.Searcher = (document, query, options, start, cancellationToken) => HeldAsync(document, query, options, start, started, gate, cancellationToken);
        var pageRequests = new List<int>();
        first.PageRequested += pageRequests.Add;

        // The search runs, but its first hit (page 10) has not arrived when the user switches to another tab.
        search.Query = "Needle";
        await WaitUntilAsync(window, () => started.Task.IsCompleted);
        var second = await OpenAsync(window, viewModel, "text-basic.pdf");
        Assert.Same(second, viewModel.ActiveTab);
        gate.SetResult();
        await WaitUntilAsync(window, () => search.State == SearchState.Done);

        // The hit is the current one, but the tab without a view did not jump: its page and saved view still agree.
        Assert.Same(search.Hits[0], search.Current);
        Assert.Equal(1, first.CurrentPage);
        Assert.Empty(pageRequests);
        Assert.Equal(0, first.SavedView!.Anchor.Page);

        // Back in the tab: the view shows where the tab was left, and the status bar says the same page; F3 goes on.
        viewModel.ActivateTabCommand.Execute(first);
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");
        Assert.Same(first, view.DataContext);
        Assert.Equal(1, first.CurrentPage);
        Assert.Equal(0, first.CurrentViewReader!()!.Anchor.Page);
        Press(window, Key.F3);
        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(20, first.CurrentPage);
    }

    [AvaloniaFact]
    public async Task ClickingAHitInANarrowWindowClosesThePanel()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        var search = await SearchWithKeyboardAsync(window, tab, "Needle");
        var list = Find<ListBox>(window, "HitList");
        await WaitUntilAsync(window, () => list.ContainerFromIndex(2) is { IsEffectivelyVisible: true });

        // Beside the document the panel stays open after a click.
        Click(window, list.ContainerFromIndex(2)!);
        Assert.Equal(2, search.CurrentIndex);
        Assert.Equal(30, tab.CurrentPage);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);

        // Over the document (narrow window) the panel closes after the click; the search stays.
        window.Width = 800;
        Settle(window);
        Assert.True(viewModel.IsNarrowWindow);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(1) is { IsEffectivelyVisible: true } && IsShown(window, "HitList"));
        Click(window, list.ContainerFromIndex(1)!);
        Assert.Equal(1, search.CurrentIndex);
        Assert.Equal(20, tab.CurrentPage);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.True(search.IsActive);
    }

    [AvaloniaFact]
    public async Task TheHitListIsDrivenByTheKeyboard()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "text-dense.pdf");
        var search = await SearchWithKeyboardAsync(window, tab, "Needle");
        var list = Find<ListBox>(window, "HitList");
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });
        list.ContainerFromIndex(0)!.Focus(NavigationMethod.Tab);
        Settle(window);

        // The keys move the cursor of the list without changing the current hit or the page.
        Press(window, Key.Down);
        Assert.Equal(1, FocusedIndex(window, list));
        Press(window, Key.End);
        Assert.Equal(19, FocusedIndex(window, list));
        Press(window, Key.Up);
        Assert.Equal(18, FocusedIndex(window, list));
        Press(window, Key.Home);
        Assert.Equal(0, FocusedIndex(window, list));
        Press(window, Key.PageDown);
        var afterPage = FocusedIndex(window, list);
        Assert.InRange(afterPage, 2, 19);
        Press(window, Key.PageUp);
        Assert.Equal(0, FocusedIndex(window, list));
        Assert.Equal(0, search.CurrentIndex);
        Assert.Equal(10, tab.CurrentPage);

        // Enter or Space chooses the hit under the cursor.
        Press(window, Key.Down);
        Press(window, Key.Down);
        Press(window, Key.Enter);
        Assert.Equal(2, search.CurrentIndex);
        Assert.Equal(30, tab.CurrentPage);
        Press(window, Key.Down);
        Press(window, Key.Space);
        Assert.Equal(3, search.CurrentIndex);
    }

    [AvaloniaFact]
    public void SearchStringsExistInBothLanguages()
    {
        string[] keys =
        [
            nameof(Strings.SearchPanelTitle), nameof(Strings.SearchTooltip), nameof(Strings.SearchPlaceholder),
            nameof(Strings.SearchMatchCase), nameof(Strings.SearchWholeWord), nameof(Strings.SearchRunning),
            nameof(Strings.SearchNone), nameof(Strings.SearchSummaryOne), nameof(Strings.SearchSummaryOnePage),
            nameof(Strings.SearchSummaryPages), nameof(Strings.SearchLimit), nameof(Strings.SearchNoText),
            nameof(Strings.SearchNext), nameof(Strings.SearchPrevious), nameof(Strings.SearchHitName),
        ];
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            foreach (var key in keys)
            {
                var value = (string)typeof(Strings).GetProperty(key)!.GetValue(null)!;
                Assert.False(string.IsNullOrWhiteSpace(value), $"{key} ({language})");
                Assert.NotEqual(key, value);
            }
        }

        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Search", Strings.SearchPanelTitle);
        Assert.Equal("Search (Ctrl+F)", Strings.SearchTooltip);
        Assert.Equal("No matches", Strings.SearchNone);
        Assert.Equal("5 matches, all on page 3", Format(Strings.SearchSummaryOnePage, 5, 3));
        Assert.Equal("12 matches on 4 pages", Format(Strings.SearchSummaryPages, 12, 4));
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Suche", Strings.SearchPanelTitle);
        Assert.Equal("Suchen (Strg+F)", Strings.SearchTooltip);
        Assert.Equal("Suche läuft …", Strings.SearchRunning);
        Assert.Equal("5 Treffer, alle auf Seite 3", Format(Strings.SearchSummaryOnePage, 5, 3));
        Assert.Equal("12 Treffer auf 4 Seiten", Format(Strings.SearchSummaryPages, 12, 4));
        Assert.Equal("1 Treffer auf Seite 7", Format(Strings.SearchSummaryOne, 7));
        Assert.Equal("Mehr als 10.000 Treffer", Format(Strings.SearchLimit, "10.000"));
    }

    /// <summary>
    /// A searcher that yields the first page of the real search, then waits for <paramref name="gate"/> (without the
    /// token) and yields that page once more, as a late result of a search that should have stopped.
    /// </summary>
    private static Func<PdfDocument, string, SearchOptions, int, CancellationToken, IAsyncEnumerable<SearchPageResult>> Gated(
        TaskCompletionSource gate, List<CancellationToken> tokens) =>
        (document, query, options, start, cancellationToken) => GatedAsync(document, query, options, start, gate, tokens, cancellationToken);

    private static async IAsyncEnumerable<SearchPageResult> GatedAsync(
        PdfDocument document,
        string query,
        SearchOptions options,
        int start,
        TaskCompletionSource gate,
        List<CancellationToken> tokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        tokens.Add(cancellationToken);
        await foreach (var result in DocumentSearch.SearchAsync(document, query, options, start, cancellationToken))
        {
            yield return result;
            await gate.Task;
            yield return result;
            yield break;
        }
    }

    /// <summary>The real search, held back until <paramref name="gate"/> opens; <paramref name="started"/> says it was asked for.</summary>
    private static async IAsyncEnumerable<SearchPageResult> HeldAsync(
        PdfDocument document,
        string query,
        SearchOptions options,
        int start,
        TaskCompletionSource started,
        TaskCompletionSource gate,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        started.TrySetResult();
        await gate.Task.WaitAsync(cancellationToken);
        await foreach (var result in DocumentSearch.SearchAsync(document, query, options, start, cancellationToken))
        {
            yield return result;
        }
    }

    private static string Format(string format, params object[] arguments) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), arguments);

    private static int FocusedIndex(Window window, ListBox list) =>
        window.FocusManager!.GetFocusedElement() is Control focused && focused.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            ? list.IndexFromContainer(item)
            : -1;

    /// <summary>Opens the panel with Ctrl+F, types the query and waits until the search is done.</summary>
    private static async Task<SearchViewModel> SearchWithKeyboardAsync(Window window, TabViewModel tab, string query)
    {
        Press(window, Key.F, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => window.FocusManager!.GetFocusedElement() is TextBox { Name: "SearchField" } && IsShown(window, "SearchField"));
        Type(window, query);
        var search = tab.Search;
        Assert.Equal(query, search.Query);
        await WaitUntilAsync(window, () => search.State == SearchState.Done);
        return search;
    }

    /// <summary>Opens the panel "Search" in the tab and waits until it is shown; returns the search of the tab.</summary>
    private static async Task<SearchViewModel> OpenSearchAsync(Window window, MainViewModel viewModel, TabViewModel tab)
    {
        viewModel.OpenSearchCommand.Execute(null);
        await WaitUntilAsync(window, () => IsShown(window, "HitList"));
        return tab.Search;
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(double width, ThemeVariant? theme = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = theme ?? ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    /// <summary>Opens a test file in a new tab and waits until the tab has shown its first sharp page; returns that tab.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage);
        return tab;
    }

    private static bool IsShown(Window window, string name) =>
        FindOrNull<Control>(window, name) is { IsEffectivelyVisible: true } control
        && control.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);

    private static object? Resource(string key) => Application.Current!.FindResource(key);

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

    private static void Type(Window window, string text)
    {
        window.KeyTextInput(text);
        Settle(window);
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKeyOf(key), null);
        window.KeyRelease(key, modifiers, PhysicalKeyOf(key), null);
        Settle(window);
    }

    private static PhysicalKey PhysicalKeyOf(Key key) => key switch
    {
        Key.Down => PhysicalKey.ArrowDown,
        Key.Up => PhysicalKey.ArrowUp,
        Key.Enter => PhysicalKey.Enter,
        Key.Space => PhysicalKey.Space,
        Key.Escape => PhysicalKey.Escape,
        Key.PageDown => PhysicalKey.PageDown,
        Key.PageUp => PhysicalKey.PageUp,
        Key.Home => PhysicalKey.Home,
        Key.End => PhysicalKey.End,
        Key.F => PhysicalKey.F,
        Key.F3 => PhysicalKey.F3,
        _ => PhysicalKey.None,
    };

    private static async Task PumpAsync(Window window, TimeSpan duration)
    {
        var end = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < end)
        {
            Settle(window);
            await Task.Delay(10);
        }

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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible)
        ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
