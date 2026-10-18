using System.Globalization;
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
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class TabDragGestureTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string[] Names = ["hello_world.pdf", "text-basic.pdf", "vector-shapes.pdf", "transparency.pdf", "annotations.pdf", "cropbox.pdf"];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly PdfEngine _engine = new();

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task PullingATabDownOutOfTheStripOpensAWindowThatFollowsThePointer()
    {
        var (workspace, first) = await WindowWithTabsAsync(3);
        var window = workspace.WindowOf(first);
        var tab = first.Tabs[1];
        var session = tab.Session;
        var start = BodyPoint(window, tab);

        window.MouseDown(start, MouseButton.Left);
        Settle(workspace);
        MoveTo(workspace, window, start + new Point(0, 10));
        Assert.Single(workspace.Windows);

        MoveTo(workspace, window, start + new Point(0, 45));
        Assert.Equal(2, workspace.Windows.Count);
        var floating = workspace.Windows[1];
        Assert.Same(tab, Assert.Single(floating.Tabs));
        var floatingWindow = workspace.WindowOf(floating);
        var before = floatingWindow.Position;

        MoveTo(workspace, window, start + new Point(200, 105));
        Assert.Equal(new PixelPoint(before.X + 200, before.Y + 60), floatingWindow.Position);

        window.MouseUp(start + new Point(200, 105), MouseButton.Left);
        Settle(workspace);

        Assert.Equal(2, workspace.Windows.Count);
        Assert.Same(tab, Assert.Single(floating.Tabs));
        Assert.Same(session, tab.Session);
        Assert.Equal(ViewerState.Ready, tab.State);
        Assert.Equal(2, first.Tabs.Count);
        Assert.DoesNotContain(tab, first.Tabs);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TabView>(), view => ReferenceEquals(view.DataContext, tab));
        Assert.Same(floating, workspace.ActiveWindow);

        // The rest of the gesture is over: moving on drags nothing.
        MoveTo(workspace, window, start, RawInputModifiers.None);
        Assert.Equal(before.X + 200, floatingWindow.Position.X);
    }

    [AvaloniaFact]
    public async Task PullingBelowTheThresholdOnlyReorders()
    {
        var (workspace, first) = await WindowWithTabsAsync(3);
        var window = workspace.WindowOf(first);
        var tabs = first.Tabs.ToList();
        var width = Find<TabStripPanel>(window, "TabPanel").TabWidth;
        var start = BodyPoint(window, tabs[0]);

        window.MouseDown(start, MouseButton.Left);
        MoveTo(workspace, window, start + new Point(width * 1.2, TabDragMath.DetachDistance - 1));
        window.MouseUp(start + new Point(width * 1.2, TabDragMath.DetachDistance - 1), MouseButton.Left);
        Settle(workspace);

        Assert.Single(workspace.Windows);
        Assert.Equal([tabs[1], tabs[0], tabs[2]], first.Tabs);
    }

    [AvaloniaFact]
    public async Task ReleasingOverAnotherWindowsStripInsertsTheTab()
    {
        var (workspace, first, second) = await TwoWindowsAsync(3, 2);
        var window = workspace.WindowOf(first);
        var secondWindow = workspace.WindowOf(second);
        var moved = first.Tabs[0];
        var session = moved.Session;
        var start = BodyPoint(window, moved);
        window.MouseDown(start, MouseButton.Left);
        MoveTo(workspace, window, start + new Point(0, 45));
        var over = window.PointToClient(StripPoint(secondWindow, 1));

        MoveTo(workspace, window, over);

        Assert.Equal(1, second.TabDropIndex);
        var panel = Find<TabStripPanel>(secondWindow, "TabPanel");
        var marker = Find<Border>(secondWindow, "DropMarker");
        Assert.True(marker.IsVisible);
        Assert.Equal(panel.TranslatePoint(new Point(panel.TabWidth, 0), secondWindow)!.Value.X - 1, marker.TranslatePoint(default, secondWindow)!.Value.X, 1.0);

        window.MouseUp(over, MouseButton.Left);
        Settle(workspace);

        Assert.Equal([second.Tabs[0], moved, second.Tabs[2]], second.Tabs);
        Assert.Equal(3, second.Tabs.Count);
        Assert.Same(moved, second.ActiveTab);
        Assert.Same(session, moved.Session);
        Assert.False(marker.IsVisible);
        Assert.Null(second.TabDropIndex);
        Assert.Equal([first, second], workspace.Windows);
        Assert.Equal(2, first.Tabs.Count);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task TheMarkerIsAStripOfTwoPixelsAndHiddenOtherwise(string theme)
    {
        var (workspace, first) = await WindowWithTabsAsync(2);
        var window = workspace.WindowOf(first);
        window.RequestedThemeVariant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var marker = Find<Border>(window, "DropMarker");
        Settle(workspace);
        Assert.False(marker.IsVisible);

        first.TabDropIndex = 1;
        Settle(workspace);

        Assert.True(marker.IsVisible);
        Assert.Equal(2, marker.Width);
        Assert.False(marker.IsHitTestVisible);
        Assert.Same(Application.Current!.FindResource(window.ActualThemeVariant, "Pw.Brush.Accent.Default"), marker.Background);

        first.TabDropIndex = null;
        Settle(workspace);
        Assert.False(marker.IsVisible);
    }

    [AvaloniaFact]
    public async Task EscapeDuringAFloatingDragPutsTheTabBack()
    {
        var (workspace, first) = await WindowWithTabsAsync(3);
        var window = workspace.WindowOf(first);
        var tabs = first.Tabs.ToList();
        var start = BodyPoint(window, tabs[1]);
        window.MouseDown(start, MouseButton.Left);
        MoveTo(workspace, window, start + new Point(0, 45));
        MoveTo(workspace, window, start + new Point(100, 100));
        Assert.Equal(2, workspace.Windows.Count);

        // The keyboard is with the new window (on Windows it ended up there, too, whatever ShowActivated says): Esc has to
        // cancel from there as well as from the window of the strip that leads the drag.
        window.KeyPress(Key.Escape, RawInputModifiers.LeftMouseButton, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.LeftMouseButton, PhysicalKey.Escape, null);
        Settle(workspace);

        Assert.Single(workspace.Windows);
        Assert.Equal(tabs, first.Tabs);
        Assert.Same(tabs[1], first.ActiveTab);
        Assert.Equal(ViewerState.Ready, tabs[1].State);

        // The rest of the gesture does nothing.
        MoveTo(workspace, window, start + new Point(300, 200));
        window.MouseUp(start + new Point(300, 200), MouseButton.Left);
        Settle(workspace);
        Assert.Single(workspace.Windows);
        Assert.Equal(tabs, first.Tabs);
    }

    [AvaloniaFact]
    public async Task DraggingTheOnlyTabMovesTheWindowAndMergesOverAnotherStrip()
    {
        var (workspace, first, second) = await TwoWindowsAsync(1, 2);
        var window = workspace.WindowOf(first);
        var secondWindow = workspace.WindowOf(second);
        var tab = first.Tabs[0];
        var origin = window.Position;
        var start = BodyPoint(window, tab);
        var startScreen = window.PointToScreen(start);
        window.MouseDown(start, MouseButton.Left);
        Settle(workspace);

        // The window follows the pointer from where the tab came loose (45 down); no new window.
        MovePointer(workspace, window, new PixelPoint(startScreen.X, startScreen.Y + 45));
        MovePointer(workspace, window, new PixelPoint(startScreen.X + 200, startScreen.Y + 100));
        Assert.Contains(first, workspace.Windows);
        Assert.Equal(2, workspace.Windows.Count);
        Assert.Equal(new PixelPoint(origin.X + 200, origin.Y + 55), window.Position);

        // Over a strip of another window it steps aside, and the release merges the tabs.
        var target = StripPoint(secondWindow, 2);
        MovePointer(workspace, window, target);
        Assert.Equal(2, second.TabDropIndex);
        window.MouseUp(window.PointToClient(target), MouseButton.Left);
        Settle(workspace);

        Assert.Same(second, Assert.Single(workspace.Windows));
        Assert.Equal(3, second.Tabs.Count);
        Assert.Same(tab, second.Tabs[2]);
        Assert.Equal(ViewerState.Ready, tab.State);
    }

    [AvaloniaFact]
    public async Task AStripWithOnlyTheActiveTabShownDoesNotReorderButLetsTheTabOut()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        window.Width = 640;
        for (var i = 0; i < 5; i++)
        {
            await OpenReadyAsync(workspace, first, Names[i]);
        }

        Settle(workspace);
        Assert.True(Find<TabStripPanel>(window, "TabPanel").ShowOnlyActive);
        var tabs = first.Tabs.ToList();
        var active = first.ActiveTab!;
        var start = BodyPoint(window, active);

        window.MouseDown(start, MouseButton.Left);
        MoveTo(workspace, window, start + new Point(200, 5));
        window.MouseUp(start + new Point(200, 5), MouseButton.Left);
        Settle(workspace);
        Assert.Single(workspace.Windows);
        Assert.Equal(tabs, first.Tabs);

        window.MouseDown(start, MouseButton.Left);
        MoveTo(workspace, window, start + new Point(0, 50));
        window.MouseUp(start + new Point(0, 50), MouseButton.Left);
        Settle(workspace);
        Assert.Equal(2, workspace.Windows.Count);
        Assert.Same(active, Assert.Single(workspace.Windows[1].Tabs));
        Assert.Equal(4, first.Tabs.Count);
    }

    [AvaloniaFact]
    public async Task ReleaseAfterTheFloatingWindowWasClosedDoesNotThrow()
    {
        var (workspace, first) = await WindowWithTabsAsync(3);
        var window = workspace.WindowOf(first);
        var start = BodyPoint(window, first.Tabs[1]);
        window.MouseDown(start, MouseButton.Left);
        MoveTo(workspace, window, start + new Point(0, 45));
        workspace.WindowOf(workspace.Windows[1]).Close();
        Settle(workspace);

        var exception = Record.Exception(() =>
        {
            MoveTo(workspace, window, start + new Point(50, 90));
            window.MouseUp(start + new Point(50, 90), MouseButton.Left);
            Settle(workspace);
        });

        Assert.Null(exception);
        Assert.Single(workspace.Windows);
        Assert.Equal(2, first.Tabs.Count);

        // The strip is usable again.
        var other = BodyPoint(window, first.Tabs[0]);
        window.MouseDown(other, MouseButton.Left);
        window.MouseUp(other, MouseButton.Left);
        Settle(workspace);
        Assert.Same(first.Tabs[0], first.ActiveTab);
    }

    private async Task<(Workspace Workspace, MainViewModel First)> WindowWithTabsAsync(int count)
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        for (var i = 0; i < count; i++)
        {
            await OpenReadyAsync(workspace, first, Names[i]);
        }

        Settle(workspace);
        return (workspace, first);
    }

    /// <summary>Two windows; the second lies far to the right on the screen, so the strips do not overlap.</summary>
    private async Task<(Workspace Workspace, MainViewModel First, MainViewModel Second)> TwoWindowsAsync(int tabsInFirst, int tabsInSecond)
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var second = workspace.OpenWindow();
        workspace.WindowOf(second).Position = new PixelPoint(2000, 0);
        for (var i = 0; i < tabsInFirst; i++)
        {
            await OpenReadyAsync(workspace, first, Names[i]);
        }

        for (var i = 0; i < tabsInSecond; i++)
        {
            await OpenReadyAsync(workspace, second, Names[i + 3]);
        }

        Settle(workspace);
        return (workspace, first, second);
    }

    private Workspace CreateWorkspace()
    {
        // The windows do not activate when they are shown, so only the tests activate them.
        var workspace = new Workspace(
            () => new ViewerSession(_engine.OpenAsync),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => new NoFilePicker());
        _cleanup.Add(workspace);
        return workspace;
    }

    private static async Task<TabViewModel> OpenReadyAsync(Workspace workspace, MainViewModel viewModel, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(workspace, () => tab.State == ViewerState.Ready);
        return tab;
    }

    /// <summary>A point on the tab away from its close button, in the coordinates of its window.</summary>
    private static Point BodyPoint(Window window, TabViewModel tab)
    {
        var view = Find<TabStripPanel>(window, "TabPanel").Children.Single(child => child.IsVisible && ReferenceEquals(child.DataContext, tab));
        return view.TranslatePoint(new Point(30, view.Bounds.Height - 17), window)!.Value;
    }

    /// <summary>The point on the screen where a tab dropped there takes position <paramref name="index"/> in the strip of <paramref name="window"/>.</summary>
    private static PixelPoint StripPoint(Window window, int index)
    {
        var panel = Find<TabStripPanel>(window, "TabPanel");
        return window.PointToScreen(panel.TranslatePoint(new Point(index * panel.TabWidth, 20), window)!.Value);
    }

    private static void MoveTo(Workspace workspace, Window window, Point point, RawInputModifiers modifiers = RawInputModifiers.LeftMouseButton)
    {
        window.MouseMove(point, modifiers);
        Settle(workspace);
    }

    /// <summary>Moves the pointer to a point on the screen, as the system reports it relative to a window that may have moved.</summary>
    private static void MovePointer(Workspace workspace, Window window, PixelPoint screen) =>
        MoveTo(workspace, window, window.PointToClient(screen));

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
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");
}
