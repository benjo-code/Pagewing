using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core;
using Pagewing.Core.Text;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The card "Pagewing X is available" under the button "Update" (design system 7.11): what it says, how it opens and
/// closes, the hint with the download page for the forms that cannot update themselves (the address goes through the
/// confirmation card like every other), its colours and names, and that a closed window stops listening to the
/// coordinator of the process. The offer comes from a fake source; nothing touches the network.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateCardTests : UpdateTestHost
{
    private static readonly string[] Keys =
    [
        "UpdateButton", "UpdateButtonName", "UpdateCardTitle", "UpdateCardHave", "UpdateCardInstallText", "UpdateInstall",
        "UpdateLater", "UpdateDownloadPage", "UpdateHintDeb", "UpdateHintPortable", "UpdateHintUnknown",
    ];

    [AvaloniaFact]
    public async Task OpensOnClickAndLaterHidesTheCardButKeepsTheButton()
    {
        var (window, viewModel) = await ShowOfferAsync();
        var button = Find<Button>(window, "UpdateButton");

        Click(window, button);

        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.True(viewModel.Update!.IsCardOpen);
        Assert.Equal("Pagewing 0.2.0 is available", Find<TextBlock>(card, "UpdateTitle").Text);
        var text = Find<TextBlock>(card, "UpdateText").Text!;
        Assert.StartsWith($"You have {AppInfo.Version}.", text, StringComparison.Ordinal);
        Assert.Contains(Strings.UpdateCardInstallText, text, StringComparison.Ordinal);
        Assert.True(Find<Button>(card, "UpdateInstallButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(card, "UpdateDownloadButton").IsEffectivelyVisible);

        // Below the title bar, under the button: its right edge lines up with the button's.
        var cardTop = card.TranslatePoint(default, window)!.Value.Y;
        var box = Find<Border>(card, "UpdateBox");
        var boxRight = box.TranslatePoint(new Point(box.Bounds.Width, 0), window)!.Value.X;
        var buttonRight = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
        Assert.True(cardTop >= Find<TitleBar>(window, "TitleBar").Bounds.Height);
        Assert.Equal(buttonRight, boxRight, 0.5);

        // Not modal: no overlay, the document stays usable.
        Assert.Equal(OverlayKind.None, viewModel.Overlay);

        Click(window, Find<Button>(card, "UpdateLaterButton"));

        Assert.False(viewModel.Update.IsCardOpen);
        Assert.False(card.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyVisible);

        // The button opens it again, and a second click closes it.
        Click(window, button);
        Assert.True(card.IsEffectivelyVisible);
        Click(window, button);
        Assert.False(card.IsEffectivelyVisible);
        Assert.True(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task EscClosesTheCardAndReturnsTheFocus()
    {
        var (window, viewModel) = await ShowOfferAsync();
        var button = Find<Button>(window, "UpdateButton");
        button.Focus(NavigationMethod.Tab);
        Settle(window);

        // Opened by keyboard, the card takes the focus on "Later" (Enter twice never installs) ...
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.Same(Find<Button>(card, "UpdateLaterButton"), Focused(window));

        // ... and Esc closes it and gives the focus back to the button.
        Press(window, Key.Escape, PhysicalKey.Escape, None);

        Assert.False(viewModel.Update!.IsCardOpen);
        Assert.False(card.IsEffectivelyVisible);
        Assert.Same(button, Focused(window));
        Assert.True(button.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task InstallStartsTheInstallationAndClosesTheCard()
    {
        var (window, viewModel) = await ShowOfferAsync();
        Click(window, Find<Button>(window, "UpdateButton"));
        var card = Find<UpdateCard>(window, "UpdateCard");

        Click(window, Find<Button>(card, "UpdateInstallButton"));

        Assert.False(viewModel.Update!.IsCardOpen);
        Assert.False(card.IsEffectivelyVisible);
        Assert.Null(viewModel.PendingLink);
    }

    [AvaloniaTheory]
    [InlineData(PackageForm.Deb, true)]
    [InlineData(PackageForm.WindowsPortable, true)]
    [InlineData(PackageForm.Unknown, true)]
    [InlineData(PackageForm.Deb, false)]
    public async Task PortableAndDebShowTheHintWithALinkAndNoInstall(PackageForm form, bool withDocument)
    {
        var launcher = new RecordingLinkLauncher();
        var (window, viewModel) = await ShowOfferAsync(form: form, file: withDocument ? "text-basic.pdf" : null);
        viewModel.LinkLauncher = launcher;
        Click(window, Find<Button>(window, "UpdateButton"));
        var card = Find<UpdateCard>(window, "UpdateCard");

        // No "Install": the hint of the form and "Open download page" instead.
        Assert.False(Find<Button>(card, "UpdateInstallButton").IsEffectivelyVisible);
        var hint = form switch
        {
            PackageForm.Deb => Strings.UpdateHintDeb,
            PackageForm.WindowsPortable => Strings.UpdateHintPortable,
            _ => Strings.UpdateHintUnknown,
        };
        var text = Find<TextBlock>(card, "UpdateText").Text!;
        Assert.StartsWith($"You have {AppInfo.Version}.", text, StringComparison.Ordinal);
        Assert.EndsWith(hint, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Strings.UpdateCardInstallText, text, StringComparison.Ordinal);
        var download = Find<Button>(card, "UpdateDownloadButton");
        Assert.True(download.IsEffectivelyVisible);

        // The address goes through ExternalLink and the confirmation card; nothing is opened before "Open".
        Click(window, download);

        Assert.False(card.IsEffectivelyVisible);
        Assert.True(ExternalLink.TryParse(UpdateTrust.ReleasesPageUrl, out var expected));
        Assert.Equal(expected.Url, viewModel.PendingLink?.Link.Url);
        Assert.True(Find<LinkConfirmCard>(window, "LinkConfirmCard").IsEffectivelyVisible);
        Assert.Empty(launcher.Calls);

        await WaitUntilAsync(window, () => viewModel.IsLinkArmed && viewModel.ConfirmLinkCommand.CanExecute(null));
        viewModel.ConfirmLinkCommand.Execute(null);
        await WaitUntilAsync(window, () => launcher.Calls.Count == 1);

        Assert.Equal([UpdateTrust.ReleasesPageUrl], launcher.Calls);
        Assert.True(Find<Button>(window, "UpdateButton").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [MemberData(nameof(TokenTests.AllThemes), MemberType = typeof(TokenTests))]
    public async Task TheCardColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) =>
            Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // The button: accent.default text and icon on accent.subtle (text 4.5:1), its accent.default edge against the
        // title bar (3:1); hovered or pressed it turns into accent.on on accent.default. The focus ring on the title bar.
        Assert.True(Ratio("Accent.Default", "Accent.Subtle") >= 4.5, $"{theme}: button text {Ratio("Accent.Default", "Accent.Subtle"):F2}");
        Assert.True(Ratio("Accent.Default", "Surface.Window") >= 3, $"{theme}: button edge {Ratio("Accent.Default", "Surface.Window"):F2}");
        Assert.True(Ratio("Accent.On", "Accent.Default") >= 4.5, $"{theme}: hovered button {Ratio("Accent.On", "Accent.Default"):F2}");
        Assert.True(Ratio("Focus.Ring", "Surface.Window") >= 3, $"{theme}: focus ring {Ratio("Focus.Ring", "Surface.Window"):F2}");

        // The card: text.primary and text.secondary on surface.raised, the icon in accent.default (3:1), the primary
        // button accent.on on accent.default, the secondary button's border.strong edge, the focus ring.
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5);
        Assert.True(Ratio("Text.Secondary", "Surface.Raised") >= 4.5);
        Assert.True(Ratio("Accent.Default", "Surface.Raised") >= 3);
        Assert.True(Ratio("Border.Strong", "Surface.Raised") >= 3);
        Assert.True(Ratio("Focus.Ring", "Surface.Raised") >= 3);

        if (variant == HighContrast.Variant)
        {
            // The window below checks which tokens the elements use; high contrast has the same keys
            // (HighContrastTests.TheVariantHasEveryTokenOfTheOtherThemes).
            return;
        }

        var (window, _) = await ShowOfferAsync();
        window.RequestedThemeVariant = variant;
        Settle(window);
        var button = Find<Button>(window, "UpdateButton");
        Assert.Equal(TokenTests.Token("Accent.Subtle", variant), Brush(button.Background));
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(button.Foreground));
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(button.BorderBrush));

        Click(window, button);
        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Brush(Find<Border>(card, "UpdateBox").Background));
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Brush(Find<TextBlock>(card, "UpdateText").Foreground));
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(Find<StrokeIcon>(card, "UpdateCardIcon").Brush));
        Assert.Contains("primary", Find<Button>(card, "UpdateInstallButton").Classes);
    }

    [AvaloniaFact]
    public void TextsExistInBothLanguages()
    {
        foreach (var key in Keys)
        {
            var accessor = typeof(Strings).GetProperty(key) ?? throw new InvalidOperationException($"No accessor {key}.");
            Strings.Culture = new CultureInfo("en");
            var english = (string)accessor.GetValue(null)!;
            Strings.Culture = new CultureInfo("de");
            var german = (string)accessor.GetValue(null)!;

            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.False(string.IsNullOrWhiteSpace(german), key);
            Assert.NotEqual(english, german);
        }

        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Pagewing 0.2.0 ist verfügbar", string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateCardTitle), "0.2.0"));
        Assert.Equal("Aktualisieren – Update verfügbar", Strings.UpdateButtonName);
        Assert.Equal("Installieren", Strings.UpdateInstall);
        Assert.Equal("Später", Strings.UpdateLater);
        Assert.Equal("Download-Seite öffnen", Strings.UpdateDownloadPage);
    }

    [AvaloniaFact]
    public async Task TheCardSpeaksGerman()
    {
        Strings.Culture = new CultureInfo("de");
        var (window, _) = await ShowOfferAsync();
        Click(window, Find<Button>(window, "UpdateButton"));
        var card = Find<UpdateCard>(window, "UpdateCard");

        Assert.Equal("Pagewing 0.2.0 ist verfügbar", Find<TextBlock>(card, "UpdateTitle").Text);
        Assert.StartsWith($"Sie haben {AppInfo.Version}.", Find<TextBlock>(card, "UpdateText").Text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task AutomationNamesOfCardAndButtons()
    {
        var (window, _) = await ShowOfferAsync();
        Click(window, Find<Button>(window, "UpdateButton"));
        var card = Find<UpdateCard>(window, "UpdateCard");

        Assert.Equal("Pagewing 0.2.0 is available", ControlAutomationPeer.CreatePeerForElement(card).GetName());
        Assert.Equal("Later", ControlAutomationPeer.CreatePeerForElement(Find<Button>(card, "UpdateLaterButton")).GetName());
        Assert.Equal("Install", ControlAutomationPeer.CreatePeerForElement(Find<Button>(card, "UpdateInstallButton")).GetName());
        Assert.Equal("Open download page", ControlAutomationPeer.CreatePeerForElement(Find<Button>(card, "UpdateDownloadButton")).GetName());
        Assert.Equal("Update available", ControlAutomationPeer.CreatePeerForElement(Find<Button>(window, "UpdateButton")).GetName());
    }

    [AvaloniaTheory]
    [InlineData("en", 1024)]
    [InlineData("en", 800)]
    [InlineData("de", 1024)]
    [InlineData("de", 800)]
    public async Task EveryAccessibleNameContainsTheVisibleText(string language, double width)
    {
        // WCAG 2.2, 2.5.3 "Label in Name": speech input reaches a button by the words it shows.
        Strings.Culture = new CultureInfo(language);
        var (window, _) = await ShowOfferAsync(width: width);
        var button = Find<Button>(window, "UpdateButton");
        var name = ControlAutomationPeer.CreatePeerForElement(button).GetName();
        Assert.StartsWith(Strings.UpdateButton, name, StringComparison.CurrentCultureIgnoreCase);
        Assert.Equal(Strings.UpdateButtonName, name);

        // The buttons of the card ("Install" and "Open download page" share the place) are named by their text.
        Click(window, button);
        var card = Find<UpdateCard>(window, "UpdateCard");
        foreach (var buttonName in new[] { "UpdateLaterButton", "UpdateInstallButton", "UpdateDownloadButton" })
        {
            var cardButton = Find<Button>(card, buttonName);
            var visible = Assert.IsType<string>(cardButton.Content);
            Assert.Contains(visible, ControlAutomationPeer.CreatePeerForElement(cardButton).GetName(), StringComparison.CurrentCultureIgnoreCase);
        }
    }

    [AvaloniaFact]
    public async Task ASecondWindowShowsTheButtonToo()
    {
        Source.Offer("0.2.0");
        var factory = CountingFactory();
        var gate = new UpdateStartGate();
        var (first, firstModel, _) = await ShowAppAsync(factory: factory, beforeShow: model => model.UpdateStartGate = gate);
        await WaitSharpAsync(first, firstModel);
        await WaitUntilAsync(first, () => firstModel.Update?.ShowButton == true);

        // A later window of the same process shares the coordinator and shows the offer without a check of its own.
        var (second, secondModel, _) = await ShowAppAsync(factory: factory, beforeShow: model => model.UpdateStartGate = gate);
        await WaitUntilAsync(second, () => secondModel.Update?.ShowButton == true);

        Assert.True(Find<Button>(second, "UpdateButton").IsEffectivelyVisible);
        Assert.Equal(1, Source.FetchCalls);
    }

    [AvaloniaFact]
    public async Task AClosedWindowStopsListening()
    {
        var factory = CountingFactory();
        var (window, viewModel, _) = await ShowAppAsync(factory: factory);
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1 && viewModel.Update is not null);
        var coordinator = factory()!;
        Assert.Equal(1, coordinator.ListenerCount);
        var update = viewModel.Update!;

        window.Close();
        viewModel.Dispose();

        // The coordinator of the process holds no handler of the window any more, so it neither tells nor keeps it.
        Assert.Equal(0, coordinator.ListenerCount);
        Source.Offer("0.2.0");
        await coordinator.CheckNowAsync();
        Assert.Equal(UpdateStage.Available, coordinator.State.Stage);
        Assert.Equal(UpdateStage.None, update.Stage);
    }

    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private async Task<(MainWindow Window, MainViewModel ViewModel)> ShowOfferAsync(
        PackageForm form = PackageForm.WindowsInstaller,
        string? file = "text-basic.pdf",
        double width = 1024)
    {
        Source.Offer("0.2.0");
        var (window, viewModel, _) = await ShowAppAsync(file: file, factory: CountingFactory(form: form), width: width);
        if (file is not null)
        {
            await WaitSharpAsync(window, viewModel);
        }

        await WaitUntilAsync(window, () => viewModel.Update?.ShowButton == true);
        Settle(window);
        return (window, viewModel);
    }

    /// <summary>Records the addresses it is asked to open and opens nothing.</summary>
    private sealed class RecordingLinkLauncher : ILinkLauncher
    {
        public List<string> Calls { get; } = [];

        public Task LaunchAsync(ExternalLink link)
        {
            ArgumentNullException.ThrowIfNull(link);
            Calls.Add(link.Url);
            return Task.CompletedTask;
        }
    }
}
