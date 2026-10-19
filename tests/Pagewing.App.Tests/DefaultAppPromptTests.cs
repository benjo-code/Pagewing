using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Distribution;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The card "Set Pagewing as default for PDFs?" at the first start and the palette command that does the same: when it
/// appears, what its buttons do, and that nothing of it runs where it must not. A fake service stands in for the
/// operating system; settings are in memory or in a temporary folder, never the user's.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DefaultAppPromptTests : PrintTestHost
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-defaultapp-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDefaultAppService _service = new();
    private readonly DefaultAppPromptGate _gate = new();

    public new void Dispose()
    {
        base.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        if (File.Exists(_directory + ".blocker"))
        {
            File.Delete(_directory + ".blocker");
        }
    }

    [AvaloniaFact]
    public async Task TheCardShowsAfterTheFirstSharpPageWhenNotDefault()
    {
        var (window, viewModel, _) = await ShowAppAsync();

        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        var card = Find<DefaultAppCard>(window, "DefaultAppCard");
        Assert.True(card.IsEffectivelyVisible);
        Assert.Same(viewModel.DefaultAppPrompt, card.DataContext);
        Assert.Equal("Set Pagewing as the default for PDFs?", Find<TextBlock>(card, "DefaultAppTitle").Text);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal(1, _service.StatusCalls);
    }

    [AvaloniaFact]
    public async Task TheCardIsNotModalAndTakesNoFocus()
    {
        var (window, viewModel, view) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        view.Focus();
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.IsNotType<Button>(Focused(window));
        Assert.False(Find<DefaultAppCard>(window, "DefaultAppCard").IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task NothingShowsWhenPagewingIsAlreadyDefault()
    {
        _service.Status = DefaultAppStatus.IsDefault;
        var (window, viewModel, _) = await ShowAppAsync();

        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => _service.StatusCalls == 1);
        await SettleForAWhileAsync(window);

        Assert.Null(viewModel.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task NothingShowsAfterNever()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { DefaultAppPrompt = DefaultAppPromptSetting.Never });
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);

        await WaitSharpAsync(window, viewModel);
        await SettleForAWhileAsync(window);

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(0, _service.StatusCalls);
    }

    [AvaloniaFact]
    public async Task NothingShowsInAMeasurementRun()
    {
        // A measurement run makes no factory (App.OnFrameworkInitializationCompleted passes the choice to the workspace) ...
        Assert.Null(PlatformDefaultAppServices.FactoryFor(isMeasurement: true));
        Assert.NotNull(PlatformDefaultAppServices.FactoryFor(isMeasurement: false));

        // ... and a window without a factory asks nobody.
        var (window, viewModel, _) = await ShowAppAsync(configure: model => model.DefaultAppServiceFactory = static () => null);
        await WaitSharpAsync(window, viewModel);
        await SettleForAWhileAsync(window);

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(0, _service.StatusCalls);
    }

    [AvaloniaFact]
    public async Task NothingShowsForAnUnsupportedPlatform()
    {
        _service.Supported = false;
        var (window, viewModel, _) = await ShowAppAsync();

        await WaitSharpAsync(window, viewModel);
        await SettleForAWhileAsync(window);

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(0, _service.StatusCalls);
        Assert.False(viewModel.Commands.ById("app.setDefault")!.CanExecute());
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailingStatusCheckShowsNothing(bool unknown)
    {
        if (unknown)
        {
            _service.Status = DefaultAppStatus.Unknown;
        }
        else
        {
            _service.StatusError = new InvalidOperationException("xdg-mime is gone");
        }

        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => _service.StatusCalls == 1);
        await SettleForAWhileAsync(window);

        Assert.Null(viewModel.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task StatusCheckNeverRunsBeforeTheFirstSharpPage()
    {
        bool? sharpWhenAsked = null;
        MainViewModel? model = null;
        _service.OnStatus = () => sharpWhenAsked = model!.ActiveTab?.HasShownSharpPage;
        var (window, viewModel, _) = await ShowAppAsync(configure: m => model = m);

        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => _service.StatusCalls == 1);

        Assert.Equal(true, sharpWhenAsked);
    }

    [AvaloniaFact]
    public async Task TheStatusCheckRunsOffTheUiThreadAndTheUiNeverWaitsForIt()
    {
        _service.StatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (window, viewModel, _) = await ShowAppAsync();
        var uiThread = Environment.CurrentManagedThreadId;

        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => _service.StatusCalls == 1);

        // The check hangs; the window goes on (it settles and answers input) and shows nothing yet.
        Assert.NotEqual(-1, _service.StatusThreadId);
        Assert.NotEqual(uiThread, _service.StatusThreadId);
        Settle(window);
        Assert.Null(viewModel.DefaultAppPrompt);

        _service.StatusGate.SetResult();
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
    }

    [AvaloniaFact]
    public async Task TheStartScreenAsksAfterItsFirstFrame()
    {
        var (window, viewModel, _) = await ShowAppAsync(file: null);

        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(1, _service.StatusCalls);
    }

    [AvaloniaFact]
    public async Task TheCardShowsOnlyOncePerProcess()
    {
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
        viewModel.DefaultAppPrompt!.LaterCommand.Execute(null);
        Assert.Null(viewModel.DefaultAppPrompt);

        // A second window, and a file handed to the running instance, ask nobody again.
        var (second, secondModel, _) = await ShowAppAsync("hello_world.pdf");
        await WaitSharpAsync(second, secondModel);
        viewModel.OnExternalOpen([TestData.Pdf("hello_world.pdf")]);
        await WaitUntilAsync(window, () => viewModel.Tabs.Count == 2);
        await SettleForAWhileAsync(window);

        Assert.Null(secondModel.DefaultAppPrompt);
        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(1, _service.StatusCalls);
    }

    [AvaloniaFact]
    public async Task LaterClosesWithoutSaving()
    {
        var settings = UserSettings.Load(_directory);
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Click(window, Find<Button>(window, "DefaultAppLaterButton"));

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(DefaultAppPromptSetting.Ask, settings.Current.DefaultAppPrompt);
        Assert.False(File.Exists(settings.FilePath));
        Assert.Equal(0, _service.MakeCalls);
    }

    [AvaloniaFact]
    public async Task NeverSavesAndSurvivesARestart()
    {
        var settings = UserSettings.Load(_directory);
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Click(window, Find<Button>(window, "DefaultAppNeverButton"));

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(DefaultAppPromptSetting.Never, settings.Current.DefaultAppPrompt);

        // The next start reads the file and does not even ask the system.
        var restarted = UserSettings.Load(_directory);
        Assert.Equal(DefaultAppPromptSetting.Never, restarted.Current.DefaultAppPrompt);
        var service = new FakeDefaultAppService();
        var (window2, viewModel2, _) = await ShowAppAsync("hello_world.pdf", restarted, service, new DefaultAppPromptGate());
        await WaitSharpAsync(window2, viewModel2);
        await SettleForAWhileAsync(window2);
        Assert.Null(viewModel2.DefaultAppPrompt);
        Assert.Equal(0, service.StatusCalls);
    }

    [AvaloniaFact]
    public async Task UnwritableSettingsStillCloseTheCard()
    {
        // The config "folder" is a file: nothing can be written there.
        File.WriteAllText(_directory + ".blocker", "not a folder");
        var settings = UserSettings.Load(_directory + ".blocker");
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Click(window, Find<Button>(window, "DefaultAppNeverButton"));

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.True(settings.LastSaveFailed);
        Assert.Equal(DefaultAppPromptSetting.Never, settings.Current.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task SetDefaultClosesOnDone()
    {
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Click(window, Find<Button>(window, "DefaultAppSetButton"));
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is null);

        Assert.Equal(1, _service.MakeCalls);
        Assert.Equal(DefaultAppPromptSetting.Ask, viewModel.Settings.Current.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task OpenedSystemSettingsShowsAHintOnTheCard()
    {
        _service.Result = DefaultAppResult.OpenedSystemSettings;
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
        var card = Find<DefaultAppCard>(window, "DefaultAppCard");
        Assert.False(Find<TextBlock>(card, "DefaultAppMessage").IsEffectivelyVisible);

        Click(window, Find<Button>(window, "DefaultAppSetButton"));
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt?.Message is not null);

        Assert.Equal("Choose Pagewing in the list that Windows shows.", viewModel.DefaultAppPrompt!.Message);
        Assert.Equal(Strings.DefaultAppChooseHint, Find<TextBlock>(card, "DefaultAppMessage").Text);
        Assert.True(Find<TextBlock>(card, "DefaultAppMessage").IsEffectivelyVisible);
        Assert.False(viewModel.DefaultAppPrompt.IsFailed);

        // "Later" still closes the card.
        Click(window, Find<Button>(window, "DefaultAppLaterButton"));
        Assert.Null(viewModel.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task FailedKeepsTheCardWithAMessage()
    {
        _service.Result = DefaultAppResult.Failed;
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);

        Click(window, Find<Button>(window, "DefaultAppSetButton"));
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt?.Message is not null);

        var prompt = viewModel.DefaultAppPrompt!;
        Assert.True(prompt.IsFailed);
        Assert.Equal(Strings.DefaultAppFailed, Find<TextBlock>(window, "DefaultAppMessage").Text);

        // The user may try again.
        Assert.True(prompt.SetDefaultCommand.CanExecute(null));
        _service.Result = DefaultAppResult.Done;
        Click(window, Find<Button>(window, "DefaultAppSetButton"));
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is null);
        Assert.Equal(2, _service.MakeCalls);
    }

    [AvaloniaFact]
    public async Task TheCommandSetsTheDefaultEvenAfterNever()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { DefaultAppPrompt = DefaultAppPromptSetting.Never });
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);
        await WaitSharpAsync(window, viewModel);

        var command = viewModel.Commands.ById("app.setDefault")!;
        Assert.True(command.CanExecute());
        Assert.True(command.Listed);
        Assert.Null(command.Gesture);
        Assert.Equal("Set Pagewing as default for PDFs", command.Title());
        Assert.Equal(CommandCatalogGroup(), command.Group);

        // Through the palette.
        viewModel.ShowPaletteCommand.Execute(null);
        Settle(window);
        viewModel.Palette!.Query = "default for PDFs";
        Assert.Contains(viewModel.Palette.Items, item => item.Key == "app.setDefault");
        command.Execute();
        await WaitUntilAsync(window, () => _service.MakeCalls == 1);

        Assert.Equal(DefaultAppPromptSetting.Never, settings.Current.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task TheCommandShowsTheHintWhereTheSystemAsksTheUser()
    {
        _service.Result = DefaultAppResult.OpenedSystemSettings;
        var (window, viewModel, _) = await ShowAppAsync(configure: _ => { });
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
        viewModel.DefaultAppPrompt!.LaterCommand.Execute(null);

        viewModel.Commands.ById("app.setDefault")!.Execute();
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt?.Message is not null);

        Assert.Equal(Strings.DefaultAppChooseHint, viewModel.DefaultAppPrompt!.Message);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task TheCardColoursMeetAA(string theme)
    {
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var (window, viewModel, _) = await ShowAppAsync(configure: _ => { });
        window.RequestedThemeVariant = variant;
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
        var card = Find<DefaultAppCard>(window, "DefaultAppCard");
        double Ratio(Color a, string surface) => Wcag.Contrast(a, TokenTests.Token(surface, variant));

        // The card is surface.raised; the title in text.primary, the explanation in text.secondary.
        var box = Find<Border>(card, "DefaultAppBox");
        Assert.Equal(TokenTests.Token("Surface.Raised", variant), Assert.IsAssignableFrom<ISolidColorBrush>(box.Background).Color);
        Assert.True(Ratio(TokenTests.Token("Text.Primary", variant), "Surface.Raised") >= 4.5);
        var text = Find<TextBlock>(card, "DefaultAppText");
        Assert.Equal(TokenTests.Token("Text.Secondary", variant), Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color);
        Assert.True(Ratio(TokenTests.Token("Text.Secondary", variant), "Surface.Raised") >= 4.5);

        // The primary button: accent.on on accent.default.
        var primary = Find<Button>(card, "DefaultAppSetButton");
        Assert.Contains("primary", primary.Classes);
        Assert.True(Wcag.Contrast(TokenTests.Token("Accent.On", variant), TokenTests.Token("Accent.Default", variant)) >= 4.5);

        // A failure: danger.default with the warning symbol on the card (never only a colour).
        Assert.True(Ratio(TokenTests.Token("Danger.Default", variant), "Surface.Raised") >= 4.5);
        _service.Result = DefaultAppResult.Failed;
        Click(window, primary);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt?.IsFailed == true);
        var message = Find<TextBlock>(card, "DefaultAppMessage");
        Assert.Equal(TokenTests.Token("Danger.Default", variant), Assert.IsAssignableFrom<ISolidColorBrush>(message.Foreground).Color);
        Assert.True(Find<Pagewing.App.Controls.StrokeIcon>(card, "DefaultAppWarning").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task KeyboardOnlyUseWorks()
    {
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
        var never = Find<Button>(window, "DefaultAppNeverButton");
        var later = Find<Button>(window, "DefaultAppLaterButton");
        var set = Find<Button>(window, "DefaultAppSetButton");

        // Tab walks the buttons in the order they stand, Enter presses.
        never.Focus(NavigationMethod.Tab);
        Settle(window);
        Assert.Same(never, Focused(window));
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.Same(later, Focused(window));
        Press(window, Key.Tab, PhysicalKey.Tab, None);
        Assert.Same(set, Focused(window));
        Press(window, Key.Enter, PhysicalKey.Enter, None);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is null);

        Assert.Equal(1, _service.MakeCalls);
    }

    [AvaloniaFact]
    public async Task EscapeIsLaterWhenTheFocusIsOnTheCard()
    {
        var settings = UserSettings.Load(_directory);
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => viewModel.DefaultAppPrompt is not null);
        Find<Button>(window, "DefaultAppSetButton").Focus(NavigationMethod.Tab);
        Settle(window);

        Press(window, Key.Escape, PhysicalKey.Escape, None);

        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Equal(0, _service.MakeCalls);
        Assert.Equal(DefaultAppPromptSetting.Ask, settings.Current.DefaultAppPrompt);
    }

    [AvaloniaFact]
    public async Task AnOverlayHoldsTheCardBack()
    {
        _service.StatusGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => _service.StatusCalls == 1);

        viewModel.ShowHelpCommand.Execute(null);
        _service.StatusGate.SetResult();
        await SettleForAWhileAsync(window);

        Assert.Null(viewModel.DefaultAppPrompt);
    }

    private static string CommandCatalogGroup() => Pagewing.App.Commands.CommandCatalog.WindowGroup;

    private async Task<(MainWindow Window, MainViewModel ViewModel, DocumentView View)> ShowAppAsync(
        string? file = "text-basic.pdf",
        UserSettings? settings = null,
        FakeDefaultAppService? service = null,
        DefaultAppPromptGate? gate = null,
        Action<MainViewModel>? configure = null)
    {
        var fake = service ?? _service;
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached())
        {
            IsWindowsTitleBar = true,
            DefaultAppServiceFactory = () => fake,
            DefaultAppPromptGate = gate ?? _gate,
        };
        configure?.Invoke(viewModel);

        // The file is opening before the window shows, as for a file on the command line: no start screen asks first.
        if (file is not null)
        {
            await viewModel.OpenAsync(TestData.Pdf(file));
        }

        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        Track(window, viewModel);
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    private static async Task WaitSharpAsync(MainWindow window, MainViewModel viewModel)
    {
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
    }

    private static async Task SettleForAWhileAsync(MainWindow window)
    {
        for (var i = 0; i < 10; i++)
        {
            Settle(window);
            await Task.Delay(20);
        }
    }
}
