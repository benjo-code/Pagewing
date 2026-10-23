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
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class ZoomModeTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public ZoomModeTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task FitWidthZoomsSoThePageFillsTheViewportWidth()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");

        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);

        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        var layout = view.Layout!;
        Assert.Equal(view.ScrollViewer.Viewport.Width, layout.GetPageRect(0).Width + (2 * DocumentLayout.Margin), 0.5);
        Assert.Equal(layout.Zoom, tab.Zoom);
        Assert.True(tab.Zoom > 2, $"zoom {tab.Zoom}");

        // Not a pixel wider than the viewport after layout rounding: no horizontal scrolling.
        Assert.True(view.ScrollViewer.Extent.Width <= view.ScrollViewer.Viewport.Width, $"extent {view.ScrollViewer.Extent}, viewport {view.ScrollViewer.Viewport}");

        // The status bar shows the computed zoom, not the 100 % the document started with.
        var text = Find<TextBlock>(window, "ZoomValueText").Text;
        Assert.Equal(tab.Zoom.ToString("P0", Strings.Culture), text);
        Assert.NotEqual(1.0.ToString("P0", Strings.Culture), text);
    }

    [AvaloniaFact]
    public async Task FitPageShowsTheWholeCurrentPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");
        tab.GoToPage(2);
        Settle(window);

        // Half of page 2 has left the viewport at the top; it is still the current page.
        var scroller = view.ScrollViewer;
        Scroll(window, view, scroller.Offset.Y + (view.Layout!.GetPageRect(1).Height / 3));
        Assert.Equal(2, tab.CurrentPage);

        tab.SetZoomModeCommand.Execute(ZoomMode.FitPage);
        Settle(window);

        // Portrait pages: the height decides. The whole page lies in the viewport, its top at the margin.
        var page = view.Layout.GetPageRect(1);
        Assert.Equal(ZoomMode.FitPage, tab.ZoomMode);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(scroller.Viewport.Height, page.Height + (2 * DocumentLayout.Margin), 0.5);
        Assert.Equal(page.Y - DocumentLayout.Margin, scroller.Offset.Y, 0.5);
        Assert.True(page.Y >= scroller.Offset.Y && page.Bottom <= scroller.Offset.Y + scroller.Viewport.Height);
        Assert.True(page.Width + (2 * DocumentLayout.Margin) <= scroller.Viewport.Width);
    }

    [AvaloniaFact]
    public async Task ActualSizeIsOneHundredPercent()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        tab.Zoom = 2;
        Settle(window);

        tab.SetZoomModeCommand.Execute(ZoomMode.ActualSize);
        Settle(window);

        Assert.Equal(ZoomMode.ActualSize, tab.ZoomMode);
        Assert.Equal(1.0, tab.Zoom);
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal("100%", Find<TextBlock>(window, "ZoomValueText").Text);
    }

    [AvaloniaFact]
    public async Task FitModeFollowsAWindowResize()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        var before = tab.Zoom;

        window.Width = 800;
        Settle(window);

        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        Assert.True(tab.Zoom < before - 0.1, $"zoom {tab.Zoom} after {before}");
        Assert.Equal(view.ScrollViewer.Viewport.Width, view.Layout!.GetPageRect(0).Width + (2 * DocumentLayout.Margin), 0.5);
        Assert.Equal(view.Layout.Zoom, tab.Zoom);

        // Scrolling does not fit again: the current page changes, the zoom stays.
        var zoom = tab.Zoom;
        Scroll(window, view, view.Layout.GetPageRect(1).Y);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(zoom, tab.Zoom);
    }

    [AvaloniaFact]
    public async Task FitModeIsKeptWhenSwitchingTabsAndRestored()
    {
        var (window, viewModel, view) = Show();
        var a = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        a.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);

        // A new tab starts with the free zoom of 100 %.
        var b = await OpenTabAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal(ZoomMode.Free, b.ZoomMode);
        Assert.Equal(1.0, b.Zoom);
        Assert.Equal(1.0, view.Layout!.Zoom);
        Assert.Equal(ZoomMode.FitWidth, a.ZoomMode);

        // The window gets narrower while the other tab is shown; back in the first tab the fit follows.
        window.Width = 760;
        Settle(window);
        viewModel.ActivateTabCommand.Execute(a);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, a.Session.Document) && view.Canvas.LastRequests.Count > 0);

        Assert.Equal(ZoomMode.FitWidth, a.ZoomMode);
        Assert.Equal(view.ScrollViewer.Viewport.Width, view.Layout!.GetPageRect(0).Width + (2 * DocumentLayout.Margin), 0.5);
        Assert.Equal(view.Layout.Zoom, a.Zoom);
        Assert.Equal(ZoomMode.Free, b.ZoomMode);
        Assert.Equal(1.0, b.Zoom);
    }

    [AvaloniaFact]
    public async Task ZoomStepsLeaveTheFitMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        var fitted = tab.Zoom;

        Press(window, Key.OemPlus, PhysicalKey.Equal, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(ZoomSteps.Next(fitted), tab.Zoom);
        Assert.Equal(tab.Zoom, view.Layout!.Zoom);

        // A resize no longer changes the zoom.
        var zoom = tab.Zoom;
        window.Width = 800;
        Settle(window);
        Assert.Equal(zoom, tab.Zoom);

        // A step from the menu leaves the fit mode as well.
        tab.SetZoomModeCommand.Execute(ZoomMode.FitPage);
        Settle(window);
        Assert.Equal(ZoomMode.FitPage, tab.ZoomMode);
        tab.SetZoomCommand.Execute(1.5);
        Settle(window);
        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(1.5, tab.Zoom);
        Assert.Equal(1.5, view.Layout.Zoom);
    }

    [AvaloniaFact]
    public async Task CtrlWheelLeavesTheFitMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        tab.SetZoomModeCommand.Execute(ZoomMode.FitPage);
        Settle(window);
        var fitted = tab.Zoom;
        var canvas = view.Canvas;
        var point = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;

        window.MouseWheel(point, new Vector(0, -1), RawInputModifiers.Control);
        Settle(window);

        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(ZoomSteps.Previous(fitted), tab.Zoom);
        Assert.Equal(tab.Zoom, view.Layout!.Zoom);
    }

    [AvaloniaFact]
    public async Task FitWidthDoesNotOscillateWhenTheScrollBarAppears()
    {
        var (window, viewModel, view) = Show(width: 700);
        var scroller = view.ScrollViewer;

        // Scroll bars that take their room from the viewport (not laid over it): the vertical one makes the viewport
        // narrower, so a width fit that brings it changes its own input.
        scroller.AllowAutoHide = false;
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var fullWidth = scroller.Viewport.Width;
        Assert.True(scroller.Extent.Height < scroller.Viewport.Height, "the document must fit without a scroll bar at 100 %");
        tab.Zoom = 3;
        Settle(window);
        var bar = fullWidth - scroller.Viewport.Width;
        Assert.True(bar > 4, $"the vertical scroll bar takes {bar} DIP");
        tab.Zoom = 1;
        Settle(window);
        Assert.Equal(fullWidth, scroller.Viewport.Width);

        // A viewport height between the content height fitted to the full width (needs the bar) and fitted next to
        // the bar (does not): each fit would bring or take away the bar that caused it.
        var pageWidth = view.Layout!.GetPageRect(0).Width;
        var pageHeight = view.Layout.GetPageRect(0).Height;
        double ContentHeight(double viewportWidth) =>
            (2 * DocumentLayout.Margin) + DocumentLayout.PageGap + (2 * pageHeight * (viewportWidth - (2 * DocumentLayout.Margin)) / pageWidth);
        var height = Math.Round((ContentHeight(fullWidth) + ContentHeight(fullWidth - bar)) / 2);
        window.Height += height - scroller.Viewport.Height;
        Settle(window);
        Assert.Equal(height, scroller.Viewport.Height, 0.5);

        var changes = 0;
        tab.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(TabViewModel.Zoom) ? 1 : 0;
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        var zoom = tab.Zoom;
        for (var i = 0; i < 10; i++)
        {
            Settle(window);
            Assert.Equal(zoom, tab.Zoom);
        }

        // Settled after at most two steps, with pages that never reach under the bar or past the viewport.
        Assert.True(changes <= 2, $"{changes} zoom changes");
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        Assert.Equal(view.Layout.Zoom, tab.Zoom);
        var width = view.Layout.GetPageRect(0).Width + (2 * DocumentLayout.Margin);
        Assert.InRange(width, fullWidth - bar - 1, scroller.Viewport.Width + 0.5);
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width + 0.5, $"the pages are wider than the viewport: extent {scroller.Extent}, viewport {scroller.Viewport}");
    }

    [AvaloniaFact]
    public async Task ZoomMenuShowsTheCheckmarkOnTheActiveMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var button = Find<Button>(window, "ZoomMenuButton");
        Assert.False(string.IsNullOrWhiteSpace(ToolTip.GetTip(button) as string));
        Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), block => block.Name == "ZoomValueText");

        // Before the menu opens it has no entries (built when it opens, nothing for the start).
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        Assert.Empty(menu.Items);

        // 100 % in the free zoom: the step has the check mark, no mode has one.
        var entries = Open(window, button);
        var steps = entries.Where(entry => entry.CommandParameter is double).ToList();
        Assert.Equal([0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0, 4.0], steps.Select(entry => (double)entry.CommandParameter!));
        var modes = entries.Where(entry => entry.CommandParameter is ZoomMode).ToList();
        Assert.Equal([ZoomMode.FitWidth, ZoomMode.FitPage, ZoomMode.ActualSize], modes.Select(entry => (ZoomMode)entry.CommandParameter!));
        Assert.Equal(["Page width", "Whole page", "Actual size"], modes.Select(AutomationName));
        Assert.Equal([1.0], entries.Where(HasCheckmark).Select(entry => entry.CommandParameter));
        Assert.Equal("100%", steps[3].Header);

        // Choosing "Page width" closes the menu; opened again, the mode has the check mark (and no step).
        Choose(window, menu, modes[0]);
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        entries = Open(window, button);
        Assert.Equal([ZoomMode.FitWidth], entries.Where(HasCheckmark).Select(entry => entry.CommandParameter));

        // Actual size is a mode of its own, even though it is 100 %.
        Choose(window, menu, entries.First(entry => ZoomMode.ActualSize.Equals(entry.CommandParameter)));
        Assert.Equal(1.0, tab.Zoom);
        entries = Open(window, button);
        Assert.Equal([ZoomMode.ActualSize], entries.Where(HasCheckmark).Select(entry => entry.CommandParameter));

        // A step leaves the mode and has the check mark itself.
        Choose(window, menu, entries.First(entry => 2.0.Equals(entry.CommandParameter)));
        Assert.Equal(ZoomMode.Free, tab.ZoomMode);
        Assert.Equal(2.0, view.Layout!.Zoom);
        entries = Open(window, button);
        Assert.Equal([2.0], entries.Where(HasCheckmark).Select(entry => entry.CommandParameter));

        // German texts.
        menu.Hide();
        Settle(window);
        Strings.Culture = new CultureInfo("de");
        entries = Open(window, button);
        Assert.Equal(["Seitenbreite", "Ganze Seite", "Tatsächliche Größe"], entries.Where(entry => entry.CommandParameter is ZoomMode).Select(AutomationName));
        menu.Hide();
        Settle(window);
    }

    [AvaloniaFact]
    public async Task CtrlGFocusesAndSelectsThePageField()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        tab.GoToPage(123);
        Settle(window);
        Assert.True(view.IsFocused);
        var field = Find<TextBox>(window, "PageField");

        Press(window, Key.G, PhysicalKey.G, RawInputModifiers.Control);

        Assert.True(field.IsFocused);
        Assert.Equal("123", field.Text);
        Assert.Equal(0, Math.Min(field.SelectionStart, field.SelectionEnd));
        Assert.Equal(3, Math.Max(field.SelectionStart, field.SelectionEnd));

        // Typing replaces the selection; Enter jumps.
        window.KeyTextInput("45");
        Settle(window);
        Assert.Equal("45", field.Text);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(45, tab.CurrentPage);
    }

    [AvaloniaFact]
    public async Task InvalidPageEntryResetsTheFieldWithoutAMessage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        tab.GoToPage(7);
        Settle(window);
        var offset = view.ScrollViewer.Offset;
        var field = Find<TextBox>(window, "PageField");
        var texts = VisibleTexts(window);

        foreach (var invalid in new[] { "abc", "0", "1001", "-1", "", "3.5", "1e3" })
        {
            Press(window, Key.G, PhysicalKey.G, RawInputModifiers.Control);
            field.Text = invalid;
            Settle(window);
            Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);

            Assert.Equal("7", field.Text);
            Assert.Equal(7, tab.CurrentPage);
            Assert.Equal(offset, view.ScrollViewer.Offset);

            // No message: no error state on the field, no new text anywhere in the window.
            Assert.DoesNotContain("invalid", field.Classes);
            Assert.False(DataValidationErrors.GetHasErrors(field));
            Assert.Equal(texts, VisibleTexts(window));
        }
    }

    [AvaloniaFact]
    public async Task GoToPageFromTheFieldScrollsThePageToTheTop()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        var field = Find<TextBox>(window, "PageField");

        Press(window, Key.G, PhysicalKey.G, RawInputModifiers.Control);
        window.KeyTextInput("250");
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);

        Assert.Equal(250, tab.CurrentPage);
        Assert.Equal("250", field.Text);
        Assert.Equal(view.Layout!.GetPageRect(249).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);

        // The same in a fit mode: the page is fitted and still starts at the margin.
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        Press(window, Key.G, PhysicalKey.G, RawInputModifiers.Control);
        window.KeyTextInput("600");
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(600, tab.CurrentPage);
        Assert.Equal(view.Layout.GetPageRect(599).Y - DocumentLayout.Margin, view.ScrollViewer.Offset.Y, 3);
        Assert.Equal(view.ScrollViewer.Viewport.Width, view.Layout.GetPageRect(599).Width + (2 * DocumentLayout.Margin), 0.5);
    }

    [AvaloniaFact]
    public async Task FitPageRefitsAfterAJumpToAnotherPage()
    {
        // Pages alternate between portrait (even indices) and wide landscape (odd indices).
        var engine = new PdfEngine();
        var (window, viewModel, view) = Show(factory: () => new ViewerSession(
            engine.OpenAsync,
            (document, firstPage, count, priority, cancellationToken) => Task.FromResult<IReadOnlyList<PdfPageSize>>(
                Enumerable.Range(firstPage, count).Select(page => page % 2 == 0 ? new PdfPageSize(300, 400) : new PdfPageSize(600, 150)).ToArray())));
        var tab = await OpenTabAsync(window, viewModel, view, "thousand-pages.pdf");
        var scroller = view.ScrollViewer;

        tab.SetZoomModeCommand.Execute(ZoomMode.FitPage);
        Settle(window);
        var portrait = tab.Zoom;
        Assert.Equal(scroller.Viewport.Height, view.Layout!.GetPageRect(0).Height + (2 * DocumentLayout.Margin), 0.5);

        // The wide page fits by its width.
        tab.GoToPage(2);
        Settle(window);
        var wide = view.Layout.GetPageRect(1);
        Assert.Equal(2, tab.CurrentPage);
        Assert.Equal(ZoomMode.FitPage, tab.ZoomMode);
        Assert.NotEqual(portrait, tab.Zoom, 3);
        Assert.Equal(scroller.Viewport.Width, wide.Width + (2 * DocumentLayout.Margin), 0.5);
        Assert.Equal(wide.Y - DocumentLayout.Margin, scroller.Offset.Y, 0.5);

        var wideZoom = tab.Zoom;

        // Scrolling to the portrait page 3 does not fit again.
        Scroll(window, view, view.Layout.GetPageRect(2).Y - DocumentLayout.Margin);
        Assert.Equal(3, tab.CurrentPage);
        Assert.Equal(wideZoom, tab.Zoom);

        // Jumps with the status bar arrow do: the wide page 4 keeps the zoom, the portrait page 5 gets its own again.
        var next = Find<Button>(window, "NextPageButton");
        next.Command!.Execute(null);
        Settle(window);
        Assert.Equal(4, tab.CurrentPage);
        Assert.Equal(wideZoom, tab.Zoom, 6);
        next.Command!.Execute(null);
        Settle(window);
        Assert.Equal(5, tab.CurrentPage);
        Assert.Equal(portrait, tab.Zoom, 6);
        Assert.Equal(view.Layout.GetPageRect(4).Y - DocumentLayout.Margin, scroller.Offset.Y, 0.5);
        Assert.Equal(scroller.Viewport.Height, view.Layout.GetPageRect(4).Height + (2 * DocumentLayout.Margin), 0.5);
    }

    [AvaloniaFact]
    public async Task ShortcutsCtrl0Ctrl1Ctrl2SetTheModes()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "outline.pdf");

        Press(window, Key.D0, PhysicalKey.Digit0, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.FitPage, tab.ZoomMode);
        Assert.Equal(view.ScrollViewer.Viewport.Height, view.Layout!.GetPageRect(0).Height + (2 * DocumentLayout.Margin), 0.5);

        Press(window, Key.D2, PhysicalKey.Digit2, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        Assert.Equal(view.ScrollViewer.Viewport.Width, view.Layout.GetPageRect(0).Width + (2 * DocumentLayout.Margin), 0.5);

        Press(window, Key.D1, PhysicalKey.Digit1, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.ActualSize, tab.ZoomMode);
        Assert.Equal(1.0, view.Layout.Zoom);

        // The number pad works as well; the shortcuts work while the page field has the focus.
        Find<TextBox>(window, "PageField").Focus();
        Settle(window);
        Press(window, Key.NumPad2, PhysicalKey.NumPad2, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
        Press(window, Key.NumPad0, PhysicalKey.NumPad0, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.FitPage, tab.ZoomMode);
        Press(window, Key.NumPad1, PhysicalKey.NumPad1, RawInputModifiers.Control);
        Assert.Equal(ZoomMode.ActualSize, tab.ZoomMode);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(Func<ViewerSession>? factory = null, double width = 1024)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(factory ?? (() => new ViewerSession(new PdfEngine())), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
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

        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        return tab;
    }

    /// <summary>Clicks the zoom value and returns the entries of the menu that opened.</summary>
    private static List<MenuItem> Open(Window window, Button button)
    {
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
        var menu = (MenuFlyout)button.Flyout!;
        Assert.True(menu.IsOpen, "the zoom menu did not open");
        return menu.Items.OfType<MenuItem>().ToList();
    }

    private static void Choose(Window window, MenuFlyout menu, MenuItem entry)
    {
        entry.Command!.Execute(entry.CommandParameter);
        menu.Hide();
        Settle(window);
    }

    private static bool HasCheckmark(MenuItem entry) =>
        entry.Icon is StrokeIcon icon && ReferenceEquals(icon.Data, Application.Current!.FindResource("Pw.Icon.Check"));

    private static string? AutomationName(MenuItem entry) => Avalonia.Automation.AutomationProperties.GetName(entry);

    private static List<string> VisibleTexts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsEffectivelyVisible && !string.IsNullOrEmpty(block.Text))
            .Select(block => block.Text!)
            .ToList();

    private static void Scroll(Window window, DocumentView view, double y)
    {
        view.ScrollViewer.Offset = new Vector(view.ScrollViewer.Offset.X, y);
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
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
