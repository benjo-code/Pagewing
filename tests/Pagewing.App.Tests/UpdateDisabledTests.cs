using Avalonia.Headless.XUnit;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The setting "check for updates" off means no network access at all, the command works on request anyway, a failing
/// check is silent, and a measurement run has no updater. Nothing here touches the network or the folder of the user.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateDisabledTests : UpdateTestHost
{
    [AvaloniaFact]
    public async Task NoCallWhenTheSettingIsOff()
    {
        // The real source of the app on a handler that fails the test on any call, even the first one a DNS lookup would be.
        var handler = new FailingHandler();
        var settings = UserSettings.Detached();
        settings.Update(current => current with { UpdateCheck = false });
        var factoryCalls = 0;
        var (window, viewModel, _) = await ShowAppAsync(
            settings: settings,
            factory: () =>
            {
                factoryCalls++;
                var checker = new UpdateChecker(
                    new GitHubUpdateSource(handler), new UpdateStatusStore(StatusPath), TimeProvider.System, Running, UpdateTrust.Keys);
                return new UpdateCoordinator(checker, settings, PackageForm.WindowsInstaller);
            });

        await WaitSharpAsync(window, viewModel);
        await RunForAsync(window, 2000);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, factoryCalls);

        // The start screen of a second window asks nothing either.
        var (startScreen, _, _) = await ShowAppAsync(file: null, settings: settings, factory: () => throw new InvalidOperationException("No coordinator when off."));
        await RunForAsync(startScreen, 500);
        Assert.Equal(0, handler.Calls);
    }

    [AvaloniaFact]
    public async Task NoCoordinatorInAMeasurementRun()
    {
        // A measurement run makes no factory (App.OnFrameworkInitializationCompleted passes the choice to the workspace) ...
        Assert.Null(PlatformUpdate.FactoryFor(isMeasurement: true, UserSettings.Detached()));
        Assert.NotNull(PlatformUpdate.FactoryFor(isMeasurement: false, UserSettings.Detached()));

        // ... and a window without a factory neither checks nor offers the command.
        var (window, viewModel, _) = await ShowAppAsync(factory: null, beforeShow: model => model.UpdateCoordinatorFactory = null);
        await WaitSharpAsync(window, viewModel);
        await RunForAsync(window, 300);

        Assert.Null(viewModel.UpdateCoordinator);
        Assert.Equal(0, Source.FetchCalls);
        Assert.False(viewModel.Commands.ById("app.checkForUpdates")!.CanExecute());
    }

    [AvaloniaFact]
    public async Task TheCommandWorksEvenWhenTheSettingIsOff()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { UpdateCheck = false });
        var (window, viewModel, _) = await ShowAppAsync(settings: settings);
        await WaitSharpAsync(window, viewModel);
        await RunForAsync(window, 300);
        Assert.Equal(0, Source.FetchCalls);

        var command = viewModel.Commands.ById("app.checkForUpdates")!;
        Assert.True(command.CanExecute());
        Assert.True(command.Listed);
        Assert.Null(command.Gesture);
        Assert.Equal("Check for updates", command.Title());
        Assert.Equal(Commands.CommandCatalog.WindowGroup, command.Group);

        // Through the palette, on request only.
        viewModel.ShowPaletteCommand.Execute(null);
        Settle(window);
        viewModel.Palette!.Query = "updates";
        Assert.Contains(viewModel.Palette.Items, item => item.Key == "app.checkForUpdates");
        command.Execute();
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);
        await RunForAsync(window, 300);

        Assert.Equal(1, Source.FetchCalls);
        Assert.False(settings.Current.UpdateCheck);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoErrorCardWhenTheCheckFails(bool sourceThrows)
    {
        // Nothing received (no network, a server error), or a source that misbehaves: no card, no overlay, nothing changes.
        if (sourceThrows)
        {
            Source.Throws = new HttpRequestException("no route to host");
        }

        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);
        await RunForAsync(window, 300);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Null(viewModel.DefaultAppPrompt);
        Assert.Null(viewModel.PendingLink);
        Assert.Equal(UpdateState.None, viewModel.UpdateCoordinator!.State);
        Assert.Equal(Strings.CommandCheckForUpdates, viewModel.Commands.ById("app.checkForUpdates")!.Title());

        // The command after a failure is silent too and tries again.
        viewModel.Commands.ById("app.checkForUpdates")!.Execute();
        await WaitUntilAsync(window, () => Source.FetchCalls == 2);
        await RunForAsync(window, 200);
        Assert.Equal(UpdateState.None, viewModel.UpdateCoordinator.State);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The update check reached the network although it is switched off: " + request.RequestUri);
        }
    }
}
