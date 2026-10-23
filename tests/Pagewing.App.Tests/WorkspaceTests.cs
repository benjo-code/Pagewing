using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
public sealed class WorkspaceTests : IDisposable
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
    public async Task MovingATabToANewWindowKeepsItsSession()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var session = tab.Session;
        var document = session.Document;
        var opens = Volatile.Read(ref _opens);

        workspace.MoveToNewWindow(first, tab);
        Settle(workspace);

        // The same tab with the same session and document, the only and active tab of a new window; nothing opened again.
        Assert.Equal(2, workspace.Windows.Count);
        var second = workspace.Windows[1];
        Assert.Same(tab, Assert.Single(second.Tabs));
        Assert.Same(tab, second.ActiveTab);
        Assert.Same(session, tab.Session);
        Assert.Same(document, session.Document);
        Assert.Equal(ViewerState.Ready, tab.State);
        Assert.Equal(ViewerState.Ready, session.State);
        Assert.True(tab.IsActive);
        Assert.True(session.IsActive);
        Assert.Equal(opens, Volatile.Read(ref _opens));

        // The new window shows the document; it closes with its last tab, the first one does not.
        var view = Find<DocumentView>(workspace.WindowOf(second), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(view.Document, document));
        Assert.True(workspace.WindowOf(second).IsVisible);
        Assert.True(second.CloseWindowWhenEmpty);
        Assert.False(first.CloseWindowWhenEmpty);
        Assert.Equal(opens, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public async Task MovedTabKeepsPageAndZoom()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var other = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "thousand-pages.pdf");
        var firstView = Find<DocumentView>(workspace.WindowOf(first), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(firstView.Document, tab.Session.Document));
        tab.Zoom = 2;
        Settle(workspace);
        tab.GoToPage(40);
        Settle(workspace);
        Assert.Equal(40, tab.CurrentPage);

        workspace.MoveToNewWindow(first, tab);
        Settle(workspace);

        // The new window shows the tab at 200 % on page 40; the source window shows its other tab.
        var second = workspace.Windows[1];
        var view = Find<DocumentView>(workspace.WindowOf(second), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(view.Document, tab.Session.Document));
        Assert.Equal(2.0, tab.Zoom);
        Assert.Equal(2.0, view.Layout!.Zoom);
        Assert.Equal(40, tab.CurrentPage);
        Assert.Equal(tab.ZoomText, Find<TextBlock>(workspace.WindowOf(second), "ZoomValueText").Text);
        await WaitUntilAsync(workspace, () => ReferenceEquals(firstView.Document, other.Session.Document));
        Assert.Same(other, first.ActiveTab);
    }

    [AvaloniaFact]
    public async Task MovedTabKeepsItsHorizontalPosition()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "thousand-pages.pdf");
        var firstView = Find<DocumentView>(workspace.WindowOf(first), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(firstView.Document, tab.Session.Document));

        // "Pages" chosen by the user, so neither window opens it on its own later (which would narrow the viewport).
        first.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);

        // Zoomed wider than the viewport and scrolled right of the middle.
        tab.Zoom = 8;
        Settle(workspace);
        var scroller = firstView.ScrollViewer;
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width + 400, $"extent {scroller.Extent}, viewport {scroller.Viewport}");
        scroller.Offset = new Vector(((scroller.Extent.Width - scroller.Viewport.Width) / 2) + 150, scroller.Offset.Y);
        Settle(workspace);
        var before = CentreFromPageCentre(firstView);

        workspace.MoveToNewWindow(first, tab);
        var view = Find<DocumentView>(workspace.WindowOf(workspace.Windows[1]), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(view.Document, tab.Session.Document) && view.ScrollViewer.Viewport.Width > 0);

        // The same spot of the pages is in the middle of the new window (same size as the source window).
        Assert.Equal(8.0, view.Layout!.Zoom);
        Assert.Equal(before, CentreFromPageCentre(view), 1.0);
    }

    [AvaloniaFact]
    public async Task MovedTabWaitsForASharpPageOfItsNewWindow()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        await WaitUntilAsync(workspace, () => tab.HasShownSharpPage);

        // Whether the view of the new window had drawn a sharp tile, each time the flag changed.
        var changes = new List<(bool Value, bool NewWindowDrewSharp)>();
        tab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TabViewModel.HasShownSharpPage))
            {
                var newView = workspace.Windows.Count > 1 ? Find<DocumentView>(workspace.WindowOf(workspace.Windows[1]), "DocumentView") : null;
                changes.Add((tab.HasShownSharpPage, newView?.Canvas.Bitmaps.Keys.Any(key => !key.IsPreview) == true));
            }
        };

        workspace.MoveToNewWindow(first, tab);
        await WaitUntilAsync(workspace, () => tab.HasShownSharpPage);

        // Reset when it left the source window (so its thumbnails wait), set again by the sharp page of the new window.
        Assert.Equal([(false, false), (true, true)], changes);
    }

    [AvaloniaFact]
    public async Task SourceWindowKeepsItsOtherTabs()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var a = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var b = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var c = await OpenReadyAsync(workspace, first, "vector-shapes.pdf");
        first.ActivateTabCommand.Execute(b);
        Settle(workspace);

        workspace.MoveToNewWindow(first, b);
        Settle(workspace);

        // The others stay in their order and open; the right neighbour of the moved active tab becomes active.
        Assert.Equal([a, c], first.Tabs);
        Assert.Same(c, first.ActiveTab);
        Assert.True(c.IsActive);
        Assert.Same(b, Assert.Single(workspace.Windows[1].Tabs));
        Assert.All([a, c], tab => Assert.Equal(ViewerState.Ready, tab.Session.State));
        var shown = Find<TabStripPanel>(workspace.WindowOf(first), "TabPanel").Children.Where(child => child.IsVisible).ToList();
        Assert.Equal(2, shown.Count);
        Assert.DoesNotContain(shown, view => ReferenceEquals(view.DataContext, b));

        // An inactive tab moves just as well and leaves the active one active.
        workspace.MoveToNewWindow(first, a);
        Settle(workspace);
        Assert.Equal([c], first.Tabs);
        Assert.Same(c, first.ActiveTab);
        Assert.Equal(3, workspace.Windows.Count);
        Assert.Same(a, Assert.Single(workspace.Windows[2].Tabs));
        Assert.Equal(ViewerState.Ready, a.Session.State);
    }

    [AvaloniaFact]
    public async Task MovingTheOnlyTabOfAWindowDoesNothing()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var tab = await OpenReadyAsync(workspace, first, "hello_world.pdf");

        workspace.MoveToNewWindow(first, tab);
        Settle(workspace);

        Assert.Same(first, Assert.Single(workspace.Windows));
        Assert.Same(tab, Assert.Single(first.Tabs));
        Assert.Same(tab, first.ActiveTab);
        Assert.Equal(ViewerState.Ready, tab.State);

        // Nor does a tab of another window, or one that is not open at all.
        var other = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        workspace.MoveToNewWindow(first, other);
        Settle(workspace);
        var second = workspace.Windows[1];
        workspace.MoveToNewWindow(first, other);
        workspace.MoveToNewWindow(second, tab);
        Settle(workspace);
        Assert.Equal(2, workspace.Windows.Count);
        Assert.Same(tab, Assert.Single(first.Tabs));
        Assert.Same(other, Assert.Single(second.Tabs));
    }

    [AvaloniaFact]
    public async Task MoveToNewWindowIsOnlyOfferedWithTwoOrMoreTabs()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        var a = await OpenReadyAsync(workspace, first, "hello_world.pdf");

        // One tab: the menu of the tab offers only "Close".
        Assert.Equal([Strings.CloseTabMenu], OpenTabMenu(window, a).Select(item => item.Header as string));
        CloseMenus(window);

        // Two tabs: "Open in new window" too, which moves the tab it was opened on.
        var b = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        var items = OpenTabMenu(window, a);
        Assert.Equal([Strings.MoveToNewWindow, Strings.CloseTabMenu], items.Select(item => item.Header as string));
        Click(window, CenterOf(window, items[0]), MouseButton.Left);
        Settle(workspace);

        Assert.Equal(2, workspace.Windows.Count);
        Assert.Same(a, Assert.Single(workspace.Windows[1].Tabs));
        Assert.Same(b, Assert.Single(first.Tabs));
        Assert.Equal(ViewerState.Ready, a.Session.State);

        // Each of the two windows has one tab again: no "Open in new window" in either.
        Assert.Equal([Strings.CloseTabMenu], OpenTabMenu(window, b).Select(item => item.Header as string));
        CloseMenus(window);
        var secondWindow = workspace.WindowOf(workspace.Windows[1]);
        Assert.Equal([Strings.CloseTabMenu], OpenTabMenu(secondWindow, a).Select(item => item.Header as string));
        CloseMenus(secondWindow);
    }

    [AvaloniaFact]
    public async Task ClosingTheLastTabOfASecondWindowClosesIt()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        workspace.MoveToNewWindow(first, tab);
        Settle(workspace);
        var second = workspace.Windows[1];
        var secondWindow = workspace.WindowOf(second);
        var closed = false;
        secondWindow.Closed += (_, _) => closed = true;

        second.CloseActiveTabCommand.Execute(null);
        Settle(workspace);

        Assert.True(closed);
        Assert.False(secondWindow.IsVisible);
        Assert.Same(first, Assert.Single(workspace.Windows));
        Assert.Equal(ViewerState.Empty, tab.Session.State);
        Assert.Single(first.Tabs);
    }

    [AvaloniaFact]
    public async Task ClosingTheLastTabOfTheOnlyWindowShowsTheStartScreen()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        var tab = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        Assert.False(first.CloseWindowWhenEmpty);

        first.CloseActiveTabCommand.Execute(null);
        Settle(workspace);

        Assert.True(window.IsVisible);
        Assert.Same(first, Assert.Single(workspace.Windows));
        Assert.True(first.IsEmpty);
        Assert.Equal(ViewerState.Empty, tab.Session.State);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task ClosingAWindowDisposesItsTabs()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var kept = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var moved = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        workspace.MoveToNewWindow(first, moved);
        Settle(workspace);
        var second = workspace.Windows[1];
        var other = await OpenReadyAsync(workspace, second, "vector-shapes.pdf");
        Assert.Equal(2, second.Tabs.Count);

        workspace.WindowOf(second).Close();
        Settle(workspace);

        // Both tabs of the closed window are released; the other window and its tab are not affected.
        Assert.Same(first, Assert.Single(workspace.Windows));
        Assert.Equal(ViewerState.Empty, moved.Session.State);
        Assert.Equal(ViewerState.Empty, other.Session.State);
        Assert.Equal(ViewerState.Ready, kept.Session.State);
        Assert.Same(kept, Assert.Single(first.Tabs));
    }

    [AvaloniaFact]
    public async Task ExternalOpenGoesToTheActiveWindow()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var moved = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        workspace.MoveToNewWindow(first, moved);
        Settle(workspace);
        var second = workspace.Windows[1];
        var activations = (First: 0, Second: 0);
        first.ActivationRequested += () => activations.First++;
        second.ActivationRequested += () => activations.Second++;

        // The second window was activated last: the handed-over file opens there, as a single-instance listener
        // hands it over (from a pool thread).
        workspace.WindowOf(first).Activate();
        Settle(workspace);
        workspace.WindowOf(second).Activate();
        Settle(workspace);
        Assert.Same(second, workspace.ActiveWindow);
        await Task.Run(() => workspace.OnExternalOpen([TestData.Pdf("vector-shapes.pdf")]));
        await WaitUntilAsync(workspace, () => second.Tabs.Count == 2 && second.Tabs.All(tab => tab.State == ViewerState.Ready));
        Assert.Equal(TestData.Pdf("vector-shapes.pdf"), second.ActiveTab!.FullPath);
        Assert.Single(first.Tabs);
        Assert.Equal((0, 1), activations);

        // Then the first one.
        workspace.WindowOf(first).Activate();
        Settle(workspace);
        Assert.Same(first, workspace.ActiveWindow);
        await Task.Run(() => workspace.OnExternalOpen([TestData.Pdf("thousand-pages.pdf")]));
        await WaitUntilAsync(workspace, () => first.Tabs.Count == 2 && first.Tabs.All(tab => tab.State == ViewerState.Ready));
        Assert.Equal(TestData.Pdf("thousand-pages.pdf"), first.ActiveTab!.FullPath);
        Assert.Equal(2, second.Tabs.Count);
        Assert.Equal((1, 1), activations);
    }

    [AvaloniaFact]
    public async Task ExternalOpenWithNoWindowActivatedUsesTheFirstWindow()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var moved = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        workspace.MoveToNewWindow(first, moved);
        Settle(workspace);
        var second = workspace.Windows[1];
        Assert.Null(workspace.ActiveWindow);

        await Task.Run(() => workspace.OnExternalOpen([TestData.Pdf("vector-shapes.pdf")]));
        await WaitUntilAsync(workspace, () => first.Tabs.Count == 2 && first.Tabs.All(tab => tab.State == ViewerState.Ready));
        Assert.Single(second.Tabs);

        // The active window closed: the first window again.
        workspace.WindowOf(second).Activate();
        Settle(workspace);
        Assert.Same(second, workspace.ActiveWindow);
        workspace.WindowOf(second).Close();
        Settle(workspace);
        Assert.Null(workspace.ActiveWindow);
        await Task.Run(() => workspace.OnExternalOpen([TestData.Pdf("thousand-pages.pdf")]));
        await WaitUntilAsync(workspace, () => first.Tabs.Count == 3 && first.Tabs.All(tab => tab.State == ViewerState.Ready));
    }

    [AvaloniaFact]
    public async Task ClosingTheFirstWindowKeepsTheOthersAlive()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var kept = await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var moved = await OpenReadyAsync(workspace, first, "text-basic.pdf");
        workspace.MoveToNewWindow(first, moved);
        Settle(workspace);
        var second = workspace.Windows[1];
        var secondWindow = workspace.WindowOf(second);

        workspace.WindowOf(first).Close();
        Settle(workspace);

        // The second window stays with its tab; the tab of the first one is released.
        Assert.Same(second, Assert.Single(workspace.Windows));
        Assert.True(secondWindow.IsVisible);
        Assert.Equal(ViewerState.Ready, moved.Session.State);
        Assert.Equal(ViewerState.Empty, kept.Session.State);

        // It is the only window now: its last tab leaves the start screen instead of closing it.
        Assert.False(second.CloseWindowWhenEmpty);
        second.CloseActiveTabCommand.Execute(null);
        Settle(workspace);
        Assert.True(secondWindow.IsVisible);
        Assert.Same(second, Assert.Single(workspace.Windows));
        Assert.True(Find<Control>(secondWindow, "StartScreen").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task NewWindowHasTheSizeOfItsSourceOffsetBy32()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var window = workspace.WindowOf(first);
        window.Width = 900;
        window.Height = 650;
        window.Position = new PixelPoint(100, 50);
        Settle(workspace);
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        var tab = await OpenReadyAsync(workspace, first, "text-basic.pdf");

        workspace.MoveToNewWindow(first, tab);
        Settle(workspace);

        var second = workspace.WindowOf(workspace.Windows[1]);
        Assert.Equal(WindowStartupLocation.Manual, second.WindowStartupLocation);
        Assert.Equal(new PixelPoint(132, 82), second.Position);
        Assert.Equal(new Size(900, 650), second.ClientSize);
    }

    [AvaloniaFact]
    public async Task CaptureSessionListsEveryWindowWithItsActiveTab()
    {
        var workspace = CreateWorkspace();
        var first = workspace.OpenWindow();
        var far = await OpenReadyAsync(workspace, first, "thousand-pages.pdf");
        await OpenReadyAsync(workspace, first, "hello_world.pdf");
        first.ActivateTabCommand.Execute(far);
        var view = Find<DocumentView>(workspace.WindowOf(first), "DocumentView");
        await WaitUntilAsync(workspace, () => ReferenceEquals(view.DataContext, far) && view.Layout is not null);
        far.GoToPage(40);
        await WaitUntilAsync(workspace, () => far.CurrentPage == 40);

        var second = workspace.OpenWindow();
        await OpenReadyAsync(workspace, second, "vector-shapes.pdf");
        await second.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        var locked = second.ActiveTab!;
        await WaitUntilAsync(workspace, () => locked.State == ViewerState.PasswordRequired);

        // A window on the start screen has nothing to bring back.
        workspace.OpenWindow();
        Settle(workspace);

        var snapshot = workspace.CaptureSession();

        Assert.Equal(2, snapshot.Windows.Count);
        var one = snapshot.Windows[0];
        Assert.Equal([TestData.Pdf("thousand-pages.pdf"), TestData.Pdf("hello_world.pdf")], one.Tabs.Select(tab => tab.Path));
        Assert.Equal(0, one.ActiveIndex);

        // The active tab is remembered where it is shown now (not where it was when it was last left), with its zoom.
        // (The top edge of the viewport lies on page 40 or in the gap above it, at the end of page 39.)
        var shown = one.Tabs[0].View;
        Assert.NotNull(shown);
        Assert.Equal(far.SavedView, shown.View);
        Assert.InRange(shown.View.Anchor.Page, 38, 39);
        Assert.Equal(far.Zoom, shown.Zoom);
        Assert.Equal(far.ZoomMode, shown.ZoomMode);
        Assert.Equal(far.LayoutMode, shown.Layout);

        // A tab without an open document keeps its path, without a view.
        var two = snapshot.Windows[1];
        Assert.Equal([TestData.Pdf("vector-shapes.pdf"), TestData.Pdf("encrypted_hello_world_r3.pdf")], two.Tabs.Select(tab => tab.Path));
        Assert.Equal(1, two.ActiveIndex);
        Assert.Null(two.Active.View);
    }

    [Theory]
    [InlineData(100, 50, 900, 650, 1.0, 132, 82)]
    [InlineData(100, 50, 900, 650, 1.5, 148, 98)]
    [InlineData(1000, 600, 900, 650, 1.0, 1020, 630)]
    [InlineData(-50, -20, 900, 650, 1.0, 0, 12)]
    [InlineData(0, 0, 2400, 1600, 1.0, 0, 0)]
    public void NewWindowStaysOnTheWorkingArea(int x, int y, int width, int height, double scaling, int expectedX, int expectedY)
    {
        // Working area 1920 × 1280 at the origin; sizes in physical pixels.
        var position = Workspace.PlaceNextTo(new Avalonia.PixelRect(x, y, width, height), new Avalonia.PixelRect(0, 0, 1920, 1280), scaling);

        Assert.Equal(new PixelPoint(expectedX, expectedY), position);
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

    /// <summary>Right-clicks <paramref name="tab"/> in the strip of <paramref name="window"/>; returns the entries of its menu.</summary>
    private static List<MenuItem> OpenTabMenu(Window window, TabViewModel tab)
    {
        var view = Find<TabStripPanel>(window, "TabPanel").Children.Single(child => child.IsVisible && ReferenceEquals(child.DataContext, tab));
        Click(window, view.TranslatePoint(new Point(30, view.Bounds.Height - 17), window)!.Value, MouseButton.Right);
        return window.GetVisualDescendants().OfType<MenuItem>().Where(item => item.IsEffectivelyVisible).ToList();
    }

    private static void CloseMenus(Window window)
    {
        foreach (var menu in window.GetVisualDescendants().OfType<ContextMenu>().ToList())
        {
            menu.Close();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<MenuItem>(), item => item.IsEffectivelyVisible);
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Point point, MouseButton button)
    {
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
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

    /// <summary>How far the middle of the viewport lies from the centre line of the pages, in DIPs.</summary>
    private static double CentreFromPageCentre(DocumentView view)
    {
        var scroller = view.ScrollViewer;
        var contentWidth = view.Layout!.ContentWidth;
        var shift = Math.Max(0, (scroller.Viewport.Width - contentWidth) / 2);
        return scroller.Offset.X - shift + (scroller.Viewport.Width / 2) - (contentWidth / 2);
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");
}
