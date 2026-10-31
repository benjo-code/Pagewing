using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using RenderOptions = Pagewing.Core.Pdf.RenderOptions;

namespace Pagewing.App.Tests;

/// <summary>
/// The dark page in the UI (M6): which theme a tab renders in (<see cref="TabViewModel.EffectivePageTheme"/>: its
/// override, else the setting <see cref="AppSettings.PageAppearance"/> with the theme in effect), and how the document
/// canvas, its marks and the thumbnails follow it. The window tests render with PDFium; the canvas tests use a fake
/// render function, so the timing of a theme switch can be played exactly.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DarkPageViewTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly Color DarkGround = Color.FromRgb(0x1E, 0x21, 0x27);
    private static readonly Color DarkText = Color.FromRgb(0xE3, 0xE5, 0xE8);

    // Colours of the fake render function: a light page is red, a dark page blue, so a stale image is easy to spot.
    private static readonly Color LightImage = Color.FromRgb(200, 30, 30);
    private static readonly Color DarkImage = Color.FromRgb(30, 30, 200);

    /// <summary>An empty part of page 1 of text-basic.pdf (300 x 200 pt; text and line are in its upper half).</summary>
    private static readonly PageRect EmptyArea = new(20, 140, 120, 190);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public DarkPageViewTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task LightIsTheDefaultAndPagesStayWhite()
    {
        // The default setting keeps the page as the PDF defines it, also in a dark theme.
        var (window, viewModel, view) = Show(UserSettings.Detached(), new FakeTheme(ThemeVariant.Dark));
        var tab = await OpenAsync(window, viewModel, view, "text-basic.pdf");

        Assert.Equal(PageAppearanceSetting.Light, viewModel.Settings.Current.PageAppearance);
        Assert.Null(tab.PageThemeOverride);
        Assert.Equal(PageTheme.Light, tab.AppPageTheme);
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);
        Assert.Equal(PageTheme.Light, tab.Session.PageTheme);
        Assert.Equal(PageTheme.Light, view.Canvas.PageTheme);
        Assert.Equal(Colors.White, PixelAt(window, view, 0, EmptyArea));
    }

    [AvaloniaFact]
    public async Task FollowThemeInTheDarkThemeRendersDark()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.FollowTheme });
        var theme = new FakeTheme(ThemeVariant.Light);
        var (window, viewModel, view) = Show(settings, theme);
        var tab = await OpenAsync(window, viewModel, view, "text-basic.pdf");

        // "Follow the theme" in a light theme: the page stays light.
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);
        Assert.Equal(Colors.White, PixelAt(window, view, 0, EmptyArea));

        // The theme turns dark: the session renders dark, and so does the canvas, without opening the file again.
        var changes = new List<string?>();
        tab.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        theme.Set(ThemeVariant.Dark);
        Assert.Equal(PageTheme.Dark, tab.AppPageTheme);
        Assert.Equal(PageTheme.Dark, tab.EffectivePageTheme);
        Assert.Equal(PageTheme.Dark, tab.Session.PageTheme);
        Assert.Contains(nameof(TabViewModel.EffectivePageTheme), changes);
        Settle(window);
        Assert.Equal(PageTheme.Dark, view.Canvas.PageTheme);
        await WaitUntilAsync(window, () => view.Canvas.LastSharpDestinations.Count > 0 && Near(PixelAt(window, view, 0, EmptyArea), DarkGround, 3));

        // And back.
        theme.Set(ThemeVariant.Light);
        Assert.Equal(PageTheme.Light, tab.Session.PageTheme);
        await WaitUntilAsync(window, () => view.Canvas.LastSharpDestinations.Count > 0 && PixelAt(window, view, 0, EmptyArea) == Colors.White);
    }

    [AvaloniaFact]
    public async Task TheOverrideBeatsTheSetting()
    {
        var settings = UserSettings.Detached();
        var theme = new FakeTheme(ThemeVariant.Dark);
        using var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { AppTheme = theme };
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);

        // The tab's own choice wins over the setting, in both directions.
        tab.PageThemeOverride = PageTheme.Dark;
        Assert.Equal(PageTheme.Light, tab.AppPageTheme);
        Assert.Equal(PageTheme.Dark, tab.EffectivePageTheme);
        Assert.Equal(PageTheme.Dark, tab.Session.PageTheme);

        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.FollowTheme });
        Assert.Equal(PageTheme.Dark, tab.AppPageTheme);
        tab.PageThemeOverride = PageTheme.Light;
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);
        Assert.Equal(PageTheme.Light, tab.Session.PageTheme);

        // Without the override the setting applies again.
        tab.PageThemeOverride = null;
        Assert.Equal(PageTheme.Dark, tab.EffectivePageTheme);
        Assert.Equal(PageTheme.Dark, tab.Session.PageTheme);

        // The toggle always sets the override to the other value.
        tab.TogglePageThemeCommand.Execute(null);
        Assert.Equal(PageTheme.Light, tab.PageThemeOverride);
        Assert.Equal(PageTheme.Light, tab.Session.PageTheme);
        tab.TogglePageThemeCommand.Execute(null);
        Assert.Equal(PageTheme.Dark, tab.PageThemeOverride);
        Assert.Equal(PageTheme.Dark, tab.Session.PageTheme);
    }

    [AvaloniaFact]
    public async Task EveryTabFollowsTheSettingAndTheTheme()
    {
        var settings = UserSettings.Detached();
        var theme = new FakeTheme(ThemeVariant.Dark);
        using var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { AppTheme = theme };
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        await viewModel.OpenInBackgroundAsync([TestData.Pdf("text-rich.pdf")]);
        Assert.Equal(2, viewModel.Tabs.Count);
        var (active, inactive) = (viewModel.Tabs[0], viewModel.Tabs[1]);
        Assert.False(inactive.IsActive);

        // The setting reaches every tab, the inactive one too.
        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.FollowTheme });
        Assert.All(viewModel.Tabs, tab => Assert.Equal(PageTheme.Dark, tab.EffectivePageTheme));
        Assert.All(viewModel.Tabs, tab => Assert.Equal(PageTheme.Dark, tab.Session.PageTheme));

        // So does the theme.
        theme.Set(ThemeVariant.Light);
        Assert.All(viewModel.Tabs, tab => Assert.Equal(PageTheme.Light, tab.Session.PageTheme));
        theme.Set(ThemeVariant.Dark);

        // A tab added later starts with it; one with an override keeps its own.
        active.PageThemeOverride = PageTheme.Light;
        await viewModel.OpenAsync(TestData.Pdf("text-dense.pdf"));
        var added = viewModel.ActiveTab!;
        Assert.Equal(PageTheme.Dark, added.EffectivePageTheme);
        Assert.Equal(PageTheme.Dark, added.Session.PageTheme);
        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.Light });
        Assert.Equal(PageTheme.Light, added.Session.PageTheme);
        Assert.Equal(PageTheme.Light, active.EffectivePageTheme);
        Assert.Equal(PageTheme.Light, inactive.EffectivePageTheme);

        // A closed window stops following.
        viewModel.Dispose();
        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.FollowTheme });
        Assert.Equal(PageTheme.Light, added.AppPageTheme);
    }

    [AvaloniaFact]
    public async Task AMovedTabKeepsItsOverride()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { PageAppearance = PageAppearanceSetting.FollowTheme });
        var theme = new FakeTheme(ThemeVariant.Dark);
        using var source = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { AppTheme = theme };
        using var target = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { AppTheme = theme };
        await source.OpenAsync(TestData.Pdf("text-basic.pdf"));
        await source.OpenAsync(TestData.Pdf("text-rich.pdf"));
        var moved = source.ActiveTab!;
        moved.PageThemeOverride = PageTheme.Light;

        Assert.True(source.DetachTab(moved));
        target.AttachTab(moved);
        Assert.Equal(PageTheme.Light, moved.PageThemeOverride);
        Assert.Equal(PageTheme.Light, moved.EffectivePageTheme);
        Assert.Equal(PageTheme.Light, moved.Session.PageTheme);

        // It follows the window it is in now, not the one it left.
        theme.Set(ThemeVariant.Light);
        moved.PageThemeOverride = null;
        Assert.Equal(PageTheme.Light, moved.EffectivePageTheme);
        source.Dispose();
        theme.Set(ThemeVariant.Dark);
        Assert.Equal(PageTheme.Dark, moved.EffectivePageTheme);
        Assert.Equal(PageTheme.Dark, moved.Session.PageTheme);
    }

    [AvaloniaFact]
    public async Task TogglingKeepsPageAndZoom()
    {
        var (window, viewModel, view) = Show(UserSettings.Detached(), new FakeTheme(ThemeVariant.Light));
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        tab.Zoom = 1.5;
        Settle(window);
        tab.GoToPage(5);
        Settle(window);
        var selection = new TextSelection(new TextPosition(4, 0), new TextPosition(4, 12));
        tab.TextSelection = selection;
        Settle(window);
        var offset = view.ScrollViewer.Offset;
        var layout = view.Layout;

        tab.TogglePageThemeCommand.Execute(null);
        Settle(window);
        Assert.Equal(PageTheme.Dark, tab.PageThemeOverride);
        Assert.Equal(PageTheme.Dark, view.Canvas.PageTheme);
        Assert.Same(layout, view.Layout);
        Assert.Equal(5, tab.CurrentPage);
        Assert.Equal(1.5, tab.Zoom);
        Assert.Equal(1.5, view.Layout!.Zoom);
        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.Equal(selection, tab.TextSelection);

        // The pages come back sharp, in the dark theme.
        await WaitUntilAsync(window, () => view.Canvas.LastSharpDestinations.Any(entry => entry.Key.Page == 4));
        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.Equal(5, tab.CurrentPage);
        Assert.Contains(view.Canvas.LastHighlightDestinations, entry => entry.Highlight.Kind == HighlightKind.Selection);

        tab.TogglePageThemeCommand.Execute(null);
        Settle(window);
        Assert.Equal(PageTheme.Light, tab.PageThemeOverride);
        Assert.Equal(PageTheme.Light, view.Canvas.PageTheme);
        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.Equal(selection, tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task SwitchingTabsShowsEachTabInItsOwnTheme()
    {
        var (window, viewModel, view) = Show(UserSettings.Detached(), new FakeTheme(ThemeVariant.Light));
        var dark = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        dark.PageThemeOverride = PageTheme.Dark;
        await WaitUntilAsync(window, () => view.Canvas.LastSharpDestinations.Count > 0 && Near(PixelAt(window, view, 0, EmptyArea), DarkGround, 3));

        // Another file in the second tab: text-rich.pdf (400 x 400 pt) has nothing in the lower part of page 1.
        var light = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var margin = new PageRect(10, 330, 60, 360);
        Assert.Equal(PageTheme.Light, view.Canvas.PageTheme);
        Assert.Equal(Colors.White, PixelAt(window, view, 0, margin));

        viewModel.ActivateTabCommand.Execute(dark);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, dark.Session.Document) && view.Canvas.LastSharpDestinations.Count > 0);
        Assert.Equal(PageTheme.Dark, view.Canvas.PageTheme);
        Assert.True(Near(PixelAt(window, view, 0, EmptyArea), DarkGround, 3));

        viewModel.ActivateTabCommand.Execute(light);
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, light.Session.Document) && view.Canvas.LastSharpDestinations.Count > 0);
        Assert.Equal(PageTheme.Light, view.Canvas.PageTheme);
        Assert.Equal(Colors.White, PixelAt(window, view, 0, margin));
    }

    [AvaloniaFact]
    public async Task SearchMatchesAreVisibleOnADarkPage()
    {
        var (window, viewModel, view) = Show(UserSettings.Detached(), new FakeTheme(ThemeVariant.Light));
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        tab.PageThemeOverride = PageTheme.Dark;
        tab.Search.Query = "Pagewing";
        await WaitUntilAsync(window, () => tab.Search.State == SearchState.Done
            && view.Canvas.LastHighlightDestinations.Count > 0
            && view.Canvas.Bitmaps.Keys.Any(key => key.Page == 0 && !key.IsPreview));
        var drawn = view.Canvas.LastHighlightDestinations.Where(entry => entry.Highlight.Page == 0).ToArray();
        var hit = drawn.First(entry => entry.Highlight.Kind == HighlightKind.SearchHit).Destination;
        var current = drawn.First(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent).Destination;

        var hitColor = TokenTests.Token("Search.HitOnDark", ThemeVariant.Light);
        var currentColor = TokenTests.Token("Search.CurrentOnDark", ThemeVariant.Light);
        var hitGround = ColorBlend.Over(hitColor, DarkGround);
        var currentGround = ColorBlend.Over(currentColor, DarkGround);

        // The page text stays readable on the mixed ground of both marks.
        Assert.True(Wcag.Contrast(DarkText, hitGround) >= 4.5, $"page text on a hit is {Wcag.Contrast(DarkText, hitGround):F2}:1");
        Assert.True(Wcag.Contrast(DarkText, currentGround) >= 4.5, $"page text on the current hit is {Wcag.Contrast(DarkText, currentGround):F2}:1");

        using var frame = FramePixels.Snapshot(view.Canvas);

        // Filled, not multiplied (multiplied, the yellow would vanish on the dark ground): the mixed colour is there, the
        // bare ground is not, and the light text is still on top of it.
        Assert.True(Count(frame, hit, color => Near(color, hitGround, 4)) > 50, "no search.hitOnDark colour in the hit");
        Assert.Equal(0, Count(frame, hit.Deflate(2), color => Near(color, DarkGround, 2)));
        Assert.True(Count(frame, hit, color => color.G > 160) > 5, "the text on the hit is gone");
        Assert.True(Count(frame, current, color => Near(color, currentGround, 4)) > 50, "no search.currentOnDark colour in the current hit");
        Assert.True(Count(frame, current, color => color.G > 160) > 5, "the text on the current hit is gone");
        Assert.NotEqual(hitGround, currentGround);

        // The frame of the current hit is the page foreground: search.on would vanish on the dark page.
        var band = new Rect(current.X - 1, current.Y - 1, current.Width + 2, 2);
        Assert.True(Count(frame, band, color => Near(color, DarkText, 60)) > current.Width / 2, "no frame on the current hit");
        Assert.True(Wcag.Contrast(DarkText, DarkGround) >= 3);
        Assert.True(Wcag.Contrast(DarkText, currentGround) >= 3);
        var hitBand = new Rect(hit.X + 2, hit.Y - 2, hit.Width - 4, 1);
        Assert.Equal(0, Count(frame, hitBand, color => Near(color, DarkText, 40)));
    }

    [AvaloniaFact]
    public async Task SelectionIsVisibleOnADarkPage()
    {
        var (window, viewModel, view) = Show(UserSettings.Detached(), new FakeTheme(ThemeVariant.Light));
        var tab = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        tab.PageThemeOverride = PageTheme.Dark;
        await WaitUntilAsync(window, () => view.Canvas.LastSharpDestinations.Count > 0 && Near(PixelAt(window, view, 0, EmptyArea), DarkGround, 3));

        view.Canvas.SetHighlights([new PageHighlight(0, EmptyArea, HighlightKind.Selection)]);
        Settle(window);
        var entry = Assert.Single(view.Canvas.LastHighlightDestinations);

        using var frame = FramePixels.Snapshot(view.Canvas);
        var expected = ColorBlend.Over(TokenTests.Token("Selection.PageOnDark", ThemeVariant.Light), DarkGround);
        var actual = frame.GetColor((int)entry.Destination.Center.X, (int)entry.Destination.Center.Y);
        Assert.True(Near(actual, expected, 3), $"{actual} is not {expected}");
        Assert.False(Near(actual, DarkGround, 12), "the selection does not stand out from the dark page");
        Assert.True(Wcag.Contrast(DarkText, expected) >= 4.5, $"page text on the selection is {Wcag.Contrast(DarkText, expected):F2}:1");
        Assert.False(new HsvColor(expected).H is >= 40 and <= 70, "the selection is yellow");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void PlaceholderFollowsTheTheme(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        using var fixture = CanvasFixture.Show(variant, static (_, _, _) => new TaskCompletionSource<RenderedImage>().Task);
        var probe = new Point(fixture.Page.X + 20, fixture.Page.Y + 20);

        // A light page waits on surface.raised, a dark one on its own ground, so nothing flashes when the image comes.
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), fixture.PixelAt(probe));
        fixture.Canvas.PageTheme = PageTheme.Dark;
        Assert.True(Near(fixture.PixelAt(probe), DarkGround, 1), $"{fixture.PixelAt(probe)} is not the dark ground");
        fixture.Canvas.PageTheme = PageTheme.Light;
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), fixture.PixelAt(probe));
    }

    [AvaloniaFact]
    public void ASwitchDropsTilesOfTheOldTheme()
    {
        var lightSharp = new TaskCompletionSource();
        var dark = new TaskCompletionSource();
        using var fixture = CanvasFixture.Show(ThemeVariant.Light, async (request, options, cancellationToken) =>
        {
            if (options.PageTheme == PageTheme.Dark)
            {
                await dark.Task.WaitAsync(cancellationToken);
                return Solid(DarkImage);
            }

            // The light preview comes at once, the light sharp tile when the test lets it.
            if (!request.Key.IsPreview)
            {
                await lightSharp.Task.WaitAsync(cancellationToken);
            }

            return Solid(LightImage);
        });

        var centre = fixture.Page.Center;
        fixture.WaitUntil(() => Near(fixture.PixelAt(centre), LightImage, 2));

        // The sharp tile of the light theme is done and on its way to the UI thread when the theme changes.
        using var ready = new ManualResetEventSlim();
        fixture.Scheduler.TileReady += key =>
        {
            if (!key.IsPreview)
            {
                ready.Set();
            }
        };
        lightSharp.SetResult();
        Assert.True(ready.Wait(Timeout));
        fixture.Scheduler.SetPageTheme(PageTheme.Dark);
        fixture.Canvas.PageTheme = PageTheme.Dark;
        fixture.Refresh();

        // Nothing of the light theme is left, not even the tile that had just come: the dark placeholder (away from its
        // page number in the middle) until the dark images arrive.
        Assert.Equal(0, fixture.Canvas.Bitmaps.Count);
        var corner = new Point(fixture.Page.X + 20, fixture.Page.Y + 20);
        Assert.True(Near(fixture.PixelAt(corner), DarkGround, 1), $"{fixture.PixelAt(corner)} is not the dark ground");
        Assert.Equal(0, fixture.Count(color => Near(color, LightImage, 20)));

        dark.SetResult();
        fixture.WaitUntil(() => Near(fixture.PixelAt(centre), DarkImage, 2));
        Assert.Equal(0, fixture.Count(color => Near(color, LightImage, 20)));
    }

    [AvaloniaFact]
    public void AHighlightSwitchKeepsThePageUntilTheNewImagesCome()
    {
        // Milestone 11: the highlight of the form fields has its own epoch like the theme, but the page around the fields
        // stays the same, so the old images stay on screen (stale) until the highlighted ones replace them.
        var highlightedImage = Color.FromRgb(30, 160, 30);
        var highlighted = new TaskCompletionSource();
        using var fixture = CanvasFixture.Show(ThemeVariant.Light, async (_, options, cancellationToken) =>
        {
            if (options.FormHighlight)
            {
                await highlighted.Task.WaitAsync(cancellationToken);
                return Solid(highlightedImage);
            }

            return Solid(LightImage);
        });

        var centre = fixture.Page.Center;
        fixture.WaitUntil(() => Near(fixture.PixelAt(centre), LightImage, 2) && fixture.Scheduler.PendingCount == 0);
        fixture.Refresh();
        var shown = fixture.Canvas.Bitmaps.Count;
        Assert.True(shown > 0);

        fixture.Scheduler.SetFormHighlight(true);
        fixture.Canvas.FormHighlight = true;
        fixture.Refresh();

        Assert.Equal(shown, fixture.Canvas.Bitmaps.Count);
        Assert.Equal(shown, fixture.Canvas.StaleKeys.Count);
        Assert.True(Near(fixture.PixelAt(centre), LightImage, 2), $"{fixture.PixelAt(centre)} is not the old page");

        highlighted.SetResult();
        fixture.WaitUntil(() => Near(fixture.PixelAt(centre), highlightedImage, 2) && fixture.Canvas.StaleKeys.Count == 0);
        Assert.Equal(0, fixture.Count(color => Near(color, LightImage, 20)));
    }

    [AvaloniaFact]
    public void ALateFailureOfTheOldThemeDoesNotMarkThePage()
    {
        var dark = new TaskCompletionSource();
        using var fixture = CanvasFixture.Show(
            ThemeVariant.Light,
            async (_, options, cancellationToken) =>
            {
                if (options.PageTheme == PageTheme.Light)
                {
                    throw new PdfRenderException("the light page fails in this test");
                }

                await dark.Task.WaitAsync(cancellationToken);
                return Solid(DarkImage);
            },
            beforeViewport: scheduler =>
            {
                // Subscribed after the canvas: when this runs, the canvas has posted the failure to the UI thread.
                var failed = new ManualResetEventSlim();
                scheduler.TileFailed += (_, _) => failed.Set();
                return failed;
            },
            settleAfterViewport: false);

        // The failure of the light theme waits on the UI thread while the theme changes.
        Assert.True(fixture.Signal!.Wait(Timeout));
        fixture.Scheduler.SetPageTheme(PageTheme.Dark);
        fixture.Canvas.PageTheme = PageTheme.Dark;
        fixture.Refresh();

        // The page is not marked as failed in the new theme, and renders there.
        Assert.Empty(fixture.Canvas.FailedPages);
        dark.SetResult();
        fixture.WaitUntil(() => Near(fixture.PixelAt(fixture.Page.Center), DarkImage, 2));
        Assert.Empty(fixture.Canvas.FailedPages);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void ThePageShadowAndBorderStayVisible(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        using var fixture = CanvasFixture.Show(variant, static (_, _, _) => Task.FromResult(Solid(DarkGround)), PageTheme.Dark);
        var page = fixture.Page;
        fixture.WaitUntil(() => fixture.Canvas.Bitmaps.Count > 0);

        var canvas = TokenTests.Token("Surface.Canvas", variant);
        var inside = fixture.PixelAt(new Point(page.X + 30, page.Y + 30));
        var edge = fixture.PixelAt(new Point(page.X - 0.5, page.Y + (page.Height / 2)));
        var below = fixture.PixelAt(new Point(page.X + (page.Width / 2), page.Bottom + 2.5));
        var far = fixture.PixelAt(new Point(page.X - 20, page.Y - 20));

        Assert.True(Near(inside, DarkGround, 1), $"{inside} is not the dark page");
        Assert.Equal(canvas, far);

        // The page stands out from the canvas, its edge (page.border with the inner part of page.shadow) is drawn on the
        // canvas, and the shadow below the page is darker than the canvas. On the dark canvas the edge is close to the
        // colour of a dark page (it outlines the page against the canvas, not against the page).
        Assert.False(Near(inside, canvas, 6), $"the page {inside} is lost against the canvas {canvas}");
        Assert.False(Near(edge, canvas, 6), $"the edge {edge} is lost against the canvas {canvas}");
        Assert.True(Wcag.RelativeLuminance(below) < Wcag.RelativeLuminance(canvas), $"no shadow below the page: {below} on {canvas}");
    }

    [AvaloniaFact]
    public async Task ThumbnailsFollowThePageTheme()
    {
        var (window, viewModel, view) = Show(UserSettings.Detached(), new FakeTheme(ThemeVariant.Light));
        var tab = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Pages);
        ThumbnailList? list = null;
        await WaitUntilAsync(window, () =>
        {
            list = window.GetVisualDescendants().OfType<ThumbnailList>().FirstOrDefault();
            return list is not null && list.Thumbnails.Count == tab.PageCount && list.Thumbnails[0].Bitmap is not null;
        });

        var lightBitmap = list!.Thumbnails[0].Bitmap!;
        Assert.Equal(Colors.White, ThumbnailPixel(lightBitmap, EmptyArea));
        var frame = Find<Border>(Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0)), "Placeholder");

        // The thumbnails render dark with the page; meanwhile the empty frame has the dark ground.
        tab.TogglePageThemeCommand.Execute(null);
        Assert.Equal(PageTheme.Dark, list.Source!.PageTheme);
        Settle(window);
        Assert.True(list.Thumbnails[0].Bitmap is null || !ReferenceEquals(lightBitmap, list.Thumbnails[0].Bitmap));
        Assert.Equal(DarkGround, Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is not null);
        Assert.True(Near(ThumbnailPixel(list.Thumbnails[0].Bitmap!, EmptyArea), DarkGround, 3));

        tab.TogglePageThemeCommand.Execute(null);
        Settle(window);
        Assert.Equal(TokenTests.Token("Surface.Raised", ThemeVariant.Light), Assert.IsAssignableFrom<ISolidColorBrush>(frame.Background).Color);
        await WaitUntilAsync(window, () => list.Thumbnails[0].Bitmap is { } bitmap && ThumbnailPixel(bitmap, EmptyArea) == Colors.White);
    }

    private static RenderedImage Solid(Color color) => new(1, 1, 4, new byte[] { color.B, color.G, color.R, 255 });

    private static bool Near(Color color, Color expected, int tolerance = 12) =>
        Math.Abs(color.R - expected.R) <= tolerance && Math.Abs(color.G - expected.G) <= tolerance && Math.Abs(color.B - expected.B) <= tolerance;

    private static int Count(FramePixels frame, Rect area, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = Math.Max(0, (int)Math.Floor(area.Y)); y < Math.Min(frame.Height, (int)Math.Ceiling(area.Bottom)); y++)
        {
            for (var x = Math.Max(0, (int)Math.Floor(area.X)); x < Math.Min(frame.Width, (int)Math.Ceiling(area.Right)); x++)
            {
                if (predicate(frame.GetColor(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>The colour of the frame in the middle of a rectangle of a page (PDF points), as the canvas draws it.</summary>
    private static Color PixelAt(Window window, DocumentView view, int page, PageRect rect)
    {
        Settle(window);
        var layout = view.Layout!;
        var pageRect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var x = pageRect.X - view.Canvas.Viewport.X + ((rect.Left + rect.Right) / 2 * scale);
        var y = pageRect.Y - view.Canvas.Viewport.Y + ((rect.Top + rect.Bottom) / 2 * scale);
        using var frame = FramePixels.Snapshot(view.Canvas);
        return frame.GetColor((int)x, (int)y);
    }

    /// <summary>The colour of a thumbnail at the middle of a rectangle of its page (PDF points of text-basic.pdf, 300 x 200).</summary>
    private static Color ThumbnailPixel(Bitmap bitmap, PageRect rect)
    {
        var x = (int)((rect.Left + rect.Right) / 2 / 300 * bitmap.PixelSize.Width);
        var y = (int)((rect.Top + rect.Bottom) / 2 / 200 * bitmap.PixelSize.Height);
        var pixel = new byte[4];
        var handle = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new Avalonia.PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings settings, FakeTheme theme)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { IsWindowsTitleBar = true, AppTheme = theme };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a file in a new tab, waits until the view shows it sharp and closes the panel the window opens by itself.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    /// <summary>The theme in effect, switched by the test.</summary>
    private sealed class FakeTheme(ThemeVariant variant) : IEffectiveTheme
    {
        public event Action? EffectiveThemeChanged;

        public ThemeVariant EffectiveTheme { get; private set; } = variant;

        public void Set(ThemeVariant value)
        {
            EffectiveTheme = value;
            EffectiveThemeChanged?.Invoke();
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    /// <summary>
    /// A canvas filling a 600 x 500 window, styled like the one of the document view, with one 400 x 300 DIP page and a
    /// scheduler over a fake render function (no PDFium).
    /// </summary>
    private sealed class CanvasFixture : IDisposable
    {
        private readonly Window _window;

        private CanvasFixture(Window window, DocumentCanvas canvas, RenderScheduler scheduler, Rect page, ManualResetEventSlim? signal)
        {
            _window = window;
            Canvas = canvas;
            Scheduler = scheduler;
            Page = page;
            Signal = signal;
        }

        public DocumentCanvas Canvas { get; }

        public RenderScheduler Scheduler { get; }

        /// <summary>Gets the page in canvas coordinates.</summary>
        public Rect Page { get; }

        /// <summary>Gets what the <c>beforeViewport</c> callback returned.</summary>
        public ManualResetEventSlim? Signal { get; }

        public static CanvasFixture Show(
            ThemeVariant variant,
            Func<RenderRequest, RenderOptions, CancellationToken, Task<RenderedImage>> render,
            PageTheme pageTheme = PageTheme.Light,
            Func<RenderScheduler, ManualResetEventSlim>? beforeViewport = null,
            bool settleAfterViewport = true)
        {
            var cache = new RenderCache();
            var scheduler = new RenderScheduler(render, cache, pageTheme);
            var layout = new DocumentLayout(1, new PdfPageSize(300, 225));
            var canvas = new DocumentCanvas { PageTheme = pageTheme };
            canvas.Bind(DocumentCanvas.PlaceholderBrushProperty, canvas.GetResourceObservable("Pw.Brush.Surface.Raised"));
            canvas.Bind(DocumentCanvas.PlaceholderBorderBrushProperty, canvas.GetResourceObservable("Pw.Brush.Border.Subtle"));
            canvas.Bind(DocumentCanvas.PlaceholderTextBrushProperty, canvas.GetResourceObservable("Pw.Brush.Text.Secondary"));
            canvas.Bind(DocumentCanvas.PageShadowProperty, canvas.GetResourceObservable("Pw.Shadow.Page"));
            canvas.Bind(DocumentCanvas.DarkPageBrushProperty, canvas.GetResourceObservable("Pw.Brush.Page.DarkBackground"));
            canvas.Bind(DocumentCanvas.DarkPageForegroundBrushProperty, canvas.GetResourceObservable("Pw.Brush.Page.DarkForeground"));

            var host = new Panel { Children = { canvas } };
            host.Bind(Panel.BackgroundProperty, host.GetResourceObservable("Pw.Brush.Surface.Canvas"));
            var window = new Window { Content = host, Width = 600, Height = 500, RequestedThemeVariant = variant };
            window.Closed += (_, _) =>
            {
                canvas.Detach();
                scheduler.Dispose();
            };
            window.Show();
            Settle(window);

            var viewport = new DipRect(-(600 - layout.ContentWidth) / 2, 0, 600, 500);
            canvas.Attach(layout, scheduler, cache);
            var signal = beforeViewport?.Invoke(scheduler);
            canvas.SetViewport(viewport, 1);
            if (settleAfterViewport)
            {
                Settle(window);
            }

            var rect = layout.GetPageRect(0);
            return new CanvasFixture(window, canvas, scheduler, new Rect(rect.X - viewport.X, rect.Y - viewport.Y, rect.Width, rect.Height), signal);
        }

        public void Refresh() => Settle(_window);

        public Color PixelAt(Point point)
        {
            Refresh();
            using var frame = new FramePixels(_window.CaptureRenderedFrame()!);
            return frame.GetColor((int)point.X, (int)point.Y);
        }

        /// <summary>The number of pixels of the page and its surroundings that match.</summary>
        public int Count(Func<Color, bool> predicate)
        {
            Refresh();
            using var frame = new FramePixels(_window.CaptureRenderedFrame()!);
            return DarkPageViewTests.Count(frame, Page.Inflate(16), predicate);
        }

        public void WaitUntil(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (true)
            {
                Refresh();
                if (condition())
                {
                    return;
                }

                Assert.True(DateTime.UtcNow < deadline, "the canvas did not reach the expected state");
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            _window.Close();
            Signal?.Dispose();
        }
    }
}
