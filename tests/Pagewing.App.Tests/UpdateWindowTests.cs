using System.Globalization;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
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
/// The window "Updating Pagewing" (design system 7.12, section 3 of the mockups): four steps in words and marks, the bar
/// with percent and megabytes, "Cancel" until the step "Install", the message with "Try again" and "Close" after a
/// failure. It opens with "Install" on the card. Network and installer are fakes.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateWindowTests : UpdateTestHost
{
    private const long MiB = 1024 * 1024;

    // 31.4 of 68 MiB: 46 %, "31 of 68 MB" as in the mockup.
    private static readonly long Received = (long)(31.4 * MiB);

    private static readonly string[] Keys =
    [
        "UpdateWindowHeader", "UpdateWindowTitle", "UpdateWindowFailedTitle", "UpdateWindowSubtitle", "UpdateStepDownload",
        "UpdateStepVerify", "UpdateStepInstall", "UpdateStepRestart", "UpdateStepDone", "UpdateStepRunning", "UpdateStepOpen",
        "UpdateStepFailed", "UpdateProgressName", "UpdateProgressPercent", "UpdateProgressSize", "UpdateWindowCancel",
        "UpdateWindowRetry", "UpdateWindowClose", "UpdateErrorUnreachable", "UpdateErrorWithdrawn", "UpdateErrorRejected",
        "UpdateErrorChecksum", "UpdateErrorStorage", "UpdateErrorNoInstaller", "UpdateErrorInstallFailed",
        "UpdateErrorHelperInstallFailed", "UpdateErrorVersionMismatch",
    ];

    [AvaloniaFact]
    public async Task ShowsTheFourStepsAndTheProgress()
    {
        var (window, _) = await InstallAsync();

        Assert.Equal("Update Pagewing", window.Title);
        Assert.Equal("Update Pagewing", Find<TextBlock>(window, "UpdateHeaderText").Text);
        Assert.Equal("Updating Pagewing", Find<TextBlock>(window, "UpdateHeading").Text);
        Assert.Equal(
            $"{AppInfo.Version} → 0.2.0 · Please keep this window open. Your tabs come back afterwards.",
            Find<TextBlock>(window, "UpdateSubtitle").Text);
        Assert.Equal(["Download", "Verify", "Install", "Restart"], Labels(window));

        var bar = Find<ProgressBar>(window, "UpdateProgress");
        Assert.Equal(46, bar.Value);
        Assert.Equal(100, bar.Maximum);
        Assert.False(bar.IsIndeterminate);
        Assert.Equal("46%", Find<TextBlock>(window, "UpdatePercent").Text);
        Assert.Equal("31 of 68 MB", Find<TextBlock>(window, "UpdateSize").Text);

        // Until the step "Install" the download can be cancelled; there is no error.
        Assert.True(Find<Button>(window, "UpdateCancelButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "UpdateRetryButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "UpdateCloseButton").IsEffectivelyVisible);
        Assert.False(Find<TextBlock>(window, "UpdateFailureText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task TheWindowSpeaksGerman()
    {
        Strings.Culture = new CultureInfo("de");
        var (window, _) = await InstallAsync();

        Assert.Equal("Pagewing aktualisieren", window.Title);
        Assert.Equal("Pagewing wird aktualisiert", Find<TextBlock>(window, "UpdateHeading").Text);
        Assert.Equal(
            $"{AppInfo.Version} → 0.2.0 · Bitte das Fenster offen lassen. Ihre Tabs kommen danach zurück.",
            Find<TextBlock>(window, "UpdateSubtitle").Text);
        Assert.Equal(["Herunterladen", "Prüfen", "Installieren", "Neustart"], Labels(window));
        Assert.Equal("46 %", Find<TextBlock>(window, "UpdatePercent").Text);
        Assert.Equal("31 von 68 MB", Find<TextBlock>(window, "UpdateSize").Text);
        Assert.Equal("Abbrechen", Find<Button>(window, "UpdateCancelButton").Content);
    }

    [AvaloniaFact]
    public async Task StepStateIsNotOnlyColour()
    {
        Applier.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (window, coordinator) = await InstallAsync(large: false);

        // Downloading: the first step runs (ring with a dot, bold), the others are open (empty ring).
        var rows = Rows(window);
        AssertStep(rows[0], "Running", "Download: in progress", bold: true);
        AssertStep(rows[1], "Open", "Verify: not started", bold: false);
        AssertStep(rows[2], "Open", "Install: not started", bold: false);
        AssertStep(rows[3], "Open", "Restart: not started", bold: false);

        // Installing: download and check are done (check mark), the installation runs; no cancelling any more.
        Source.DownloadGate!.SetResult();
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Installing);
        rows = Rows(window);
        AssertStep(rows[0], "Done", "Download: done", bold: false);
        AssertStep(rows[1], "Done", "Verify: done", bold: false);
        AssertStep(rows[2], "Running", "Install: in progress", bold: true);
        AssertStep(rows[3], "Open", "Restart: not started", bold: false);
        Assert.False(Find<Button>(window, "UpdateCancelButton").IsEffectivelyVisible);
        Assert.True(Find<ProgressBar>(window, "UpdateProgress").IsIndeterminate);

        Applier.Gate.SetResult();
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Restarting);
        rows = Rows(window);
        AssertStep(rows[2], "Done", "Install: done", bold: false);
        AssertStep(rows[3], "Running", "Restart: in progress", bold: true);
    }

    [AvaloniaFact]
    public async Task AFailedStepShowsACrossAndTheMessageWithRetryAndClose()
    {
        Source.Offer("0.2.0");
        Source.Corrupt = true;
        var (window, coordinator) = await InstallAsync(offer: false);
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Failed);

        var rows = Rows(window);
        AssertStep(rows[0], "Done", "Download: done", bold: false);
        AssertStep(rows[1], "Failed", "Verify: failed", bold: true);
        Assert.Equal("The update did not go through", Find<TextBlock>(window, "UpdateHeading").Text);
        var text = Find<TextBlock>(window, "UpdateFailureText");
        Assert.True(text.IsEffectivelyVisible);
        Assert.Equal(Strings.UpdateErrorChecksum, text.Text);
        Assert.True(Find<StrokeIcon>(window, "UpdateFailureIcon").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "UpdateCancelButton").IsEffectivelyVisible);
        var retry = Find<Button>(window, "UpdateRetryButton");
        Assert.True(retry.IsEffectivelyVisible);
        Assert.Contains("primary", retry.Classes);
        Assert.Same(retry, Focused(window));

        // "Try again" runs it again in the same window.
        Source.Corrupt = false;
        Click(window, retry);
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Restarting);
        Assert.Equal(1, Applier.Calls);
        Assert.False(Find<TextBlock>(window, "UpdateFailureText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task CloseAfterAFailureGoesBackToTheOffer()
    {
        Source.Offer("0.2.0");
        Source.DownloadBreaks = true;
        var (window, coordinator) = await InstallAsync(offer: false);
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Failed);
        Assert.Equal(Strings.UpdateErrorUnreachable, Find<TextBlock>(window, "UpdateFailureText").Text);

        Click(window, Find<Button>(window, "UpdateCloseButton"));

        Assert.False(window.IsVisible);
        Assert.Null(UpdateWindow.For(coordinator));
        Assert.Equal(UpdateStage.Available, coordinator.State.Stage);
        Assert.True(Find<Button>(LastWindow!, "UpdateButton").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task WithoutAnInstallerTheWindowOffersTheDownloadPage()
    {
        Source.Offer("0.2.0");
        var (window, coordinator) = await InstallAsync(offer: false, factory: () => NewCoordinator(noApplier: true));
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Failed);

        Assert.Equal(Strings.UpdateErrorNoInstaller, Find<TextBlock>(window, "UpdateFailureText").Text);
        Assert.False(Find<Button>(window, "UpdateRetryButton").IsEffectivelyVisible);
        var download = Find<Button>(window, "UpdateDownloadPageButton");
        Assert.True(download.IsEffectivelyVisible);
        Assert.Contains("primary", download.Classes);

        // The address goes through the confirmation card of the main window; the progress window closes.
        Click(window, download);

        Assert.False(window.IsVisible);
        Assert.True(ExternalLink.TryParse(UpdateTrust.ReleasesPageUrl, out var expected));
        Assert.Equal(expected.Url, LastViewModel!.PendingLink?.Link.Url);
    }

    [AvaloniaFact]
    public async Task EscOrCancelDuringTheDownloadStopsItAndClosesTheWindow()
    {
        var (window, coordinator) = await InstallAsync();

        // Only the press: the window may be gone before a release would reach it.
        window.KeyPress(Key.Escape, None, PhysicalKey.Escape, null);
        await WaitUntilAsync(LastWindow!, () => !window.IsVisible);

        Assert.Equal(UpdateState.Offer(new UpdateVersion(0, 2, 0)), coordinator.State);
        Assert.False(File.Exists(Source.LastPart));
        Assert.Equal(0, Applier.Calls);

        // Installing again opens a new window, whose "Cancel" does the same.
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LastViewModel!.Update!.InstallCommand.Execute(null);
        await WaitUntilAsync(LastWindow!, () => UpdateWindow.For(coordinator) is { IsVisible: true } && File.Exists(Source.LastPart));
        var second = UpdateWindow.For(coordinator)!;
        OwnWindow(second);
        Assert.NotSame(window, second);
        Click(second, Find<Button>(second, "UpdateCancelButton"));
        await WaitUntilAsync(LastWindow!, () => !second.IsVisible);
        Assert.Equal(UpdateStage.Available, coordinator.State.Stage);
    }

    [AvaloniaFact]
    public async Task EscDoesNotCloseWhileInstalling()
    {
        Applier.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (window, coordinator) = await InstallAsync(large: false);
        Source.DownloadGate!.SetResult();
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Installing);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        window.Close();
        Settle(window);

        // Neither Esc nor closing the window stops an installation that has begun.
        Assert.True(window.IsVisible);
        Assert.Equal(UpdateStage.Installing, coordinator.State.Stage);
        Applier.Gate.SetResult();
        await WaitUntilAsync(window, () => coordinator.State.Stage == UpdateStage.Restarting);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.True(window.IsVisible);

        // When the app ends (the installer closes the windows), the progress window goes with its owner.
        LastWindow!.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        Assert.Null(UpdateWindow.For(coordinator));
    }

    [AvaloniaFact]
    public async Task TheUpdateButtonBringsTheRunningWindowBack()
    {
        var (window, coordinator) = await InstallAsync();
        var main = LastWindow!;

        Click(main, Find<Button>(main, "UpdateButton"));

        // No card with a second "Install" while it runs: the progress window comes back instead.
        Assert.False(LastViewModel!.Update!.IsCardOpen);
        Assert.Same(window, UpdateWindow.For(coordinator));
        Assert.True(window.IsVisible);
        Assert.Equal(1, Source.DownloadCalls);
    }

    [AvaloniaFact]
    public async Task AutomationNamesAndProgressValue()
    {
        var (window, _) = await InstallAsync();

        Assert.Equal("Update Pagewing", ControlAutomationPeer.CreatePeerForElement(window).GetName());
        var bar = ControlAutomationPeer.CreatePeerForElement(Find<ProgressBar>(window, "UpdateProgress"));
        Assert.Equal("Progress", bar.GetName());
        var range = Assert.IsAssignableFrom<IRangeValueProvider>(bar);
        Assert.Equal(46, range.Value);
        Assert.Equal(0, range.Minimum);
        Assert.Equal(100, range.Maximum);

        // Every step is named with its text and its state; the buttons by their text.
        Assert.Equal(
            ["Download: in progress", "Verify: not started", "Install: not started", "Restart: not started"],
            Rows(window).Select(row => ControlAutomationPeer.CreatePeerForElement(row).GetName()));
        var cancel = Find<Button>(window, "UpdateCancelButton");
        Assert.Equal("Cancel", ControlAutomationPeer.CreatePeerForElement(cancel).GetName());
    }

    [AvaloniaTheory]
    [MemberData(nameof(TokenTests.AllThemes), MemberType = typeof(TokenTests))]
    public async Task ColoursMeetAA(string theme)
    {
        var variant = theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "HighContrast" => HighContrast.Variant,
            _ => ThemeVariant.Light,
        };
        double Ratio(string foreground, string background) =>
            Wcag.Contrast(TokenTests.Token(foreground, variant), TokenTests.Token(background, variant));

        // The header: text.secondary and the app symbol (accent.default) on surface.window. The body on surface.raised: the
        // texts, the done mark (accent.default, its check in accent.on), the ring of an open step (border.strong), the
        // failure text and symbol (danger.default), the bar (accent.default on its surface.hover track), the focus ring.
        Assert.True(Ratio("Text.Secondary", "Surface.Window") >= 4.5, $"{theme}: header text");
        Assert.True(Ratio("Accent.Default", "Surface.Window") >= 3, $"{theme}: header symbol");
        Assert.True(Ratio("Text.Primary", "Surface.Raised") >= 4.5, $"{theme}: text");
        Assert.True(Ratio("Text.Secondary", "Surface.Raised") >= 4.5, $"{theme}: secondary text");
        Assert.True(Ratio("Accent.Default", "Surface.Raised") >= 3, $"{theme}: done and running mark");
        Assert.True(Ratio("Accent.On", "Accent.Default") >= 4.5, $"{theme}: check on the done mark");
        Assert.True(Ratio("Border.Strong", "Surface.Raised") >= 3, $"{theme}: open ring");
        Assert.True(Ratio("Danger.Default", "Surface.Raised") >= 4.5, $"{theme}: failure");
        Assert.True(Ratio("Accent.Default", "Surface.Hover") >= 3, $"{theme}: bar against its track");
        Assert.True(Ratio("Focus.Ring", "Surface.Raised") >= 3, $"{theme}: focus ring");

        if (variant == HighContrast.Variant)
        {
            // The elements below use the same keys in high contrast (HighContrastTests.TheVariantHasEveryTokenOfTheOtherThemes).
            return;
        }

        var (window, _) = await InstallAsync();
        window.RequestedThemeVariant = variant;
        Settle(window);
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Brush(window.Background));
        Assert.Equal(TokenTests.Token("Surface.Window", variant), Brush(Find<Border>(window, "UpdateHeader").Background));
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Brush(Find<TextBlock>(window, "UpdateHeaderText").Foreground));
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(Find<StrokeIcon>(window, "UpdateHeaderIcon").Brush));
        var bar = Find<ProgressBar>(window, "UpdateProgress");
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(bar.Foreground));
        Assert.Equal(TokenTests.Token("Surface.Hover", variant), Brush(bar.Background));
        var rows = Rows(window);
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(Find<Border>(rows[0], "UpdateStepRunning").BorderBrush));
        Assert.Equal(TokenTests.Token("Border.Strong", variant), Brush(Find<Border>(rows[1], "UpdateStepOpen").BorderBrush));
        Assert.Equal(TokenTests.Token("Accent.Default", variant), Brush(Find<Border>(rows[0], "UpdateStepDone").Background));
        Assert.Equal(TokenTests.Token("Danger.Default", variant), Brush(Find<TextBlock>(window, "UpdateFailureText").Foreground));
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

        // German addresses the user formally, like the rest of the interface.
        Strings.Culture = new CultureInfo("de");
        Assert.Contains("Ihre Tabs", Strings.UpdateWindowSubtitle, StringComparison.Ordinal);
        Assert.Equal("Erneut versuchen", Strings.UpdateWindowRetry);
        Assert.Equal("Schließen", Strings.UpdateWindowClose);
    }

    private MainViewModel? LastViewModel { get; set; }

    private static Color Brush(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

    private static List<Border> Rows(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(border => border.Name == "UpdateStepRow")];

    private static List<string?> Labels(Window window) =>
        [.. Rows(window).Select(row => Find<TextBlock>(row, "UpdateStepLabel").Text)];

    private static void AssertStep(Border row, string state, string name, bool bold)
    {
        // Exactly one mark per step, and each state has its own shape.
        foreach (var mark in new[] { "Done", "Running", "Open", "Failed" })
        {
            var element = row.GetVisualDescendants().OfType<Control>().First(control => control.Name == "UpdateStep" + mark);
            Assert.Equal(mark == state, element.IsEffectivelyVisible);
        }

        Assert.Equal(name, ControlAutomationPeer.CreatePeerForElement(row).GetName());
        Assert.Equal(bold, Find<TextBlock>(row, "UpdateStepLabel").FontWeight >= FontWeight.SemiBold);
    }

    /// <summary>
    /// Shows a window with an offer, opens the card and clicks "Install"; returns the progress window once the download
    /// has started. The offer is 0.2.0 of 68 MiB whose download stops at 31.4 MiB for good (<paramref name="large"/>), or
    /// one of the small test file that stops at half until <c>Source.DownloadGate</c> opens and then verifies; without
    /// <paramref name="offer"/> the test prepared the source.
    /// </summary>
    private async Task<(UpdateWindow Window, UpdateCoordinator Coordinator)> InstallAsync(
        bool offer = true, bool large = true, Func<UpdateCoordinator?>? factory = null)
    {
        if (offer)
        {
            Source.Offer("0.2.0", size: large ? 68 * MiB : null);
            Source.ReportBytes = large ? Received : null;
            Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        UpdateCoordinator? made = null;
        var (main, viewModel, _) = await ShowAppAsync(factory: () => made ??= (factory ?? (() => NewCoordinator()))());
        LastViewModel = viewModel;
        await WaitSharpAsync(main, viewModel);
        await WaitUntilAsync(main, () => viewModel.Update?.ShowButton == true);

        Click(main, Find<Button>(main, "UpdateButton"));
        Click(main, Find<Button>(Find<UpdateCard>(main, "UpdateCard"), "UpdateInstallButton"));

        var coordinator = (UpdateCoordinator)viewModel.Update!.Coordinator;
        await WaitUntilAsync(main, () => UpdateWindow.For(coordinator) is not null);
        var window = UpdateWindow.For(coordinator)!;
        OwnWindow(window);
        await WaitUntilAsync(main, () => Source.DownloadCalls == 1 && (!offer || coordinator.State.BytesReceived == (large ? Received : Source.Content.Length / 2)));
        Settle(window);
        return (window, coordinator);
    }
}
