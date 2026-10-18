using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
/// Theme, motion and panel state from the settings (<see cref="Appearance"/>, <see cref="UserSettings"/>): the
/// headless tests of the theme switch. Each test has its own <see cref="UserSettings.Detached"/> settings and its own
/// <see cref="Appearance"/> on the app, and puts the app back to the defaults at the end.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class ThemeSwitchTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly Color LightWindow = Color.Parse("#EEEFF2");
    private static readonly Color DarkWindow = Color.Parse("#15171B");

    private readonly List<IDisposable> _cleanup = [];
    private UserSettings? _settings;
    private Appearance? _appearance;

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        // Back to the defaults (system theme, full motion) before the appearance lets go of the app.
        _settings?.Update(_ => AppSettings.Default);
        _appearance?.Dispose();
    }

    [AvaloniaFact]
    public void SystemFollowsTheVariantOfThePlatform()
    {
        var (app, settings, appearance) = Start();
        var changes = 0;
        appearance.EffectiveThemeChanged += () => changes++;

        // "System" asks for no variant of its own (Default): Avalonia takes the one the platform reports, and follows
        // it when it changes. The headless platform always reports light (DefaultPlatformSettings), so a change of the
        // system theme cannot be played here; the event that announces it is the one the user's choice raises below.
        var platform = app.PlatformSettings!.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Assert.Equal(ThemeSetting.System, settings.Current.Theme);
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.Equal(platform, appearance.EffectiveTheme);
        Assert.Equal(platform, app.ActualThemeVariant);

        // A choice of the user wins over the platform; back to "System" the platform's variant applies again, at once.
        var other = platform == ThemeVariant.Dark ? ThemeSetting.Light : ThemeSetting.Dark;
        settings.Update(current => current with { Theme = other });
        Assert.NotEqual(platform, appearance.EffectiveTheme);
        Assert.Equal(1, changes);

        settings.Update(current => current with { Theme = ThemeSetting.System });
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.Equal(platform, appearance.EffectiveTheme);
        Assert.Equal(2, changes);

        // Choosing the variant that is in effect already changes nothing that is visible: no event.
        settings.Update(current => current with { Theme = platform == ThemeVariant.Dark ? ThemeSetting.Dark : ThemeSetting.Light });
        Assert.Equal(platform, appearance.EffectiveTheme);
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public void ChoosingDarkSwitchesTheVariantAndTheTokens()
    {
        var (app, settings, appearance) = Start();
        var changes = 0;
        appearance.EffectiveThemeChanged += () => changes++;
        var window = ShowEmptyWindow();
        Assert.Equal(LightWindow, WindowSurface(window));

        settings.Update(current => current with { Theme = ThemeSetting.Dark });
        Settle(window);

        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, appearance.EffectiveTheme);
        Assert.Equal(1, changes);
        Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);
        Assert.Equal(DarkWindow, WindowSurface(window));
        Assert.Equal(DarkWindow, Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
    }

    [AvaloniaFact]
    public void ChoosingLightSwitchesBack()
    {
        var (app, settings, appearance) = Start();
        var window = ShowEmptyWindow();
        settings.Update(current => current with { Theme = ThemeSetting.Dark });
        Settle(window);
        Assert.Equal(DarkWindow, WindowSurface(window));

        settings.Update(current => current with { Theme = ThemeSetting.Light });
        Settle(window);

        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Light, appearance.EffectiveTheme);
        Assert.Equal(ThemeVariant.Light, window.ActualThemeVariant);
        Assert.Equal(LightWindow, WindowSurface(window));
        Assert.Equal(LightWindow, Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
    }

    [AvaloniaFact]
    public async Task TheSwitchNeedsNoRestart()
    {
        var (_, settings, _) = Start();
        var (window, viewModel, view) = Show(1024, settings);
        await OpenAsync(window, viewModel, view, "hello_world.pdf");
        var statusBar = Find<Border>(window, "StatusBarRow");
        Assert.Equal(LightWindow, PixelOf(window, statusBar));

        // The open window, with its document, changes in the next frame: no new window, no restart.
        settings.Update(current => current with { Theme = ThemeSetting.Dark });
        Settle(window);
        Assert.Equal(DarkWindow, PixelOf(window, statusBar));
        Assert.Same(view, Find<DocumentView>(window, "DocumentView"));
        Assert.NotNull(view.Document);

        settings.Update(current => current with { Theme = ThemeSetting.Light });
        Settle(window);
        Assert.Equal(LightWindow, PixelOf(window, statusBar));
    }

    [AvaloniaFact]
    public void ReduceMotionSetsBothDurationsToZero()
    {
        var (app, settings, _) = Start();

        // Design system 5.6: panels and popups open in 150 ms and close in 100 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(150), Duration(app, Appearance.OpenDurationKey));
        Assert.Equal(TimeSpan.FromMilliseconds(100), Duration(app, Appearance.CloseDurationKey));

        settings.Update(current => current with { ReduceMotion = true });
        Assert.Equal(TimeSpan.Zero, Duration(app, Appearance.OpenDurationKey));
        Assert.Equal(TimeSpan.Zero, Duration(app, Appearance.CloseDurationKey));

        settings.Update(current => current with { ReduceMotion = false });
        Assert.Equal(TimeSpan.FromMilliseconds(150), Duration(app, Appearance.OpenDurationKey));
        Assert.Equal(TimeSpan.FromMilliseconds(100), Duration(app, Appearance.CloseDurationKey));
    }

    [AvaloniaFact]
    public async Task ReduceMotionOpensThePanelWithoutTransition()
    {
        var (_, settings, _) = Start();

        // Narrow, so no panel opens by itself.
        var (window, viewModel, view) = Show(800, settings);
        await OpenAsync(window, viewModel, view, "outline.pdf");
        var host = Find<Panel>(window, "PanelHost");

        // With motion the panel fades in over the duration of the resource.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Settle(window);
        var fade = Assert.IsType<DoubleTransitionProbe>(DoubleTransitionProbe.Of(host));
        Assert.Equal(TimeSpan.FromMilliseconds(150), fade.Duration);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        await WaitUntilAsync(window, () => !host.IsVisible);

        // Without motion it is there at once, fully opaque, and gone at once when it closes.
        settings.Update(current => current with { ReduceMotion = true });
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Assert.True(host.IsVisible);
        Assert.Equal(1, host.Opacity);
        Assert.Null(DoubleTransitionProbe.Of(host));

        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Assert.False(host.IsVisible);
        Assert.Equal(0, host.Opacity);
    }

    [AvaloniaFact]
    public async Task PanelWidthIsRestoredAndSaved()
    {
        var (_, settings, _) = Start();
        settings.Update(current => current with { PanelWidth = 300 });

        // A new window starts with the stored width, and the panel that opens by itself has it.
        var (window, viewModel, view) = Show(1200, settings);
        Assert.Same(settings, viewModel.Settings);
        Assert.Equal(300, viewModel.PanelWidth);
        await OpenAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => viewModel.ActivePanel == SidePanelKind.Pages && Find<Panel>(window, "PanelHost").Opacity >= 1);
        Assert.Equal(300, Find<Panel>(window, "PanelHost").Bounds.Width);

        // Dragging the edge writes the width once, when the button is released.
        var splitter = Find<GridSplitter>(window, "PanelSplitter");
        var start = CenterOf(window, splitter);
        var writes = 0;
        settings.Changed += _ => writes++;
        window.MouseDown(start, MouseButton.Left);
        Settle(window);
        window.MouseMove(start + new Point(20, 0), RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseMove(start + new Point(40, 0), RawInputModifiers.LeftMouseButton);
        Settle(window);
        Assert.Equal(340, viewModel.PanelWidth, 0.5);
        Assert.Equal(300, settings.Current.PanelWidth);
        Assert.Equal(0, writes);
        window.MouseUp(start + new Point(40, 0), MouseButton.Left);
        Settle(window);
        Assert.Equal(340, settings.Current.PanelWidth, 0.5);
        Assert.Equal(1, writes);

        // The next window starts with it.
        var (_, next, _) = Show(1200, settings);
        Assert.Equal(340, next.PanelWidth, 0.5);

        // A double click on the edge goes back to the default width and stores it.
        start = CenterOf(window, splitter);
        Click(window, start);
        Click(window, start);
        Assert.Equal(AppSettings.DefaultPanelWidth, viewModel.PanelWidth);
        Assert.Equal(AppSettings.DefaultPanelWidth, settings.Current.PanelWidth);
    }

    [AvaloniaFact]
    public async Task TheLastChosenPanelOpensAfterTheFirstSharpPage()
    {
        var (_, settings, _) = Start();
        Assert.Equal(StartPanelSetting.Pages, settings.Current.StartPanel);

        // First start: "Pages" opens by itself; the user chooses "Contents".
        var (window, viewModel, view) = Show(1024, settings);
        await OpenAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => viewModel.ActivePanel == SidePanelKind.Pages);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Contents);
        Assert.Equal(StartPanelSetting.Contents, settings.Current.StartPanel);
        _cleanup.Remove(_cleanup[^1]);
        window.Close();

        // Next start: "Contents" opens after the first sharp page.
        (window, viewModel, view) = Show(1024, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Contents);
        Assert.True(viewModel.IsContentsPanelOpen);

        // Closing it means no panel next time.
        viewModel.CloseSidePanelCommand.Execute(null);
        Assert.Equal(StartPanelSetting.None, settings.Current.StartPanel);
        (window, viewModel, view) = Show(1024, settings);
        var drawn = false;
        view.FirstSharpTileDrawn += () => drawn = true;
        tab = await OpenAsync(window, viewModel, view, "outline.pdf");
        await WaitUntilAsync(window, () => drawn);
        Settle(window);
        Settle(window);
        Assert.Equal(SidePanelKind.None, tab.SidePanel);

        // Opening "Pages" by hand makes it the start panel again.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Assert.Equal(StartPanelSetting.Pages, settings.Current.StartPanel);
    }

    [AvaloniaFact]
    public async Task TheSearchPanelIsNeverTheStartPanel()
    {
        var (_, settings, _) = Start();
        settings.Update(current => current with { StartPanel = StartPanelSetting.Contents });
        var (window, viewModel, view) = Show(800, settings);
        var tab = await OpenAsync(window, viewModel, view, "outline.pdf");

        // The action bar entry, Ctrl+F and closing the search leave the start panel as it was.
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Search);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        Assert.Equal(StartPanelSetting.Contents, settings.Current.StartPanel);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Search);
        Assert.Equal(StartPanelSetting.Contents, settings.Current.StartPanel);
        viewModel.OpenSearchCommand.Execute(null);
        Settle(window);
        Assert.Equal(SidePanelKind.Search, tab.SidePanel);
        viewModel.CloseSidePanelCommand.Execute(null);
        Assert.Equal(StartPanelSetting.Contents, settings.Current.StartPanel);

        // Switching from "Pages" to "Search" keeps "Pages".
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        Assert.Equal(StartPanelSetting.Pages, settings.Current.StartPanel);
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Search);
        Assert.Equal(StartPanelSetting.Pages, settings.Current.StartPanel);
    }

    private (Application App, UserSettings Settings, Appearance Appearance) Start()
    {
        var app = Application.Current ?? throw new InvalidOperationException("No app.");
        _settings = UserSettings.Detached();
        _appearance = new Appearance(app, _settings);
        return (app, _settings, _appearance);
    }

    private static TimeSpan Duration(Application app, string key)
    {
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value), $"Resource {key} is missing");
        return Assert.IsType<TimeSpan>(value);
    }

    private Window ShowEmptyWindow()
    {
        // No variant of its own: it follows the app.
        var window = new Window { Width = 400, Height = 300 };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("Pw.Brush.Surface.Window"));
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return window;
    }

    // The token as the window finds it for the variant it shows.
    private static Color WindowSurface(Window window)
    {
        Assert.True(window.TryFindResource("Pw.Brush.Surface.Window", window.ActualThemeVariant, out var brush));
        return Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
    }

    /// <summary>The colour of the frame at the left end of <paramref name="control"/>, half way down.</summary>
    private static Color PixelOf(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(2, control.Bounds.Height / 2), window)!.Value;
        using var frame = new FramePixels(window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame."));
        return frame.GetColor((int)point.X, (int)point.Y);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(double width, UserSettings settings)
    {
        // No variant of its own: the window follows the app, like the real one.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        return tab;
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
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

    /// <summary>The opacity transition of a control, if it has one.</summary>
    private sealed record DoubleTransitionProbe(TimeSpan Duration)
    {
        public static DoubleTransitionProbe? Of(Animatable control) =>
            control.Transitions?.OfType<Avalonia.Animation.DoubleTransition>().FirstOrDefault() is { } transition
                ? new DoubleTransitionProbe(transition.Duration)
                : null;
    }
}
