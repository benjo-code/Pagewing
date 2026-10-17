using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
public sealed class OutlinePanelTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // The entries of outline.pdf with a target that are shown at first.
    private static readonly string[] EntriesWithTarget = ["Kapitel 1", "Abschnitt 1.1", "Kapitel 2"];

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
    public async Task OutlineIsNotLoadedBeforeThePanelOpens()
    {
        var (window, viewModel) = Show(1024);
        var reads = 0;
        var opening = viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        var tab = viewModel.ActiveTab!;
        tab.OutlineReader = (document, cancellationToken) =>
        {
            reads++;
            return document.GetOutlineAsync(cancellationToken);
        };
        await opening.WaitAsync(Timeout);

        // The document is open, its first sharp page shown and "Pages" opened by the wide window: no outline read yet.
        await WaitUntilAsync(window, () => tab.HasShownSharpPage && tab.SidePanel == SidePanelKind.Pages);
        Settle(window);
        Assert.Equal(0, reads);
        Assert.Equal(OutlineState.NotLoaded, tab.OutlineState);
        Assert.Null(tab.Outline);
        Assert.Null(FindOrNull<OutlinePanel>(window, "ContentsContent"));

        // The first opening of "Contents" reads it, once.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitForTreeAsync(window, tab);
        Assert.Equal(1, reads);
        Assert.Equal(OutlineState.Loaded, tab.OutlineState);
        Assert.Equal(3, tab.Outline!.Count);

        // Closing and opening the panel again, or calling EnsureOutlineAsync again, does not read it again.
        var outline = tab.Outline;
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitForTreeAsync(window, tab);
        await tab.EnsureOutlineAsync();
        Assert.Equal(1, reads);
        Assert.Same(outline, tab.Outline);
    }

    [AvaloniaFact]
    public async Task OutlineShowsTheTreeOfTheTestFile()
    {
        Strings.Culture = new CultureInfo("en");
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var tree = await OpenContentsAsync(window, viewModel, tab);

        Assert.Equal(Strings.OutlineList, AutomationProperties.GetName(tree));
        Assert.Equal(["Kapitel 1", "Kapitel 2", "Fern"], tab.Outline!.Select(node => node.Title));
        Assert.Equal([0, 2, null], tab.Outline!.Select(node => node.PageIndex));
        var chapter1 = tab.Outline![0];
        Assert.Equal(["Abschnitt 1.1", "Abschnitt 1.2"], chapter1.Children.Select(node => node.Title));
        Assert.Equal("Über uns – 日本", Assert.Single(tab.Outline![1].Children).Title);

        // Shown: the top level and the children of the open "Kapitel 1", in document order.
        Assert.Equal(
            ["Kapitel 1", "Abschnitt 1.1", "Abschnitt 1.2", "Kapitel 2", "Fern"],
            ShownItems(tree).Select(item => ((OutlineNodeViewModel)item.DataContext!).Title));

        // Each entry: the title in type.body, the page number right-aligned in type.mono; no number without a target.
        foreach (var (title, page) in new[] { ("Kapitel 1", "1"), ("Abschnitt 1.1", "2"), ("Kapitel 2", "3") })
        {
            var item = ItemOf(window, title);
            var titleText = Find<TextBlock>(Header(item), "OutlineTitle");
            var pageText = Find<TextBlock>(Header(item), "OutlinePage");
            Assert.Equal(title, titleText.Text);
            Assert.Contains("body", titleText.Classes);
            Assert.Equal(13, titleText.FontSize);
            Assert.Equal(page, pageText.Text);
            Assert.True(pageText.IsEffectivelyVisible);
            Assert.Contains("mono", pageText.Classes);
            Assert.Equal(11.5, pageText.FontSize);

            // Right-aligned: the numbers of all levels end at the same x, near the right edge of the tree.
            var right = pageText.TranslatePoint(new Point(pageText.Bounds.Width, 0), tree)!.Value.X;
            Assert.InRange(right, tree.Bounds.Width - 24, tree.Bounds.Width);
            Assert.Equal($"{title}, {string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ThumbnailName), page)}", AutomationProperties.GetName(item));
        }

        var numbers = EntriesWithTarget
            .Select(title => Find<TextBlock>(Header(ItemOf(window, title)), "OutlinePage"))
            .Select(text => text.TranslatePoint(new Point(text.Bounds.Width, 0), tree)!.Value.X)
            .ToArray();
        Assert.All(numbers, x => Assert.Equal(numbers[0], x, 0.5));

        // The child is indented against its parent.
        var parentX = Find<TextBlock>(Header(ItemOf(window, "Kapitel 1")), "OutlineTitle").TranslatePoint(default, tree)!.Value.X;
        var childX = Find<TextBlock>(Header(ItemOf(window, "Abschnitt 1.1")), "OutlineTitle").TranslatePoint(default, tree)!.Value.X;
        Assert.True(childX > parentX, "the child is indented");
        Assert.False(Find<TextBlock>(Header(ItemOf(window, "Fern")), "OutlinePage").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ClickingAnEntryJumpsToItsPage()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        await OpenContentsAsync(window, viewModel, tab);
        Assert.Equal(1, tab.CurrentPage);

        // Beside the document the panel stays open after a jump.
        Click(window, Header(ItemOf(window, "Abschnitt 1.1")));
        Assert.Equal(2, tab.CurrentPage);
        Click(window, Header(ItemOf(window, "Kapitel 2")));
        Assert.Equal(3, tab.CurrentPage);
        Click(window, Header(ItemOf(window, "Kapitel 1")));
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(SidePanelKind.Contents, tab.SidePanel);

        // Over the document (narrow window) the panel closes after the jump.
        window.Width = 800;
        Settle(window);
        Assert.True(viewModel.IsNarrowWindow);
        await WaitForTreeAsync(window, tab);
        Click(window, Header(ItemOf(window, "Kapitel 2")));
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task EntryWithoutTargetIsNotClickable(string theme)
    {
        Strings.Culture = new CultureInfo("en");
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel) = Show(1024, variant);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        await OpenContentsAsync(window, viewModel, tab);
        tab.GoToPage(3);
        Settle(window);

        var item = ItemOf(window, "Abschnitt 1.2");
        var node = (OutlineNodeViewModel)item.DataContext!;
        Assert.Null(node.PageIndex);
        Assert.False(node.CanJump);

        // A click changes nothing.
        Click(window, Header(item));
        Assert.Equal(3, tab.CurrentPage);

        // The title is text.secondary (with a target text.primary), and there is no page number. Not by colour alone:
        // no number, no hover surface, and a hint as tooltip and in the automation name.
        var secondary = TokenTests.Token("Text.Secondary", variant);
        var primary = TokenTests.Token("Text.Primary", variant);
        Assert.Equal(secondary, SolidColor(Find<TextBlock>(Header(item), "OutlineTitle").Foreground));
        Assert.Equal(primary, SolidColor(Find<TextBlock>(Header(ItemOf(window, "Abschnitt 1.1")), "OutlineTitle").Foreground));
        Assert.False(Find<TextBlock>(Header(item), "OutlinePage").IsEffectivelyVisible);
        Assert.Equal(Strings.OutlineNoTarget, ToolTip.GetTip(Find<Control>(Header(item), "OutlineRow")));
        Assert.Null(ToolTip.GetTip(Find<Control>(Header(ItemOf(window, "Abschnitt 1.1")), "OutlineRow")));
        Assert.Equal($"Abschnitt 1.2, {Strings.OutlineNoTarget}", AutomationProperties.GetName(item));
        Assert.True(Wcag.Contrast(secondary, TokenTests.Token("Surface.Sidebar", variant)) >= 4.5, "text.secondary on surface.sidebar");

        var hover = TokenTests.Token("Surface.Hover", variant);
        var sidebar = TokenTests.Token("Surface.Sidebar", variant);
        window.MouseMove(CenterOf(window, Header(item)));
        Settle(window);
        Assert.Equal(sidebar, PixelLeftOf(window, Header(item)));
        var target = Header(ItemOf(window, "Abschnitt 1.1"));
        window.MouseMove(CenterOf(window, target));
        Settle(window);
        Assert.Equal(hover, PixelLeftOf(window, target));
    }

    [AvaloniaFact]
    public async Task EntryWithInvalidPageIsNotClickable()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        await OpenContentsAsync(window, viewModel, tab);
        tab.GoToPage(2);
        Settle(window);

        // "Fern" points at page 99 of a document with three pages.
        var item = ItemOf(window, "Fern");
        var node = (OutlineNodeViewModel)item.DataContext!;
        Assert.Null(node.PageIndex);
        Assert.False(node.CanJump);
        Assert.False(Find<TextBlock>(Header(item), "OutlinePage").IsEffectivelyVisible);
        Click(window, Header(item));
        Assert.Equal(2, tab.CurrentPage);

        // Enter on it does not jump either.
        item.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter);
        Assert.Equal(2, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task ExpandedStateFollowsTheDocument()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var tree = await OpenContentsAsync(window, viewModel, tab);

        // "Kapitel 1" is open in the file, "Kapitel 2" closed: its child is not even built.
        Assert.True(tab.Outline![0].IsExpanded);
        Assert.False(tab.Outline![1].IsExpanded);
        Assert.True(ItemOf(window, "Kapitel 1").IsExpanded);
        var chapter2 = ItemOf(window, "Kapitel 2");
        Assert.False(chapter2.IsExpanded);
        Assert.DoesNotContain(ShownItems(tree), item => ((OutlineNodeViewModel)item.DataContext!).Title == "Über uns – 日本");

        // The chevron (Tabler, 16 px) opens it without jumping; the view model follows.
        var chevron = Find<ToggleButton>(chapter2, "PART_ExpandCollapseChevron");
        var icon = Assert.Single(chevron.GetVisualDescendants().OfType<StrokeIcon>());
        Assert.Equal(new Size(16, 16), icon.Bounds.Size);
        Assert.Same(Application.Current!.FindResource("Pw.Icon.ChevronRight"), icon.Data);
        Click(window, chevron);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(chapter2.IsExpanded);
        Assert.True(tab.Outline![1].IsExpanded);
        Assert.Same(Application.Current!.FindResource("Pw.Icon.ChevronDown"), icon.Data);
        Assert.Contains(ShownItems(tree), item => ((OutlineNodeViewModel)item.DataContext!).Title == "Über uns – 日本");

        // And the other way round: the view model closes "Kapitel 1".
        tab.Outline![0].IsExpanded = false;
        Settle(window);
        Assert.False(ItemOf(window, "Kapitel 1").IsExpanded);
        Assert.DoesNotContain(ShownItems(tree), item => ((OutlineNodeViewModel)item.DataContext!).Title == "Abschnitt 1.1");

        // A leaf has no chevron.
        Assert.False(Find<ToggleButton>(ItemOf(window, "Fern"), "PART_ExpandCollapseChevron").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("en", "This document has no table of contents.")]
    [InlineData("de", "Dieses Dokument hat kein Inhaltsverzeichnis.")]
    public async Task DocumentWithoutOutlineShowsTheHint(string language, string hint)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "hello_world.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitUntilAsync(window, () => tab.OutlineState == OutlineState.Loaded && IsShown(window, "OutlineMessage"));

        Assert.Empty(tab.Outline!);
        var message = Find<TextBlock>(window, "OutlineMessage");
        Assert.Equal(hint, message.Text);
        Assert.Equal(TokenTests.Token("Text.Secondary", ThemeVariant.Light), SolidColor(message.Foreground));
        Assert.Empty(ShownItems(Find<TreeView>(window, "OutlineTree")));

        // A document with an outline shows no hint.
        var other = await OpenAsync(window, viewModel, "outline.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitForTreeAsync(window, other);
        Assert.False(Find<TextBlock>(window, "OutlineMessage").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("en", "The table of contents could not be read.")]
    [InlineData("de", "Das Inhaltsverzeichnis konnte nicht gelesen werden.")]
    public async Task UnreadableOutlineShowsTheFailure(string language, string text)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        tab.OutlineReader = (_, _) => Task.FromException<IReadOnlyList<PdfOutlineItem>>(new InvalidDataException("broken"));

        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitUntilAsync(window, () => IsShown(window, "OutlineMessage"));
        Assert.Equal(OutlineState.Failed, tab.OutlineState);
        Assert.Null(tab.Outline);
        Assert.Equal(text, Find<TextBlock>(window, "OutlineMessage").Text);

        // A synchronous throw of the reader ends the same way, never as an unobserved exception.
        var second = await OpenAsync(window, viewModel, "hello_world.pdf");
        second.OutlineReader = (_, _) => throw new InvalidOperationException("broken");
        await second.EnsureOutlineAsync();
        Assert.Equal(OutlineState.Failed, second.OutlineState);
    }

    [AvaloniaFact]
    public async Task CyclicOutlineDoesNotHangTheUi()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline-cycle.pdf");
        var tree = await OpenContentsAsync(window, viewModel, tab);

        Assert.Equal(["Eins", "Zwei", "Drei"], ShownItems(tree).Select(item => ((OutlineNodeViewModel)item.DataContext!).Title));
        Click(window, Header(ItemOf(window, "Drei")));
        Assert.Equal(3, tab.CurrentPage);
    }

    [AvaloniaTheory]
    [InlineData("en", "(untitled)")]
    [InlineData("de", "(ohne Titel)")]
    public async Task EmptyTitleShowsAPlaceholder(string language, string placeholder)
    {
        Strings.Culture = new CultureInfo(language);
        Assert.Equal(placeholder, new OutlineNodeViewModel(new PdfOutlineItem(string.Empty, 0, false, [])).Title);
        Assert.Equal(placeholder, new OutlineNodeViewModel(new PdfOutlineItem("  ", 0, false, [])).Title);
        Assert.Equal("Zeile 1 Zeile 2", new OutlineNodeViewModel(new PdfOutlineItem("Zeile 1\r\nZeile 2", 0, false, [])).Title);

        // In the panel.
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        tab.OutlineReader = (_, _) => Task.FromResult<IReadOnlyList<PdfOutlineItem>>([new PdfOutlineItem(string.Empty, 1, false, [])]);
        var tree = await OpenContentsAsync(window, viewModel, tab);
        var item = Assert.Single(ShownItems(tree));
        Assert.Equal(placeholder, Find<TextBlock>(Header(item), "OutlineTitle").Text);
        Click(window, Header(item));
        Assert.Equal(2, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task KeyboardEnterJumps()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        await OpenContentsAsync(window, viewModel, tab);

        // The arrow keys move the focus without jumping; Enter jumps to the focused entry.
        ItemOf(window, "Kapitel 1").Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Down);
        Assert.Same(ItemOf(window, "Abschnitt 1.1"), window.FocusManager!.GetFocusedElement());
        Assert.Equal(1, tab.CurrentPage);
        Press(window, Key.Enter);
        Assert.Equal(2, tab.CurrentPage);

        // Enter on an entry without a target does nothing.
        Press(window, Key.Down);
        Assert.Same(ItemOf(window, "Abschnitt 1.2"), window.FocusManager!.GetFocusedElement());
        Press(window, Key.Enter);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Same(ItemOf(window, "Abschnitt 1.2"), window.FocusManager!.GetFocusedElement());

        // Right opens a closed entry, Enter jumps, Left closes it again.
        Press(window, Key.Down);
        var chapter2 = ItemOf(window, "Kapitel 2");
        Assert.Same(chapter2, window.FocusManager!.GetFocusedElement());
        Press(window, Key.Right);
        Assert.True(chapter2.IsExpanded);
        Press(window, Key.Enter);
        Assert.Equal(3, tab.CurrentPage);
        Assert.True(chapter2.IsExpanded);
        Press(window, Key.Left);
        Assert.False(chapter2.IsExpanded);

        // The focus ring of the design system (2 px focus.ring) is on the entry.
        var ring = AdornerLayer.GetAdornerLayer(chapter2)!.Children.OfType<Border>().Single(border => border.IsVisible);
        Assert.Equal(TokenTests.Token("Focus.Ring", ThemeVariant.Light), SolidColor(ring.BorderBrush));
        Assert.Equal(new Thickness(2), ring.BorderThickness);
    }

    [AvaloniaFact]
    public async Task SwitchingTabsKeepsEachTabsOutline()
    {
        Strings.Culture = new CultureInfo("en");
        var (window, viewModel) = Show(1024);
        var first = await OpenAsync(window, viewModel, "outline.pdf");
        var firstReads = 0;
        first.OutlineReader = (document, cancellationToken) =>
        {
            firstReads++;
            return document.GetOutlineAsync(cancellationToken);
        };
        await OpenContentsAsync(window, viewModel, first);
        var outline = first.Outline!;
        outline[0].IsExpanded = false;
        Settle(window);

        // The second tab has its own outline (none).
        var second = await OpenAsync(window, viewModel, "hello_world.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitUntilAsync(window, () => second.OutlineState == OutlineState.Loaded && IsShown(window, "OutlineMessage"));
        Assert.Empty(second.Outline!);
        Assert.Same(outline, first.Outline);

        // Back in the first tab: its outline, with the state it had, without reading it again.
        viewModel.ActivateTabCommand.Execute(first);
        var tree = await WaitForTreeAsync(window, first);
        Assert.Equal(1, firstReads);
        Assert.Same(outline, first.Outline);
        Assert.Same(outline, tree.ItemsSource);
        Assert.Equal(["Kapitel 1", "Kapitel 2", "Fern"], ShownItems(tree).Select(item => ((OutlineNodeViewModel)item.DataContext!).Title));
        Assert.False(ItemOf(window, "Kapitel 1").IsExpanded);
        Assert.False(Find<TextBlock>(window, "OutlineMessage").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task OutlineOfAClosedTabIsDiscarded()
    {
        var (window, viewModel) = Show(1024);

        // A loaded outline goes with its tab.
        var loaded = await OpenAsync(window, viewModel, "outline.pdf");
        await OpenContentsAsync(window, viewModel, loaded);
        viewModel.CloseTabCommand.Execute(loaded);
        Settle(window);
        Assert.Null(loaded.Outline);
        Assert.Equal(OutlineState.NotLoaded, loaded.OutlineState);

        // A tab closed while its outline is read: the read is cancelled and its result dropped.
        var gate = new TaskCompletionSource<IReadOnlyList<PdfOutlineItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CancellationToken.None;
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        tab.OutlineReader = (_, cancellationToken) =>
        {
            token = cancellationToken;
            return gate.Task;
        };
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Settle(window);
        Assert.Equal(OutlineState.Loading, tab.OutlineState);
        var loading = tab.EnsureOutlineAsync();
        viewModel.CloseTabCommand.Execute(tab);
        Settle(window);
        Assert.True(token.IsCancellationRequested);
        gate.SetResult([new PdfOutlineItem("Spät", 0, false, [])]);
        await loading.WaitAsync(Timeout);
        Settle(window);
        Assert.Null(tab.Outline);
        Assert.Equal(OutlineState.NotLoaded, tab.OutlineState);
    }

    [AvaloniaFact]
    public async Task OutlineOfAReplacedDocumentIsDiscarded()
    {
        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        var gate = new TaskCompletionSource<IReadOnlyList<PdfOutlineItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokens = new List<CancellationToken>();
        tab.OutlineReader = (document, cancellationToken) =>
        {
            tokens.Add(cancellationToken);
            return tokens.Count == 1 ? gate.Task : document.GetOutlineAsync(cancellationToken);
        };
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Settle(window);
        Assert.Equal(OutlineState.Loading, tab.OutlineState);

        // Another file in the same tab while the first outline is read: the panel shows the outline of the new one.
        await tab.OpenAsync(TestData.Pdf("hello_world.pdf"));
        await WaitUntilAsync(window, () => tab.OutlineState == OutlineState.Loaded && IsShown(window, "OutlineMessage"));
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.Equal(2, tokens.Count);
        gate.SetResult([new PdfOutlineItem("Alt", 0, false, [])]);
        Settle(window);
        Assert.Empty(tab.Outline!);
        Assert.Equal(OutlineState.Loaded, tab.OutlineState);
    }

    [AvaloniaFact]
    public async Task LargeOutlineBuildsOnlyWhatIsShown()
    {
        // The most bookmarks the core returns: an open part with 9 999 sections, then 5 000 closed chapters with one
        // section each.
        var sections = Enumerable.Range(1, 9_999).Select(i => new PdfOutlineItem($"Abschnitt {i}", 0, false, [])).ToArray();
        var chapters = Enumerable.Range(1, 5_000)
            .Select(i => new PdfOutlineItem($"Kapitel {i}", 1, false, [new PdfOutlineItem($"Unterabschnitt {i}", 2, false, [])]));
        IReadOnlyList<PdfOutlineItem> outline = [new PdfOutlineItem("Teil", 0, true, sections), .. chapters];
        Assert.Equal(PdfDocument.MaxOutlineItems, Count(outline));

        var (window, viewModel) = Show(1024);
        var tab = await OpenAsync(window, viewModel, "outline.pdf");
        tab.OutlineReader = (_, _) => Task.FromResult(outline);
        var tree = await OpenContentsAsync(window, viewModel, tab);

        // Virtualized on every level, and the view models of children exist only for the entries that were shown.
        Assert.Equal(5_001, tab.Outline!.Count);
        Assert.InRange(tree.GetVisualDescendants().OfType<TreeViewItem>().Count(), 1, 200);
        Assert.InRange(tab.Outline!.Count(node => node.AreChildrenCreated), 1, 200);

        // Scrolling to the end shows the last chapter.
        var scroller = tree.GetVisualDescendants().OfType<ScrollViewer>().First();
        for (var i = 0; i < 20 && !ShownItems(tree).Any(item => ((OutlineNodeViewModel)item.DataContext!).Title == "Kapitel 5000"); i++)
        {
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            Settle(window);
        }

        Assert.Contains(ShownItems(tree), item => ((OutlineNodeViewModel)item.DataContext!).Title == "Kapitel 5000");
        Assert.InRange(tree.GetVisualDescendants().OfType<TreeViewItem>().Count(), 1, 200);

        static int Count(IReadOnlyList<PdfOutlineItem> items) => items.Sum(item => 1 + Count(item.Children));
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

    /// <summary>Opens "Contents" in the active tab (it must not be open) and waits for its tree.</summary>
    private static async Task<TreeView> OpenContentsAsync(Window window, MainViewModel viewModel, TabViewModel tab)
    {
        Assert.NotEqual(SidePanelKind.Contents, viewModel.ActivePanel);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        return await WaitForTreeAsync(window, tab);
    }

    /// <summary>Waits until the window shows the loaded outline of <paramref name="tab"/>, fully faded in.</summary>
    private static async Task<TreeView> WaitForTreeAsync(Window window, TabViewModel tab)
    {
        TreeView? tree = null;
        await WaitUntilAsync(window, () =>
        {
            tree = FindOrNull<TreeView>(window, "OutlineTree");
            return tab.OutlineState == OutlineState.Loaded
                && tree is { IsEffectivelyVisible: true }
                && ReferenceEquals(tree.DataContext, tab)
                && ReferenceEquals(tree.ItemsSource, tab.Outline)
                && ShownItems(tree).Count > 0
                && tree.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);
        });
        return tree!;
    }

    private static bool IsShown(Window window, string name) =>
        FindOrNull<Control>(window, name) is { IsEffectivelyVisible: true } control
        && control.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);

    /// <summary>The entries the tree shows, in order from top to bottom.</summary>
    private static List<TreeViewItem> ShownItems(TreeView tree) =>
        tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(item => item.IsEffectivelyVisible && item.DataContext is OutlineNodeViewModel && item.Bounds.Height > 0)
            .OrderBy(item => item.TranslatePoint(default, tree)!.Value.Y)
            .ToList();

    private static TreeViewItem ItemOf(Window window, string title) =>
        window.GetVisualDescendants().OfType<TreeViewItem>()
            .Single(item => item.IsEffectivelyVisible && item.DataContext is OutlineNodeViewModel node && node.Title == title);

    /// <summary>The header row of an entry (without its children).</summary>
    private static Border Header(TreeViewItem item) =>
        item.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_LayoutRoot");

    private static Color PixelLeftOf(Window window, Control header)
    {
        // A point in the right padding of the header row, right of the page number.
        var point = header.TranslatePoint(new Point(header.Bounds.Width - 3, header.Bounds.Height / 2), window)!.Value;
        using var pixels = new FramePixels(window.CaptureRenderedFrame()!);
        return pixels.GetColor((int)point.X, (int)point.Y);
    }

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
        Key.Left => PhysicalKey.ArrowLeft,
        Key.Right => PhysicalKey.ArrowRight,
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible)
        ?? root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
