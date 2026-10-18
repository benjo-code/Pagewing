using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
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
public sealed class TabStripTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private string? _tempDirectory;

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        if (_tempDirectory is not null)
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task TabStripShowsOneItemPerTab()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        var shown = ShownTabs(window);
        Assert.Equal(3, shown.Count);
        Assert.Equal(tabs.Select(tab => tab.FileName), shown.Select(NameOf));
        Assert.All(shown, view => Assert.Equal("Tab", AutomationProperties.GetAutomationId(TabBorder(view))));

        // Only the active tab carries the names of the M3 tab (Tab, TabName, CloseTabButton, ...).
        Assert.Same(TabBorder(shown[2]), Find<Control>(window, "Tab"));
        Assert.Equal(tabs[2].FileName, Find<MiddleEllipsisTextBlock>(window, "TabName").Text);

        // Left to right, side by side, each as wide as the strip layout says.
        var width = Find<TabStripPanel>(window, "TabPanel").TabWidth;
        Assert.Equal(Math.Floor(TabStripLayout.Compute(3, 1024 - 52 - 36 - 140).TabWidth), width);
        for (var i = 0; i < shown.Count; i++)
        {
            Assert.Equal(i * width, shown[i].Bounds.X, 3);
            Assert.Equal(width, shown[i].Bounds.Width, 3);
        }
    }

    [AvaloniaFact]
    public async Task ClickingATabActivatesIt()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);
        Assert.Same(tabs[2], viewModel.ActiveTab);

        Click(window, BodyPoint(window, ShownTabs(window)[0]), MouseButton.Left);

        Assert.Same(tabs[0], viewModel.ActiveTab);
        Assert.Equal(tabs.Select(tab => tab.FileName), ShownTabs(window).Select(NameOf));
        Assert.Equal(tabs[0].FileName, Find<MiddleEllipsisTextBlock>(window, "TabName").Text);
        Assert.Same(TabBorder(ShownTabs(window)[0]), Find<Control>(window, "Tab"));

        // The document area follows the click.
        var view = Find<DocumentView>(window, "DocumentView");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tabs[0].Session.Document));
    }

    [AvaloniaFact]
    public async Task MiddleClickClosesATab()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        // A middle click on an inactive tab closes it and keeps the active one.
        Click(window, BodyPoint(window, ShownTabs(window)[0]), MouseButton.Middle);

        Assert.Equal([tabs[1], tabs[2]], viewModel.Tabs);
        Assert.Same(tabs[2], viewModel.ActiveTab);
        Assert.Equal(ViewerState.Empty, tabs[0].Session.State);
        Assert.Equal([tabs[1].FileName, tabs[2].FileName], ShownTabs(window).Select(NameOf));

        // The view of the closed tab is gone and lets go of its view model.
        var panel = Find<TabStripPanel>(window, "TabPanel");
        Assert.DoesNotContain(panel.Children, child => ReferenceEquals(child.DataContext, tabs[0]));

        // A middle click on the active tab closes it too.
        Click(window, BodyPoint(window, ShownTabs(window)[1]), MouseButton.Middle);
        Assert.Equal([tabs[1]], viewModel.Tabs);
        Assert.Same(tabs[1], viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task CloseButtonClosesTheTab()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        // × of an inactive tab closes that tab, not the active one.
        var closeButton = CloseButtonOf(ShownTabs(window)[1]);
        Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(closeButton));
        Assert.Equal(Strings.CloseTabTooltip, ToolTip.GetTip(closeButton));
        Click(window, CenterOf(window, closeButton), MouseButton.Left);

        Assert.Equal([tabs[0], tabs[2]], viewModel.Tabs);
        Assert.Same(tabs[2], viewModel.ActiveTab);
        Assert.Equal(ViewerState.Empty, tabs[1].Session.State);

        // × of the active tab (named CloseTabButton, tooltip with Ctrl+W) closes the active tab.
        var activeClose = Find<Button>(window, "CloseTabButton");
        Assert.Same(activeClose, CloseButtonOf(ShownTabs(window)[1]));
        Assert.Equal(Strings.CloseTab, ToolTip.GetTip(activeClose));
        Click(window, CenterOf(window, activeClose), MouseButton.Left);

        Assert.Equal([tabs[0]], viewModel.Tabs);
        Assert.Same(tabs[0], viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task RightClickOpensAMenuWithCloseForThatTab()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        // A right click does not activate the tab; its menu offers "Close". "Open in new window" needs a workspace that
        // takes the tab (WorkspaceTests.MoveToNewWindowIsOnlyOfferedWithTwoOrMoreTabs), which this window has not.
        Click(window, BodyPoint(window, ShownTabs(window)[0]), MouseButton.Right);
        Assert.Same(tabs[2], viewModel.ActiveTab);
        var close = Assert.Single(window.GetVisualDescendants().OfType<MenuItem>(), item => item.IsEffectivelyVisible);
        Assert.Equal(Strings.CloseTabMenu, close.Header);

        Click(window, CenterOf(window, close), MouseButton.Left);

        Assert.Equal([tabs[1], tabs[2]], viewModel.Tabs);
        Assert.Same(tabs[2], viewModel.ActiveTab);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<MenuItem>(), item => item.IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task ActiveTabIsMarkedBoldAndMergesIntoTheDocumentArea(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel) = Show();
        window.RequestedThemeVariant = variant;
        var tabs = await OpenTabsAsync(window, viewModel, 3);
        viewModel.ActivateTabCommand.Execute(tabs[1]);
        Settle(window);

        var shown = ShownTabs(window);
        var canvas = TokenTests.Token("Surface.Canvas", variant);
        var surfaceWindow = TokenTests.Token("Surface.Window", variant);

        // Active: the colour of the document area and bold (type.strong); inactive: no own surface, regular weight.
        Assert.Equal(canvas, Assert.IsAssignableFrom<ISolidColorBrush>(TabBorder(shown[1]).Background).Color);
        Assert.Equal(FontWeight.SemiBold, NameBlockOf(shown[1]).FontWeight);
        foreach (var inactive in new[] { shown[0], shown[2] })
        {
            Assert.True(TabBorder(inactive).Background is not ISolidColorBrush { Color.A: > 0 }, "an inactive tab has its own surface");
            Assert.Equal(FontWeight.Normal, NameBlockOf(inactive).FontWeight);
        }

        // The active tab ends where the document area starts and has its colour there: it merges into it.
        var area = Find<Control>(window, "DocumentArea");
        var activeBorder = TabBorder(shown[1]);
        var tabBottom = activeBorder.TranslatePoint(new Point(0, activeBorder.Bounds.Height), window)!.Value.Y;
        Assert.Equal(area.TranslatePoint(default, window)!.Value.Y, tabBottom, 3);

        using var frame = new FramePixels(window.CaptureRenderedFrame()!);
        var activeX = (int)CenterOf(window, activeBorder).X;
        var inactiveX = (int)BodyPoint(window, shown[0]).X - 20;
        var y = (int)tabBottom;
        Assert.Equal(canvas, frame.GetColor(activeX, y - 2));
        Assert.Equal(canvas, frame.GetColor(activeX, y + 2));
        Assert.Equal(surfaceWindow, frame.GetColor(inactiveX, y - 2));
    }

    [AvaloniaFact]
    public async Task DraggingATabReordersTheTabs()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);
        var panel = Find<TabStripPanel>(window, "TabPanel");
        var width = panel.TabWidth;
        var start = BodyPoint(window, ShownTabs(window)[0]);

        // Pressing activates the tab; a move below the threshold does not drag.
        window.MouseDown(start, MouseButton.Left);
        Settle(window);
        Assert.Same(tabs[0], viewModel.ActiveTab);
        MoveTo(window, start + new Point(TabStrip.DragThreshold - 1, 0));
        Assert.Equal(tabs, viewModel.Tabs);
        Assert.Equal(0, ShownTabs(window)[0].Bounds.X, 3);

        // Along the strip: the tab follows the pointer, and the order changes on the way.
        MoveTo(window, start + new Point(width * 0.4, 0));
        Assert.Equal(width * 0.4, TabViewOf(window, tabs[0]).Bounds.X, tolerance: 1);
        MoveTo(window, start + new Point(width * 1.2, 0));
        Assert.Equal([tabs[1], tabs[0], tabs[2]], viewModel.Tabs);
        Assert.Equal(width * 1.2, TabViewOf(window, tabs[0]).Bounds.X, tolerance: 1);

        // Up and down (out of the strip) changes nothing here: this window has no workspace that takes a loose tab
        // (TabDragGestureTests), so the tab keeps to the strip.
        MoveTo(window, start + new Point(width * 2.1, 80));
        Assert.Equal([tabs[1], tabs[2], tabs[0]], viewModel.Tabs);
        MoveTo(window, start + new Point(width * 5, 0));
        Assert.Equal(width * 2, TabViewOf(window, tabs[0]).Bounds.X, tolerance: 1);

        window.MouseUp(start + new Point(width * 5, 0), MouseButton.Left);
        Settle(window);

        // Dropped at the end: the dragged tab stays active and snaps into its place.
        Assert.Equal([tabs[1], tabs[2], tabs[0]], viewModel.Tabs);
        Assert.Same(tabs[0], viewModel.ActiveTab);
        Assert.True(tabs[0].IsActive);
        Assert.Equal([tabs[1].FileName, tabs[2].FileName, tabs[0].FileName], ShownTabs(window).Select(NameOf));
        Assert.Equal(2 * width, TabViewOf(window, tabs[0]).Bounds.X, 3);

        // Moving after the release drags nothing.
        MoveTo(window, start, RawInputModifiers.None);
        Assert.Equal([tabs[1], tabs[2], tabs[0]], viewModel.Tabs);
    }

    [AvaloniaFact]
    public async Task EscapeCancelsTheDrag()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);
        var width = Find<TabStripPanel>(window, "TabPanel").TabWidth;
        var start = BodyPoint(window, ShownTabs(window)[0]);

        window.MouseDown(start, MouseButton.Left);
        MoveTo(window, start + new Point(width * 0.5, 0));
        MoveTo(window, start + new Point(width * 2, 0));
        Assert.Equal([tabs[1], tabs[2], tabs[0]], viewModel.Tabs);

        window.KeyPress(Key.Escape, RawInputModifiers.LeftMouseButton, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.LeftMouseButton, PhysicalKey.Escape, null);
        Settle(window);

        // Back to the order before the drag, in its place; the rest of the gesture does nothing.
        Assert.Equal(tabs, viewModel.Tabs);
        Assert.Equal(0, TabViewOf(window, tabs[0]).Bounds.X, 3);
        MoveTo(window, start + new Point(width * 1.5, 0));
        window.MouseUp(start + new Point(width * 1.5, 0), MouseButton.Left);
        Settle(window);
        Assert.Equal(tabs, viewModel.Tabs);
        Assert.Same(tabs[0], viewModel.ActiveTab);
        Assert.Equal(0, TabViewOf(window, tabs[0]).Bounds.X, 3);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NarrowWindowShowsOnlyTheActiveTabAndTheAllTabsMenu(bool windowsTitleBar)
    {
        var (window, viewModel) = Show(640, windowsTitleBar);
        var tabs = await OpenTabsAsync(window, viewModel, 5);
        viewModel.ActivateTabCommand.Execute(tabs[1]);
        Settle(window);

        // 5 tabs of 120 px do not fit next to the drag area: only the active tab, then "All tabs (5)".
        var available = 640 - 52 - 36 - (windowsTitleBar ? 140 : 0);
        Assert.True(TabStripLayout.Compute(5, available).ShowOnlyActive);
        var shown = ShownTabs(window);
        var active = Assert.Single(shown);
        Assert.Same(TabBorder(active), Find<Control>(window, "Tab"));
        Assert.Equal(tabs[1].FileName, NameOf(active));
        Assert.Equal(Math.Floor(TabStripLayout.Compute(5, available).TabWidth), active.Bounds.Width, 3);
        Assert.True(active.Bounds.Width >= TabStripLayout.MinTabWidth);

        var allTabs = Find<Button>(window, "AllTabsButton");
        Assert.True(allTabs.IsEffectivelyVisible);
        Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(allTabs));
        Assert.Equal(TabStripLayout.AllTabsButtonWidth, allTabs.Bounds.Width + allTabs.Margin.Left + allTabs.Margin.Right, 3);
        Assert.Contains(allTabs.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "5");
        Assert.Equal(Strings.AllTabs.Replace("{0}", "5", StringComparison.Ordinal), AutomationProperties.GetName(allTabs));
        Assert.Equal(Strings.AllTabsTooltip.Replace("{0}", "5", StringComparison.Ordinal), ToolTip.GetTip(allTabs));

        // Everything still fits into the window, with the drag area at its minimum or more.
        foreach (var name in new[] { "Tab", "AllTabsButton", "OpenButton", "DragArea" })
        {
            var control = Find<Control>(window, name);
            Assert.True(control.IsEffectivelyVisible, $"{name} is hidden");
            var right = control.TranslatePoint(new Point(control.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= 640.5 - (windowsTitleBar ? 140 : 0), $"{name} ends at {right}");
        }

        Assert.True(Find<Control>(window, "DragArea").Bounds.Width >= TabStripLayout.MinDragArea);

        // Wide enough again: all tabs come back, the button goes.
        window.Width = 1024;
        Settle(window);
        Assert.Equal(5, ShownTabs(window).Count);
        Assert.False(allTabs.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task AllTabsMenuListsEveryTabAndActivatesTheChosenOne()
    {
        var (window, viewModel) = Show(640);
        var tabs = await OpenTabsAsync(window, viewModel, 5);
        var allTabs = Find<Button>(window, "AllTabsButton");

        Click(window, CenterOf(window, allTabs), MouseButton.Left);

        var flyout = Assert.IsType<MenuFlyout>(allTabs.Flyout);
        Assert.True(flyout.IsOpen);
        var items = flyout.Items.OfType<MenuItem>().ToList();
        Assert.Equal(tabs.Select(tab => tab.FileName), items.Select(item => item.Header as string));

        // The active tab is marked in the menu by more than colour: bold.
        Assert.Equal(FontWeight.SemiBold, items[4].FontWeight);
        Assert.All(items.Take(4), item => Assert.Equal(FontWeight.Normal, item.FontWeight));

        // A click on an entry of the menu.
        var root = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(items[2]));
        var entry = items[2].TranslatePoint(new Point(20, items[2].Bounds.Height / 2), root)!.Value;
        root.MouseDown(entry, MouseButton.Left);
        root.MouseUp(entry, MouseButton.Left);
        Settle(window);

        Assert.Same(tabs[2], viewModel.ActiveTab);
        Assert.Equal(tabs[2].FileName, NameOf(Assert.Single(ShownTabs(window))));
        Assert.False(flyout.IsOpen);

        // The menu follows the tabs: after closing one, it lists four.
        viewModel.CloseTabCommand.Execute(tabs[0]);
        Settle(window);
        Click(window, CenterOf(window, allTabs), MouseButton.Left);
        Assert.Equal(tabs.Skip(1).Select(tab => tab.FileName), flyout.Items.OfType<MenuItem>().Select(item => item.Header as string));
        flyout.Hide();
        Settle(window);

        // The closed menu lets go of its entries, and with them of the tabs (a closed tab is not kept alive).
        Assert.Empty(flyout.Items);
    }

    [AvaloniaFact]
    public async Task AllTabsButtonIsHiddenWithoutTabs()
    {
        var (window, viewModel) = Show(640);
        var tabs = await OpenTabsAsync(window, viewModel, 5);
        var allTabs = Find<Button>(window, "AllTabsButton");
        Assert.True(allTabs.IsEffectivelyVisible);

        // All tabs closed at once, before the strip measures again: no "All tabs (0)" next to the start screen.
        foreach (var tab in tabs)
        {
            viewModel.CloseTabCommand.Execute(tab);
        }

        Settle(window);
        Assert.Empty(viewModel.Tabs);
        Assert.False(allTabs.IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "OpenButton").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData(true, 640)]
    [InlineData(false, 640)]
    [InlineData(true, 1024)]
    public async Task DragAreaStaysAtLeast48PixelsWide(bool windowsTitleBar, double width)
    {
        var (window, viewModel) = Show(width, windowsTitleBar);
        var dragArea = Find<Control>(window, "DragArea");
        Assert.Equal(WindowDecorationsElementRole.TitleBar, WindowDecorationProperties.GetElementRole(dragArea));
        Assert.True(dragArea.Bounds.Width >= 48, $"no tab: drag area {dragArea.Bounds.Width} px");

        for (var count = 1; count <= 8; count++)
        {
            await OpenReadyAsync(window, viewModel, CopyWithName($"Ein recht langer Dateiname Nummer {count}.pdf"));
            Assert.True(dragArea.Bounds.Width >= 48, $"{count} tabs: drag area {dragArea.Bounds.Width} px");
            var right = dragArea.TranslatePoint(new Point(dragArea.Bounds.Width, 0), window)!.Value.X;
            Assert.True(right <= width - (windowsTitleBar ? 140 : 0) + 0.5, $"{count} tabs: drag area ends at {right}");
            Assert.All(ShownTabs(window), view => Assert.Equal(WindowDecorationsElementRole.User, WindowDecorationProperties.GetElementRole(TabBorder(view))));
        }
    }

    [AvaloniaFact]
    public async Task TabsHaveToolTipWithTheFullPath()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        var shown = ShownTabs(window);
        for (var i = 0; i < tabs.Count; i++)
        {
            Assert.Equal(tabs[i].FullPath, ToolTip.GetTip(TabBorder(shown[i])));
            Assert.Equal(tabs[i].FileName, AutomationProperties.GetName(TabBorder(shown[i])));
        }
    }

    [AvaloniaFact]
    public async Task CtrlTabSwitchesToTheNextTab()
    {
        var (window, viewModel) = Show();
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Same(tabs[0], viewModel.ActiveTab);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control);
        Assert.Same(tabs[1], viewModel.ActiveTab);
        Assert.Equal(tabs[1].FileName, Find<MiddleEllipsisTextBlock>(window, "TabName").Text);

        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Control | RawInputModifiers.Shift);
        Assert.Same(tabs[2], viewModel.ActiveTab);

        // Ctrl+W closes the active tab; Ctrl+1 stays the zoom key ("actual size"), not a tab.
        Press(window, Key.W, PhysicalKey.W, RawInputModifiers.Control);
        Assert.Equal([tabs[0], tabs[1]], viewModel.Tabs);
        Press(window, Key.D1, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.Same(tabs[1], viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task ErrorAndPasswordTabsShowTheirSymbol()
    {
        var (window, viewModel) = Show();
        await viewModel.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        var failed = viewModel.ActiveTab!;
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var locked = viewModel.ActiveTab!;
        var ready = await OpenReadyAsync(window, viewModel, TestData.Pdf("hello_world.pdf"));
        await WaitUntilAsync(window, () => failed.State == ViewerState.Failed && locked.State == ViewerState.PasswordRequired);

        // Inactive tabs show their symbol too: the warning triangle (in danger.default) and the lock.
        var shown = ShownTabs(window);
        Assert.Equal(["Pw.Icon.AlertTriangle"], ShownIcons(shown[0]));
        Assert.Equal(["Pw.Icon.Lock"], ShownIcons(shown[1]));
        Assert.Equal(["Pw.Icon.FileText"], ShownIcons(shown[2]));
        var danger = Application.Current!.FindResource(window.ActualThemeVariant, "Pw.Brush.Danger.Default");
        var triangle = Application.Current!.FindResource("Pw.Icon.AlertTriangle");
        Assert.Same(danger, shown[0].GetVisualDescendants().OfType<StrokeIcon>().Single(icon => ReferenceEquals(icon.Data, triangle)).Brush);

        // The same for the active tab, under the names of the M3 tab.
        viewModel.ActivateTabCommand.Execute(failed);
        Settle(window);
        Assert.True(Find<Control>(window, "TabErrorIcon").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "TabDocumentIcon").IsEffectivelyVisible);
        viewModel.ActivateTabCommand.Execute(locked);
        Settle(window);
        Assert.True(Find<Control>(window, "TabLockIcon").IsEffectivelyVisible);
        Assert.Equal(["Pw.Icon.FileText"], ShownIcons(ShownTabs(window)[2]));
        Assert.Same(ready, viewModel.Tabs[2]);

        // New pairs: the warning triangle on the title bar surface and on the hover surface of an inactive tab (icons 3:1).
        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            foreach (var surface in new[] { "Surface.Window", "Surface.Hover" })
            {
                var ratio = Wcag.Contrast(TokenTests.Token("Danger.Default", variant), TokenTests.Token(surface, variant));
                Assert.True(ratio >= 3, $"{variant}: danger.default on {surface} is {ratio:F2}:1");
            }
        }
    }

    [AvaloniaFact]
    public async Task LinuxLayoutShowsTheSameStripBelowTheSystemTitleBar()
    {
        var (window, viewModel) = Show(1024, windowsTitleBar: false);
        var tabs = await OpenTabsAsync(window, viewModel, 3);

        // The system title bar stays; the tab row is the first row of the client area, without caption space.
        Assert.False(window.ExtendClientAreaToDecorationsHint);
        Assert.False(Find<Control>(window, "CaptionButtonsSpace").IsEffectivelyVisible);
        Assert.Contains("linux", Find<Control>(window, "Root").Classes);
        var titleBar = Find<Control>(window, "TitleBar");
        Assert.Equal(0, titleBar.TranslatePoint(default, window)!.Value.Y);
        Assert.Equal(40, titleBar.Bounds.Height);

        // The same strip, with the width of the caption buttons for the tabs.
        var shown = ShownTabs(window);
        Assert.Equal(tabs.Select(tab => tab.FileName), shown.Select(NameOf));
        Assert.Equal(Math.Floor(TabStripLayout.Compute(3, 1024 - 52 - 36).TabWidth), shown[0].Bounds.Width, 3);
        Assert.False(Find<Button>(window, "AllTabsButton").IsEffectivelyVisible);
        Click(window, BodyPoint(window, shown[0]), MouseButton.Left);
        Assert.Same(tabs[0], viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task TheStripLetsGoOfTheTabsWhenItLeavesTheWindow()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var before = (Subscribers(viewModel.Tabs, "CollectionChanged"), Subscribers(viewModel.Tabs, "ActiveChanged"));
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 600 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var tabs = await OpenTabsAsync(window, viewModel, 2);
        var views = Find<TabStripPanel>(window, "TabPanel").Children.ToList();
        Assert.True(Subscribers(viewModel.Tabs, "CollectionChanged") > before.Item1);

        window.Content = null;
        Settle(window);

        // No event of the tab list keeps the strip alive, and no view keeps a tab.
        Assert.Equal(before, (Subscribers(viewModel.Tabs, "CollectionChanged"), Subscribers(viewModel.Tabs, "ActiveChanged")));
        Assert.All(views, view => Assert.Null(view.DataContext));
        Assert.Equal(tabs, viewModel.Tabs);
    }

    private static int Subscribers(object source, string eventName)
    {
        var field = source.GetType().GetField(eventName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No backing field for {eventName}.");
        return (field.GetValue(source) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(double width = 1024, bool windowsTitleBar = true)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = windowsTitleBar };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 600 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    /// <summary>Opens <paramref name="count"/> copies of a small test file with distinct names, each in its own tab.</summary>
    private async Task<IReadOnlyList<TabViewModel>> OpenTabsAsync(Window window, MainViewModel viewModel, int count)
    {
        var tabs = new List<TabViewModel>();
        for (var i = 0; i < count; i++)
        {
            tabs.Add(await OpenReadyAsync(window, viewModel, CopyWithName($"tab-{i}.pdf")));
        }

        Assert.Equal(tabs, viewModel.Tabs);
        return tabs;
    }

    private static async Task<TabViewModel> OpenReadyAsync(Window window, MainViewModel viewModel, string path)
    {
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready);
        return tab;
    }

    private string CopyWithName(string fileName)
    {
        _tempDirectory ??= Directory.CreateTempSubdirectory("pagewing-app-tests-").FullName;
        var path = Path.Combine(_tempDirectory, fileName);
        File.Copy(TestData.Pdf("hello_world.pdf"), path, overwrite: true);
        return path;
    }

    /// <summary>The tabs the strip shows, left to right.</summary>
    private static List<Control> ShownTabs(Window window) =>
        Find<TabStripPanel>(window, "TabPanel").Children.Where(child => child.IsVisible).OrderBy(child => child.Bounds.X).ToList();

    private static Control TabViewOf(Window window, TabViewModel tab) =>
        Find<TabStripPanel>(window, "TabPanel").Children.Single(child => child.IsVisible && ReferenceEquals(child.DataContext, tab));

    private static Border TabBorder(Control view) =>
        view.GetVisualDescendants().OfType<Border>().Single(border => AutomationProperties.GetAutomationId(border) == "Tab");

    private static MiddleEllipsisTextBlock NameBlockOf(Control view) =>
        view.GetVisualDescendants().OfType<MiddleEllipsisTextBlock>().Single();

    private static string? NameOf(Control view) => NameBlockOf(view).Text;

    private static Button CloseButtonOf(Control view) => view.GetVisualDescendants().OfType<Button>().Single();

    private static string[] ShownIcons(Control view)
    {
        string[] keys = ["Pw.Icon.FileText", "Pw.Icon.Lock", "Pw.Icon.AlertTriangle", "Pw.Icon.X"];
        return view.GetVisualDescendants().OfType<StrokeIcon>()
            .Where(icon => icon.IsEffectivelyVisible)
            .Select(icon => keys.Single(key => ReferenceEquals(Application.Current!.FindResource(key), icon.Data)))
            .Where(key => key != "Pw.Icon.X")
            .ToArray();
    }

    /// <summary>A point on the tab away from its close button: over the symbol and the start of the name.</summary>
    private static Point BodyPoint(Window window, Control view) =>
        view.TranslatePoint(new Point(30, view.Bounds.Height - 17), window)!.Value;

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Point point, MouseButton button)
    {
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Settle(window);
    }

    private static void MoveTo(Window window, Point point, RawInputModifiers modifiers = RawInputModifiers.LeftMouseButton)
    {
        window.MouseMove(point, modifiers);
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
