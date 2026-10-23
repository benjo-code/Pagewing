using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The recently opened files and the position per file (<see cref="RecentFilesService"/>, the start screen, the
/// settings card and the windows that record and restore). Every test keeps its history in a temporary folder (never
/// the user's) or in memory.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class RecentFilesViewTests : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-recent-" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public RecentFilesViewTests()
    {
        Strings.Culture = new CultureInfo("en");
    }

    private string HistoryFile => Path.Combine(_directory, RecentFilesService.FileName);

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OpeningAFileAddsItToTheHistory()
    {
        var recent = RecentFilesService.Load(_directory);
        var (window, viewModel, view) = Show(recent);

        // An open file goes to the top and is written at once.
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        await OpenAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal([Full("hello_world.pdf"), Full("text-basic.pdf")], recent.Files.Items.Select(item => item.Path));
        Assert.Equal([Full("hello_world.pdf"), Full("text-basic.pdf")], StoredPaths());

        // A file that fails to open is not added; a protected one only once it is open.
        await viewModel.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        await WaitUntilAsync(window, () => viewModel.ActiveTab!.IsFailed);
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var locked = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => locked.IsPasswordPrompt);
        Settle(window);
        Assert.Equal(2, recent.Files.Items.Count);
        locked.Password = UserPassword;
        locked.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => locked.IsReady);
        Assert.Equal(Full("encrypted_hello_world_r3.pdf"), recent.Files.Items[0].Path);
        await WaitUntilAsync(window, () => locked.HasShownSharpPage && !recent.HasUnsavedChanges);
        Assert.Equal(3, StoredPaths().Count);

        // Opening it again (another tab activated, then this file) changes nothing but its place.
        viewModel.Tabs.Activate(viewModel.Tabs[0]);
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        Assert.Equal(3, recent.Files.Items.Count);
        Assert.False(recent.LastSaveFailed);

        // The palette lists the same history ("Recently opened").
        Assert.Same(recent.Files.Items, viewModel.RecentFilesSource());
    }

    [AvaloniaFact]
    public async Task ClosingATabStoresPageZoomAndLayout()
    {
        var recent = RecentFilesService.Load(_directory);
        var (window, viewModel, view) = Show(recent);
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        tab.LayoutMode = PageLayoutMode.TwoPages;
        tab.Zoom = 1.5;
        Settle(window);
        tab.GoToPage(3);
        Settle(window);
        Assert.Equal(3, tab.CurrentPage);
        var shown = view.Layout!.GetAnchor(view.ScrollViewer.Offset.Y);

        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);

        // What the view showed when the tab closed: the row of page 3 at 150 %, two pages, free zoom.
        var stored = Assert.Single(new RecentFilesStore(HistoryFile).Load().Items);
        Assert.Equal(Full("text-dense.pdf"), stored.Path);
        var remembered = Assert.IsType<RememberedView>(stored.View);
        Assert.Equal(shown, remembered.View.Anchor);
        Assert.Equal(1.5, remembered.Zoom);
        Assert.Equal(ZoomMode.Free, remembered.ZoomMode);
        Assert.Equal(PageLayoutMode.TwoPages, remembered.Layout);
        Assert.Equal(remembered, recent.Files.TryGet(Full("text-dense.pdf"))!.View);

        // A fit mode is stored as the mode, with the zoom it computed.
        var fitted = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        fitted.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        await WaitUntilAsync(window, () => fitted.Zoom != 1.0);
        viewModel.CloseActiveTabCommand.Execute(null);
        var fit = recent.Files.TryGet(Full("text-rich.pdf"))!.View!;
        Assert.Equal(ZoomMode.FitWidth, fit.ZoomMode);
        Assert.Equal(PageLayoutMode.Continuous, fit.Layout);
        Assert.Equal(fitted.Zoom, fit.Zoom);
    }

    [AvaloniaFact]
    public async Task ReopeningRestoresTheRememberedView()
    {
        // The defaults of new tabs say otherwise: what the file remembers wins.
        var settings = UserSettings.Detached();
        settings.Update(current => current with { DefaultLayout = PageLayoutMode.SinglePage, DefaultZoomMode = ZoomMode.FitPage });
        var recent = RecentFilesService.Load(_directory);
        recent.Closed(Full("text-dense.pdf"), new RememberedView(new ViewState(new PageAnchor(2, 0), 0), 1.5, ZoomMode.Free, PageLayoutMode.TwoPages));
        var (window, viewModel, view) = Show(recent, settings);

        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        Settle(window);

        // Page 3, 150 %, two pages: in the tab and in the view.
        Assert.Equal(PageLayoutMode.TwoPages, tab.LayoutMode);
        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(1.5, tab.Zoom);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(PageLayoutMode.TwoPages, view.Layout!.Mode);
        Assert.Equal(1.5, view.Layout.Zoom);
        Assert.Equal(view.Layout.GetOffset(new PageAnchor(2, 0)), view.ScrollViewer.Offset.Y, 1);

        // Round trip: scrolled on, closed and opened again, it comes back where it was left.
        tab.GoToPage(9);
        Settle(window);
        var left = view.ScrollViewer.Offset.Y;
        viewModel.CloseActiveTabCommand.Execute(null);
        tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        Settle(window);
        Assert.Equal(9, tab.CurrentPage);
        Assert.Equal(left, view.ScrollViewer.Offset.Y, 1);

        // Left at the very top (the margin above the first page: a negative fraction), it comes back at the very top.
        tab.GoToPage(1);
        Settle(window);
        view.ScrollViewer.Offset = default;
        Settle(window);
        viewModel.CloseActiveTabCommand.Execute(null);
        Assert.True(recent.Files.TryGet(Full("text-dense.pdf"))!.View!.View.Anchor.Fraction < 0);
        tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        Settle(window);
        Assert.Equal(0, view.ScrollViewer.Offset.Y, 0.5);

        // A remembered fit mode stays the mode (the zoom does not turn it into a free zoom).
        recent.Closed(Full("text-rich.pdf"), new RememberedView(new ViewState(new PageAnchor(0, 0.25), 0), 2, ZoomMode.FitWidth, PageLayoutMode.Continuous));
        var fitted = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Equal(ZoomMode.FitWidth, fitted.ZoomMode);
        Assert.Equal(PageLayoutMode.Continuous, fitted.LayoutMode);
        await WaitUntilAsync(window, () => Math.Abs(view.ScrollViewer.Viewport.Width - (view.Layout!.GetPageRect(0).Width + (2 * view.Layout.PageMargin))) < 0.5);

        // A file without a remembered view takes the defaults of the settings.
        var plain = await OpenAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal(PageLayoutMode.SinglePage, plain.LayoutMode);
        Assert.Equal(ZoomMode.FitPage, plain.ZoomMode);
    }

    [AvaloniaFact]
    public async Task ARememberedPositionOpensThePanelAfterItsFirstSharpPageOnly()
    {
        // Reopened on page 31: page 1 is not drawn, the window's one chance for the start panel is that first sharp page.
        var recent = RecentFilesService.Load(_directory);
        recent.Closed(Full("text-dense.pdf"), new RememberedView(new ViewState(new PageAnchor(30, 0), 0), 1, ZoomMode.Free, PageLayoutMode.Continuous));
        var (window, viewModel, view) = Show(recent);
        Assert.Equal(StartPanelSetting.Pages, viewModel.Settings.Current.StartPanel);
        await viewModel.OpenAsync(TestData.Pdf("text-dense.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages);
        Assert.DoesNotContain(view.Canvas.Bitmaps.Keys, key => key.Page == 0 && !key.IsPreview);
        Assert.Equal(31, tab.CurrentPage);

        // Closed again (not by the user's hand), then scrolled to page 1: its first sharp tile does not open it again.
        tab.SidePanel = SidePanelKind.None;
        Settle(window);
        tab.GoToPage(1);
        await WaitUntilAsync(window, () => view.Canvas.Bitmaps.Keys.Any(key => key.Page == 0 && !key.IsPreview));
        Settle(window);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
    }

    [AvaloniaFact]
    public async Task ALaterOpenWritesTheHistoryOnlyAfterItsFirstSharpPage()
    {
        WriteHistory(Full("hello_world.pdf"));
        var recent = RecentFilesService.Load(_directory);
        var (window, viewModel, view) = Show(recent);
        await WaitUntilAsync(window, () => recent.IsLoaded);
        Assert.Equal(0, recent.Writes);
        var changes = 0;
        recent.Changed += () => changes++;

        // At Ready the list knows the file (palette and start screen see it), the disk does not; not at the first sharp
        // page either (the write is a background job after it), then once.
        var writesAtReady = -1;
        var writesAtSharpPage = -1;
        viewModel.Tabs.CollectionChanged += (_, _) =>
        {
            foreach (var tab in viewModel.Tabs)
            {
                tab.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TabViewModel.Document) && tab.Document is not null && writesAtReady < 0)
                    {
                        writesAtReady = recent.Writes;
                        Assert.Equal(Full("text-dense.pdf"), recent.Files.Items[0].Path);
                    }
                    else if (e.PropertyName == nameof(TabViewModel.HasShownSharpPage) && tab.HasShownSharpPage && writesAtSharpPage < 0)
                    {
                        writesAtSharpPage = recent.Writes;
                    }
                };
            }
        };
        await OpenAsync(window, viewModel, view, "text-dense.pdf");
        Assert.Equal(0, writesAtReady);
        Assert.Equal(0, writesAtSharpPage);
        Assert.Equal(1, changes);
        Assert.Equal(1, recent.Writes);
        Assert.Equal([Full("text-dense.pdf"), Full("hello_world.pdf")], StoredPaths());

        // Opening the file on top again changes nothing and writes nothing.
        viewModel.CloseActiveTabCommand.Execute(null);
        var writes = recent.Writes;
        recent.Opened(Full("text-dense.pdf"));
        Assert.False(recent.HasUnsavedChanges);
        recent.SaveLater();
        Settle(window);
        Assert.Equal(writes, recent.Writes);

        // A burst of opens (three files at once, two behind the active one) is one write, after the active one's sharp page.
        changes = 0;
        await viewModel.OpenManyAsync([TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-rich.pdf"), TestData.Pdf("text-basic.pdf")]);
        var active = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => viewModel.Tabs.All(tab => tab.IsReady) && active.HasShownSharpPage);
        Settle(window);
        Assert.Equal(3, changes);
        Assert.Equal(writes + 1, recent.Writes);
        // The three in the order they became ready (they open side by side), above the older entry.
        var stored = StoredPaths();
        Assert.Equal(4, stored.Count);
        Assert.Equal(
            [Full("hello_world.pdf"), Full("text-basic.pdf"), Full("text-rich.pdf")],
            stored.Take(3).Order(StringComparer.Ordinal));
        Assert.Equal(Full("text-dense.pdf"), stored[3]);

        // A file opened since then (a tab that never showed a sharp page) is written when the window closes.
        recent.Opened(Full("thousand-pages.pdf"));
        Assert.True(recent.HasUnsavedChanges);
        Assert.DoesNotContain(Full("thousand-pages.pdf"), StoredPaths());
        window.Close();
        Assert.False(recent.HasUnsavedChanges);
        Assert.Contains(Full("thousand-pages.pdf"), StoredPaths());
    }

    [AvaloniaFact]
    public async Task ARememberedViewWithValuesOutOfRangeIsLimitedOrIgnored()
    {
        // A fraction far beyond the page is limited to its bottom (-1 to 1: a negative one is the margin above the first
        // page); a zoom outside the steps is limited by the tab.
        var tab = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(tab);
        tab.InitialView = new RememberedView(new ViewState(new PageAnchor(0, 7), 0), RememberedView.MaxZoom, ZoomMode.Free, PageLayoutMode.SinglePage);
        await tab.OpenAsync(TestData.Pdf("hello_world.pdf"));
        Assert.Equal(new PageAnchor(0, 1), tab.SavedView!.Anchor);
        Assert.Equal(PageLayoutMode.SinglePage, tab.LayoutMode);
        Assert.Equal(ZoomSteps.Max, tab.Zoom);

        // A view with unusable values is ignored: the tab opens as without one.
        var other = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(other);
        other.InitialView = new RememberedView(new ViewState(new PageAnchor(0, double.NaN), 0), 1.5, ZoomMode.Free, PageLayoutMode.TwoPages);
        await other.OpenAsync(TestData.Pdf("hello_world.pdf"));
        Assert.Null(other.SavedView);
        Assert.Equal(PageLayoutMode.Continuous, other.LayoutMode);
        Assert.Equal(ZoomMode.Free, other.ZoomMode);

        // The initial view is used once: opening another file in the tab does not take it again.
        await tab.OpenAsync(TestData.Pdf("text-basic.pdf"));
        Assert.Null(tab.SavedView);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindowStoresTheActiveTabsPosition()
    {
        var recent = RecentFilesService.Load(_directory);
        var workspace = CreateWorkspace(recent);
        var first = workspace.OpenWindow();
        var second = workspace.OpenWindow();
        var firstView = Find<DocumentView>(workspace.WindowOf(first), "DocumentView");
        var secondView = Find<DocumentView>(workspace.WindowOf(second), "DocumentView");

        // First window: two tabs; the dense one is left on page 5 (saved by the switch), then shown again and moved on
        // to page 12 without another switch. Second window: one tab on page 7.
        var dense = await OpenAsync(workspace, first, firstView, "text-dense.pdf");
        dense.GoToPage(5);
        Settle(workspace);
        var rich = await OpenAsync(workspace, first, firstView, "text-rich.pdf");
        first.Tabs.Activate(dense);
        await WaitUntilAsync(workspace, () => ReferenceEquals(firstView.Document, dense.Session.Document));
        var leftOnPage5 = dense.SavedView!.Anchor;
        dense.GoToPage(12);
        Settle(workspace);
        var denseNow = firstView.Layout!.GetAnchor(firstView.ScrollViewer.Offset.Y);
        Assert.NotEqual(leftOnPage5, denseNow);
        var thousand = await OpenAsync(workspace, second, secondView, "thousand-pages.pdf");
        thousand.GoToPage(7);
        Settle(workspace);
        var thousandNow = secondView.Layout!.GetAnchor(secondView.ScrollViewer.Offset.Y);
        var richLeft = rich.SavedView!.Anchor;

        // Quitting the app closes every window: each tab is stored, the active one with where it is now.
        workspace.Dispose();
        var stored = new RecentFilesStore(HistoryFile).Load();
        Assert.Equal(3, stored.Items.Count);
        Assert.Equal(denseNow, stored.TryGet(Full("text-dense.pdf"))!.View!.View.Anchor);
        Assert.Equal(richLeft, stored.TryGet(Full("text-rich.pdf"))!.View!.View.Anchor);
        Assert.Equal(thousandNow, stored.TryGet(Full("thousand-pages.pdf"))!.View!.View.Anchor);

        // The active tab of a window is the newest entry of its window.
        var order = stored.Items.Select(item => item.Path).ToList();
        Assert.True(order.IndexOf(Full("text-dense.pdf")) < order.IndexOf(Full("text-rich.pdf")));
    }

    [AvaloniaFact]
    public async Task AFileWithFewerPagesOpensOnTheLastPage()
    {
        // Remembered on page 41 of a file that has 2 pages now (it was changed).
        var recent = RecentFilesService.Load(_directory);
        recent.Closed(Full("text-rich.pdf"), new RememberedView(new ViewState(new PageAnchor(40, 0.5), 0), 1, ZoomMode.Free, PageLayoutMode.Continuous));
        var (window, viewModel, view) = Show(recent);

        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Settle(window);

        Assert.Equal(2, tab.PageCount);
        Assert.Equal(1, tab.SavedView!.Anchor.Page);
        Assert.Equal(2, tab.CurrentPage);
        Assert.True(view.ScrollViewer.Offset.Y > 0, $"offset {view.ScrollViewer.Offset}");
        Assert.Equal(1, view.Layout!.GetMostVisiblePage(view.ScrollViewer.Offset.Y, view.ScrollViewer.Offset.Y + view.ScrollViewer.Viewport.Height));
    }

    [AvaloniaFact]
    public async Task ARememberedViewDoesNotApplyToAMovedTab()
    {
        var recent = RecentFilesService.Load(_directory);
        var workspace = CreateWorkspace(recent);
        var first = workspace.OpenWindow();
        var firstView = Find<DocumentView>(workspace.WindowOf(first), "DocumentView");
        await OpenAsync(workspace, first, firstView, "hello_world.pdf");
        var tab = await OpenAsync(workspace, first, firstView, "text-dense.pdf");
        tab.GoToPage(5);
        Settle(workspace);

        // The history knows another position for the file; moving the tab is no new opening.
        recent.Closed(Full("text-dense.pdf"), new RememberedView(new ViewState(new PageAnchor(30, 0), 0), 3, ZoomMode.Free, PageLayoutMode.SinglePage));
        workspace.MoveToNewWindow(first, tab);
        var second = workspace.Windows[1];
        var view = Find<DocumentView>(workspace.WindowOf(second), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(view.Document, tab.Session.Document) && view.ScrollViewer.Viewport.Height > 0);
        Settle(workspace);

        Assert.Equal(5, tab.CurrentPage);
        Assert.Equal(1.0, tab.Zoom);
        Assert.Equal(PageLayoutMode.Continuous, view.Layout!.Mode);
        Assert.Equal(new PageAnchor(30, 0), recent.Files.TryGet(Full("text-dense.pdf"))!.View!.View.Anchor);
    }

    [AvaloniaFact]
    public async Task TheStartScreenListsFiveEntriesNewestFirst()
    {
        var folder = Path.GetDirectoryName(Full("hello_world.pdf"))!;
        var names = new[] { "seven.pdf", "six.pdf", "five.pdf", "four.pdf", "three.pdf", "two.pdf", "hello_world.pdf" };
        WriteHistory(names.Reverse().Select(name => Path.Combine(folder, name)).ToArray());
        var recent = RecentFilesService.Load(_directory);
        var (window, viewModel, _) = Show(recent);
        await WaitUntilAsync(window, () => Section(window) is { IsEffectivelyVisible: true });
        var section = Section(window)!;

        // The newest five, under the heading "Recently opened": the file name in type.strong, the folder in
        // text.secondary, both shortened in the middle.
        Assert.Equal(Strings.StartRecent, Find<TextBlock>(section, "RecentHeading").Text);
        Assert.Equal("Recently opened", Strings.StartRecent);
        var entries = Entries(window);
        Assert.Equal(["hello_world.pdf", "two.pdf", "three.pdf", "four.pdf", "five.pdf"], entries.Select(entry => NameOf(entry).Text));
        foreach (var entry in entries)
        {
            var name = NameOf(entry);
            var place = FolderOf(entry);
            Assert.Equal(FontWeight.SemiBold, name.FontWeight);
            Assert.Equal(13, name.FontSize);
            Assert.Equal(folder, place.Text);
            Assert.Equal(TokenTests.Token("Text.Secondary", window.ActualThemeVariant), Assert.IsAssignableFrom<ISolidColorBrush>(place.Foreground).Color);
            Assert.Equal($"{name.Text}, {folder}", AutomationProperties.GetName(entry));
            Assert.Equal(Strings.StartRecentHelp, AutomationProperties.GetHelpText(entry));
            Assert.Equal(Path.Combine(folder, name.Text!), ToolTip.GetTip(entry));
        }

        // Text secondary on the canvas of the start screen keeps AA, the focus ring 3:1.
        var canvas = TokenTests.Token("Surface.Canvas", window.ActualThemeVariant);
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Secondary", window.ActualThemeVariant), canvas) >= 4.5);
        Assert.True(Wcag.Contrast(TokenTests.Token("Focus.Ring", window.ActualThemeVariant), canvas) >= 3);

        // Each entry has a button that removes it (for the mouse; the keys use Delete on the entry).
        var removes = section.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("recentRemove")).ToArray();
        Assert.Equal(5, removes.Length);
        Assert.All(removes, remove => Assert.Equal(Strings.StartRecentRemove, ToolTip.GetTip(remove)));
        Assert.All(removes, remove => Assert.False(remove.IsTabStop));

        // Keyboard: Tab reaches the first entry after "Choose file", Down and Up move through the entries, Enter opens.
        Find<Button>(window, "ChooseFileButton").Focus(NavigationMethod.Tab);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.Same(entries[0], Focused(window));
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Same(entries[2], Focused(window));
        Press(window, Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None);
        Assert.Same(entries[1], Focused(window));
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.Same(entries[2], Focused(window));
        entries[0].Focus(NavigationMethod.Tab);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntilAsync(window, () => viewModel.ActiveTab is { IsReady: true, FileName: "hello_world.pdf" });

        // In German.
        Assert.Equal("Zuletzt geöffnet", Localized("de", () => Strings.StartRecent));
        Assert.Equal("Aus dem Verlauf entfernen (Entf)", Localized("de", () => Strings.StartRecentRemove));
    }

    [AvaloniaFact]
    public async Task EntriesNeverTouchTheDiskWhenShown()
    {
        // A folder that does not exist (a removed drive, a deleted folder): listed as it is, nothing is asked of it.
        var missingFolder = Path.Combine(Path.GetTempPath(), "pagewing-missing-" + Guid.NewGuid().ToString("N"), "deep");
        WriteHistory(Path.Combine(missingFolder, "gone.pdf"));
        var recent = RecentFilesService.Load(_directory);
        var (window, _, _) = Show(recent);
        await WaitUntilAsync(window, () => Section(window) is { IsEffectivelyVisible: true });

        var entry = Assert.Single(Entries(window));
        Assert.Equal("gone.pdf", NameOf(entry).Text);
        Assert.Equal(missingFolder, FolderOf(entry).Text);
        Assert.True(entry.IsEnabled);
        Assert.False(Directory.Exists(missingFolder));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missingFolder)));
    }

    [AvaloniaFact]
    public async Task OpeningAMissingEntryShowsTheErrorCard()
    {
        var missing = Path.Combine(_directory, "gone", "missing.pdf");
        WriteHistory(missing);
        var recent = RecentFilesService.Load(_directory);
        var (window, viewModel, _) = Show(recent);
        await WaitUntilAsync(window, () => Section(window) is { IsEffectivelyVisible: true });

        Click(window, Assert.Single(Entries(window)));
        await WaitUntilAsync(window, () => viewModel.ActiveTab is { IsFailed: true });

        // The error card of the tab says it was not found; the entry stays in the history (the file may come back).
        Assert.Equal(PdfOpenError.FileNotFound, viewModel.ActiveTab!.Error);
        Assert.True(Find<ErrorCard>(window, "ErrorCard").IsEffectivelyVisible);
        Assert.Equal([missing], recent.Files.Items.Select(item => item.Path));
    }

    [AvaloniaFact]
    public async Task DeleteRemovesAnEntry()
    {
        var folder = Path.Combine(_directory, "docs");
        WriteHistory(Path.Combine(folder, "c.pdf"), Path.Combine(folder, "b.pdf"), Path.Combine(folder, "a.pdf"));
        var recent = RecentFilesService.Load(_directory);
        var (window, _, _) = Show(recent);
        await WaitUntilAsync(window, () => Section(window) is { IsEffectivelyVisible: true });

        // Delete on the second entry: gone from the list and the file, the keys go to the entry that took its place.
        Assert.Equal(["c.pdf", "b.pdf", "a.pdf"], Entries(window).Select(entry => NameOf(entry).Text));
        Entries(window)[1].Focus(NavigationMethod.Tab);
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Equal(["c.pdf", "a.pdf"], Entries(window).Select(entry => NameOf(entry).Text));
        Assert.Equal([Path.Combine(folder, "c.pdf"), Path.Combine(folder, "a.pdf")], StoredPaths());
        Assert.Same(Entries(window)[1], Focused(window));

        // Delete on the last entry: the keys go to the one before it.
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Same(Assert.Single(Entries(window)), Focused(window));

        // The remove button does the same with the mouse; the empty list takes the section away, the keys go to
        // "Choose file".
        var remove = Section(window)!.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("recentRemove"));
        Assert.Equal(string.Format(CultureInfo.InvariantCulture, CompositeFormat.Parse(Strings.StartRecentRemoveName), "c.pdf"), AutomationProperties.GetName(remove));
        Assert.Equal("Remove “c.pdf” from the history", AutomationProperties.GetName(remove));
        Entries(window)[0].Focus(NavigationMethod.Tab);
        Click(window, remove);
        Assert.Empty(recent.Files.Items);
        Assert.Empty(StoredPaths());
        Assert.False(Section(window)!.IsVisible);
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        Assert.Empty(StoredPaths());
    }

    [AvaloniaFact]
    public async Task ClearEmptiesTheListAndThePositions()
    {
        var recent = RecentFilesService.Load(_directory);
        recent.Closed(Full("text-dense.pdf"), new RememberedView(new ViewState(new PageAnchor(30, 0), 0), 2, ZoomMode.Free, PageLayoutMode.TwoPages));
        recent.Opened(Full("hello_world.pdf"));
        var (window, viewModel, view) = Show(recent);
        await WaitUntilAsync(window, () => Section(window) is { IsEffectivelyVisible: true });

        // "Clear history" in the settings card: no question, the button itself says it is done.
        Press(window, Key.OemComma, PhysicalKey.Comma, RawInputModifiers.Control);
        var card = Find<SettingsCard>(window, "SettingsCard");
        await WaitUntilAsync(window, () => card.IsEffectivelyVisible);
        var button = Find<Button>(card, "ClearHistoryButton");
        Assert.Equal(Strings.SettingsClearHistory, TextOf(button));
        Assert.Equal(Strings.SettingsClearHistory, AutomationProperties.GetName(button));
        Assert.Equal("Clear history", Strings.SettingsClearHistory);

        // The card has grown with the printing group: the button lies below the visible part until it is scrolled to.
        button.BringIntoView();
        Settle(window);
        Click(window, button);
        Assert.Empty(recent.Files.Items);
        Assert.Empty(StoredPaths());
        Assert.Equal(Strings.SettingsHistoryCleared, TextOf(button));
        Assert.Equal(Strings.SettingsHistoryCleared, AutomationProperties.GetName(button));
        Assert.Equal("History cleared.", Strings.SettingsHistoryCleared);
        Assert.Equal("Verlauf gelöscht.", Localized("de", () => Strings.SettingsHistoryCleared));
        Assert.Equal("Verlauf löschen", Localized("de", () => Strings.SettingsClearHistory));
        Assert.False(Section(window)!.IsVisible);

        // The positions went with the list: the file opens at the top with the defaults.
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(1.0, tab.Zoom);
        Assert.Equal(PageLayoutMode.Continuous, tab.LayoutMode);

        // Shown again, the card offers to clear again, also by keyboard (Tab from the update switch, then Space); the
        // command of the palette clears as well.
        Press(window, Key.OemComma, PhysicalKey.Comma, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => card.IsEffectivelyVisible);
        Assert.Equal(Strings.SettingsClearHistory, TextOf(button));
        Assert.Single(recent.Files.Items);
        Find<ToggleSwitch>(card, "UpdateCheckSwitch").Focus(NavigationMethod.Tab);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.Same(button, Focused(window));
        Press(window, Key.Space, PhysicalKey.Space, RawInputModifiers.None);
        Assert.Empty(recent.Files.Items);
        Assert.Equal(Strings.SettingsHistoryCleared, TextOf(button));
        Assert.Same(button, Focused(window));
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        recent.Opened(Full("text-rich.pdf"));
        var command = viewModel.Commands.ById("recent.clear")!;
        Assert.True(command.CanExecute());
        command.Execute();
        Assert.Empty(recent.Files.Items);
        Assert.Empty(StoredPaths());
    }

    [AvaloniaFact]
    public async Task MeasurementRunsNeverWriteTheHistory()
    {
        // A measurement takes a history in memory, never the user's file.
        var loads = 0;
        var measured = Program.LoadRecentFiles(StartupOptions.Parse(["--measure-startup", "a.pdf"]), () =>
        {
            loads++;
            return RecentFilesService.Load(_directory);
        });
        Assert.Equal(0, loads);
        Assert.Null(measured.FilePath);
        var normal = Program.LoadRecentFiles(StartupOptions.Parse(["a.pdf"]), () =>
        {
            loads++;
            return RecentFilesService.Load(_directory);
        });
        Assert.Equal(1, loads);
        Assert.Equal(HistoryFile, normal.FilePath);

        // Opening and closing in a window with that history writes nothing anywhere.
        var (window, viewModel, view) = Show(measured);
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        tab.GoToPage(4);
        Settle(window);
        viewModel.CloseActiveTabCommand.Execute(null);
        Assert.Equal(Full("text-dense.pdf"), Assert.Single(measured.Files.Items).Path);
        Assert.False(File.Exists(HistoryFile));
        Assert.False(Directory.Exists(_directory));
    }

    [AvaloniaFact]
    public async Task TheHistoryIsNotReadInTheStartPath()
    {
        WriteHistory(Full("hello_world.pdf"));
        var recent = RecentFilesService.Load(_directory);
        Assert.Equal(0, recent.Reads);

        // As Program.Main and the app do it: the session opens the file, the window adopts the tab.
        var session = new ViewerSession(new PdfEngine());
        _ = session.OpenAsync(TestData.Pdf("text-dense.pdf"));
        var tab = new TabViewModel(session);
        int? readsAtReady = null;
        int? readsAtSharpPage = null;
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.State) && tab.IsReady)
            {
                readsAtReady ??= recent.Reads;
            }
            else if (e.PropertyName == nameof(TabViewModel.HasShownSharpPage) && tab.HasShownSharpPage)
            {
                readsAtSharpPage ??= recent.Reads;
            }
        };
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached(), recent) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        viewModel.AttachTab(tab);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        _cleanup.Add(new Closer(window));
        window.Show();

        // Read once, after the first sharp page; the file opened before that is at the top, the older entry stays.
        await WaitUntilAsync(window, () => recent.Reads > 0);
        Assert.Equal(0, readsAtReady);
        Assert.Equal(0, readsAtSharpPage);
        Assert.Equal(1, recent.Reads);
        await WaitUntilAsync(window, () => !recent.HasUnsavedChanges);
        Assert.Equal(1, recent.Writes);
        Assert.Equal([Full("text-dense.pdf"), Full("hello_world.pdf")], StoredPaths());

        // The start screen of a window without a file reads it after its first frame, not before.
        var other = RecentFilesService.Load(_directory);
        var start = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached(), other) { IsWindowsTitleBar = true };
        _cleanup.Add(start);
        var startWindow = new MainWindow { DataContext = start, Width = 1024, Height = 720 };
        _cleanup.Add(new Closer(startWindow));
        startWindow.Show();
        Assert.Equal(0, other.Reads);
        await WaitUntilAsync(startWindow, () => Section(startWindow) is { IsEffectivelyVisible: true });
        Assert.Equal(1, other.Reads);
    }

    [Fact]
    public void AnEntryTakesAnyPathApartWithoutThrowing()
    {
        // A history file may hold any text as a path: characters no file system takes, no folder, nothing at all.
        var folder = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var odd = new RecentEntry(Path.Combine(folder, "a\0b<>|\"*?.pdf"));
        Assert.Equal("a\0b<>|\"*?.pdf", odd.FileName);
        Assert.Equal(folder, odd.Folder);

        var bare = new RecentEntry("report.pdf");
        Assert.Equal("report.pdf", bare.FileName);
        Assert.Equal(string.Empty, bare.Folder);
        Assert.Equal("report.pdf", bare.AutomationName);

        var empty = new RecentEntry(string.Empty);
        Assert.Equal(string.Empty, empty.FileName);
        Assert.Equal(string.Empty, empty.Folder);
    }

    [AvaloniaFact]
    public void AFlushWithNothingChangedAfterReadingWritesNothing()
    {
        var first = RecentFilesService.Load(_directory);
        first.Opened(Full("hello_world.pdf"));
        first.Flush();
        Assert.Equal(1, first.Writes);

        // The next start opens the file that is on top already, before the list is read: noted, but no change.
        var next = RecentFilesService.Load(_directory);
        next.Opened(Full("hello_world.pdf"));
        Assert.True(next.HasUnsavedChanges);
        next.Flush();
        Assert.Equal(1, next.Reads);
        Assert.Equal(0, next.Writes);
        Assert.False(next.HasUnsavedChanges);
    }

    [AvaloniaFact]
    public void RecentFilesSurviveARestart()
    {
        // The first run opens two files and leaves one of them on page 12 at 125 %, two pages with a cover.
        var view = new RememberedView(new ViewState(new PageAnchor(11, 0.25), -40), 1.25, ZoomMode.Free, PageLayoutMode.TwoPagesWithCover);
        var first = RecentFilesService.Load(_directory);
        first.Opened(Full("hello_world.pdf"));
        first.Opened(Full("text-dense.pdf"));
        first.Closed(Full("text-dense.pdf"), view);
        Assert.False(first.LastSaveFailed);

        // The next start reads the same folder: same files, same order, same position.
        var next = RecentFilesService.Load(_directory);
        Assert.Equal([Full("text-dense.pdf"), Full("hello_world.pdf")], next.Files.Items.Select(item => item.Path));
        Assert.Equal(view, next.Files.Items[0].View);
        Assert.Null(next.Files.Items[1].View);

        // Removing and clearing survive it too.
        next.Remove(Full("hello_world.pdf"));
        Assert.Equal([Full("text-dense.pdf")], RecentFilesService.Load(_directory).Files.Items.Select(item => item.Path));
        next.Clear();
        Assert.Empty(RecentFilesService.Load(_directory).Files.Items);
    }

    [AvaloniaFact]
    public async Task ACorruptHistoryFileDoesNotStopTheApp()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(HistoryFile, "{ \"Version\": 1, \"Items\": [ { \"Path\": ");
        var recent = RecentFilesService.Load(_directory);
        var (window, viewModel, view) = Show(recent);
        Settle(window);

        // Read as empty, kept as <name>.bad; the start screen shows no section, a file opens and is written anew.
        Assert.Equal(1, recent.Reads);
        Assert.Empty(recent.Files.Items);
        Assert.True(File.Exists(HistoryFile + ".bad"));
        Assert.True(Section(window) is null or { IsVisible: false });
        await OpenAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal([Full("hello_world.pdf")], StoredPaths());

        // A folder that cannot be written: the value stays in memory, nothing throws.
        var blocker = _directory + ".blocker";
        File.WriteAllText(blocker, "not a folder");
        try
        {
            var blocked = RecentFilesService.Load(blocker);
            blocked.Opened(Full("hello_world.pdf"));
            blocked.Closed(Full("hello_world.pdf"), new RememberedView(new ViewState(new PageAnchor(0, 0), 0), 1, ZoomMode.Free, PageLayoutMode.Continuous));
            blocked.Remove(Full("hello_world.pdf"));
            blocked.Clear();
            Assert.True(blocked.LastSaveFailed);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    private static string Full(string name) => Path.GetFullPath(TestData.Pdf(name));

    private static string Localized(string language, Func<string> text)
    {
        var before = Strings.Culture;
        Strings.Culture = new CultureInfo(language);
        try
        {
            return text();
        }
        finally
        {
            Strings.Culture = before;
        }
    }

    private void WriteHistory(params string[] newestFirst)
    {
        var files = new RecentFiles(newestFirst.Select((path, i) => new RecentFile(path, DateTimeOffset.UnixEpoch.AddDays(newestFirst.Length - i), null)));
        Assert.True(new RecentFilesStore(HistoryFile).TrySave(files));
    }

    private List<string> StoredPaths() => [.. new RecentFilesStore(HistoryFile).Load().Items.Select(item => item.Path)];

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(RecentFilesService recent, UserSettings? settings = null)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached(), recent)
        {
            IsWindowsTitleBar = true,
        };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    private Workspace CreateWorkspace(RecentFilesService recent)
    {
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => new NoFilePicker(),
            recentFiles: recent);
        _cleanup.Add(workspace);
        return workspace;
    }

    /// <summary>
    /// Opens a file in a new tab and waits until the view shows it with a sharp page (a restored view need not show
    /// page 1), then for the panel the window may open after its first sharp page, which it closes again (without it
    /// counting as the user's choice), so the viewport has the width the tests expect. The writes posted after the sharp
    /// page have run when it returns.
    /// </summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);
        Settle(window);
        Settle(window);
        if (tab.SidePanel != SidePanelKind.None)
        {
            tab.SidePanel = SidePanelKind.None;
            var host = Find<Control>(window, AutoOpenedPanel.HostName);
            await WaitUntilAsync(window, () => !host.IsVisible);
        }

        return tab;
    }

    private static Task<TabViewModel> OpenAsync(Workspace workspace, MainViewModel viewModel, DocumentView view, string name) =>
        OpenAsync((Window)workspace.WindowOf(viewModel), viewModel, view, name);

    private static Control? Section(Window window) =>
        window.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == "RecentSection");

    private static List<Button> Entries(Window window) =>
        [.. window.GetVisualDescendants().OfType<Button>().Where(button => button.Classes.Contains("recentEntry") && button.IsEffectivelyVisible)];

    private static MiddleEllipsisTextBlock NameOf(Button entry) =>
        entry.GetVisualDescendants().OfType<MiddleEllipsisTextBlock>().Single(text => text.Name == "RecentName");

    private static MiddleEllipsisTextBlock FolderOf(Button entry) =>
        entry.GetVisualDescendants().OfType<MiddleEllipsisTextBlock>().Single(text => text.Name == "RecentFolder");

    private static string? TextOf(Button button) =>
        button.Content as string ?? button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault()?.Text;

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

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

    private static async Task WaitUntilAsync(Workspace workspace, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(workspace);
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The windows did not reach the expected state.");
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

    private static void Settle(Workspace workspace)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            foreach (var viewModel in workspace.Windows)
            {
                workspace.WindowOf(viewModel).UpdateLayout();
            }
        }
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
