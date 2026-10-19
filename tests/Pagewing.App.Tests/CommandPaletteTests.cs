using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class CommandPaletteTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public CommandPaletteTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task CtrlKOpensWithTheFocusInTheField()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await FocusAsync(window, view);

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        var palette = Find<CommandPalette>(window, "CommandPalette");
        Assert.True(palette.IsEffectivelyVisible);
        Assert.Same(viewModel.Palette, palette.DataContext);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Assert.Equal(string.Empty, Find<TextBox>(palette, "PaletteInput").Text);
        Assert.Equal(Strings.CommandPalette, AutomationProperties.GetName(palette));

        // 560 wide, centred just below the title bar, radius.overlay, surface.raised and the card shadow.
        var box = Find<Border>(palette, "PaletteBox");
        Assert.Equal(560, box.Bounds.Width);
        Assert.Equal(new CornerRadius(10), box.CornerRadius);
        Assert.Equal(TokenTests.Token("Surface.Raised", ThemeVariant.Light), Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color);
        Assert.Equal(window.FindResource(ThemeVariant.Light, "Pw.Shadow.Card"), box.BoxShadow);
        var origin = box.TranslatePoint(default, window)!.Value;
        var titleBar = Find<Control>(window, "TitleBar");
        Assert.InRange(origin.Y, titleBar.Bounds.Height, titleBar.Bounds.Height + 16);
        Assert.Equal(window.Bounds.Width / 2, origin.X + (box.Bounds.Width / 2), 1);

        // Without a query: the commands that can run, the selection on the first one.
        var commands = Assert.Single(viewModel.Palette!.Groups);
        Assert.Equal(PaletteKind.Command, commands.Kind);
        Assert.Equal(Strings.PaletteGroupCommands, commands.Title);
        Assert.Equal(0, viewModel.Palette.SelectedIndex);
    }

    [AvaloniaFact]
    public async Task TypingFiltersCommandsAndShowsTheirShortcuts()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var palette = viewModel.Palette!;
        var catalog = viewModel.Commands;

        // Every listed command that can run now, the palette's own excepted, each with its shortcut.
        var offered = Ids(palette, PaletteKind.Command);
        Assert.DoesNotContain("palette.show", offered);
        Assert.DoesNotContain("recent.clear", offered);
        Assert.DoesNotContain("link.cancel", offered);
        Assert.DoesNotContain("overlay.hide", offered);
        Assert.DoesNotContain("nav.back", offered);
        Assert.DoesNotContain("tab.next", offered);
        Assert.Contains("help.show", offered);
        Assert.Contains("view.zoomIn", offered);
        foreach (var item in palette.Items)
        {
            var command = catalog.ById(item.Key!)!;
            Assert.True(command.Listed && command.CanExecute(), $"{command.Id} is offered");
            Assert.Equal(command.Title(), item.Title);
            Assert.Equal(command.Gesture is { } gesture ? GestureText.Of(gesture) : null, item.Gesture);
        }

        window.KeyTextInput("zoom");
        Settle(window);
        Assert.Equal("zoom", palette.Query);
        Assert.Equal(["view.zoomIn", "view.zoomOut"], Ids(palette, PaletteKind.Command));
        Assert.All(palette.Items, item => Assert.Contains("zoom", item.Title, StringComparison.OrdinalIgnoreCase));

        // The view shows them with the shortcut in the mono kbd frame.
        var rows = Find<CommandPalette>(window, "CommandPalette").ItemRows;
        Assert.Equal(2, rows.Count);
        var zoomIn = catalog.ById("view.zoomIn")!;
        var texts = rows[0].GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).ToArray();
        Assert.Equal(zoomIn.Title(), texts.First(text => text.Classes.Contains("paletteTitle")).Text);
        var kbd = texts.Single(text => text.Classes.Contains("mono"));
        Assert.Equal(GestureText.Of(zoomIn.Gesture!), kbd.Text);
        Assert.Equal("Ctrl+Plus", kbd.Text);
        Assert.Equal(new Thickness(1), kbd.GetVisualAncestors().OfType<Border>().First().BorderThickness);

        // A shortcut finds its command too; a command that cannot run is not offered.
        Find<TextBox>(window, "PaletteInput").Text = "F3";
        Settle(window);
        Assert.Empty(palette.Items);
        Assert.True(Find<TextBlock>(window, "PaletteEmpty").IsEffectivelyVisible);
        Assert.Equal(Strings.PaletteEmpty, Find<TextBlock>(window, "PaletteEmpty").Text);
        tab.Search.Query = "Pagewing";
        await WaitUntilAsync(window, () => tab.Search.State == SearchState.Done);
        palette.Query = string.Empty;
        palette.Query = "F3";
        Assert.Equal(["search.next", "search.previous"], Ids(palette, PaletteKind.Command));

        // In German the titles and shortcuts are German.
        Strings.Culture = new CultureInfo("de");
        palette.Query = "vergr";
        Assert.Equal(["view.zoomIn"], Ids(palette, PaletteKind.Command));
        Assert.Equal("Strg+Plus", palette.Items[0].Gesture);
        Assert.Equal("Befehle", palette.Groups[0].Title);
    }

    [AvaloniaFact]
    public async Task ArrowsEnterAndEscWork()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await FocusAsync(window, view);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var palette = viewModel.Palette!;
        var paletteView = Find<CommandPalette>(window, "CommandPalette");

        // Page Down and Page Up move by a page of entries, within the list.
        Assert.True(palette.Items.Count > PaletteViewModel.PageRows);
        Press(window, Key.PageDown, PhysicalKey.PageDown, None);
        Assert.Equal(PaletteViewModel.PageRows, palette.SelectedIndex);
        Assert.Equal(1, tab.CurrentPage);
        Press(window, Key.PageUp, PhysicalKey.PageUp, None);
        Assert.Equal(0, palette.SelectedIndex);

        window.KeyTextInput("zoom");
        Settle(window);
        Assert.Equal(0, palette.SelectedIndex);
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        Assert.Equal("view.zoomOut", palette.SelectedItem!.Key);
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        Assert.Equal("view.zoomIn", palette.SelectedItem!.Key);
        Press(window, Key.Up, PhysicalKey.ArrowUp, None);
        Assert.Equal("view.zoomOut", palette.SelectedItem!.Key);
        Press(window, Key.Up, PhysicalKey.ArrowUp, None);
        Assert.Equal("view.zoomIn", palette.SelectedItem!.Key);

        // The caret stays where it was: the arrows belong to the list.
        Assert.Equal("zoom", Find<TextBox>(window, "PaletteInput").Text);

        // The chosen row is marked by more than its colour: an accent edge on the left that the others do not have.
        var rows = paletteView.ItemRows.Cast<Border>().ToArray();
        Assert.Contains("selected", rows[0].Classes);
        Assert.DoesNotContain("selected", rows[1].Classes);
        Assert.Equal(new Thickness(2, 0, 0, 0), rows[0].BorderThickness);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), Assert.IsAssignableFrom<ISolidColorBrush>(rows[0].BorderBrush).Color);
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(rows[1].BorderBrush).Color);

        // Enter runs the chosen entry and closes; the focus goes back to the document.
        var zoom = tab.Zoom;
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(paletteView.IsVisible);
        Assert.True(tab.Zoom > zoom);
        await WaitUntilAsync(window, () => view.IsFocused);

        // Esc closes without running anything, the focus goes back.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Assert.Equal(string.Empty, palette.Query);
        zoom = tab.Zoom;
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal(zoom, tab.Zoom);
        await WaitUntilAsync(window, () => view.IsFocused);

        // Also from the search field: the focus goes back there.
        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        var field = Focused(window);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => ReferenceEquals(Focused(window), field));
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task ANumberJumpsToThePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Equal(2, tab.PageCount);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var palette = viewModel.Palette!;

        window.KeyTextInput("2");
        Settle(window);
        var pages = palette.Groups[0];
        Assert.Equal(PaletteKind.Page, pages.Kind);
        Assert.Equal(Strings.PaletteGroupPages, pages.Title);
        var item = Assert.Single(pages.Items);
        Assert.Equal("Go to page 2", item.Title);
        Assert.Same(item, palette.SelectedItem);

        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => tab.CurrentPage == 2);

        // In German: "Gehe zu Seite 1".
        Strings.Culture = new CultureInfo("de");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        palette.Query = " 1 ";
        Assert.Equal("Gehe zu Seite 1", palette.Groups[0].Items[0].Title);
        Assert.Equal("Seiten", palette.Groups[0].Title);
    }

    [AvaloniaFact]
    public async Task ANumberBeyondTheLastPageOffersNothing()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        var palette = viewModel.Palette!;

        foreach (var query in new[] { "3", "0", "-1", "+1", "1.5", "99999999999" })
        {
            palette.Query = query;
            Assert.DoesNotContain(palette.Groups, group => group.Kind == PaletteKind.Page);
        }

        // Enter without an entry does nothing and keeps the palette.
        palette.Query = "3";
        Assert.Empty(palette.Items);
        Assert.Equal(-1, palette.SelectedIndex);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
    }

    [AvaloniaTheory]
    [InlineData("en", "Open file", "Settings")]
    [InlineData("de", "Datei öffnen", "Einstellungen")]
    public void WithoutATabOnlyFileCommandsAreOffered(string language, string openFile, string settings)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _) = Show();
        window.Focus();
        Settle(window);

        var exception = Record.Exception(() => Press(window, Key.K, PhysicalKey.K, Ctrl));
        Assert.Null(exception);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        var palette = viewModel.Palette!;
        Assert.True(Find<CommandPalette>(window, "CommandPalette").IsEffectivelyVisible);

        // Nothing that needs a document or a second tab: opening a file, the settings and the shortcuts.
        Assert.Equal(PaletteKind.Command, Assert.Single(palette.Groups).Kind);
        Assert.Equal(["file.open", "settings.show", "help.show", "app.about"], Ids(palette, PaletteKind.Command));
        Assert.Contains(palette.Items, item => item.Title == openFile);
        Assert.Contains(palette.Items, item => item.Title == settings);

        // A number is no page without a document (F1 still matches by its shortcut).
        palette.Query = "1";
        Assert.DoesNotContain(palette.Groups, group => group.Kind == PaletteKind.Page);
        Assert.Equal(["help.show"], Ids(palette, PaletteKind.Command));
    }

    [AvaloniaFact]
    public async Task OpenTabsAreListedAndActivated()
    {
        var (window, viewModel, view) = Show();
        var basic = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var rich = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Same(rich, viewModel.ActiveTab);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var palette = viewModel.Palette!;

        // Both tabs in the order of the strip, with their folder.
        var tabs = palette.Groups.Single(group => group.Kind == PaletteKind.Tab);
        Assert.Equal(Strings.PaletteGroupTabs, tabs.Title);
        Assert.Equal(["text-basic.pdf", "text-rich.pdf"], tabs.Items.Select(item => item.Title));
        Assert.Equal(Path.GetDirectoryName(basic.FullPath), tabs.Items[0].Detail);

        window.KeyTextInput("basic");
        Settle(window);
        var item = Assert.Single(palette.Items);
        Assert.Equal(PaletteKind.Tab, item.Kind);
        Assert.Equal("text-basic.pdf", item.Title);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Same(basic, viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task OutlineEntriesAreFoundAndJump()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var reads = 0;
        tab.OutlineReader = (_, _) =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<PdfOutlineItem>>(
            [
                new PdfOutlineItem("Introduction", 0, false, []),
                new PdfOutlineItem("Chapter Two", 1, false, [new PdfOutlineItem("Details of\ntwo", 1, false, [])]),
                new PdfOutlineItem("Two without a target", null, false, []),
            ]);
        };

        // Nothing reads the outline before the palette comes.
        Settle(window);
        Assert.Equal(OutlineState.NotLoaded, tab.OutlineState);
        Assert.Equal(0, reads);

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => tab.OutlineState == OutlineState.Loaded);
        var palette = viewModel.Palette!;
        Assert.Equal(1, reads);

        // Only for a query, and only entries with a target, the title in one line, the page as detail.
        Assert.DoesNotContain(palette.Groups, group => group.Kind == PaletteKind.Outline);
        window.KeyTextInput("two");
        Settle(window);
        var contents = palette.Groups.Single(group => group.Kind == PaletteKind.Outline);
        Assert.Equal(Strings.PaletteGroupContents, contents.Title);
        Assert.Equal(["Chapter Two", "Details of two"], contents.Items.Select(item => item.Title));
        Assert.Equal("Page 2", contents.Items[0].Detail);
        Assert.Same(contents.Items[0], palette.SelectedItem);

        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => tab.CurrentPage == 2);

        // Read once per document: opening the palette again does not read it again.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Settle(window);
        Assert.Equal(1, reads);
        palette.Query = "intro";
        Assert.Equal("Introduction", palette.Groups.Single(group => group.Kind == PaletteKind.Outline).Items[0].Title);
    }

    [AvaloniaFact]
    public async Task AHugeOutlineIsLimitedToFiftyRows()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");

        // 100 chapters of 99 sections each: 10 000 entries.
        var chapters = Enumerable.Range(1, 100).Select(chapter => new PdfOutlineItem(
            $"Chapter {chapter}",
            chapter % 200,
            false,
            Enumerable.Range(1, 99).Select(section => new PdfOutlineItem($"Section {chapter}.{section}", (chapter + section) % 200, false, [])).ToArray()))
            .ToArray();
        Assert.Equal(10_000, chapters.Length + chapters.Sum(chapter => chapter.Children.Count));
        tab.OutlineReader = (_, _) => Task.FromResult<IReadOnlyList<PdfOutlineItem>>(chapters);

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => tab.OutlineState == OutlineState.Loaded);
        var palette = viewModel.Palette!;

        // One query before the timing: the first one also makes the rows and warms up the JIT (Debug, headless). The bound
        // only catches an algorithmic regression (a pass over all 10 000 entries per keystroke that builds them all);
        // the row limit and AreChildrenCreated below are the real guards, and a slow CI machine must not make it flake.
        palette.Query = "s";
        foreach (var query in new[] { "section", "section 5", "c", "chapter 1" })
        {
            var watch = Stopwatch.StartNew();
            palette.Query = query;
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 1000, $"'{query}' took {watch.ElapsedMilliseconds} ms");
            Assert.True(palette.Items.Count <= PaletteViewModel.MaxRows, $"'{query}': {palette.Items.Count} entries");
            Assert.Equal(palette.Items.Count, palette.Groups.Sum(group => group.Items.Count));
            Assert.Contains(palette.Groups, group => group.Kind == PaletteKind.Outline);
        }

        // The best matches first: "chapter 1" puts "Chapter 1" before "Chapter 10".
        Assert.Equal("Chapter 1", palette.Groups.Single(group => group.Kind == PaletteKind.Outline).Items[0].Title);
        palette.Query = "section";
        Assert.Equal(PaletteViewModel.MaxRows, palette.Items.Count);
        Settle(window);
        Assert.True(Find<CommandPalette>(window, "CommandPalette").ItemRows.Count <= PaletteViewModel.MaxRows);

        // No view model of an entry below the top level was made.
        Assert.All(tab.Outline!, chapter => Assert.False(chapter.AreChildrenCreated));
    }

    [AvaloniaFact]
    public async Task ClosingTheActiveTabWhileOpenRefreshesTheList()
    {
        var (window, viewModel, view) = Show();
        var basic = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        var palette = viewModel.Palette!;
        palette.Query = "pdf";
        Assert.Equal(["text-basic.pdf", "text-rich.pdf"], palette.Groups.Single(group => group.Kind == PaletteKind.Tab).Items.Select(item => item.Title).Order(StringComparer.Ordinal));

        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);
        Assert.Same(basic, viewModel.ActiveTab);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.Equal(["text-basic.pdf"], palette.Groups.Single(group => group.Kind == PaletteKind.Tab).Items.Select(item => item.Title));
        var rows = Find<CommandPalette>(window, "CommandPalette").ItemRows;
        Assert.DoesNotContain(rows, row => AutomationProperties.GetName(row)!.StartsWith("text-rich.pdf", StringComparison.Ordinal));

        // The last tab: nothing of a document is offered any more.
        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);
        palette.Query = string.Empty;
        Assert.Equal(["file.open", "settings.show", "help.show", "app.about"], Ids(palette, PaletteKind.Command));
    }

    [AvaloniaFact]
    public async Task PaletteDoesNotBuildAnythingAtStartup()
    {
        var (window, viewModel, view) = Show();
        Assert.Null(FindOrNull<CommandPalette>(window, "CommandPalette"));
        Assert.Empty(window.GetVisualDescendants().OfType<CommandPalette>());
        Assert.Null(viewModel.Palette);

        // Also not with a document open; the title bar has only its light button (icon, label, shortcut).
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Empty(window.GetVisualDescendants().OfType<CommandPalette>());
        Assert.Null(viewModel.Palette);
        var button = Find<Button>(window, "PaletteButton");
        Assert.True(button.GetVisualDescendants().Count() <= 12, $"{button.GetVisualDescendants().Count()} visuals in the palette button");

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Single(window.GetVisualDescendants().OfType<CommandPalette>());
        Assert.NotNull(viewModel.Palette);

        // Shown again, it is the same palette.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Single(window.GetVisualDescendants().OfType<CommandPalette>());
    }

    [AvaloniaFact]
    public async Task EscapeInsideThePaletteDoesNotEndTheSearch()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var search = tab.Search;
        search.Query = "Pagewing";
        await WaitUntilAsync(window, () => search.State == SearchState.Done && search.Hits.Count == 4);
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));
        await FocusAsync(window, view);

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);

        // Only the palette closed: the selection, the search and its hits stay (the Esc of the document did not run).
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(tab.HasTextSelection);
        Assert.True(search.IsActive);
        Assert.Equal(4, search.Hits.Count);
        await WaitUntilAsync(window, () => view.IsFocused);

        // The next Esc is the document's again: it clears the selection first.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.HasTextSelection);
        Assert.True(search.IsActive);
    }

    [AvaloniaFact]
    public async Task EveryRowHasAnAutomationName()
    {
        var (window, viewModel, view) = Show();
        var folder = Path.GetDirectoryName(TestData.Pdf("text-basic.pdf"))!;
        viewModel.RecentFilesSource = () =>
        [
            new RecentFile(Path.Combine(folder, "hello_world.pdf"), DateTimeOffset.UnixEpoch, null),
            new RecentFile(Path.Combine(folder, "missing", "gone.pdf"), DateTimeOffset.UnixEpoch, null),
        ];
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        var palette = viewModel.Palette!;
        var paletteView = Find<CommandPalette>(window, "CommandPalette");

        // Recently opened first (only the list is read, a missing file is listed as it is), then tabs and commands.
        Assert.Equal([PaletteKind.Recent, PaletteKind.Tab, PaletteKind.Command], palette.Groups.Select(group => group.Kind));
        Assert.Equal(["hello_world.pdf", "gone.pdf"], palette.Groups[0].Items.Select(item => item.Title));
        Assert.Equal(
            [Strings.PaletteGroupRecent, Strings.PaletteGroupTabs, Strings.PaletteGroupCommands],
            paletteView.Headings.Select(heading => heading.Text));

        var rows = paletteView.ItemRows;
        Assert.Equal(palette.Items.Count, rows.Count);
        foreach (var row in rows)
        {
            var item = paletteView.ItemOf(row)!;
            var name = AutomationProperties.GetName(row);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.Equal(item.AutomationName, name);
            Assert.StartsWith(item.Title, name, StringComparison.Ordinal);
            if (item.Detail is { } detail)
            {
                Assert.Contains(detail, name, StringComparison.Ordinal);
            }

            if (item.Gesture is { } gesture)
            {
                Assert.EndsWith(gesture, name, StringComparison.Ordinal);
            }
        }

        Assert.Equal("Zoom in, Ctrl+Plus", AutomationProperties.GetName(rows.Single(row => paletteView.ItemOf(row)!.Key == "view.zoomIn")));
        foreach (var heading in paletteView.Headings)
        {
            Assert.Equal(heading.Text, AutomationProperties.GetName(heading.GetVisualParent()!.GetVisualParent() as Control ?? heading));
        }

        // Choosing a recent file opens it.
        palette.Query = "hello";
        Assert.Equal(PaletteKind.Recent, palette.SelectedItem!.Kind);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        await WaitUntilAsync(window, () => viewModel.ActiveTab?.FileName == "hello_world.pdf");
    }

    [AvaloniaFact]
    public async Task CtrlKInTheSearchFieldOpensThePalette()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.F, PhysicalKey.F, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
        window.KeyTextInput("abc");
        Settle(window);

        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Assert.Equal("abc", tab.Search.Query);

        // A second Ctrl+K closes it again (also from its own field), back to the search field.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "SearchField" });
    }

    [AvaloniaTheory]
    [InlineData(true, 1024)]
    [InlineData(false, 1024)]
    [InlineData(true, 640)]
    [InlineData(false, 640)]
    public async Task TheTitleBarButtonOpensThePaletteAndKeepsTheDragArea(bool windowsTitleBar, double width)
    {
        var (window, viewModel, view) = Show(width, windowsTitleBar);
        var button = Find<Button>(window, "PaletteButton");
        var dragArea = Find<Control>(window, "DragArea");
        var gesture = GestureText.Of(viewModel.Commands.ById("palette.show")!.Gesture!);

        // From the left: drag area, the button, then (Windows) the caption buttons; the button takes its own clicks.
        void AssertLayout()
        {
            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(button));
            Assert.True(dragArea.Bounds.Width >= 48, $"drag area {dragArea.Bounds.Width} px");
            var dragRight = dragArea.TranslatePoint(new Point(dragArea.Bounds.Width, 0), window)!.Value.X;
            var left = button.TranslatePoint(default, window)!.Value.X;
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
            Assert.True(dragRight <= left, $"the drag area ends at {dragRight}, the button starts at {left}");
            var end = windowsTitleBar ? Find<Control>(window, "CaptionButtonsSpace").TranslatePoint(default, window)!.Value.X : width;
            Assert.True(right <= end - 8 + 0.5, $"the button ends at {right}, the row at {end}");
            Assert.Equal(28, button.Bounds.Height);
        }

        AssertLayout();
        foreach (var name in new[] { "text-basic.pdf", "hello_world.pdf", "text-rich.pdf", "vector-shapes.pdf", "wide-page.pdf" })
        {
            await OpenAsync(window, viewModel, view, name);
        }

        AssertLayout();

        // Below 900 px only the icon, 32 wide; otherwise "Search or command" and the shortcut. The tooltip names it.
        var narrow = width < MainViewModel.NarrowWindowWidth;
        Assert.Equal(!narrow, Find<TextBlock>(window, "PaletteButtonText").IsEffectivelyVisible);
        Assert.Equal(!narrow, Find<Control>(window, "PaletteKbd").IsEffectivelyVisible);
        if (narrow)
        {
            Assert.Equal(32, button.Bounds.Width);
        }
        else
        {
            Assert.Equal(Strings.PaletteButton, Find<TextBlock>(window, "PaletteButtonText").Text);
            Assert.Equal(gesture, Find<Control>(window, "PaletteKbd").GetVisualDescendants().OfType<TextBlock>().Single().Text);
        }

        Assert.Equal($"{Strings.PaletteButton} ({gesture})", ToolTip.GetTip(button));
        Assert.Equal(gesture, AutomationProperties.GetAcceleratorKey(button));
        Assert.Equal(Strings.PaletteButton, AutomationProperties.GetName(button));

        // A click opens the palette with the focus in its field; a second click closes it and does not open it again.
        Click(window, button);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Click(window, button);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(Find<CommandPalette>(window, "CommandPalette").IsVisible);
    }

    [AvaloniaFact]
    public async Task AClickChoosesAnEntryAndAClickBesideClosesThePalette()
    {
        var (window, viewModel, view) = Show();
        var basic = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var rich = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var paletteView = Find<CommandPalette>(window, "CommandPalette");

        // A click on an entry runs it.
        var row = paletteView.ItemRows.Single(row => paletteView.ItemOf(row)!.Title == "text-basic.pdf");
        Click(window, row);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Same(basic, viewModel.ActiveTab);

        // A click on a heading or the field keeps it open with the focus in the field.
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Click(window, paletteView.Headings[0]);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.True(Focused(window) is TextBox { Name: "PaletteInput" });

        // A click beside it closes it and reaches what is there: the inactive tab activates.
        var tabView = window.GetVisualDescendants().OfType<TabView>().Single(tab => ReferenceEquals(tab.DataContext, rich));
        Click(window, tabView);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Same(rich, viewModel.ActiveTab);

        // The focus leaving it closes it too (here: Ctrl+G puts it into the page field).
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.G, PhysicalKey.G, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PageField" });
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(paletteView.IsVisible);
    }

    [AvaloniaFact]
    public async Task TheScrollBarOfTheListWorksAndKeepsThePaletteOpen()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var paletteView = Find<CommandPalette>(window, "CommandPalette");
        var scroller = Find<ScrollViewer>(paletteView, "PaletteScroller");
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, "the list does not scroll");

        // A press on the track below the thumb pages down; the palette stays open with the focus in its field.
        var bar = scroller.GetVisualDescendants().OfType<ScrollBar>().Single(bar => bar.Orientation == Orientation.Vertical);
        var point = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, bar.Bounds.Height - 24), window)!.Value;
        window.MouseMove(point);
        Settle(window);
        window.MouseDown(point, MouseButton.Left);
        Settle(window);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.True(scroller.Offset.Y > 0, "the list did not scroll");
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ThePaletteColoursMeetAA(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        double Ratio(string text, string surface) => Wcag.Contrast(TokenTests.Token(text, variant), TokenTests.Token(surface, variant));

        // The title-bar button: text.secondary on surface.hover; the chosen entry: text and detail on accent.subtle, its
        // edge against the card and the selection.
        foreach (var (text, surface, minimum) in new[]
        {
            ("Text.Secondary", "Surface.Hover", 4.5),
            ("Text.Primary", "Surface.Hover", 4.5),
            ("Text.Primary", "Accent.Subtle", 4.5),
            ("Text.Secondary", "Accent.Subtle", 4.5),
            ("Accent.Default", "Surface.Raised", 3.0),
            ("Accent.Default", "Accent.Subtle", 3.0),
            ("Border.Strong", "Surface.Raised", 3.0),
        })
        {
            var ratio = Ratio(text, surface);
            Assert.True(ratio >= minimum, $"{theme}: {text} on {surface} is {ratio:F2}:1");
        }
    }

    [AvaloniaFact]
    public async Task ThePaletteStaysOpenWhileTheWindowIsInactiveAndGetsTheKeysBack()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        window.KeyTextInput("zoom");
        Settle(window);

        // Alt+Tab: another window takes the keys. The palette stays open with what was typed.
        var other = new Window { Width = 200, Height = 200 };
        _cleanup.Add(new Closer(other));
        other.Show();
        other.Activate();
        await WaitUntilAsync(window, () => Focused(window) is null);
        Settle(window);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.True(Find<CommandPalette>(window, "CommandPalette").IsVisible);

        // Back in the window, the keys go to the palette's field again, not to nothing.
        window.Activate();
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Assert.Equal("zoom", viewModel.Palette!.Query);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    [AvaloniaFact]
    public async Task AClickOnThePaletteButtonGivesTheKeysBackToTheDocument()
    {
        var (window, viewModel, view) = Show();
        await OpenAsync(window, viewModel, view, "text-dense.pdf");
        await FocusAsync(window, view);

        // Opened with the mouse: the button took the focus on the press, but it is not where the keys go back to.
        Click(window, Find<Button>(window, "PaletteButton"));
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => view.IsFocused);

        // Space does not press the button again, and the arrows scroll the document.
        var offset = view.ScrollViewer.Offset.Y;
        Press(window, Key.Space, PhysicalKey.Space, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(view.ScrollViewer.Offset.Y > offset, "Space did not scroll the document");
        offset = view.ScrollViewer.Offset.Y;
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        Assert.True(view.ScrollViewer.Offset.Y > offset, "the arrow did not scroll the document");
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // Reached with the keyboard, the button is where the keys go back to.
        var button = Find<Button>(window, "PaletteButton");
        button.Focus(NavigationMethod.Tab);
        await WaitUntilAsync(window, () => button.IsFocused);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        await WaitUntilAsync(window, () => button.IsFocused);
    }

    [AvaloniaFact]
    public async Task ARefreshInTheBackgroundKeepsTheSelectionAndThePosition()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var outline = new TaskCompletionSource<IReadOnlyList<PdfOutlineItem>>();
        tab.OutlineReader = (_, _) => outline.Task;
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is TextBox { Name: "PaletteInput" });
        var palette = viewModel.Palette!;
        var scroller = Find<ScrollViewer>(window, "PaletteScroller");
        Assert.Equal(OutlineState.Loading, tab.OutlineState);

        // Without a query: an entry further down is chosen and the list scrolled; the outline arrives meanwhile.
        for (var i = 0; i < 6; i++)
        {
            Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        }

        var chosen = palette.SelectedItem!;
        scroller.Offset = new Vector(0, 40);
        Settle(window);
        var position = scroller.Offset;
        outline.SetResult([new PdfOutlineItem("Zoom levels", 1, false, [])]);
        await WaitUntilAsync(window, () => tab.OutlineState == OutlineState.Loaded);
        Assert.Equal(chosen.Key, palette.SelectedItem!.Key);
        Assert.Equal(chosen.Kind, palette.SelectedItem.Kind);
        Assert.Equal(position, scroller.Offset);

        // With a query: the chosen command stays chosen when the outline group comes in before it.
        palette.Query = "zoom";
        Settle(window);
        Assert.Equal(PaletteKind.Outline, palette.Groups[0].Kind);
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        var command = palette.SelectedItem!;
        Assert.Equal(PaletteKind.Command, command.Kind);
        tab.HasShownSharpPage = false;
        tab.HasShownSharpPage = true;
        Settle(window);
        Assert.Equal(command.Key, palette.SelectedItem!.Key);
        Assert.Equal(PaletteKind.Command, palette.SelectedItem.Kind);

        // A new query starts at the first entry and the top of the list.
        palette.Query = "zoo";
        Settle(window);
        Assert.Equal(0, palette.SelectedIndex);
        Assert.Equal(default, scroller.Offset);
    }

    [AvaloniaFact]
    public async Task ThePaletteKeepsNoOutlineOfAClosedTabOrAfterItCloses()
    {
        var (window, viewModel, view) = Show();
        var basic = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var basicOutline = new TaskCompletionSource<IReadOnlyList<PdfOutlineItem>>();
        basic.OutlineReader = (_, _) => basicOutline.Task;
        var rich = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        rich.OutlineReader = (_, _) => Task.FromResult<IReadOnlyList<PdfOutlineItem>>([new PdfOutlineItem("Chapter", 1, false, [])]);
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        await WaitUntilAsync(window, () => rich.OutlineState == OutlineState.Loaded);
        var palette = viewModel.Palette!;
        palette.Query = "chapter";
        Assert.Equal(1, palette.CachedOutlineEntries);

        // The tab with the outline closes while the palette is open (the next one is still reading its own): the
        // entries of the closed one go with it.
        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);
        Assert.Same(basic, viewModel.ActiveTab);
        Assert.Equal(OutlineState.Loading, basic.OutlineState);
        Assert.Equal(OverlayKind.Palette, viewModel.Overlay);
        Assert.Equal(0, palette.CachedOutlineEntries);

        // And closing the palette lets go of what it flattened.
        basicOutline.SetResult([new PdfOutlineItem("Chapter", 0, false, [])]);
        await WaitUntilAsync(window, () => basic.OutlineState == OutlineState.Loaded);
        palette.Query = "chap";
        Assert.Equal(1, palette.CachedOutlineEntries);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal(0, palette.CachedOutlineEntries);
    }

    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;

    private static string[] Ids(PaletteViewModel palette, PaletteKind kind) =>
        palette.Groups.Where(group => group.Kind == kind).SelectMany(group => group.Items).Select(item => item.Key!).ToArray();

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    private static async Task FocusAsync(Window window, DocumentView view)
    {
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    private static void Click(Window window, Control control)
    {
        Settle(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    private (Window Window, MainViewModel ViewModel, DocumentView View) Show(double width = 1024, bool windowsTitleBar = true)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = windowsTitleBar };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
