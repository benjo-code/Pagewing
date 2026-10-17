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
public sealed class PanelTests : IDisposable
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
    public async Task ActionBarIsHiddenWithoutTabs()
    {
        // Narrow, so no panel opens and the document area lies right of the action bar.
        var (window, viewModel, view) = Show(800);
        var actionBar = Find<ActionBar>(window, "ActionBar");

        Assert.False(actionBar.IsEffectivelyVisible);
        Assert.False(FindOrNull<Button>(window, "PagesButton")?.IsEffectivelyVisible ?? false);
        Assert.Empty(SidePanels(window));

        await OpenTabAsync(window, viewModel, view, "outline.pdf");

        // Design system 5.4 and 6: 52 px wide below the title bar, buttons 40 px with 20 px icons; Pages, then Contents.
        Assert.True(actionBar.IsEffectivelyVisible);
        Assert.Equal(52, actionBar.Bounds.Width);
        var origin = actionBar.TranslatePoint(default, window)!.Value;
        Assert.Equal(0, origin.X);
        Assert.Equal(40, origin.Y);
        var pages = Find<Button>(window, "PagesButton");
        var contents = Find<Button>(window, "ContentsButton");
        foreach (var button in new[] { pages, contents })
        {
            Assert.Equal(new Size(40, 40), button.Bounds.Size);
            var icon = Assert.Single(button.GetVisualDescendants().OfType<StrokeIcon>());
            Assert.Equal(new Size(20, 20), icon.Bounds.Size);
        }

        Assert.True(pages.TranslatePoint(default, window)!.Value.Y < contents.TranslatePoint(default, window)!.Value.Y);
        Assert.Same(Resource("Pw.Icon.LayoutGrid"), Assert.Single(pages.GetVisualDescendants().OfType<StrokeIcon>()).Data);
        Assert.Same(Resource("Pw.Icon.ListTree"), Assert.Single(contents.GetVisualDescendants().OfType<StrokeIcon>()).Data);
        Assert.Equal(Strings.PagesTooltip, ToolTip.GetTip(pages));
        Assert.Equal(Strings.ContentsTooltip, ToolTip.GetTip(contents));

        // The document area starts right of the action bar; the status bar is indented by it as well.
        var area = Find<Control>(window, "DocumentArea");
        Assert.Equal(52, area.TranslatePoint(default, window)!.Value.X);

        // Closing the last tab takes the action bar away with the document area.
        viewModel.CloseActiveTabCommand.Execute(null);
        Settle(window);
        Assert.False(actionBar.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ClickingAnEntryOpensItsPanelAndClickingAgainClosesIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");

        // The wide window opened "Pages" after the first sharp tile; a click on the active entry closes it.
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));
        var pages = Find<Button>(window, "PagesButton");
        Click(window, pages);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        await WaitUntilAsync(window, () => SidePanels(window).All(panel => !panel.IsEffectivelyVisible));

        // A click on an entry opens its panel, with the title of that panel and the close button.
        Click(window, pages);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));
        Assert.Equal(Strings.PagesPanelTitle, Find<TextBlock>(window, "PanelTitle").Text);
        Assert.Equal(Strings.ClosePanel, ToolTip.GetTip(Find<Button>(window, "ClosePanelButton")));

        Click(window, pages);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        await WaitUntilAsync(window, () => SidePanels(window).All(panel => !panel.IsEffectivelyVisible));

        // The close button of the panel closes it as well.
        Click(window, Find<Button>(window, "ContentsButton"));
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Contents));
        Assert.Equal(Strings.ContentsPanelTitle, Find<TextBlock>(window, "PanelTitle").Text);
        Click(window, Find<Button>(window, "ClosePanelButton"));
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        await WaitUntilAsync(window, () => SidePanels(window).All(panel => !panel.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public async Task OnlyOnePanelIsOpen()
    {
        var (window, viewModel, view) = Show(800);
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");

        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));

        // Another entry switches the panel: one panel, with the content of the new entry only.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Settle(window);
        Assert.Equal(SidePanelKind.Contents, tab.SidePanel);
        Assert.Single(SidePanels(window));
        Assert.True(IsPanelShown(window, SidePanelKind.Contents));
        Assert.Null(FindOrNull<Control>(window, "PagesContent"));
        Assert.Equal(Strings.ContentsPanelTitle, Find<TextBlock>(window, "PanelTitle").Text);
        Assert.True(viewModel.IsContentsPanelOpen);
        Assert.False(viewModel.IsPagesPanelOpen);
        Assert.Contains("active", Find<Button>(window, "ContentsButton").Classes);
        Assert.DoesNotContain("active", Find<Button>(window, "PagesButton").Classes);
    }

    [AvaloniaFact]
    public async Task PanelChoiceIsPerTab()
    {
        var (window, viewModel, view) = Show(800);
        var first = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        var second = await OpenTabAsync(window, viewModel, view, "hello_world.pdf");

        // A new tab starts without a panel; the first one keeps its choice.
        Assert.Equal(SidePanelKind.None, second.SidePanel);
        Assert.Equal(SidePanelKind.Contents, first.SidePanel);
        Assert.True(SidePanels(window).All(panel => !panel.IsEffectivelyVisible));

        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));

        // Switching tabs switches the panel with them, at once.
        viewModel.ActivateTabCommand.Execute(first);
        Settle(window);
        Assert.True(IsPanelShown(window, SidePanelKind.Contents));
        Assert.Contains("active", Find<Button>(window, "ContentsButton").Classes);

        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        viewModel.ActivateTabCommand.Execute(second);
        Settle(window);
        Assert.Equal(SidePanelKind.None, first.SidePanel);
        Assert.Equal(SidePanelKind.Pages, second.SidePanel);
        Assert.True(IsPanelShown(window, SidePanelKind.Pages));
    }

    [AvaloniaFact]
    public async Task PanelWidthIsLimitedAndResetByDoubleClick()
    {
        var (window, viewModel, view) = Show(1200);
        await OpenTabAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));

        // Design system 6: 264 by default, 200 to 400; the panel lies between the action bar and the document area.
        Assert.Equal(MainViewModel.DefaultPanelWidth, viewModel.PanelWidth);
        Assert.Equal(264, MainViewModel.DefaultPanelWidth);
        var panel = Assert.Single(SidePanels(window));
        var area = Find<Control>(window, "DocumentArea");
        Assert.Equal(264, panel.Bounds.Width);
        Assert.Equal(52, panel.TranslatePoint(default, window)!.Value.X);
        Assert.Equal(52 + 264, area.TranslatePoint(default, window)!.Value.X);

        viewModel.PanelWidth = 500;
        Assert.Equal(400, viewModel.PanelWidth);
        viewModel.PanelWidth = 100;
        Assert.Equal(200, viewModel.PanelWidth);
        viewModel.PanelWidth = 300;
        Settle(window);
        Assert.Equal(300, panel.Bounds.Width);
        Assert.Equal(52 + 300, area.TranslatePoint(default, window)!.Value.X);

        // Dragging the edge between panel and document resizes the panel, within the limits.
        var splitter = Find<GridSplitter>(window, "PanelSplitter");
        var start = CenterOf(window, splitter);
        Assert.Equal(52 + 300, start.X, 0.5);
        Drag(window, start, start + new Point(50, 0));
        Assert.Equal(350, viewModel.PanelWidth, 0.5);
        Assert.Equal(350, panel.Bounds.Width, 0.5);
        Assert.Equal(52 + 350, area.TranslatePoint(default, window)!.Value.X, 0.5);

        start = CenterOf(window, splitter);
        Drag(window, start, start + new Point(200, 0));
        Assert.Equal(400, viewModel.PanelWidth, 0.5);
        Assert.Equal(400, panel.Bounds.Width, 0.5);

        start = CenterOf(window, splitter);
        Drag(window, start, start - new Point(400, 0));
        Assert.Equal(200, viewModel.PanelWidth, 0.5);

        // A double click on the edge goes back to the default width.
        start = CenterOf(window, splitter);
        Click(window, start);
        Click(window, start);
        Assert.Equal(264, viewModel.PanelWidth);
        Assert.Equal(264, panel.Bounds.Width);
        Assert.Equal(52 + 264, area.TranslatePoint(default, window)!.Value.X);
    }

    [AvaloniaFact]
    public async Task PanelOpensAfterTheFirstSharpTileOnAWideWindow()
    {
        var (window, viewModel, view) = Show(1024);
        var drawn = false;
        view.FirstSharpTileDrawn += () => drawn = true;

        await viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        var tab = viewModel.ActiveTab!;
        Assert.False(viewModel.IsNarrowWindow);

        await WaitUntilAsync(window, () => drawn);
        Settle(window);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));
        Assert.Contains("active", Find<Button>(window, "PagesButton").Classes);

        // Only the first sharp tile of the window opens it: a second document leaves its tab without a panel.
        var second = await OpenTabAsync(window, viewModel, view, "hello_world.pdf");
        await WaitUntilAsync(window, () => HasSharpTile(view, 0));
        Settle(window);
        Assert.Equal(SidePanelKind.None, second.SidePanel);
    }

    [AvaloniaFact]
    public async Task AutoOpenRunsAfterTheFirstSharpTileHandlers()
    {
        var (window, viewModel, view) = Show(1024);
        int? panelsInHandler = null;
        SidePanelKind? kindInHandler = null;

        // Subscribed after the window, like a measurement run: inside the handlers of the first sharp tile (where a
        // measurement takes its time) the panel is neither chosen nor built.
        view.FirstSharpTileDrawn += () =>
        {
            panelsInHandler ??= SidePanels(window).Count;
            kindInHandler ??= viewModel.ActiveTab?.SidePanel;
        };

        await viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        await WaitUntilAsync(window, () => panelsInHandler is not null);
        Assert.Equal(0, panelsInHandler);
        Assert.Equal(SidePanelKind.None, kindInHandler);

        // A background job after the handlers opens it.
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));
    }

    [AvaloniaFact]
    public async Task StatusBarIsIndentedByTheActionBar()
    {
        var (window, viewModel, view) = Show(800);
        await OpenTabAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => Find<Control>(window, "StatusBar").IsEffectivelyVisible);

        // Design system 6, item 5: the status bar starts flush with the document area, right of the action bar.
        var area = Find<Control>(window, "DocumentArea").TranslatePoint(default, window)!.Value.X;
        var first = Find<Button>(window, "PreviousPageButton").TranslatePoint(default, window)!.Value.X;
        Assert.Equal(52, area);
        Assert.Equal(52 + 4, first);
        Assert.True(first >= area);
    }

    [AvaloniaFact]
    public async Task PanelStaysClosedOnANarrowWindow()
    {
        var (window, viewModel, view) = Show(899);
        var drawn = false;
        view.FirstSharpTileDrawn += () => drawn = true;

        await viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => drawn);
        Settle(window);

        Assert.True(viewModel.IsNarrowWindow);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Empty(SidePanels(window));

        // Making the window wide later does not open it: the moment has passed.
        window.Width = 1024;
        Settle(window);
        Assert.False(viewModel.IsNarrowWindow);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.Empty(SidePanels(window));
    }

    [AvaloniaFact]
    public async Task PanelOverlaysTheDocumentBelow900Pixels()
    {
        var (window, viewModel, view) = Show(800);
        await OpenTabAsync(window, viewModel, view, "outline.pdf");
        var area = Find<Control>(window, "DocumentArea");
        var before = area.Bounds;
        var viewport = view.ScrollViewer.Viewport;

        Click(window, Find<Button>(window, "PagesButton"));
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));

        // The document keeps its width and position; the panel lies over its left part.
        var panel = Assert.Single(SidePanels(window));
        Assert.Equal(before, area.Bounds);
        Assert.Equal(viewport, view.ScrollViewer.Viewport);
        var left = panel.TranslatePoint(default, window)!.Value.X;
        Assert.Equal(52, left);
        Assert.Equal(264, panel.Bounds.Width);
        Assert.True(area.TranslatePoint(default, window)!.Value.X < left + panel.Bounds.Width);
        var inside = panel.TranslatePoint(new Point(panel.Bounds.Width / 2, panel.Bounds.Height / 2), window)!.Value;
        Assert.Contains(panel, window.InputHitTest(inside) is Visual hit ? hit.GetSelfAndVisualAncestors() : []);

        // Edge border.subtle on the document side, no shadow, no splitter.
        var frame = Find<Border>(panel, "PanelFrame");
        Assert.Equal(new Thickness(0, 0, 1, 0), frame.BorderThickness);
        Assert.Equal(TokenTests.Token("Border.Subtle", ThemeVariant.Light), Assert.IsType<ISolidColorBrush>(frame.BorderBrush, exactMatch: false).Color);
        Assert.Equal(default, frame.BoxShadow);
        Assert.False(Find<GridSplitter>(window, "PanelSplitter").IsEffectivelyVisible);

        // Widening the window past 900 px puts the panel beside the document again, without the edge.
        window.Width = 1024;
        Settle(window);
        Assert.Equal(52 + 264, area.TranslatePoint(default, window)!.Value.X);
        Assert.Equal(new Thickness(0), frame.BorderThickness);
        Assert.True(Find<GridSplitter>(window, "PanelSplitter").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task NarrowPanelClosesAfterPickingAPage()
    {
        var (window, viewModel, view) = Show(1024);
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));

        // Beside the document the panel stays open after a pick.
        tab.ShowPageFromPanel(2);
        Settle(window);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);

        // Over the document it gets out of the way.
        window.Width = 800;
        Settle(window);
        Assert.True(viewModel.IsNarrowWindow);
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        tab.ShowPageFromPanel(3);
        Settle(window);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        await WaitUntilAsync(window, () => SidePanels(window).All(panel => !panel.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public async Task UserClosingThePanelPreventsTheAutoOpen()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var (window, viewModel, view) = Show(1024, factory: () => new ViewerSession(async (path, password, cancellationToken) =>
        {
            await gate.Task.ConfigureAwait(false);
            return await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        }));
        var drawn = false;
        view.FirstSharpTileDrawn += () => drawn = true;

        // While the file still loads (before any sharp tile) the user opens and closes "Pages" with the action bar.
        var opening = viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        Settle(window);
        var tab = viewModel.ActiveTab!;
        Assert.Equal(ViewerState.Loading, tab.State);
        Click(window, Find<Button>(window, "PagesButton"));
        Assert.Equal(SidePanelKind.Pages, tab.SidePanel);
        Click(window, Find<Button>(window, "PagesButton"));
        Assert.Equal(SidePanelKind.None, tab.SidePanel);

        gate.SetResult();
        await opening.WaitAsync(Timeout);
        await WaitUntilAsync(window, () => drawn);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        Assert.True(SidePanels(window).All(panel => !panel.IsEffectivelyVisible));
    }

    [AvaloniaFact]
    public async Task PanelContentIsBuiltOnlyWhenOpened()
    {
        var (window, viewModel, view) = Show(800);
        await OpenTabAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => HasSharpTile(view, 0));
        Settle(window);

        // Nothing of the panel exists while it is closed.
        Assert.Empty(SidePanels(window));
        Assert.Null(FindOrNull<Control>(window, "PagesContent"));
        Assert.Null(FindOrNull<Control>(window, "ContentsContent"));

        // Opening "Contents" builds the panel with the contents content, and only that.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Settle(window);
        Assert.Single(SidePanels(window));
        Assert.NotNull(FindOrNull<Control>(window, "ContentsContent"));
        Assert.Null(FindOrNull<Control>(window, "PagesContent"));

        // The content of a closed panel is released: it is built again when the panel opens again.
        var built = Find<Control>(window, "ContentsContent");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        await WaitUntilAsync(window, () => FindOrNull<Control>(window, "ContentsContent") is null);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Settle(window);
        Assert.NotSame(built, Find<Control>(window, "ContentsContent"));
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task ActiveEntryHasAFillNotOnlyAColour(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel, view) = Show(800, variant);
        await OpenTabAsync(window, viewModel, view, "outline.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);

        var pages = Find<Button>(window, "PagesButton");
        var contents = Find<Button>(window, "ContentsButton");
        var subtle = TokenTests.Token("Accent.Subtle", variant);
        var surface = TokenTests.Token("Surface.Window", variant);

        // Design system 7.3 and 7.10: the active entry has an accent.subtle surface behind its icon, which is
        // accent.default; the others lie on the action bar with text.secondary icons.
        using (var frame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            var active = pages.TranslatePoint(new Point(5, 5), window)!.Value;
            var inactive = contents.TranslatePoint(new Point(5, 5), window)!.Value;
            Assert.Equal(subtle, frame.GetColor((int)active.X, (int)active.Y));
            Assert.Equal(surface, frame.GetColor((int)inactive.X, (int)inactive.Y));
        }

        Assert.Equal(TokenTests.Token("Accent.Default", variant), IconColor(pages));
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), IconColor(contents));
        Assert.True(Wcag.Contrast(TokenTests.Token("Accent.Default", variant), subtle) >= 3, "accent.default on accent.subtle");
        Assert.True(Wcag.Contrast(TokenTests.Token("Text.Secondary", variant), surface) >= 3, "text.secondary on surface.window");

        // Hovering the active entry keeps its fill.
        window.MouseMove(CenterOf(window, pages));
        Settle(window);
        using (var frame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            var active = pages.TranslatePoint(new Point(5, 5), window)!.Value;
            Assert.Equal(subtle, frame.GetColor((int)active.X, (int)active.Y));
        }

        Assert.Equal(TokenTests.Token("Accent.Default", variant), IconColor(pages));
    }

    [AvaloniaFact]
    public async Task FirstFrameDoesNotContainThePanel()
    {
        // The file opens only when the test lets it, so the first frames are those of the startup path.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var (window, viewModel, view) = Show(1024, factory: () => new ViewerSession(async (path, password, cancellationToken) =>
        {
            await gate.Task.ConfigureAwait(false);
            return await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        }));
        var drawn = false;
        view.FirstSharpTileDrawn += () => drawn = true;

        // The first frame with the tab (the file still loads, design system 7.1) shows the action bar and no panel:
        // the area right of the action bar is canvas.
        var opening = viewModel.OpenAsync(TestData.Pdf("outline.pdf"));
        Settle(window);
        Assert.Equal(ViewerState.Loading, viewModel.ActiveTab!.State);
        Assert.True(Find<ActionBar>(window, "ActionBar").IsEffectivelyVisible);
        Assert.Empty(SidePanels(window));
        using (var frame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            Assert.Equal(TokenTests.Token("Surface.Window", ThemeVariant.Light), frame.GetColor(26, 100));
            Assert.Equal(TokenTests.Token("Surface.Canvas", ThemeVariant.Light), frame.GetColor(52 + 40, 100));
        }

        gate.SetResult();
        await opening.WaitAsync(Timeout);

        // Up to the first sharp tile nothing of the panel is built; after it, the wide window opens "Pages".
        var deadline = DateTime.UtcNow + Timeout;
        while (!drawn)
        {
            Assert.Empty(SidePanels(window));
            Assert.Null(FindOrNull<Control>(window, "PagesContent"));
            Assert.True(DateTime.UtcNow < deadline, "no sharp tile");
            Settle(window);
            await Task.Delay(10);
        }

        Settle(window);
        await WaitUntilAsync(window, () => IsPanelShown(window, SidePanelKind.Pages));
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(double width = 1024, ThemeVariant? theme = null, Func<ViewerSession>? factory = null)
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

    /// <summary>Opens a test file in a new tab and waits until the view shows it; returns that tab.</summary>
    private static async Task<TabViewModel> OpenTabAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        return tab;
    }

    private static List<SidePanel> SidePanels(Window window) => window.GetVisualDescendants().OfType<SidePanel>().ToList();

    /// <summary>Whether the panel is in the window, visible, fully faded in and shows the content of <paramref name="kind"/>.</summary>
    private static bool IsPanelShown(Window window, SidePanelKind kind)
    {
        var panels = SidePanels(window);
        var content = kind == SidePanelKind.Pages ? "PagesContent" : "ContentsContent";
        return panels.Count == 1
            && panels[0].IsEffectivelyVisible
            && panels[0].GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1)
            && FindOrNull<Control>(window, content) is { IsEffectivelyVisible: true };
    }

    private static Color IconColor(Button button)
    {
        var icon = Assert.Single(button.GetVisualDescendants().OfType<StrokeIcon>());
        var brush = Assert.IsType<ISolidColorBrush>(icon.Brush ?? Avalonia.Controls.Documents.TextElement.GetForeground(icon), exactMatch: false);
        return brush.Color;
    }

    private static bool HasSharpTile(DocumentView view, int page) =>
        view.Canvas.Bitmaps.Keys.Any(key => key.Page == page && !key.IsPreview);

    private static object? Resource(string key) => Application.Current!.FindResource(key);

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control) => Click(window, CenterOf(window, control));

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
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
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name);

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
