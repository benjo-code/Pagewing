using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The density "Kompakt" (design system 5.4, <see cref="Appearance"/>): every dimension of the table in both densities,
/// the switch without a restart and the two columns of thumbnails. Each test has its own
/// <see cref="UserSettings.Detached"/> settings and its own <see cref="Appearance"/> on the app, and puts the app back to
/// the defaults at the end.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests); the density is app-wide as well.
[Collection("Localization")]
public sealed class DensityTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly List<IDisposable> _cleanup = [];
    private UserSettings? _settings;
    private Appearance? _appearance;

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        // Back to the defaults (normal density) before the appearance lets go of the app.
        _settings?.Update(_ => AppSettings.Default);
        _appearance?.Dispose();
    }

    [AvaloniaFact]
    public async Task NormalKeepsEveryDimensionOfTheSpec()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        var list = await WaitForListAsync(window, tab);

        // The literals the views had before the density existed.
        Assert.Equal(40, Find<Control>(window, "TitleBar").Bounds.Height);
        Assert.Equal(40, window.ExtendClientAreaTitleBarHeightHint);
        Assert.Equal(52, Find<Control>(window, "AppMark").Bounds.Width);
        Assert.Equal(52, Find<Control>(window, "ActionBar").Bounds.Width);
        var pages = Find<Button>(window, "PagesButton");
        Assert.Equal(new Size(40, 40), pages.Bounds.Size);
        Assert.Equal(264, viewModel.PanelWidth);
        Assert.Equal(264, Find<Panel>(window, "PanelHost").Bounds.Width);
        Assert.Equal(32, Find<Control>(window, "StatusBarRow").Bounds.Height);
        Assert.Equal(32, Find<Control>(window, "StatusBar").Bounds.Height);
        Assert.Equal(new Thickness(56, 0, 8, 0), Find<Grid>(window, "BarLayout").Margin);
        Assert.Equal(44, Resource(window, "Pw.Size.ToolbarHeight"));
        Assert.Equal(32, view.Layout!.PageMargin);
        Assert.Equal(DocumentLayout.Margin, view.Layout.GetPageRect(0).Y);
        Assert.Equal(DocumentLayout.PageGap, view.Layout.GetPageRect(1).Y - view.Layout.GetPageRect(0).Bottom, 6);
        Assert.Equal(13, window.FontSize);
        Assert.Equal(13, Resource(window, "Pw.Font.Body"));
        Assert.Equal(18, Resource(window, "Pw.Font.BodyLineHeight"));

        // One column of 136 wide thumbnails, in the list box's own panel.
        AssertThumbnails(window, list, columns: 1, width: 136);
        Assert.IsType<VirtualizingStackPanel>(list.ItemsPanelRoot);
    }

    [AvaloniaFact]
    public async Task CompactSetsEveryDimensionOfTheSpec()
    {
        var settings = Start();
        settings.Update(current => current with { Density = DensitySetting.Compact });
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        var list = await WaitForListAsync(window, tab);

        Assert.Equal(34, Find<Control>(window, "TitleBar").Bounds.Height);
        Assert.Equal(34, window.ExtendClientAreaTitleBarHeightHint);

        // The app symbol stays as wide as the action bar, the status bar stays indented by it (design system 6).
        Assert.Equal(44, Find<Control>(window, "AppMark").Bounds.Width);
        Assert.Equal(44, Find<Control>(window, "ActionBar").Bounds.Width);
        Assert.Equal(new Size(34, 34), Find<Button>(window, "PagesButton").Bounds.Size);
        Assert.Equal(236, viewModel.PanelWidth);
        Assert.Equal(236, Find<Panel>(window, "PanelHost").Bounds.Width);
        Assert.Equal(26, Find<Control>(window, "StatusBarRow").Bounds.Height);
        Assert.Equal(26, Find<Control>(window, "StatusBar").Bounds.Height);
        Assert.Equal(new Thickness(48, 0, 8, 0), Find<Grid>(window, "BarLayout").Margin);
        Assert.Equal(38, Resource(window, "Pw.Size.ToolbarHeight"));
        Assert.Equal(20, view.Layout!.PageMargin);
        Assert.Equal(20, view.Layout.GetPageRect(0).Y);

        // The gap between the pages stays 20 (design system 5.4).
        Assert.Equal(DocumentLayout.PageGap, view.Layout.GetPageRect(1).Y - view.Layout.GetPageRect(0).Bottom, 6);
        Assert.Equal(12.5, window.FontSize);
        Assert.Equal(12.5, Resource(window, "Pw.Font.Body"));
        Assert.Equal(17, Resource(window, "Pw.Font.BodyLineHeight"));
        AssertThumbnails(window, list, columns: 2, width: 72);
        Assert.IsType<VirtualizingGridPanel>(list.ItemsPanelRoot);
    }

    [AvaloniaFact]
    public async Task SwitchingNeedsNoRestartAndKeepsThePage()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        tab.Zoom = 1.5;
        tab.GoToPage(50);
        Settle(window);
        var selection = new TextSelection(new TextPosition(49, 0), new TextPosition(49, 10));
        tab.TextSelection = selection;
        Settle(window);
        var layout = view.Layout!;
        var document = view.Document;
        var height = layout.ContentHeight;
        var anchor = layout.GetAnchor(view.ScrollViewer.Offset.Y);
        Assert.Equal(50, tab.CurrentPage);

        // Compact: the same window, document and layout, with the new margin; page, zoom, position and selection stay.
        settings.Update(current => current with { Density = DensitySetting.Compact });
        Settle(window);
        Assert.Same(view, Find<DocumentView>(window, "DocumentView"));
        Assert.Same(document, view.Document);
        Assert.Same(layout, view.Layout);
        Assert.Equal(20, layout.PageMargin);
        Assert.Equal(height - (2 * (32 - 20)), layout.ContentHeight, 6);
        Assert.Equal(1.5, tab.Zoom);
        Assert.Equal(1.5, layout.Zoom);
        Assert.Equal(50, tab.CurrentPage);
        Assert.Equal(anchor.Page, layout.GetAnchor(view.ScrollViewer.Offset.Y).Page);
        Assert.Equal(anchor.Fraction, layout.GetAnchor(view.ScrollViewer.Offset.Y).Fraction, 3);
        Assert.Equal(selection, tab.TextSelection);
        Assert.Equal(34, Find<Control>(window, "TitleBar").Bounds.Height);
        Assert.Equal(26, Find<Control>(window, "StatusBarRow").Bounds.Height);

        // A page jump puts the page at the new margin below the top of the viewport.
        tab.GoToPage(10);
        Settle(window);
        Assert.Equal(layout.GetPageRect(9).Y - 20, view.ScrollViewer.Offset.Y, 0.5);

        // And back.
        settings.Update(current => current with { Density = DensitySetting.Normal });
        Settle(window);
        Assert.Equal(32, layout.PageMargin);
        Assert.Equal(height, layout.ContentHeight, 6);
        Assert.Equal(10, tab.CurrentPage);
        Assert.Equal(1.5, tab.Zoom);
        Assert.Equal(40, Find<Control>(window, "TitleBar").Bounds.Height);
        Assert.Equal(32, Find<Control>(window, "StatusBarRow").Bounds.Height);
    }

    [AvaloniaFact]
    public async Task SwitchingRefitsAFitMode()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        viewModel.CloseSidePanelCommand.Execute(null);
        tab.SetZoomModeCommand.Execute(ZoomMode.FitWidth);
        Settle(window);
        var scroller = view.ScrollViewer;
        Assert.Equal(scroller.Viewport.Width, view.Layout!.GetPageRect(0).Width + (2 * 32), 0.5);

        // Page width fits the narrower margin: the page grows by the 24 DIP the margins give up.
        settings.Update(current => current with { Density = DensitySetting.Compact });
        await WaitUntilAsync(window, () => Math.Abs(scroller.Viewport.Width - (view.Layout.GetPageRect(0).Width + (2 * 20))) < 0.5);
        Assert.Equal(ZoomMode.FitWidth, tab.ZoomMode);
    }

    [AvaloniaFact]
    public async Task CompactThumbnailsUseTwoColumns()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        var list = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.Equal(136, list.Thumbnails[0].Bitmap!.PixelSize.Width);

        // With the panel open: two columns at once, rendered again at their new width (one image pixel per device pixel).
        settings.Update(current => current with { Density = DensitySetting.Compact });
        Settle(window);
        list = await WaitForListAsync(window, tab);
        AssertThumbnails(window, list, columns: 2, width: 72);
        await WaitUntilAsync(window, () => list.Thumbnails.All(item => item.Bitmap is { PixelSize.Width: 72 }));
        Assert.All(list.LastRequests, request => Assert.Equal(72, request.Rect.Width));

        // The current page keeps its frame and bold number (design system 7.3 and 7.10).
        tab.GoToPage(2);
        Settle(window);
        var current = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
        Assert.Contains("current", Find<Border>(current, "ThumbnailCell").Classes);
        Assert.Equal(new Thickness(2), Find<Border>(current, "CurrentRing").BorderThickness);
        Assert.True(Find<TextBlock>(current, "PageNumber").FontWeight >= Avalonia.Media.FontWeight.SemiBold);

        // The arrow keys: left and right by one entry, up and down by a row; the document keeps its page.
        tab.GoToPage(1);
        Settle(window);
        Focus(window, list, 0);
        Press(window, Key.Right);
        AssertFocusedThumbnail(window, list, 1);
        Press(window, Key.Left);
        AssertFocusedThumbnail(window, list, 0);
        Press(window, Key.Down);
        AssertFocusedThumbnail(window, list, 2);
        Press(window, Key.Down);
        AssertFocusedThumbnail(window, list, 2);
        Press(window, Key.Up);
        AssertFocusedThumbnail(window, list, 0);
        Press(window, Key.End);
        AssertFocusedThumbnail(window, list, 2);
        Press(window, Key.Home);
        AssertFocusedThumbnail(window, list, 0);
        Assert.Equal(1, tab.CurrentPage);
        Press(window, Key.Down);
        Press(window, Key.Enter);
        Assert.Equal(3, tab.CurrentPage);

        // Back to normal: one column of 136 again.
        settings.Update(current => current with { Density = DensitySetting.Normal });
        Settle(window);
        list = await WaitForListAsync(window, tab);
        AssertThumbnails(window, list, columns: 1, width: 136);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is { PixelSize.Width: 136 });
    }

    [AvaloniaTheory]
    [InlineData(200.0, 56.0)]
    [InlineData(236.0, 72.0)]
    [InlineData(400.0, 160.0)]
    public async Task CompactThumbnailsFitTheirColumnsAtEveryPanelWidth(double panelWidth, double frameWidth)
    {
        var settings = Start();
        settings.Update(current => current with { Density = DensitySetting.Compact });
        var (window, viewModel, view) = Show(1200, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        var list = await WaitForListAsync(window, tab);
        viewModel.PanelWidth = panelWidth;
        Settle(window);
        Assert.Equal(panelWidth, Find<Panel>(window, "PanelHost").Bounds.Width);

        // The frames are as wide as the column allows (steps of 8), the thumbnails are rendered at that width.
        Assert.Equal(frameWidth, list.FrameWidth);
        Assert.All(list.Thumbnails, item => Assert.Equal(frameWidth, item.FrameWidth));
        await WaitUntilAsync(window, () => list.Thumbnails.All(item => item.Bitmap is { } bitmap && bitmap.PixelSize.Width == frameWidth));
        Assert.NotEmpty(list.LastRequests);
        Assert.All(list.LastRequests, request => Assert.Equal(frameWidth, request.Rect.Width));

        // Never overlapping or overflowing: inside the list's padding, 16 apart in a row and between the rows.
        list.ScrollIntoView(0);
        Settle(window);
        var entries = Enumerable.Range(0, 3)
            .Select(index => Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index)))
            .Select(container => new Rect(container.TranslatePoint(default, list)!.Value, container.Bounds.Size))
            .ToArray();
        foreach (var entry in entries)
        {
            Assert.Equal(frameWidth + ThumbnailList.EntryChrome, entry.Width, 0.5);
            Assert.True(entry.X >= list.Padding.Left - 0.5, $"entry at {entry} leaves the list on the left");
            Assert.True(entry.Right <= list.Bounds.Width - list.Padding.Right + 0.5, $"entry at {entry} leaves the list on the right");
        }

        Assert.Equal(entries[0].Y, entries[1].Y, 0.5);
        Assert.Equal(ThumbnailList.EntryGap, entries[1].X - entries[0].Right, 0.5);
        Assert.Equal(ThumbnailList.EntryGap, entries[2].Y - entries[0].Bottom, 0.5);
        Assert.Equal(entries[0].X, entries[2].X, 0.5);

        // The row is centred: the spare width goes to both edges alike.
        Assert.Equal(entries[0].X, list.Bounds.Width - entries[1].Right, 0.5);
    }

    [AvaloniaFact]
    public async Task DraggingThePanelEdgeRendersTheThumbnailsInSteps()
    {
        var settings = Start();
        settings.Update(current => current with { Density = DensitySetting.Compact });
        var (window, viewModel, view) = Show(1200, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        var list = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => list.Thumbnails.All(item => item.Bitmap is { PixelSize.Width: 72 }));
        var bitmap = list.Thumbnails[0].Bitmap;

        // A drag over 16 DIP of panel width (224 to 239) stays within the step of 8: same frames, the same images, nothing rendered again.
        for (var width = 224; width <= 239; width++)
        {
            viewModel.PanelWidth = width;
            Settle(window);
            Assert.Equal(72, list.FrameWidth);
        }

        Assert.Same(bitmap, list.Thumbnails[0].Bitmap);

        // The next step: frames and images of 80.
        viewModel.PanelWidth = 240;
        Settle(window);
        Assert.Equal(80, list.FrameWidth);
        await WaitUntilAsync(window, () => list.Thumbnails.All(item => item.Bitmap is { PixelSize.Width: 80 }));
    }

    [AvaloniaFact]
    public async Task CompactThumbnailsStayVirtualizedAndKeyboardReachesEveryPage()
    {
        var settings = Start();
        settings.Update(current => current with { Density = DensitySetting.Compact });
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var list = await WaitForListAsync(window, tab);
        await WaitUntilAsync(window, () => list.LastRequests.Count > 0);

        // A handful of the 1000 entries exist, and only those are planned.
        var realized = RealizedIndexes(list);
        Assert.InRange(realized.Count, 2, 100);
        Assert.All(list.LastRequests, request => Assert.Contains(request.Key.Page, realized));

        // Down goes a row (two entries) at a time, far beyond the entries realized at first, each one in view.
        Focus(window, list, 0);
        for (var i = 1; i <= 40; i++)
        {
            Press(window, Key.Down);
            AssertFocusedThumbnail(window, list, 2 * i);
        }

        Press(window, Key.Right);
        AssertFocusedThumbnail(window, list, 81);
        Press(window, Key.Up);
        AssertFocusedThumbnail(window, list, 79);

        // Page Down and Page Up move by about a view and keep the column; the document stays on its page.
        Press(window, Key.PageDown);
        var afterPage = AssertFocusedThumbnail(window, list, null);
        Assert.True(afterPage > 81, "Page Down moved by more than one row");
        Assert.Equal(1, afterPage % 2);
        Press(window, Key.PageUp);
        var afterPageUp = AssertFocusedThumbnail(window, list, null);
        Assert.True(afterPageUp < afterPage, "Page Up moved back");
        Assert.Equal(1, tab.CurrentPage);

        Press(window, Key.End);
        AssertFocusedThumbnail(window, list, 999);
        Press(window, Key.Enter);
        Assert.Equal(1000, tab.CurrentPage);
        Press(window, Key.Home);
        AssertFocusedThumbnail(window, list, 0);

        // A jump in the document brings its entry into view.
        tab.GoToPage(500);
        Settle(window);
        Assert.Equal(499, list.SelectedIndex);
        var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(499));
        var top = container.TranslatePoint(default, list)!.Value.Y;
        Assert.InRange(top, -2, list.Bounds.Height - container.Bounds.Height + 2);
    }

    [AvaloniaFact]
    public void TheWindowsTitleBarHintFollowsTheDensity()
    {
        var settings = Start();
        var (window, _, _) = Show(1024, settings);
        Assert.Equal(40, window.ExtendClientAreaTitleBarHeightHint);
        Assert.Equal(140, Find<Control>(window, "CaptionButtonsSpace").Bounds.Width);

        settings.Update(current => current with { Density = DensitySetting.Compact });
        Settle(window);
        Assert.Equal(34, window.ExtendClientAreaTitleBarHeightHint);
        Assert.Equal(34, Find<Control>(window, "TitleBar").Bounds.Height);

        // The caption buttons keep their room.
        Assert.Equal(140, Find<Control>(window, "CaptionButtonsSpace").Bounds.Width);

        settings.Update(current => current with { Density = DensitySetting.Normal });
        Settle(window);
        Assert.Equal(40, window.ExtendClientAreaTitleBarHeightHint);
    }

    [AvaloniaFact]
    public async Task TextStaysReadable()
    {
        var settings = Start();
        settings.Update(current => current with { Density = DensitySetting.Compact });
        var (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        await WaitForListAsync(window, tab);

        // Every text in the window is at least 12 px. type.mono (11.5, the page numbers of the thumbnails and the keys)
        // is a size of its own in the spec (5.2) that the density does not touch: it is the same in both densities.
        var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).ToList();
        Assert.NotEmpty(texts);
        foreach (var text in texts)
        {
            if (text.Classes.Contains("mono"))
            {
                Assert.Equal(11.5, text.FontSize);
            }
            else
            {
                Assert.True(text.FontSize >= 12, $"{text.Name ?? text.Text} is {text.FontSize} px");
            }
        }

        foreach (var presenter in window.GetVisualDescendants().OfType<TextPresenter>().Where(text => text.IsEffectivelyVisible))
        {
            Assert.True(presenter.FontSize >= 12, $"a text field is {presenter.FontSize} px");
        }

        Assert.True(Resource(window, "Pw.Font.Body") >= 12);
    }

    private UserSettings Start()
    {
        var app = Application.Current ?? throw new InvalidOperationException("No app.");
        _settings = UserSettings.Detached();
        _appearance = new Appearance(app, _settings);
        return _settings;
    }

    private static double Resource(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value), $"Resource {key} is missing");
        return Assert.IsType<double>(value);
    }

    /// <summary>Asserts that the first three entries lie in <paramref name="columns"/> columns, each frame <paramref name="width"/> wide.</summary>
    private static void AssertThumbnails(Window window, ThumbnailList list, int columns, double width)
    {
        list.ScrollIntoView(0);
        Settle(window);
        var origins = new List<Point>();
        for (var index = 0; index < 3; index++)
        {
            var container = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
            var frame = Find<Control>(container, "ThumbnailFrame");
            Assert.Equal(width, frame.Bounds.Width, 0.5);
            Assert.Equal(width * 400 / 300, frame.Bounds.Height, 1.0);
            Assert.Equal(width, list.Thumbnails[index].FrameWidth);
            origins.Add(frame.TranslatePoint(default, list)!.Value);
        }

        if (columns == 1)
        {
            Assert.Equal(origins[0].X, origins[1].X, 0.5);
            Assert.True(origins[1].Y > origins[0].Y + width, "the second entry lies below the first");
        }
        else
        {
            // Pages 1 and 2 side by side in the first row, page 3 below page 1; the frames stay inside the list.
            Assert.Equal(origins[0].Y, origins[1].Y, 0.5);
            Assert.True(origins[1].X > origins[0].X + width, "the second entry lies right of the first");
            Assert.Equal(origins[0].X, origins[2].X, 0.5);
            Assert.True(origins[2].Y > origins[0].Y + width, "the third entry lies below the first");
            Assert.True(origins[1].X + width <= list.Bounds.Width, "the second column fits into the panel");
        }
    }

    private static void Focus(Window window, ThumbnailList list, int index)
    {
        list.ScrollIntoView(index);
        Settle(window);
        Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index)).Focus(NavigationMethod.Tab);
        Settle(window);
        AssertFocusedThumbnail(window, list, index);
    }

    /// <summary>
    /// Asserts that the keyboard focus is on the thumbnail <paramref name="index"/> (any entry when null), that it is the
    /// selected one and that it lies within the visible part of the list; returns its index.
    /// </summary>
    private static int AssertFocusedThumbnail(Window window, ThumbnailList list, int? index)
    {
        var item = Assert.IsType<ListBoxItem>(window.FocusManager!.GetFocusedElement());
        var focused = list.IndexFromContainer(item);
        if (index is not null)
        {
            Assert.Equal(index, focused);
        }

        Assert.Equal(focused, list.SelectedIndex);
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var top = item.TranslatePoint(default, scroller)!.Value.Y;
        Assert.InRange(top, -2, scroller.Viewport.Height - item.Bounds.Height + 2);
        return focused;
    }

    private static HashSet<int> RealizedIndexes(ThumbnailList list) =>
        list.GetRealizedContainers().Select(list.IndexFromContainer).Where(index => index >= 0).ToHashSet();

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(double width, UserSettings settings)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a test file in a new tab and waits until the tab has shown its first sharp page; returns that tab.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && tab.HasShownSharpPage && ReferenceEquals(view.Document, tab.Session.Document));
        return tab;
    }

    /// <summary>Waits until the window shows the list of thumbnails of <paramref name="tab"/>, fully faded in.</summary>
    private static async Task<ThumbnailList> WaitForListAsync(Window window, TabViewModel tab)
    {
        ThumbnailList? list = null;
        await WaitUntilAsync(window, () =>
        {
            list = window.GetVisualDescendants().OfType<ThumbnailList>().SingleOrDefault(control => control.Name == "ThumbnailList");
            return list is { IsEffectivelyVisible: true }
                && ReferenceEquals(list.DataContext, tab)
                && list.Thumbnails.Count == tab.PageCount
                && list.GetSelfAndVisualAncestors().OfType<Visual>().All(visual => visual.Opacity >= 1);
        });
        return list!;
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
        Key.PageDown => PhysicalKey.PageDown,
        Key.PageUp => PhysicalKey.PageUp,
        Key.Home => PhysicalKey.Home,
        Key.End => PhysicalKey.End,
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
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
