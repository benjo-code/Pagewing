using System.Globalization;
using System.Text;
using Avalonia.Automation;
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
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// What the command "Check for updates" shows when it is done (design system 7.13, Ruling R18): an update opens the card,
/// "up to date" and "could not check" show a short note in the place of the card, which goes with Esc, "Close" or after a
/// few seconds. The check in the background stays silent. The answers come from a fake source; nothing touches the
/// network.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateCheckNoteTests : UpdateTestHost
{
    private static readonly string[] Keys = ["UpdateNoteUpToDate", "UpdateNoteFailed", "CommandCloseUpdateCard"];

    [AvaloniaFact]
    public async Task UpToDateShowsTheNote()
    {
        Source.Offer("0.1.0");
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();

        await CheckFromCommandAsync(window, viewModel);

        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.True(card.IsEffectivelyVisible);
        var expected = string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateNoteUpToDate), AppInfo.Version);
        Assert.Equal(expected, Find<TextBlock>(card, "UpdateNoteText").Text);
        Assert.Equal($"You have the latest version of Pagewing ({AppInfo.Version}).", expected);

        // The state is the text and its symbol, never only a colour: the check mark, not the warning.
        Assert.True(Find<StrokeIcon>(card, "UpdateNoteUpToDateIcon").IsEffectivelyVisible);
        Assert.False(Find<StrokeIcon>(card, "UpdateNoteFailedIcon").IsEffectivelyVisible);

        // The note takes the place of the offer: no "Install", no button "Update" (nothing is on offer).
        Assert.False(Find<TextBlock>(card, "UpdateTitle").IsEffectivelyVisible);
        Assert.False(Find<Button>(card, "UpdateInstallButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "UpdateButton").IsEffectivelyVisible);

        // Not modal, and it does not take the focus.
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(card.IsKeyboardFocusWithin);
    }

    [AvaloniaTheory]
    [InlineData("throws")]
    [InlineData("nothing")]
    [InlineData("refused")]
    public async Task ACheckThatFailedShowsTheOtherNote(string failure)
    {
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();
        switch (failure)
        {
            case "throws":
                Source.Throws = new HttpRequestException("no network");
                break;
            case "refused":
                Source.Offer("0.2.0");
                Source.Documents = Source.Documents! with { Signature = "test-key:" + Convert.ToBase64String(new byte[64]) };
                break;
            default:
                Source.Documents = null;
                break;
        }

        await CheckFromCommandAsync(window, viewModel);

        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal(Strings.UpdateNoteFailed, Find<TextBlock>(card, "UpdateNoteText").Text);
        Assert.Equal("Pagewing could not check for updates right now. Check the internet connection or try again later.", Strings.UpdateNoteFailed);
        Assert.True(Find<StrokeIcon>(card, "UpdateNoteFailedIcon").IsEffectivelyVisible);
        Assert.False(Find<StrokeIcon>(card, "UpdateNoteUpToDateIcon").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task AnUpdateOpensTheCard()
    {
        Source.Offer("0.2.0");
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();
        Assert.Null(viewModel.Update);

        viewModel.Commands.ById("app.checkForUpdates")!.Execute();
        await WaitUntilAsync(window, () => viewModel.Update?.IsCardOpen == true);
        Settle(window);

        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Equal("Pagewing 0.2.0 is available", Find<TextBlock>(card, "UpdateTitle").Text);
        Assert.True(Find<Button>(card, "UpdateInstallButton").IsEffectivelyVisible);
        Assert.False(viewModel.Update!.IsNoteOpen);
        Assert.False(Find<TextBlock>(card, "UpdateNoteText").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("0.1.0")]
    [InlineData(null)]
    public async Task TheCheckInTheBackgroundShowsNothing(string? offered)
    {
        // Up to date, or no answer: the check after the first sharp page stays silent.
        if (offered is not null)
        {
            Source.Offer(offered);
        }

        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1 && viewModel.Update is not null);
        await RunForAsync(window, 200);

        Assert.False(viewModel.Update!.IsNoteOpen);
        Assert.False(viewModel.Update.IsCardOpen);
        Assert.Null(TryFind<UpdateCard>(window, "UpdateCard"));
    }

    [AvaloniaFact]
    public async Task EscCloseAndTheTimeTakeTheNoteAway()
    {
        Source.Offer("0.1.0");
        var clock = new PrintClock();
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync(model => model.Clock = clock);
        var card = await ShowNoteAsync(window, viewModel);

        // Esc, wherever the focus is (the note never takes it).
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Settle(window);
        Assert.False(viewModel.Update!.IsNoteOpen);
        Assert.False(card.IsEffectivelyVisible);

        // "Close".
        card = await ShowNoteAsync(window, viewModel);
        Click(window, Find<Button>(card, "UpdateNoteCloseButton"));
        Assert.False(viewModel.Update.IsNoteOpen);
        Assert.False(card.IsEffectivelyVisible);

        // A few seconds.
        card = await ShowNoteAsync(window, viewModel);
        clock.Advance(UpdateViewModel.NoteLifetime - TimeSpan.FromSeconds(1));
        await RunForAsync(window, 50);
        Assert.True(card.IsEffectivelyVisible);
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(window, () => !viewModel.Update.IsNoteOpen);
        Settle(window);
        Assert.False(card.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task EscStillGoesToAnOverlayFirst()
    {
        Source.Offer("0.1.0");
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();
        await ShowNoteAsync(window, viewModel);
        viewModel.ShowHelpCommand.Execute(null);
        Settle(window);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Settle(window);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(viewModel.Update!.IsNoteOpen);
    }

    [AvaloniaFact]
    public async Task TheNoteSpeaksGerman()
    {
        Strings.Culture = new CultureInfo("de");
        Source.Offer("0.1.0");
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();

        var card = await ShowNoteAsync(window, viewModel);

        Assert.Equal($"Sie haben die neueste Version von Pagewing ({AppInfo.Version}).", Find<TextBlock>(card, "UpdateNoteText").Text);
        Assert.Equal("Schließen", Find<Button>(card, "UpdateNoteCloseButton").Content);
        Assert.Equal(
            "Pagewing konnte gerade nicht nach Updates suchen. Prüfen Sie die Internetverbindung oder versuchen Sie es später erneut.",
            Strings.UpdateNoteFailed);
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
    }

    [AvaloniaTheory]
    [InlineData("en")]
    [InlineData("de")]
    public async Task TheAccessibleNamesContainTheVisibleText(string language)
    {
        Strings.Culture = new CultureInfo(language);
        Source.Offer("0.1.0");
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();
        var card = await ShowNoteAsync(window, viewModel);

        var text = Find<TextBlock>(card, "UpdateNoteText");
        Assert.Equal(text.Text, ControlAutomationPeer.CreatePeerForElement(card).GetName());
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(text));
        var close = Find<Button>(card, "UpdateNoteCloseButton");
        Assert.Contains(Assert.IsType<string>(close.Content), ControlAutomationPeer.CreatePeerForElement(close).GetName(), StringComparison.CurrentCultureIgnoreCase);
    }

    [AvaloniaTheory]
    [MemberData(nameof(TokenTests.AllThemes), MemberType = typeof(TokenTests))]
    public async Task TheNoteColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) =>
            Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // The text in text.primary on surface.raised (4.5:1), the symbols (3:1), the focus ring and the button's edge.
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5, $"{theme}: text {Ratio("Text.Primary", "Surface.Raised"):F2}");
        Assert.True(Ratio("Accent.Default", "Surface.Raised") >= 3, $"{theme}: check mark {Ratio("Accent.Default", "Surface.Raised"):F2}");
        Assert.True(Ratio("Danger.Default", "Surface.Raised") >= 3, $"{theme}: warning {Ratio("Danger.Default", "Surface.Raised"):F2}");
        Assert.True(Ratio("Border.Strong", "Surface.Raised") >= 3);
        Assert.True(Ratio("Focus.Ring", "Surface.Raised") >= 3);

        if (variant == HighContrast.Variant)
        {
            // High contrast has the same keys (HighContrastTests.TheVariantHasEveryTokenOfTheOtherThemes).
            return;
        }

        Source.Throws = new HttpRequestException("no network");
        var (window, viewModel) = await ShowWithoutBackgroundCheckAsync();
        window.RequestedThemeVariant = variant;
        var card = await ShowNoteAsync(window, viewModel);

        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Brush(Find<Border>(card, "UpdateBox").Background));
        Assert.Equal(TokenTests.Token("Text.Primary", variant), Brush(Find<TextBlock>(card, "UpdateNoteText").Foreground));
        Assert.Equal(TokenTests.Token("Danger.Default", variant), Brush(Find<StrokeIcon>(card, "UpdateNoteFailedIcon").Brush));
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(Find<StrokeIcon>(card, "UpdateNoteUpToDateIcon").Brush));
    }

    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static async Task CheckFromCommandAsync(MainWindow window, MainViewModel viewModel)
    {
        viewModel.Commands.ById("app.checkForUpdates")!.Execute();
        await WaitUntilAsync(window, () => viewModel.Update?.IsNoteOpen == true);
        Settle(window);
    }

    private static async Task<UpdateCard> ShowNoteAsync(MainWindow window, MainViewModel viewModel)
    {
        await CheckFromCommandAsync(window, viewModel);
        var card = Find<UpdateCard>(window, "UpdateCard");
        Assert.True(card.IsEffectivelyVisible);
        return card;
    }

    /// <summary>A window with a document and the check in the background switched off: only the command asks.</summary>
    private async Task<(MainWindow Window, MainViewModel ViewModel)> ShowWithoutBackgroundCheckAsync(Action<MainViewModel>? beforeShow = null)
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { UpdateCheck = false });
        var (window, viewModel, _) = await ShowAppAsync(settings: settings, factory: CountingFactory(settings), beforeShow: beforeShow);
        await WaitSharpAsync(window, viewModel);
        await RunForAsync(window, 50);
        Assert.Equal(0, Source.FetchCalls);
        return (window, viewModel);
    }
}
