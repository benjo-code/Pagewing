using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
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
/// The settings card (<see cref="SettingsCard"/>, <see cref="SettingsViewModel"/>). Each test has its own settings in a
/// temporary folder (never the user's) and its own <see cref="Appearance"/> on the app, and puts the app back to the
/// defaults at the end.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests); theme and density are app-wide.
[Collection("Localization")]
public sealed class SettingsCardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-settingscard-" + Guid.NewGuid().ToString("N"));
    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly List<IDisposable> _cleanup = [];
    private UserSettings? _settings;
    private Appearance? _appearance;

    public SettingsCardTests()
    {
        Strings.Culture = new CultureInfo("en");
        CultureInfo.CurrentUICulture = new CultureInfo("en");
    }

    private string Blocker => _directory + ".blocker";

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        // Back to the defaults (system theme, normal density, full motion) before the appearance lets go of the app.
        _settings?.Update(_ => AppSettings.Default);
        _appearance?.Dispose();
        Strings.Culture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        if (File.Exists(Blocker))
        {
            File.Delete(Blocker);
        }

        // A test that freed the blocker leaves a folder there once the defaults above are written.
        if (Directory.Exists(Blocker))
        {
            Directory.Delete(Blocker, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task OpensFromTheActionBarAndWithCtrlComma()
    {
        var (window, viewModel, view) = Show(Start());

        // Without a tab there is no action bar: Ctrl+Comma opens the card, a second Ctrl+Comma closes it.
        window.Focus();
        Settle(window);
        Assert.False(Find<ActionBar>(window, "ActionBar").IsVisible);
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        var card = Find<SettingsCard>(window, "SettingsCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Same(viewModel.SettingsCard, card.DataContext);
        Assert.Equal(Strings.CommandSettings, AutomationProperties.GetName(card));
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(card.IsVisible);

        // And from the palette: the command "Settings".
        Press(window, Key.K, PhysicalKey.K, Ctrl);
        viewModel.Palette!.Query = "settings";
        Assert.Equal("settings.show", viewModel.Palette.SelectedItem!.Key);
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // With a tab: the button at the bottom of the action bar, below the panel entries and like them.
        await OpenAsync(window, viewModel, view, "text-dense.pdf");
        var bar = Find<ActionBar>(window, "ActionBar");
        var button = Find<Button>(window, "SettingsButton");
        var search = Find<Button>(window, "SearchButton");
        Assert.True(button.IsEffectivelyVisible);
        var top = button.TranslatePoint(default, bar)!.Value.Y;
        Assert.Equal(bar.Bounds.Height - 8, top + button.Bounds.Height, 0.5);
        Assert.True(top > search.TranslatePoint(new Point(0, search.Bounds.Height), bar)!.Value.Y + 100, "the button is not at the bottom");
        Assert.Equal(search.Bounds.Size, button.Bounds.Size);
        Assert.Equal(search.TranslatePoint(default, bar)!.Value.X, button.TranslatePoint(default, bar)!.Value.X);
        Assert.Contains("action", button.Classes);
        var icon = button.GetVisualDescendants().OfType<StrokeIcon>().Single();
        Assert.Same(TokenTests.Resource("Pw.Icon.Settings", ThemeVariant.Light), icon.Data);
        Assert.Equal(20, icon.Width);
        var gesture = GestureText.Of(viewModel.Commands.ById("settings.show")!.Gesture!);
        Assert.Equal("Ctrl+Comma", gesture);
        Assert.Equal($"{Strings.CommandSettings} ({gesture})", ToolTip.GetTip(button));
        Assert.Equal(Strings.SettingsTooltip, ToolTip.GetTip(button));
        Assert.Equal(Strings.CommandSettings, AutomationProperties.GetName(button));

        // A click opens the card with the focus on its first option; it shows no panel and never marks the entry active.
        await FocusAsync(window, view);
        Click(window, button);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });
        Assert.Equal(SidePanelKind.None, viewModel.ActivePanel);
        Assert.DoesNotContain("active", button.Classes);

        // A second click closes it; the keys go back to the document, not to the button (Space does not open it again).
        Click(window, button);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => view.IsFocused);
        Press(window, Key.Space, PhysicalKey.Space, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        // "Done", the close button and Esc close it, the focus goes back to the document.
        foreach (var close in new Action[]
        {
            () => Click(window, Find<Button>(window, "SettingsDoneButton")),
            () => Click(window, Find<Button>(window, "SettingsCloseButton")),
            () => Press(window, Key.Escape, PhysicalKey.Escape, None),
        })
        {
            Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
            Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
            await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });
            close();
            Assert.Equal(OverlayKind.None, viewModel.Overlay);
            Assert.False(card.IsVisible);
            await WaitUntilAsync(window, () => view.IsFocused);
        }
    }

    [AvaloniaFact]
    public async Task EveryOptionWritesAtOnce()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var app = Application.Current!;
        var card = await OpenCardAsync(window);
        var model = viewModel.SettingsCard!;
        AppSettings Stored() => UserSettings.Load(_directory).Current;

        // Theme: the app's variant changes at once, and it is written.
        Find<ComboBox>(card, "ThemeChoice").SelectedItem = model.ThemeChoices.Single(choice => choice.Value == ThemeSetting.Dark);
        Settle(window);
        Assert.Equal(ThemeSetting.Dark, settings.Current.Theme);
        Assert.Equal(ThemeSetting.Dark, Stored().Theme);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.Equal(ThemeVariant.Dark, window.ActualThemeVariant);

        // Density: the title bar is 34 high in "Kompakt".
        Find<ComboBox>(card, "DensityChoice").SelectedItem = model.DensityChoices.Single(choice => choice.Value == DensitySetting.Compact);
        Settle(window);
        Assert.Equal(DensitySetting.Compact, settings.Current.Density);
        Assert.Equal(DensitySetting.Compact, Stored().Density);
        Assert.Equal(34, Resource(window, "Pw.Size.TitleBarHeight"));
        Assert.Equal(34, Find<Control>(window, "TitleBar").Bounds.Height);

        // Motion: every duration is 0.
        Find<ToggleSwitch>(card, "ReduceMotionSwitch").IsChecked = true;
        Settle(window);
        Assert.True(settings.Current.ReduceMotion);
        Assert.True(Stored().ReduceMotion);
        Assert.Equal(TimeSpan.Zero, Duration(app, Appearance.OpenDurationKey));
        Assert.Equal(TimeSpan.Zero, Duration(app, Appearance.CloseDurationKey));

        // Pages: dark in the dark theme, in the open tab at once.
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);
        Find<ToggleSwitch>(card, "DarkPagesSwitch").IsChecked = true;
        Settle(window);
        Assert.Equal(PageAppearanceSetting.FollowTheme, settings.Current.PageAppearance);
        Assert.Equal(PageAppearanceSetting.FollowTheme, Stored().PageAppearance);
        Assert.Equal(PageTheme.Dark, tab.EffectivePageTheme);
        Find<ToggleSwitch>(card, "DarkPagesSwitch").IsChecked = false;
        Settle(window);
        Assert.Equal(PageAppearanceSetting.Light, Stored().PageAppearance);
        Assert.Equal(PageTheme.Light, tab.EffectivePageTheme);

        // Default layout and zoom of new tabs, and the language (from the next start).
        Find<ComboBox>(card, "LayoutChoice").SelectedItem = model.LayoutChoices.Single(choice => choice.Value == PageLayoutMode.TwoPages);
        Find<ComboBox>(card, "ZoomChoice").SelectedItem = model.ZoomModeChoices.Single(choice => choice.Value == ZoomMode.FitPage);
        Find<ComboBox>(card, "LanguageChoice").SelectedItem = model.LanguageChoices.Single(choice => choice.Value == LanguageSetting.German);
        Settle(window);
        Assert.Equal(PageLayoutMode.TwoPages, Stored().DefaultLayout);
        Assert.Equal(ZoomMode.FitPage, Stored().DefaultZoomMode);
        Assert.Equal(LanguageSetting.German, Stored().Language);
        Assert.Equal(settings.Current, Stored());

        // Back to the defaults through the card: the light theme, the normal density and full motion again.
        Find<ComboBox>(card, "ThemeChoice").SelectedItem = model.ThemeChoices.Single(choice => choice.Value == ThemeSetting.System);
        Find<ComboBox>(card, "DensityChoice").SelectedItem = model.DensityChoices.Single(choice => choice.Value == DensitySetting.Normal);
        Find<ToggleSwitch>(card, "ReduceMotionSwitch").IsChecked = false;
        Settle(window);
        Assert.Equal(ThemeVariant.Default, app.RequestedThemeVariant);
        Assert.Equal(40, Find<Control>(window, "TitleBar").Bounds.Height);
        Assert.Equal(TimeSpan.FromMilliseconds(150), Duration(app, Appearance.OpenDurationKey));
    }

    [AvaloniaFact]
    public async Task LanguageShowsTheRestartHint()
    {
        // The rule: the choice ("System" is the language of the system) against the language the interface runs in.
        var english = new CultureInfo("en");
        var german = new CultureInfo("de-AT");
        var running = new SettingsViewModel(UserSettings.Detached(), english, english);
        Assert.False(running.LanguageNeedsRestart);
        running.Language = LanguageSetting.German;
        Assert.True(running.LanguageNeedsRestart);
        running.Language = LanguageSetting.English;
        Assert.False(running.LanguageNeedsRestart);
        var startedWithGerman = new SettingsViewModel(UserSettings.Detached(), german, english);
        Assert.True(startedWithGerman.LanguageNeedsRestart);
        startedWithGerman.Language = LanguageSetting.German;
        Assert.False(startedWithGerman.LanguageNeedsRestart);
        var systemGerman = new SettingsViewModel(UserSettings.Detached(), german, german);
        Assert.False(systemGerman.LanguageNeedsRestart);

        // On the card (the interface runs in English, so does the system): the hint line comes and goes, and the
        // interface keeps its language until the next start.
        var (window, viewModel, _) = Show(Start());
        var card = await OpenCardAsync(window);
        var hint = Find<TextBlock>(card, "RestartHint");
        var list = Find<ComboBox>(card, "LanguageChoice");
        Assert.False(hint.IsEffectivelyVisible);
        list.SelectedItem = viewModel.SettingsCard!.LanguageChoices.Single(choice => choice.Value == LanguageSetting.German);
        Settle(window);
        Assert.True(hint.IsEffectivelyVisible);
        Assert.Equal(Strings.SettingsRestartHint, hint.Text);
        Assert.Equal("Takes effect the next time Pagewing starts.", hint.Text);
        Assert.Equal("en", Strings.Culture.Name);
        Assert.Equal("Settings", Find<TextBlock>(card, "SettingsTitle").Text);
        list.SelectedItem = viewModel.SettingsCard.LanguageChoices.Single(choice => choice.Value == LanguageSetting.System);
        Settle(window);
        Assert.False(hint.IsEffectivelyVisible);
        list.SelectedItem = viewModel.SettingsCard.LanguageChoices.Single(choice => choice.Value == LanguageSetting.English);
        Settle(window);
        Assert.False(hint.IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SavingFailureIsShownWithATextNotOnlyAColour(string theme)
    {
        // The config "folder" is a file: nothing can be written there. A failure before the card opens shows at once.
        File.WriteAllText(Blocker, "not a folder");
        var settings = Start(Blocker);
        settings.Update(current => current with { StartPanel = StartPanelSetting.None });
        Assert.True(settings.LastSaveFailed);
        var (window, viewModel, _) = Show(settings, theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light);
        var card = await OpenCardAsync(window);
        var row = Find<Control>(card, "SettingsSaveFailed");
        Assert.True(row.IsEffectivelyVisible);
        Assert.True(viewModel.SettingsCard!.SaveFailed);

        // A change through the card fails as well: the value is in effect, the card says it was not saved.
        Find<ToggleSwitch>(card, "ReduceMotionSwitch").IsChecked = true;
        Settle(window);
        Assert.True(settings.Current.ReduceMotion);
        Assert.True(row.IsEffectivelyVisible);

        // The warning symbol and a text, both in danger.default (4.5:1 on the card), not a colour alone.
        var variant = window.ActualThemeVariant;
        var danger = TokenTests.Token("Danger.Default", variant);
        var icon = Find<StrokeIcon>(card, "SettingsSaveFailedIcon");
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Same(TokenTests.Resource("Pw.Icon.AlertTriangle", ThemeVariant.Light), icon.Data);
        Assert.Equal(danger, Assert.IsAssignableFrom<ISolidColorBrush>(icon.Brush).Color);
        var text = Find<TextBlock>(card, "SettingsSaveFailedText");
        Assert.Equal(Strings.SettingsSaveFailed, text.Text);
        Assert.Equal("The settings could not be saved.", text.Text);
        Assert.Equal(danger, Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
        Assert.True(Wcag.Contrast(danger, TokenTests.Token("Surface.Raised", variant)) >= 4.5);

        // Once the folder can be written, the next change is saved and the message goes.
        File.Delete(Blocker);
        Find<ToggleSwitch>(card, "ReduceMotionSwitch").IsChecked = false;
        Settle(window);
        Assert.False(settings.LastSaveFailed);
        Assert.False(viewModel.SettingsCard.SaveFailed);
        Assert.False(row.IsEffectivelyVisible);
        Assert.Equal(settings.Current, UserSettings.Load(Blocker).Current);
        Directory.Delete(Blocker, recursive: true);
    }

    [AvaloniaFact]
    public async Task SaveFailedFollowsWritesMadeElsewhereWhileTheCardIsOpen()
    {
        // Writable at first: the card opens without the warning.
        var settings = Start(Blocker);
        var (window, viewModel, _) = Show(settings);
        var card = await OpenCardAsync(window);
        var row = Find<Control>(card, "SettingsSaveFailed");
        Assert.False(row.IsEffectivelyVisible);

        // Another window changes a setting while the folder cannot be written: the card says so, although the change
        // was announced before it was written.
        File.WriteAllText(Blocker, "not a folder");
        settings.Update(current => current with { StartPanel = StartPanelSetting.None });
        Settle(window);
        Assert.True(settings.LastSaveFailed);
        Assert.True(viewModel.SettingsCard!.SaveFailed);
        Assert.True(row.IsEffectivelyVisible);

        // The next write elsewhere succeeds: the warning goes.
        File.Delete(Blocker);
        settings.Update(current => current with { StartPanel = StartPanelSetting.Pages });
        Settle(window);
        Assert.False(settings.LastSaveFailed);
        Assert.False(viewModel.SettingsCard.SaveFailed);
        Assert.False(row.IsEffectivelyVisible);
        Directory.Delete(Blocker, recursive: true);
    }

    [AvaloniaFact]
    public async Task TheWheelOverAClosedListScrollsTheCard()
    {
        var settings = Start();
        var (window, _, _) = Show(settings);
        window.Height = 480;
        Settle(window);
        var card = await OpenCardAsync(window);
        var list = Find<ComboBox>(card, "ThemeChoice");
        var scroller = Find<ScrollViewer>(card, "SettingsScroller");
        Assert.True(list.IsFocused);
        Assert.True(scroller.Extent.Height > scroller.Viewport.Height, $"extent {scroller.Extent}, viewport {scroller.Viewport}");
        var before = settings.Current.Theme;

        // The wheel over the focused, closed list: the setting stays, the card scrolls as it does elsewhere.
        var point = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseWheel(point, new Vector(0, -1));
        Settle(window);
        Assert.Equal(before, settings.Current.Theme);
        Assert.Equal(before, Assert.IsType<SettingsChoice<ThemeSetting>>(list.SelectedItem).Value);
        Assert.True(scroller.Offset.Y > 0, $"offset {scroller.Offset}");
        var scrolled = scroller.Offset.Y;
        window.MouseWheel(point, new Vector(0, 1));
        Settle(window);
        Assert.Equal(before, settings.Current.Theme);
        Assert.True(scroller.Offset.Y < scrolled, $"offset {scroller.Offset}");

        // An open list still takes the wheel itself (it scrolls its entries); the card stays where it is.
        list.IsDropDownOpen = true;
        Settle(window);
        var offset = scroller.Offset;
        window.MouseWheel(point, new Vector(0, -1));
        Settle(window);
        Assert.Equal(offset, scroller.Offset);
        list.IsDropDownOpen = false;
    }

    [AvaloniaFact]
    public async Task NewTabsUseTheDefaultLayoutAndZoomMode()
    {
        var (window, viewModel, view) = Show(Start());
        var card = await OpenCardAsync(window);
        var model = viewModel.SettingsCard!;

        // Two pages, whole page.
        Find<ComboBox>(card, "LayoutChoice").SelectedItem = model.LayoutChoices.Single(choice => choice.Value == PageLayoutMode.TwoPages);
        Find<ComboBox>(card, "ZoomChoice").SelectedItem = model.ZoomModeChoices.Single(choice => choice.Value == ZoomMode.FitPage);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        var two = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Equal(PageLayoutMode.TwoPages, two.LayoutMode);
        Assert.Equal(ZoomMode.FitPage, two.ZoomMode);
        Assert.Equal(PageLayoutMode.TwoPages, view.Layout!.Mode);
        await WaitUntilAsync(window, () => view.Layout.Zoom == two.Zoom && two.Zoom != 1.0);

        // The whole row of both pages is in the viewport, and it fills its height or its width.
        var margin = view.Layout.PageMargin;
        var left = view.Layout.GetPageRect(0);
        var right = view.Layout.GetPageRect(1);
        var rowHeight = Math.Max(left.Height, right.Height) + (2 * margin);
        var rowWidth = right.Right - left.X + (2 * margin);
        var viewport = view.ScrollViewer.Viewport;
        Assert.True(rowHeight <= viewport.Height + 1 && rowWidth <= viewport.Width + 1, $"row {rowWidth} x {rowHeight} in {viewport}");
        Assert.True(Math.Abs(rowHeight - viewport.Height) < 1 || Math.Abs(rowWidth - viewport.Width) < 1, $"row {rowWidth} x {rowHeight} in {viewport}");

        // Continuous, page width: the page fills the width.
        model.DefaultLayout = PageLayoutMode.Continuous;
        model.DefaultZoomMode = ZoomMode.FitWidth;
        var wide = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(PageLayoutMode.Continuous, wide.LayoutMode);
        Assert.Equal(ZoomMode.FitWidth, wide.ZoomMode);
        await WaitUntilAsync(window, () => view.Layout!.Zoom == wide.Zoom && wide.Zoom != 1.0);
        Assert.Equal(view.ScrollViewer.Viewport.Width, view.Layout!.GetPageRect(0).Width + (2 * view.Layout.PageMargin), 0.5);

        // Single page, actual size: 100 % as a tab always started (the free zoom, a resize keeps it).
        model.DefaultLayout = PageLayoutMode.SinglePage;
        model.DefaultZoomMode = ZoomMode.ActualSize;
        var single = await OpenAsync(window, viewModel, view, "hello_world.pdf");
        Assert.Equal(PageLayoutMode.SinglePage, single.LayoutMode);
        Assert.Equal(ZoomMode.Free, single.ZoomMode);
        Assert.Equal(1.0, single.Zoom);
        Assert.Equal(PageLayoutMode.SinglePage, view.Layout!.Mode);
        Assert.Equal(1.0, view.Layout.Zoom);

        // The free zoom is no default: the card does not offer it, and setting it changes nothing.
        Assert.DoesNotContain(model.ZoomModeChoices, choice => choice.Value == ZoomMode.Free);
        model.DefaultZoomMode = ZoomMode.Free;
        Assert.Equal(ZoomMode.ActualSize, viewModel.Settings.Current.DefaultZoomMode);
    }

    [AvaloniaFact]
    public async Task ExistingTabsAreNotChanged()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(settings);
        var open = await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Equal(PageLayoutMode.Continuous, open.LayoutMode);
        Assert.Equal(ZoomMode.Free, open.ZoomMode);
        Assert.Equal(1.0, open.Zoom);

        var card = await OpenCardAsync(window);
        var model = viewModel.SettingsCard!;
        Find<ComboBox>(card, "LayoutChoice").SelectedItem = model.LayoutChoices.Single(choice => choice.Value == PageLayoutMode.SinglePage);
        Find<ComboBox>(card, "ZoomChoice").SelectedItem = model.ZoomModeChoices.Single(choice => choice.Value == ZoomMode.FitWidth);
        Press(window, Key.Escape, PhysicalKey.Escape, None);

        // The open tab keeps its layout and zoom, in the tab and in the view.
        Assert.Equal(PageLayoutMode.Continuous, open.LayoutMode);
        Assert.Equal(ZoomMode.Free, open.ZoomMode);
        Assert.Equal(1.0, open.Zoom);
        Assert.Equal(PageLayoutMode.Continuous, view.Layout!.Mode);
        Assert.Equal(1.0, view.Layout.Zoom);

        // A tab that moves to another window keeps its state there too (it is no new tab).
        var other = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings);
        _cleanup.Add(other);
        Assert.True(viewModel.DetachTab(open));
        other.AttachTab(open);
        Assert.Same(open, other.ActiveTab);
        Assert.Equal(PageLayoutMode.Continuous, open.LayoutMode);
        Assert.Equal(ZoomMode.Free, open.ZoomMode);
        Assert.Equal(1.0, open.Zoom);

        // A new tab takes the new defaults.
        var next = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Equal(PageLayoutMode.SinglePage, next.LayoutMode);
        Assert.Equal(ZoomMode.FitWidth, next.ZoomMode);
    }

    [AvaloniaFact]
    public async Task KeyboardOnlyUseWorks()
    {
        var settings = Start();
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenAsync(window, viewModel, view, "text-dense.pdf");
        await FocusAsync(window, view);
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });

        // Tab goes from top to bottom and stays on the card; Shift+Tab goes back.
        var order = new List<string?>();
        for (var i = 0; i < 10; i++)
        {
            Press(window, Key.Tab, PhysicalKey.Tab, None);
            order.Add((Focused(window) as Control)?.Name);
        }

        Assert.Equal(
            ["DensityChoice", "DarkPagesSwitch", "ReduceMotionSwitch", "LayoutChoice", "ZoomChoice", "PrintScalingChoice", "LanguageChoice", "SettingsDoneButton", "SettingsCloseButton", "ThemeChoice"],
            order);
        Press(window, Key.Tab, PhysicalKey.Tab, Shift);
        Assert.Equal("SettingsCloseButton", (Focused(window) as Control)?.Name);

        // The arrows change a closed list (written at once), Space a switch.
        Find<ComboBox>(window, "LayoutChoice").Focus(NavigationMethod.Tab);
        Settle(window);
        Assert.Equal(PageLayoutMode.Continuous, settings.Current.DefaultLayout);
        Press(window, Key.Down, PhysicalKey.ArrowDown, None);
        Assert.Equal(PageLayoutMode.TwoPages, settings.Current.DefaultLayout);
        Press(window, Key.Up, PhysicalKey.ArrowUp, None);
        Press(window, Key.Up, PhysicalKey.ArrowUp, None);
        Assert.Equal(PageLayoutMode.SinglePage, settings.Current.DefaultLayout);
        Assert.Equal(PageLayoutMode.SinglePage, UserSettings.Load(_directory).Current.DefaultLayout);
        Find<ToggleSwitch>(window, "DarkPagesSwitch").Focus(NavigationMethod.Tab);
        Press(window, Key.Space, PhysicalKey.Space, None);
        Assert.Equal(PageAppearanceSetting.FollowTheme, settings.Current.PageAppearance);
        Press(window, Key.Space, PhysicalKey.Space, None);
        Assert.Equal(PageAppearanceSetting.Light, settings.Current.PageAppearance);

        // The page keys stay on the card: the document behind it does not turn.
        Press(window, Key.PageDown, PhysicalKey.PageDown, None);
        Assert.Equal(1, tab.CurrentPage);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);

        // Alt+Down opens a list; Esc closes only the list, the next Esc the card, and the keys go back to the document.
        var zoom = Find<ComboBox>(window, "ZoomChoice");
        zoom.Focus(NavigationMethod.Tab);
        Press(window, Key.Down, PhysicalKey.ArrowDown, Alt);
        Assert.True(zoom.IsDropDownOpen);
        Press(window, Key.PageDown, PhysicalKey.PageDown, None);
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(zoom.IsDropDownOpen);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(zoom.IsDropDownOpen);
        Assert.Equal(OverlayKind.Settings, viewModel.Overlay);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    [AvaloniaFact]
    public async Task NothingIsBuiltBeforeTheFirstOpen()
    {
        var (window, viewModel, view) = Show(Start());
        Assert.Empty(window.GetVisualDescendants().OfType<SettingsCard>());
        Assert.Null(viewModel.SettingsCard);

        // Also not with a document open; the action bar has only its light button, built like the others.
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Empty(window.GetVisualDescendants().OfType<SettingsCard>());
        Assert.Null(viewModel.SettingsCard);
        var button = Find<Button>(window, "SettingsButton");
        var search = Find<Button>(window, "SearchButton");
        Assert.Equal(search.GetVisualDescendants().Count(), button.GetVisualDescendants().Count());

        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        Assert.Single(window.GetVisualDescendants().OfType<SettingsCard>());
        var model = viewModel.SettingsCard;
        Assert.NotNull(model);

        // Shown again, it is the same card with the same view model.
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        Assert.Single(window.GetVisualDescendants().OfType<SettingsCard>());
        Assert.Same(model, viewModel.SettingsCard);
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task LabelsAreLocalizedAndEveryControlHasAnAutomationName(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, view) = Show(Start());
        await OpenAsync(window, viewModel, view, "text-basic.pdf");
        var card = await OpenCardAsync(window);
        var model = viewModel.SettingsCard!;

        string[] expected = language == "de"
            ? ["Einstellungen", "Darstellung", "Design", "Dichte", "Seiten dunkel darstellen, wenn das Design dunkel ist", "Animationen reduzieren",
               "Ansicht", "Standard-Layout", "Standard-Zoom", "Gilt für Dateien, die Sie danach öffnen.", "Drucken", "Seitengröße beim Drucken", "Sprache", "Sprache der Oberfläche"]
            : ["Settings", "Appearance", "Theme", "Density", "Show pages dark when the theme is dark", "Reduce animations",
               "View", "Default layout", "Default zoom", "Applies to files you open from now on.", "Printing", "Page size when printing", "Language", "Interface language"];
        var texts = card.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
        foreach (var text in expected)
        {
            Assert.Contains(text, texts);
        }

        Assert.Equal(language == "de" ? "Fertig" : "Done", Find<Button>(card, "SettingsDoneButton").Content);
        Assert.Equal(
            language == "de" ? ["Wie im System", "Hell", "Dunkel"] : ["Same as system", "Light", "Dark"],
            model.ThemeChoices.Select(choice => choice.Title));
        Assert.Equal(language == "de" ? ["Normal", "Kompakt"] : ["Standard", "Compact"], model.DensityChoices.Select(choice => choice.Title));
        Assert.Equal([Strings.LayoutSingle, Strings.LayoutContinuous, Strings.LayoutTwoPages, Strings.LayoutTwoPagesCover], model.LayoutChoices.Select(choice => choice.Title));
        Assert.Equal([Strings.ZoomFitWidth, Strings.ZoomFitPage, Strings.ZoomActualSize], model.ZoomModeChoices.Select(choice => choice.Title));
        Assert.Equal([language == "de" ? "Systemsprache" : "System language", "Deutsch", "English"], model.LanguageChoices.Select(choice => choice.Title));
        Assert.Equal(language == "de" ? "Einstellungen (Strg+Komma)" : "Settings (Ctrl+Comma)", ToolTip.GetTip(Find<Button>(window, "SettingsButton")));

        // Every control of the card has a name for screen readers; a list or a switch is named by the label of its row.
        var controls = card.GetVisualDescendants().OfType<Control>()
            .Where(control => control is Button or ComboBox or ToggleSwitch && control.IsEffectivelyVisible)
            .Where(control => control.GetVisualAncestors().OfType<ComboBox>().FirstOrDefault() is null && control.GetVisualAncestors().OfType<ToggleSwitch>().FirstOrDefault() is null)
            .Where(control => control.GetVisualAncestors().OfType<ScrollBar>().FirstOrDefault() is null)
            .ToArray();
        Assert.Equal(10, controls.Length);
        foreach (var control in controls)
        {
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), $"{control.Name} has no name");
            if (control is ComboBox or ToggleSwitch)
            {
                var label = control.GetVisualParent()!.GetVisualChildren().OfType<TextBlock>().Single();
                Assert.Equal(label.Text, AutomationProperties.GetName(control));
            }
        }

        Assert.Equal(Strings.SettingsClose, AutomationProperties.GetName(Find<Button>(card, "SettingsCloseButton")));
        Assert.Equal(Strings.SettingsClose, ToolTip.GetTip(Find<Button>(card, "SettingsCloseButton")));
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task TheCardColoursMeetAA(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, _, _) = Show(Start(), variant);
        var card = await OpenCardAsync(window);
        double Ratio(Color a, string surface) => Wcag.Contrast(a, TokenTests.Token(surface, variant));

        // The knob of a switch that is on: accent.on on accent.default (Fluent's white is too faint on the dark accent).
        var toggle = Find<ToggleSwitch>(card, "ReduceMotionSwitch");
        toggle.IsChecked = true;
        Settle(window);
        var knob = toggle.GetVisualDescendants().OfType<Ellipse>().Single(ellipse => ellipse.Name == "SwitchKnobOn");
        var fill = Assert.IsAssignableFrom<ISolidColorBrush>(knob.Fill).Color;
        Assert.Equal(TokenTests.Token("Accent.On", variant), fill);
        Assert.True(Ratio(fill, "Accent.Default") >= 4.5, $"{theme}: knob {Ratio(fill, "Accent.Default"):F2}:1");
        toggle.IsChecked = false;

        // The lists: text.primary in a 1 px border.strong frame on surface.raised; hints in text.secondary.
        var list = Find<ComboBox>(card, "ThemeChoice");
        Assert.Equal(TokenTests.Token("Border.Strong", variant), Assert.IsAssignableFrom<ISolidColorBrush>(list.BorderBrush).Color);
        Assert.Equal(new Thickness(1), list.BorderThickness);
        Assert.True(Ratio(TokenTests.Token("Border.Strong", variant), "Surface.Raised") >= 3);
        Assert.True(Ratio(TokenTests.Token("Text.Primary", variant), "Surface.Raised") >= 4.5);
        Assert.True(Ratio(TokenTests.Token("Text.Secondary", variant), "Surface.Raised") >= 4.5);

        // The open list: drawn like the card, the chosen entry on accent.subtle with a check mark (not only a colour).
        list.IsDropDownOpen = true;
        Settle(window);
        var items = list.GetRealizedContainers().OfType<ComboBoxItem>().ToArray();
        Assert.Equal(3, items.Length);
        foreach (var item in items)
        {
            var check = item.GetVisualDescendants().OfType<StrokeIcon>().Single();
            Assert.Equal(item.IsSelected, check.IsEffectivelyVisible);
            Assert.Same(TokenTests.Resource("Pw.Icon.Check", ThemeVariant.Light), check.Data);
        }

        var popup = Assert.IsAssignableFrom<Border>(list.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().Child);
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Assert.IsAssignableFrom<ISolidColorBrush>(popup.Background).Color);
        Assert.True(Ratio(TokenTests.Token("Text.Primary", variant), "Accent.Subtle") >= 4.5);
        Assert.DoesNotContain(list.GetVisualDescendants().OfType<StrokeIcon>(), icon => icon.IsEffectivelyVisible);
        list.IsDropDownOpen = false;
        Settle(window);

        // The card: surface.raised, radius.overlay, the card shadow.
        var box = Find<Border>(card, "SettingsBox");
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color);
        Assert.Equal(new CornerRadius(10), box.CornerRadius);
        Assert.Equal(window.FindResource(variant, "Pw.Shadow.Card"), box.BoxShadow);
    }

    [AvaloniaFact]
    public async Task SettingsSurviveARestartThroughTheCard()
    {
        var (window, viewModel, _) = Show(Start());
        var card = await OpenCardAsync(window);
        var model = viewModel.SettingsCard!;
        Find<ComboBox>(card, "ThemeChoice").SelectedItem = model.ThemeChoices.Single(choice => choice.Value == ThemeSetting.Dark);
        Find<ComboBox>(card, "DensityChoice").SelectedItem = model.DensityChoices.Single(choice => choice.Value == DensitySetting.Compact);
        Find<ToggleSwitch>(card, "DarkPagesSwitch").IsChecked = true;
        Find<ToggleSwitch>(card, "ReduceMotionSwitch").IsChecked = true;
        Find<ComboBox>(card, "LayoutChoice").SelectedItem = model.LayoutChoices.Single(choice => choice.Value == PageLayoutMode.SinglePage);
        Find<ComboBox>(card, "ZoomChoice").SelectedItem = model.ZoomModeChoices.Single(choice => choice.Value == ZoomMode.FitWidth);
        Find<ComboBox>(card, "LanguageChoice").SelectedItem = model.LanguageChoices.Single(choice => choice.Value == LanguageSetting.German);
        Settle(window);

        // The next start reads every value back from the same folder, and its card shows them.
        var restarted = UserSettings.Load(_directory);
        Assert.Equal(
            AppSettings.Default with
            {
                Theme = ThemeSetting.Dark,
                Density = DensitySetting.Compact,
                PageAppearance = PageAppearanceSetting.FollowTheme,
                ReduceMotion = true,
                DefaultLayout = PageLayoutMode.SinglePage,
                DefaultZoomMode = ZoomMode.FitWidth,
                Language = LanguageSetting.German,
            },
            restarted.Current);
        var again = new SettingsViewModel(restarted);
        Assert.Equal(ThemeSetting.Dark, again.SelectedTheme!.Value);
        Assert.Equal(DensitySetting.Compact, again.SelectedDensity!.Value);
        Assert.True(again.DarkPages);
        Assert.True(again.ReduceMotion);
        Assert.Equal(PageLayoutMode.SinglePage, again.SelectedLayout!.Value);
        Assert.Equal(ZoomMode.FitWidth, again.SelectedZoomMode!.Value);
        Assert.Equal(LanguageSetting.German, again.SelectedLanguage!.Value);
    }

    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;
    private const RawInputModifiers Shift = RawInputModifiers.Shift;
    private const RawInputModifiers Alt = RawInputModifiers.Alt;

    private UserSettings Start(string? directory = null)
    {
        var app = Application.Current ?? throw new InvalidOperationException("No app.");
        _settings = UserSettings.Load(directory ?? _directory);
        _appearance = new Appearance(app, _settings);
        return _settings;
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings settings, ThemeVariant? variant = null)
    {
        // Without a variant of its own the window follows the app, like the real one.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings) { IsWindowsTitleBar = true };
        viewModel.AppTheme = _appearance;
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        if (variant is not null)
        {
            window.RequestedThemeVariant = variant;
        }

        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    private static async Task<SettingsCard> OpenCardAsync(Window window)
    {
        Press(window, Key.OemComma, PhysicalKey.Comma, Ctrl);
        await WaitUntilAsync(window, () => Focused(window) is ComboBox { Name: "ThemeChoice" });
        return Find<SettingsCard>(window, "SettingsCard");
    }

    /// <summary>Opens a file in a new tab, waits until the view shows it and closes the panel the window opens by itself.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        return tab;
    }

    private static double Resource(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value), $"Resource {key} is missing");
        return Assert.IsType<double>(value);
    }

    private static TimeSpan Duration(Application app, string key)
    {
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value), $"Resource {key} is missing");
        return Assert.IsType<TimeSpan>(value);
    }

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

    private static async Task FocusAsync(Window window, DocumentView view)
    {
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    private static void Click(Window window, Control control)
    {
        Settle(window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
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
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
