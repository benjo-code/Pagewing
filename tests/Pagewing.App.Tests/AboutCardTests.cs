using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>The about card (<see cref="AboutCard"/>, <see cref="AboutViewModel"/>): name, version, licence, third-party notices.</summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class AboutCardTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;
    private readonly List<IDisposable> _cleanup = [];

    public AboutCardTests()
    {
        Strings.Culture = new CultureInfo("en");
        CultureInfo.CurrentUICulture = new CultureInfo("en");
    }

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        CultureInfo.CurrentUICulture = _originalUiCulture;
    }

    [AvaloniaFact]
    public void ShowsNameVersionAndLicense()
    {
        var (window, viewModel, _) = Show();
        viewModel.ShowOverlay(OverlayKind.About);
        Settle(window);
        var card = Find<AboutCard>(window, "AboutCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Same(viewModel.About, card.DataContext);
        Assert.Equal(Strings.AboutTitle, AutomationProperties.GetName(card));
        Assert.Equal("Pagewing", Find<TextBlock>(card, "AboutName").Text);
        Assert.Equal(AppInfo.Version, viewModel.About!.Version);
        Assert.Contains(AppInfo.Version, Find<TextBlock>(card, "AboutVersion").Text);
        Assert.Equal("GPL-3.0-or-later", Find<TextBlock>(card, "AboutLicense").Text);
        Assert.Equal(Strings.UpdateCheckNote, Find<TextBlock>(card, "AboutUpdateNote").Text);

        // No link anywhere: an external address would have to go through the confirmation card.
        Assert.Empty(card.GetVisualDescendants().OfType<HyperlinkButton>());
    }

    /// <summary>
    /// A test build of the updater (packaging/README.md, "Update prüfen") says so after the version, so nobody mistakes
    /// it for a release; the release build shows the version alone.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("en", "Version", "(test build)")]
    [InlineData("de", "Programmversion", "(Testaufbau)")]
    public void TheVersionLineNamesOnlyATestBuild(string language, string label, string note)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _) = Show();
        viewModel.ShowOverlay(OverlayKind.About);
        Settle(window);
        var text = Find<TextBlock>(Find<AboutCard>(window, "AboutCard"), "AboutVersion").Text;

#if PAGEWING_UPDATE_TESTBUILD
        Assert.Equal($"{label} {AppInfo.Version} {note}", text);
#else
        Assert.Equal($"{label} {AppInfo.Version}", text);
        Assert.DoesNotContain(note, text, StringComparison.Ordinal);
#endif
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public void NamesWhatTheCheckAsksAndWhatItDoesNot(string language)
    {
        Strings.Culture = new CultureInfo(language);
        var (window, viewModel, _) = Show();
        viewModel.ShowOverlay(OverlayKind.About);
        Settle(window);
        var card = Find<AboutCard>(window, "AboutCard");
        var text = Find<TextBlock>(card, "AboutUpdateNote").Text!;

        // What is asked: the public release page of Pagewing on GitHub. What is not: an identifier, the version, data about
        // the user or the computer. And where to switch it off. No claim of the old pledge is left.
        string[] expected = language == "de"
            ? ["öffentliche Release-Seite", "Pagewing", "GitHub", "keine Kennung", "keine Version", "keine Daten über Sie oder Ihren Rechner", "Einstellungen", "abschalten"]
            : ["public release page", "Pagewing", "GitHub", "no identifier", "no version", "no data about you or your computer", "settings", "switch"];
        foreach (var part in expected)
        {
            Assert.Contains(part, text);
        }

        Assert.DoesNotContain("offline", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Nothing leaves", text, StringComparison.Ordinal);
        Assert.DoesNotContain("keine Netzwerkzugriffe", text, StringComparison.Ordinal);
        Assert.Contains("GitHub", Strings.SettingsUpdateHint);

        // The start screen says the same.
        var start = Find<StartScreen>(window, "StartScreen");
        Assert.Contains(start.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == Strings.UpdateCheckNote);
    }

    [AvaloniaFact]
    public async Task NoticesAreLoadedOnlyWhenTheListIsOpened()
    {
        var loads = 0;
        var (window, viewModel, _) = Show(() =>
        {
            loads++;
            return "intro\n====\nLib\nMIT\n\ntext\n";
        });
        viewModel.ShowOverlay(OverlayKind.About);
        Settle(window);
        var card = Find<AboutCard>(window, "AboutCard");
        Assert.Equal(0, loads);
        var list = Find<ItemsControl>(card, "NoticesList");
        Assert.False(list.IsEffectivelyVisible);

        var toggle = Find<ToggleButton>(card, "NoticesToggle");
        toggle.IsChecked = true;
        await WaitUntilAsync(window, () => list.IsEffectivelyVisible && list.GetVisualDescendants().OfType<TextBlock>().Any());
        Assert.Equal(1, loads);
        Assert.Contains(list.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Lib");

        // Closed and opened again: the text is kept.
        toggle.IsChecked = false;
        Settle(window);
        Assert.False(list.IsEffectivelyVisible);
        toggle.IsChecked = true;
        Settle(window);
        Assert.Equal(1, loads);
    }

    [AvaloniaFact]
    public async Task NoticesContainPdfium()
    {
        var (window, viewModel, _) = Show();
        viewModel.ShowOverlay(OverlayKind.About);
        Settle(window);
        var text = viewModel.About!.NoticesText;
        Assert.Contains("PDFium", text);
        Assert.Contains("FreeType", text);

        // The real text opens fast: the list only makes the rows in view, not the 390 KB of text at once.
        var card = Find<AboutCard>(window, "AboutCard");
        var list = Find<ItemsControl>(card, "NoticesList");
        var watch = Stopwatch.StartNew();
        Find<ToggleButton>(card, "NoticesToggle").IsChecked = true;
        await WaitUntilAsync(window, () => list.GetVisualDescendants().OfType<TextBlock>().Any());
        watch.Stop();
        var rows = list.GetVisualDescendants().OfType<TextBlock>().Count();
        Assert.True(rows < 200, $"{rows} rows made");
        Assert.True(watch.ElapsedMilliseconds < 1000, $"opened the list in {watch.ElapsedMilliseconds} ms");
        Console.WriteLine($"about-notices-open ms={watch.ElapsedMilliseconds} rows={rows}");
    }

    [AvaloniaFact]
    public async Task EscClosesTheCardAndFocusReturns()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(TestData.Pdf("text-dense.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        view.Focus();
        await WaitUntilAsync(window, () => view.IsFocused);

        viewModel.ShowOverlay(OverlayKind.About);
        var card = Find<AboutCard>(window, "AboutCard");
        await WaitUntilAsync(window, () => card.IsEffectivelyVisible && Focused(window) is ToggleButton { Name: "NoticesToggle" });
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(card.IsVisible);
        await WaitUntilAsync(window, () => view.IsFocused);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void TheCardColoursMeetAA(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel, _) = Show(variant: variant);
        viewModel.ShowOverlay(OverlayKind.About);
        Settle(window);
        var card = Find<AboutCard>(window, "AboutCard");
        double Ratio(Color a, string surface) => Wcag.Contrast(a, TokenTests.Token(surface, variant));
        Assert.True(Ratio(TokenTests.Token("Text.Primary", variant), "Surface.Raised") >= 4.5);
        Assert.True(Ratio(TokenTests.Token("Text.Secondary", variant), "Surface.Raised") >= 4.5);

        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Assert.IsAssignableFrom<ISolidColorBrush>(Find<TextBlock>(card, "AboutUpdateNote").Foreground).Color);
        var box = Find<Border>(card, "AboutBox");
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color);
        Assert.Equal(new CornerRadius(10), box.CornerRadius);
        Assert.Equal(window.FindResource(variant, "Pw.Shadow.Card"), box.BoxShadow);

        // The notices in the primary text colour on the card.
        Find<ToggleButton>(card, "NoticesToggle").IsChecked = true;
        Settle(window);
        var row = Find<ItemsControl>(card, "NoticesList").GetVisualDescendants().OfType<TextBlock>().First();
        Assert.Equal(TokenTests.Token("Text.Primary", variant), Assert.IsAssignableFrom<ISolidColorBrush>(row.Foreground).Color);
    }

    [AvaloniaFact]
    public void PaletteListsTheAboutCommand()
    {
        var (window, viewModel, _) = Show();
        window.KeyPress(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
        window.KeyRelease(Key.K, RawInputModifiers.Control, PhysicalKey.K, null);
        Settle(window);
        viewModel.Palette!.Query = "about";
        var item = Assert.Single(viewModel.Palette.Items, entry => entry.Key == "app.about");
        Assert.Equal(Strings.CommandAbout, item.Title);
        Assert.Null(viewModel.Commands.ById("app.about")!.Gesture);
        Assert.Same(item, viewModel.Palette.SelectedItem);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Assert.Equal(OverlayKind.About, viewModel.Overlay);
    }

    [AvaloniaFact]
    public void TheCardIsNotBuiltAtStartup()
    {
        var (window, viewModel, _) = Show();
        Assert.Null(viewModel.About);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<AboutCard>(), _ => true);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(Func<string>? notices = null, ThemeVariant? variant = null)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        if (notices is not null)
        {
            viewModel.NoticesLoader = notices;
        }

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

    private static object? Focused(Window window) => window.FocusManager!.GetFocusedElement();

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
