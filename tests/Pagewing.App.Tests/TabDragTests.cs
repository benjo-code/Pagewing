using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
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
public sealed class TabDragTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly PdfEngine _engine = new();
    private int _opens;

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task BeginningADragWithTwoTabsOpensAFloatingWindowAtThePointer()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var session = tab.Session;
        var document = session.Document;
        var opens = Volatile.Read(ref _opens);
        var sourceSize = workspace.WindowOf(first).ClientSize;

        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(500, 300), new PixelPoint(60, 16));
        Settle(workspace);

        Assert.NotNull(drag);
        Assert.False(drag.MovesWholeWindow);
        Assert.False(drag.IsEnded);
        Assert.Equal(2, workspace.Windows.Count);
        Assert.Same(drag.Floating, workspace.Windows[1]);
        Assert.Same(tab, Assert.Single(drag.Floating.Tabs));
        Assert.Same(tab, drag.Floating.ActiveTab);
        Assert.Same(session, tab.Session);
        Assert.Same(document, session.Document);
        Assert.Equal(ViewerState.Ready, tab.State);
        Assert.Equal(opens, Volatile.Read(ref _opens));
        var window = workspace.WindowOf(drag.Floating);
        Assert.Equal(sourceSize, window.ClientSize);
        Assert.Equal(new PixelPoint(440, 284), window.Position);
        Assert.Single(first.Tabs);
    }

    [AvaloniaFact]
    public async Task MovingTheDragMovesTheFloatingWindowWithThePointer()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(500, 300), new PixelPoint(60, 16))!;

        drag.MoveTo(new PixelPoint(600, 350));

        Assert.Equal(new PixelPoint(540, 334), workspace.WindowOf(drag.Floating).Position);
    }

    [AvaloniaFact]
    public async Task BeginningADragWithOneTabMovesTheWholeWindow()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        window.Position = new PixelPoint(100, 50);
        var tab = await OpenReadyAsync(workspace, first, "hello_world.pdf");

        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(160, 66), new PixelPoint(1, 1));
        Assert.NotNull(drag);
        drag.MoveTo(new PixelPoint(260, 166));

        Assert.True(drag.MovesWholeWindow);
        Assert.Same(first, drag.Floating);
        Assert.Single(workspace.Windows);
        Assert.Same(tab, Assert.Single(first.Tabs));
        Assert.Equal(new PixelPoint(200, 150), window.Position);
    }

    [AvaloniaFact]
    public async Task CancelPutsTheTabBackAtItsPlace()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var a = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var b = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var c = await OpenReadyAsync(workspace, first, "vector-shapes.pdf");
        var session = b.Session;
        var drag = workspace.BeginTabDrag(first, b, new PixelPoint(500, 300), new PixelPoint(60, 16))!;
        drag.MoveTo(new PixelPoint(700, 400));
        Assert.Equal([a, c], first.Tabs);

        drag.Cancel();
        Settle(workspace);

        Assert.True(drag.IsEnded);
        Assert.Equal([a, b, c], first.Tabs);
        Assert.Same(b, first.ActiveTab);
        Assert.True(b.IsActive);
        Assert.Single(workspace.Windows);
        Assert.Same(session, b.Session);
        Assert.Equal(ViewerState.Ready, b.Session.State);
    }

    [AvaloniaFact]
    public async Task CancelPutsAMovedWindowBack()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        window.Position = new PixelPoint(100, 50);
        var tab = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(160, 66), new PixelPoint(1, 1))!;
        drag.MoveTo(new PixelPoint(900, 700));
        Assert.NotEqual(new PixelPoint(100, 50), window.Position);

        drag.Cancel();

        Assert.Equal(new PixelPoint(100, 50), window.Position);
        Assert.Same(tab, Assert.Single(first.Tabs));
        Assert.True(drag.IsEnded);
    }

    [AvaloniaFact]
    public async Task CompleteWithoutATargetKeepsTheNewWindowAndActivatesIt()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(500, 300), new PixelPoint(60, 16))!;

        drag.Complete(new PixelPoint(620, 360));
        Settle(workspace);

        Assert.True(drag.IsEnded);
        Assert.Equal(2, workspace.Windows.Count);
        Assert.Same(tab, Assert.Single(drag.Floating.Tabs));
        Assert.Equal(new PixelPoint(560, 344), workspace.WindowOf(drag.Floating).Position);
        Assert.Same(drag.Floating, workspace.ActiveWindow);

        // A finished drag does nothing more, and a new one can start.
        drag.MoveTo(new PixelPoint(0, 0));
        drag.Cancel();
        Assert.Equal(new PixelPoint(560, 344), workspace.WindowOf(drag.Floating).Position);
        Assert.Same(tab, Assert.Single(drag.Floating.Tabs));
        Assert.NotNull(workspace.BeginTabDrag(drag.Floating, tab, new PixelPoint(10, 10), new PixelPoint(1, 1)));
    }

    [AvaloniaFact]
    public async Task BeginningADragForAnUnknownTabDoesNothing()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var stranger = new TabViewModel(new ViewerSession(new PdfEngine()));
        _cleanup.Add(stranger);
        var pointer = new PixelPoint(500, 300);
        var grip = new PixelPoint(60, 16);

        Assert.Null(workspace.BeginTabDrag(first, stranger, pointer, grip));
        using var outsider = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker());
        Assert.Null(workspace.BeginTabDrag(outsider, tab, pointer, grip));
        Assert.Single(workspace.Windows);
        Assert.Equal(2, first.Tabs.Count);

        // One drag at a time.
        var drag = workspace.BeginTabDrag(first, tab, pointer, grip);
        Assert.NotNull(drag);
        Assert.Null(workspace.BeginTabDrag(first, first.Tabs[0], pointer, grip));
        drag.Cancel();
        Settle(workspace);
        Assert.NotNull(workspace.BeginTabDrag(first, tab, pointer, grip));
    }

    [AvaloniaFact]
    public async Task ADragSurvivesAFloatingWindowThatIsClosed()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(500, 300), new PixelPoint(60, 16))!;
        workspace.WindowOf(drag.Floating).Close();
        Settle(workspace);

        // The tab went with its window and is released once; the drag ends without a trace.
        Assert.Single(workspace.Windows);
        Assert.Equal(ViewerState.Empty, tab.Session.State);
        var exception = Record.Exception(() =>
        {
            drag.MoveTo(new PixelPoint(10, 10));
            drag.Complete(new PixelPoint(10, 10));
            drag.Cancel();
        });
        Assert.Null(exception);
        Assert.True(drag.IsEnded);
        Assert.Single(first.Tabs);

        // The workspace is free for the next drag.
        var other = await OpenReadyAsync(workspace, first, "vector-shapes.pdf");
        Assert.NotNull(workspace.BeginTabDrag(first, other, new PixelPoint(500, 300), new PixelPoint(60, 16)));
    }

    [AvaloniaTheory]
    [InlineData("hello_world.pdf", ViewerState.Ready)]
    [InlineData("encrypted_hello_world_r3.pdf", ViewerState.PasswordRequired)]
    [InlineData("not-a-pdf.pdf", ViewerState.Failed)]
    public async Task AnyTabStateCanBeDragged(string name, ViewerState state)
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "text-basic.pdf");
        await first.OpenAsync(TestData.Pdf(name));
        var tab = first.ActiveTab!;
        await WaitUntilAsync(workspace, () => tab.State == state);
        var session = tab.Session;

        var drag = workspace.BeginTabDrag(first, tab, new PixelPoint(500, 300), new PixelPoint(60, 16))!;
        drag.Complete(new PixelPoint(520, 600));
        Settle(workspace);

        Assert.Same(tab, Assert.Single(drag.Floating.Tabs));
        Assert.Equal(state, tab.State);
        Assert.Same(session, tab.Session);
        Assert.True(tab.IsActive);
    }

    [AvaloniaFact]
    public async Task HoveringAnotherStripShowsTheMarkerAndParksTheFloatingWindow()
    {
        var (workspace, a, b) = await TwoWindowsAsync(2, 2);
        var drag = workspace.BeginTabDrag(a.ViewModel, a.Tabs[1], new PixelPoint(300, 20), new PixelPoint(60, 16))!;
        var floating = workspace.WindowOf(drag.Floating);
        Settle(workspace);

        drag.MoveTo(StripPoint(b.Window, 2));

        Assert.Equal(2, b.ViewModel.TabDropIndex);
        Assert.Null(a.ViewModel.TabDropIndex);
        Assert.Null(drag.Floating.TabDropIndex);
        Assert.True(drag.IsParked);
        Assert.Equal(TabDrag.ParkedPosition, floating.Position);

        drag.MoveTo(new PixelPoint(6000, 5000));

        Assert.Null(b.ViewModel.TabDropIndex);
        Assert.False(drag.IsParked);
        Assert.Equal(new PixelPoint(5940, 4984), floating.Position);

        drag.Cancel();
        Assert.Null(b.ViewModel.TabDropIndex);
    }

    [AvaloniaFact]
    public async Task DroppingOnAStripMovesTheTabWithItsSession()
    {
        var (workspace, a, b) = await TwoWindowsAsync(2, 2);
        var moved = a.Tabs[1];
        var session = moved.Session;
        var document = session.Document;
        var opens = Volatile.Read(ref _opens);
        var drag = workspace.BeginTabDrag(a.ViewModel, moved, new PixelPoint(300, 20), new PixelPoint(60, 16))!;
        Settle(workspace);

        drag.Complete(StripPoint(b.Window, 1));
        Settle(workspace);

        Assert.True(drag.IsEnded);
        Assert.Equal([b.Tabs[0], moved, b.Tabs[1]], b.ViewModel.Tabs);
        Assert.Same(moved, b.ViewModel.ActiveTab);
        Assert.True(moved.IsActive);
        Assert.Same(session, moved.Session);
        Assert.Same(document, session.Document);
        Assert.Equal(ViewerState.Ready, moved.State);
        Assert.Equal(opens, Volatile.Read(ref _opens));
        Assert.Equal([a.ViewModel, b.ViewModel], workspace.Windows);
        Assert.Same(a.Tabs[0], Assert.Single(a.ViewModel.Tabs));
        Assert.Null(b.ViewModel.TabDropIndex);
        Assert.Same(b.ViewModel, workspace.ActiveWindow);
    }

    [AvaloniaFact]
    public async Task DroppingBackOnTheOwnStripPutsTheTabAtTheChosenPlace()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        var a1 = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var a2 = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var a3 = await OpenReadyAsync(workspace, first, "vector-shapes.pdf");
        var drag = workspace.BeginTabDrag(first, a2, new PixelPoint(300, 20), new PixelPoint(60, 16))!;
        Settle(workspace);

        drag.Complete(StripPoint(window, 0));
        Settle(workspace);

        Assert.Equal([a2, a1, a3], first.Tabs);
        Assert.Same(a2, first.ActiveTab);
        Assert.Single(workspace.Windows);
    }

    [AvaloniaFact]
    public async Task DroppingTheOnlyTabOfAWindowMergesAndClosesTheWindow()
    {
        var (workspace, a, b) = await TwoWindowsAsync(1, 1);
        var moved = b.Tabs[0];
        var drag = workspace.BeginTabDrag(b.ViewModel, moved, new PixelPoint(2300, 20), new PixelPoint(60, 16))!;
        Assert.True(drag.MovesWholeWindow);

        drag.MoveTo(StripPoint(a.Window, 1));
        Assert.True(drag.IsParked);
        drag.Complete(StripPoint(a.Window, 1));
        Settle(workspace);

        Assert.Equal([a.Tabs[0], moved], a.ViewModel.Tabs);
        Assert.Same(moved, a.ViewModel.ActiveTab);
        Assert.Same(a.ViewModel, Assert.Single(workspace.Windows));
        Assert.Equal(ViewerState.Ready, moved.Session.State);
    }

    [AvaloniaFact]
    public async Task DroppingTheOnlyTabOfTheOldestWindowClosesItAndTheAppGoesOn()
    {
        var (workspace, a, b) = await TwoWindowsAsync(1, 2);
        var moved = a.Tabs[0];
        var drag = workspace.BeginTabDrag(a.ViewModel, moved, new PixelPoint(300, 20), new PixelPoint(60, 16))!;

        drag.Complete(StripPoint(b.Window, 0));
        Settle(workspace);

        Assert.Same(b.ViewModel, Assert.Single(workspace.Windows));
        Assert.Equal(3, b.ViewModel.Tabs.Count);
        Assert.Same(moved, b.ViewModel.Tabs[0]);
        Assert.False(b.ViewModel.CloseWindowWhenEmpty);
        Assert.True(b.Window.IsVisible);
        Assert.Equal(ViewerState.Ready, moved.Session.State);
    }

    [AvaloniaFact]
    public async Task DroppingOnAWindowWithoutTabsOpensTheTabThere()
    {
        var (workspace, a, _) = await TwoWindowsAsync(2, 0);
        var empty = workspace.Windows[1];
        var window = workspace.WindowOf(empty);
        Assert.True(empty.IsEmpty);
        var moved = a.Tabs[1];
        var drag = workspace.BeginTabDrag(a.ViewModel, moved, new PixelPoint(300, 20), new PixelPoint(60, 16))!;
        Settle(workspace);

        drag.MoveTo(window.PointToScreen(new Point(300, 20)));
        Assert.Equal(0, empty.TabDropIndex);
        drag.Complete(window.PointToScreen(new Point(300, 20)));
        Settle(workspace);

        Assert.Same(moved, Assert.Single(empty.Tabs));
        Assert.Same(moved, empty.ActiveTab);
        Assert.False(empty.IsEmpty);
        Assert.Equal(ViewerState.Ready, moved.Session.State);
    }

    [AvaloniaFact]
    public async Task AStripWithOverflowTakesTheTabAtTheEnd()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var one = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var two = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var second = workspace.OpenWindow();
        var window = workspace.WindowOf(second);
        window.Position = new PixelPoint(2000, 0);
        window.Width = 640;
        foreach (var name in new[] { "vector-shapes.pdf", "transparency.pdf", "annotations.pdf", "cropbox.pdf", "wide-page.pdf" })
        {
            await OpenReadyAsync(workspace, second, name);
        }

        Settle(workspace);
        Assert.True(TabStripLayout.Compute(5, 640 - 52 - 36 - 140).ShowOnlyActive);
        var drag = workspace.BeginTabDrag(first, two, new PixelPoint(300, 20), new PixelPoint(60, 16))!;

        drag.MoveTo(window.PointToScreen(new Point(300, 20)));
        Assert.Equal(5, second.TabDropIndex);
        drag.Complete(window.PointToScreen(new Point(300, 20)));
        Settle(workspace);

        Assert.Equal(6, second.Tabs.Count);
        Assert.Same(two, second.Tabs[5]);
        Assert.Same(two, second.ActiveTab);
        Assert.Same(one, Assert.Single(first.Tabs));
    }

    [AvaloniaFact]
    public async Task OverlappingStripsPreferTheActiveWindow()
    {
        var (workspace, a, b) = await TwoWindowsAsync(2, 2);
        b.Window.Position = a.Window.Position;
        var drag = workspace.BeginTabDrag(a.ViewModel, a.Tabs[1], new PixelPoint(300, 20), new PixelPoint(60, 16))!;
        var point = a.Window.PointToScreen(new Point(300, 20));

        b.Window.Activate();
        Settle(workspace);
        Assert.Same(b.ViewModel, workspace.ActiveWindow);
        drag.MoveTo(point);
        Assert.NotNull(b.ViewModel.TabDropIndex);
        Assert.Null(a.ViewModel.TabDropIndex);

        a.Window.Activate();
        Settle(workspace);
        Assert.Same(a.ViewModel, workspace.ActiveWindow);
        drag.MoveTo(point);
        Assert.NotNull(a.ViewModel.TabDropIndex);
        Assert.Null(b.ViewModel.TabDropIndex);
        drag.Cancel();
    }

    [AvaloniaFact]
    public async Task DroppingATabWhoseFileIsAlreadyOpenInTheTargetKeepsBoth()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var moved = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var second = workspace.OpenWindow();
        var window = workspace.WindowOf(second);
        window.Position = new PixelPoint(2000, 0);
        var already = await OpenReadyAsync(workspace, second, "hello_world.pdf");
        Settle(workspace);
        var drag = workspace.BeginTabDrag(first, moved, new PixelPoint(300, 20), new PixelPoint(60, 16))!;

        drag.Complete(StripPoint(window, 1));
        Settle(workspace);

        Assert.Equal([already, moved], second.Tabs);
        Assert.NotSame(already.Session, moved.Session);
        Assert.Equal(ViewerState.Ready, already.State);
        Assert.Equal(ViewerState.Ready, moved.State);
    }

    [AvaloniaFact]
    public async Task NoTargetForAHiddenOrMinimizedWindow()
    {
        var (workspace, _, b) = await TwoWindowsAsync(1, 1);
        var point = b.Window.PointToScreen(new Point(300, 20));
        Assert.NotNull(b.Window.TabDropIndexAt(point));

        b.Window.WindowState = WindowState.Minimized;
        Assert.Null(b.Window.TabDropIndexAt(point));
        b.Window.WindowState = WindowState.Normal;
        Assert.NotNull(b.Window.TabDropIndexAt(point));

        b.Window.Hide();
        Assert.Null(b.Window.TabDropIndexAt(point));
        Settle(workspace);
    }

    /// <summary>
    /// Two windows with the given numbers of tabs (named in order from a fixed list); the second lies far to the right
    /// on the screen, so their strips do not overlap.
    /// </summary>
    private async Task<(Workspace Workspace, WindowOf A, WindowOf B)> TwoWindowsAsync(int tabsInFirst, int tabsInSecond)
    {
        string[] names = ["hello_world.pdf", "text-basic.pdf", "vector-shapes.pdf", "transparency.pdf"];
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var second = workspace.OpenWindow();
        var secondWindow = workspace.WindowOf(second);
        secondWindow.Position = new PixelPoint(2000, 0);
        var firstTabs = new List<TabViewModel>();
        var secondTabs = new List<TabViewModel>();
        for (var i = 0; i < tabsInFirst; i++)
        {
            firstTabs.Add(await OpenReadyAsync(workspace, first, names[i]));
        }

        for (var i = 0; i < tabsInSecond; i++)
        {
            secondTabs.Add(await OpenReadyAsync(workspace, second, names[i + 2]));
        }

        Settle(workspace);
        return (workspace, new WindowOf(first, (MainWindow)workspace.WindowOf(first), firstTabs), new WindowOf(second, (MainWindow)secondWindow, secondTabs));
    }

    private sealed record WindowOf(MainViewModel ViewModel, MainWindow Window, IReadOnlyList<TabViewModel> Tabs);

    /// <summary>The point on the screen where a tab dropped there takes position <paramref name="index"/> in the strip of <paramref name="window"/>.</summary>
    private static PixelPoint StripPoint(Window window, int index)
    {
        var panel = window.GetVisualDescendants().OfType<TabStripPanel>().Single();
        var inWindow = panel.TranslatePoint(new Point(index * panel.TabWidth, 20), window)!.Value;
        return window.PointToScreen(inWindow);
    }

    private Workspace CreateWorkspace()
    {
        // The windows do not activate when they are shown, so only the tests activate them.
        var workspace = new Workspace(
            () => new ViewerSession((path, password, cancellationToken) =>
            {
                Interlocked.Increment(ref _opens);
                return _engine.OpenAsync(path, password, cancellationToken);
            }),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => new NoFilePicker());
        _cleanup.Add(workspace);
        return workspace;
    }

    /// <summary>Opens a test file in a new tab of <paramref name="viewModel"/> and waits until it is ready.</summary>
    private static async Task<TabViewModel> OpenReadyAsync(Workspace workspace, MainViewModel viewModel, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(workspace, () => tab.State == ViewerState.Ready);
        return tab;
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
}
