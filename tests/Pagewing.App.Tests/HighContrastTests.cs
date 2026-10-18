using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
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
/// The fixed high-contrast palette (Windows <see cref="ColorContrastPreference.High"/>, design system 5.1): its tokens,
/// their contrast and when <see cref="Appearance"/> selects it. The contrast request of the system comes from a fake
/// <see cref="IContrastSource"/>. Each test puts the app back to the defaults and removes the high-contrast resources.
/// </summary>
// Changes the theme of the shared app, like ThemeSwitchTests; the window reads Strings (see LocalizationTests).
[Collection("Localization")]
public sealed class HighContrastTests : IDisposable
{
    private static readonly Color LightWindow = Color.Parse("#EEEFF2");
    private static readonly Color DarkWindow = Color.Parse("#15171B");
    private static readonly Color Black = Color.Parse("#000000");
    private static readonly Color Yellow = Color.Parse("#FFFF00");
    private static readonly Color Cyan = Color.Parse("#00FFFF");

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly string[] Surfaces =
        ["Surface.Window", "Surface.Toolbar", "Surface.Sidebar", "Surface.Canvas", "Surface.Raised", "Surface.Hover", "Accent.Subtle"];

    private readonly List<IDisposable> _cleanup = [];
    private readonly FakeContrast _contrast = new();
    private UserSettings? _settings;
    private Appearance? _appearance;

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        // Back to the defaults (system theme without a contrast request) before the appearance lets go of the app.
        _contrast.Set(ColorContrastPreference.NoPreference);
        _settings?.Update(_ => AppSettings.Default);
        _appearance?.Dispose();
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Default;
        app.Resources.ThemeDictionaries.Remove(HighContrast.Variant);
    }

    [AvaloniaFact]
    public void TheVariantHasEveryTokenOfTheOtherThemes()
    {
        var tokens = Assert.IsAssignableFrom<ResourceDictionary>(AvaloniaXamlLoader.Load(new Uri("avares://Pagewing/Styles/Tokens.axaml")));
        var light = Keys(Assert.IsAssignableFrom<IResourceDictionary>(tokens.ThemeDictionaries[ThemeVariant.Light]));
        var dark = Keys(Assert.IsAssignableFrom<IResourceDictionary>(tokens.ThemeDictionaries[ThemeVariant.Dark]));
        var highContrast = new Styles.HighContrast();

        // The same keys in all three: a key missing in high contrast would fall back to the dark value unnoticed.
        Assert.Equal(light, dark);
        Assert.Equal(light, Keys(highContrast));
        Assert.Contains("Pw.Shadow.Page", light);
        Assert.Contains("Pw.Brush.Selection.PageOnDark", light);

        // Each value has the type of the other themes, and every brush has the colour of its colour token.
        var lightDictionary = Assert.IsAssignableFrom<IResourceDictionary>(tokens.ThemeDictionaries[ThemeVariant.Light]);
        foreach (var key in light)
        {
            Assert.Equal(lightDictionary[key]!.GetType(), highContrast[key]!.GetType());
            if (highContrast[key] is SolidColorBrush brush)
            {
                Assert.Equal(Assert.IsType<Color>(highContrast[key.Replace("Pw.Brush.", "Pw.Color.", StringComparison.Ordinal)]), brush.Color);
            }
        }
    }

    [AvaloniaFact]
    public void EveryTextPairReachesSevenToOne()
    {
        const double minimum = 7.0;
        var pairs = new List<(string Foreground, string Background)>();

        // Text, accent (links, active entries), focus and borders on every surface, also hovered and selected.
        foreach (var surface in Surfaces)
        {
            pairs.Add(("Text.Primary", surface));
            pairs.Add(("Text.Secondary", surface));
            pairs.Add(("Accent.Default", surface));
            pairs.Add(("Focus.Ring", surface));
            pairs.Add(("Border.Strong", surface));
            pairs.Add(("Border.Subtle", surface));
        }

        // Errors (text, icon, border of a field) stand on the plain surfaces, never on a hovered or selected entry.
        // Disabled text is exempt from the contrast rule (WCAG 1.4.3), but on the plain surfaces it reaches 7:1 too.
        foreach (var surface in Surfaces.Take(5))
        {
            pairs.Add(("Danger.Default", surface));
            pairs.Add(("Text.Disabled", surface));
        }
        pairs.Add(("Accent.On", "Accent.Default"));
        pairs.Add(("Search.On", "Search.Hit"));
        pairs.Add(("Search.On", "Search.Current"));

        foreach (var (foreground, background) in pairs)
        {
            var ratio = Wcag.Contrast(Token(foreground), Token(background));
            Assert.True(ratio >= minimum, $"high contrast: {foreground} on {background} is {ratio:F2}:1, expected at least {minimum}:1");
        }

        // The fixed values of the decision.
        Assert.Equal(Black, Token("Surface.Window"));
        Assert.Equal(Colors.White, Token("Text.Primary"));
        Assert.Equal(Colors.White, Token("Text.Secondary"));
        Assert.Equal(Yellow, Token("Accent.Default"));
        Assert.Equal(Cyan, Token("Focus.Ring"));
        Assert.Equal(Colors.White, Token("Border.Strong"));
        Assert.Equal(Color.Parse("#A0A0A0"), Token("Text.Disabled"));
        Assert.Equal(Color.Parse("#FF8080"), Token("Danger.Default"));
        Assert.Equal(Black, Token("Search.On"));

        // The pages stay black on white (PDF content): the page text on the marks of a light page still reaches 7:1,
        // and the 1 px page edge sets even a dark page off the black canvas.
        foreach (var mark in new[] { "Search.Hit", "Search.Current", "Selection.Page" })
        {
            var ground = ColorBlend.Over(Token(mark), Colors.White);
            Assert.True(Wcag.Contrast(Colors.Black, ground) >= minimum, $"page text on {mark}");
        }

        Assert.True(Wcag.Contrast(Token("Page.Border"), Token("Surface.Canvas")) >= minimum);
        Assert.True(Wcag.Contrast(Token("Page.Border"), Token("Page.DarkBackground")) >= minimum);
    }

    [AvaloniaFact]
    public void ABorderIsNeverOnlyColour()
    {
        Start(ColorContrastPreference.High);
        var button = new Button { Content = "Open", Width = 120 };
        var window = ShowWindow(new StackPanel { Margin = new Thickness(40), Children = { button } });

        // The secondary button keeps its 1 px border, white on black.
        Assert.Equal(new Thickness(1), button.BorderThickness);
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(button.BorderBrush).Color);

        // Keyboard focus: the 2 px ring in focus.ring with 1 px gap, around the border.
        button.Focus(NavigationMethod.Tab);
        Settle(window);
        var left = button.TranslatePoint(new Point(0, button.Bounds.Height / 2), window)!.Value;
        using var frame = new FramePixels(window.CaptureRenderedFrame()!);
        var x = (int)left.X;
        var y = (int)left.Y;
        Assert.Equal(Colors.White, frame.GetColor(x, y));
        Assert.Equal(Black, frame.GetColor(x - 1, y));
        Assert.Equal(Cyan, frame.GetColor(x - 2, y));
        Assert.Equal(Cyan, frame.GetColor(x - 3, y));
        Assert.Equal(Black, frame.GetColor(x - 4, y));
    }

    [AvaloniaFact]
    public void RequestedContrastSelectsTheVariantForSystemTheme()
    {
        var (app, settings, appearance) = Start(ColorContrastPreference.High);
        Assert.Equal(ThemeSetting.System, settings.Current.Theme);
        Assert.Equal(HighContrast.Variant, app.RequestedThemeVariant);
        Assert.Equal(HighContrast.Variant, appearance.EffectiveTheme);
        Assert.Equal(ThemeVariant.Dark, HighContrast.Variant.InheritVariant);

        // The tokens and the Fluent controls (here a check box, which Pagewing does not restyle) take the fixed palette.
        var check = new CheckBox { IsChecked = true, Content = "Match case" };
        var window = ShowWindow(check);
        Assert.Equal(HighContrast.Variant, window.ActualThemeVariant);
        Assert.Equal(Black, WindowSurface(window));
        Assert.Equal(Yellow, CheckFill(check));
        Assert.Equal(Colors.White, Assert.IsAssignableFrom<ISolidColorBrush>(check.Foreground).Color);

        // The pages stay light when they follow the theme: high contrast is not the dark theme.
        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.FollowTheme });
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { AppTheme = appearance };
        _cleanup.Add(viewModel);
        Assert.Equal(PageTheme.Light, viewModel.AppPageTheme);
    }

    [AvaloniaFact]
    public void AnExplicitThemeBeatsTheContrastRequest()
    {
        var (app, settings, appearance) = Start(ColorContrastPreference.High);
        var window = ShowWindow(new Panel());
        Assert.Equal(Black, WindowSurface(window));

        settings.Update(current => current with { Theme = ThemeSetting.Dark });
        Settle(window);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, appearance.EffectiveTheme);
        Assert.Equal(DarkWindow, WindowSurface(window));

        settings.Update(current => current with { Theme = ThemeSetting.Light });
        Settle(window);
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        Assert.Equal(LightWindow, WindowSurface(window));

        // A change of the request while a theme is chosen changes nothing; back to "System" it applies again.
        _contrast.Set(ColorContrastPreference.NoPreference);
        _contrast.Set(ColorContrastPreference.High);
        Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
        settings.Update(current => current with { Theme = ThemeSetting.System });
        Settle(window);
        Assert.Equal(HighContrast.Variant, app.RequestedThemeVariant);
        Assert.Equal(Black, WindowSurface(window));
    }

    [AvaloniaFact]
    public void NoSignalMeansNoHighContrast()
    {
        // Linux and the headless platform report no contrast preference (or no colour values at all).
        Assert.False(HighContrast.IsRequested(null));
        Assert.False(HighContrast.IsRequested(new PlatformColorValues()));
        Assert.False(HighContrast.IsRequested(new PlatformColorValues { ContrastPreference = ColorContrastPreference.NoPreference }));
        Assert.True(HighContrast.IsRequested(new PlatformColorValues { ContrastPreference = ColorContrastPreference.High }));

        var (app, _, appearance) = Start(null);
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.NotEqual(HighContrast.Variant, appearance.EffectiveTheme);
        appearance.Dispose();

        // The appearance of the app reads the platform: headless has no contrast request, so nothing of high contrast
        // is loaded (not in the startup path).
        var platform = new Appearance(app, _settings!);
        _cleanup.Add(platform);
        Assert.Equal(ColorContrastPreference.NoPreference, app.PlatformSettings!.GetColorValues().ContrastPreference);
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.False(app.Resources.ThemeDictionaries.ContainsKey(HighContrast.Variant));
    }

    [AvaloniaFact]
    public void TheSwitchWorksAtRuntime()
    {
        var (app, _, appearance) = Start(ColorContrastPreference.NoPreference);
        var changes = 0;
        appearance.EffectiveThemeChanged += () => changes++;
        var check = new CheckBox { IsChecked = true, Content = "Match case" };
        var window = ShowWindow(check);
        Assert.Equal(LightWindow, WindowSurface(window));
        var lightFill = CheckFill(check);

        // The system asks for high contrast (PlatformSettings.ColorValuesChanged): the open window follows at once.
        _contrast.Set(ColorContrastPreference.High);
        Settle(window);
        Assert.Equal(HighContrast.Variant, app.RequestedThemeVariant);
        Assert.Equal(1, changes);
        Assert.Equal(Black, WindowSurface(window));
        Assert.Equal(Black, PixelOf(window, window));
        Assert.Equal(Yellow, CheckFill(check));

        // And back.
        _contrast.Set(ColorContrastPreference.NoPreference);
        Settle(window);
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.Equal(2, changes);
        Assert.Equal(LightWindow, WindowSurface(window));
        Assert.Equal(LightWindow, PixelOf(window, window));
        Assert.Equal(lightFill, CheckFill(check));

        // The same request again changes nothing.
        _contrast.Set(ColorContrastPreference.NoPreference);
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public void AThemeChangeBeforeTheContrastEventShowsNoOtherThemeFirst()
    {
        // On Windows the app follows the new colour values of the platform before the contrast event reaches the
        // appearance (Avalonia subscribed first): the theme changes while the request is not read yet. Played here with
        // a request that changes without its event and a change of the theme that lets the app take the platform's.
        var (app, settings, appearance) = Start(ColorContrastPreference.NoPreference);
        settings.Update(current => current with { Theme = ThemeSetting.Dark });
        var announced = new List<ThemeVariant>();
        appearance.EffectiveThemeChanged += () => announced.Add(appearance.EffectiveTheme);
        _contrast.Values = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = ColorContrastPreference.High };

        settings.Update(current => current with { Theme = ThemeSetting.System });

        // Only high contrast is announced, not the platform's light theme on the way: pages that follow the theme do
        // not turn and turn back.
        Assert.Equal(HighContrast.Variant, app.RequestedThemeVariant);
        Assert.Equal([HighContrast.Variant], announced);

        // The event that comes afterwards changes nothing.
        _contrast.Set(ColorContrastPreference.High);
        Assert.Single(announced);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public async Task RegionEdgesAreSeparatedOnlyInHighContrast(string theme)
    {
        // Design system 5.3: in high contrast every surface is black, so 1 px border.subtle lines part the regions where
        // they meet; in light and dark the same pixels keep the colours of the surfaces (the lines are 0 wide).
        var highContrast = theme == "HighContrast";
        var (_, settings, _) = Start(highContrast ? ColorContrastPreference.High : ColorContrastPreference.NoPreference);
        settings.Update(current => current with { Theme = theme switch { "Light" => ThemeSetting.Light, "Dark" => ThemeSetting.Dark, _ => ThemeSetting.System } });
        var variant = highContrast ? HighContrast.Variant : theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");
        await viewModel.OpenAsync(TestData.Pdf("hello_world.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);

        // The "Pages" panel beside the document, fully faded in. The window opens it by itself in a background job after
        // the first sharp tile (see AutoOpenedPanel); opened here only if it did not.
        Settle(window);
        Settle(window);
        if (viewModel.ActivePanel != SidePanelKind.Pages)
        {
            viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        }

        var host = Find<Panel>(window, "PanelHost");
        await WaitUntilAsync(window, () => host.IsVisible && host.Opacity >= 1);

        var edge = highContrast ? Token("Border.Subtle") : (Color?)null;
        Color Surface(string name) => edge ?? TokenTests.Token(name, variant);
        var titleBar = Find<TitleBar>(window, "TitleBar");
        var actionBar = Find<ActionBar>(window, "ActionBar");
        var activeTab = Find<Border>(window, "Tab");
        var dragArea = Find<Border>(window, "DragArea");
        var statusBar = Find<Border>(window, "StatusBarRow");
        var document = Find<Border>(window, "DocumentArea");
        var bottomOfTitleBar = (int)titleBar.Bounds.Height - 1;
        var workMiddle = (int)(titleBar.Bounds.Height + 200);
        var tabLeft = activeTab.TranslatePoint(default, window)!.Value;
        var tabMiddle = (int)(tabLeft.Y + (activeTab.Bounds.Height / 2));
        using var frame = new FramePixels(window.CaptureRenderedFrame()!);

        // Title bar | panel and document area: the bottom row of the title bar, beside the tabs.
        var dragX = (int)(dragArea.TranslatePoint(default, window)!.Value.X + (dragArea.Bounds.Width / 2));
        Assert.Equal(Surface("Surface.Window"), frame.GetColor(dragX, bottomOfTitleBar));

        // The active tab: outlined at the sides, open at the bottom (it flows into the document area).
        Assert.Equal(Surface("Surface.Canvas"), frame.GetColor((int)tabLeft.X, tabMiddle));
        Assert.Equal(Surface("Surface.Canvas"), frame.GetColor((int)(tabLeft.X + activeTab.Bounds.Width) - 1, tabMiddle));
        Assert.Equal(TokenTests.Token("Surface.Canvas", variant), frame.GetColor((int)(tabLeft.X + (activeTab.Bounds.Width / 2)), bottomOfTitleBar));

        // Action bar | panel: the right column of the action bar.
        Assert.Equal(Surface("Surface.Window"), frame.GetColor((int)actionBar.Bounds.Width - 1, workMiddle));

        // Panel | document area: the right column of the panel, in its header.
        var panelRight = (int)(host.TranslatePoint(default, window)!.Value.X + host.Bounds.Width) - 1;
        Assert.Equal(Surface("Surface.Sidebar"), frame.GetColor(panelRight, (int)titleBar.Bounds.Height + 20));

        // Document area | status bar: the bottom row of the document area.
        var documentLeft = (int)document.TranslatePoint(default, window)!.Value.X;
        var statusTop = (int)statusBar.TranslatePoint(default, window)!.Value.Y;
        Assert.Equal(Surface("Surface.Canvas"), frame.GetColor(documentLeft + 30, statusTop - 1));

        // Inside the regions the surfaces stay as they are.
        Assert.Equal(TokenTests.Token("Surface.Canvas", variant), frame.GetColor(documentLeft + 30, statusTop - 3));
        Assert.Equal(TokenTests.Token("Surface.Window", variant), frame.GetColor((int)actionBar.Bounds.Width - 3, workMiddle));
    }

    [AvaloniaFact]
    public async Task AChangeFromAnotherThreadAfterDisposeChangesNothing()
    {
        // The platform may announce a change on another thread; the appearance posts it to the UI thread. Disposed in
        // between, it applies nothing.
        var (app, _, appearance) = Start(ColorContrastPreference.NoPreference);
        await Task.Run(() => _contrast.Set(ColorContrastPreference.High));
        appearance.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
    }

    private static Color Token(string name) => TokenTests.Token(name, HighContrast.Variant);

    private static SortedSet<string> Keys(IResourceDictionary dictionary) =>
        new(dictionary.Keys.Cast<string>(), StringComparer.Ordinal);

    private (Application App, UserSettings Settings, Appearance Appearance) Start(ColorContrastPreference? preference)
    {
        var app = Application.Current ?? throw new InvalidOperationException("No app.");
        _contrast.Values = preference is { } value ? new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = value } : null;
        _settings = UserSettings.Detached();
        _appearance = new Appearance(app, _settings, _contrast);
        return (app, _settings, _appearance);
    }

    private Window ShowWindow(Control content)
    {
        // No variant of its own: it follows the app, like the real window.
        var window = new Window { Width = 400, Height = 300, Content = content };
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

    /// <summary>The fill of the box of a checked check box (Fluent template part NormalRectangle).</summary>
    private static Color CheckFill(CheckBox check)
    {
        var box = check.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "NormalRectangle");
        return Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color;
    }

    /// <summary>The colour of the frame near the bottom right corner of <paramref name="control"/>.</summary>
    private static Color PixelOf(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width - 4, control.Bounds.Height - 4), window)!.Value;
        using var frame = new FramePixels(window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame."));
        return frame.GetColor((int)point.X, (int)point.Y);
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

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    /// <summary>The contrast request of the system, switched by the test.</summary>
    private sealed class FakeContrast : IContrastSource
    {
        public event Action? ColorValuesChanged;

        public PlatformColorValues? Values { get; set; }

        public PlatformColorValues? GetColorValues() => Values;

        public void Set(ColorContrastPreference preference)
        {
            Values = new PlatformColorValues { ThemeVariant = PlatformThemeVariant.Dark, ContrastPreference = preference };
            ColorValuesChanged?.Invoke();
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
