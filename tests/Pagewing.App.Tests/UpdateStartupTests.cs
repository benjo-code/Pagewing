using Avalonia.Headless.XUnit;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// When the update check starts: never before the first sharp page (or the first frame of the start screen), once per
/// process whatever the number of windows, on the thread pool, and with a fake source that never touches the network.
/// </summary>
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateStartupTests : UpdateTestHost
{
    [AvaloniaFact]
    public async Task NoNetworkAccessBeforeTheFirstSharpPage()
    {
        MainViewModel? model = null;
        bool? sharpWhenFactoryRan = null;
        bool? sharpWhenFetched = null;
        var inner = CountingFactory();
        Source.OnFetch = () => sharpWhenFetched = model!.ActiveTab?.HasShownSharpPage;

        var (window, viewModel, _) = await ShowAppAsync(
            factory: () =>
            {
                sharpWhenFactoryRan = model!.ActiveTab?.HasShownSharpPage;
                return inner();
            },
            beforeShow: m =>
            {
                model = m;

                // The file is open, the window not yet shown: nothing of the check exists.
                Assert.Equal(0, CoordinatorsMade);
                Assert.Equal(0, Source.FetchCalls);
            });
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);
        await RunForAsync(window, 200);

        // The factory (and with it the HTTP client of the app) and the fetch came after the first sharp page, once.
        Assert.Equal(true, sharpWhenFactoryRan);
        Assert.Equal(true, sharpWhenFetched);
        Assert.Equal(1, CoordinatorsMade);
        Assert.Equal(1, Source.FetchCalls);
    }

    [AvaloniaFact]
    public async Task TheCheckRunsOnTheThreadPoolAndTheUiNeverWaitsForIt()
    {
        Source.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uiThread = Environment.CurrentManagedThreadId;
        var (window, viewModel, _) = await ShowAppAsync();

        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);

        // The fetch hangs; the window goes on and nothing changes in it.
        Assert.NotEqual(-1, Source.FetchThreadId);
        Assert.NotEqual(uiThread, Source.FetchThreadId);
        Settle(window);
        Assert.Equal(UpdateStage.None, viewModel.UpdateCoordinator!.State.Stage);
        Source.Gate.SetResult();
    }

    [AvaloniaFact]
    public async Task NoNetworkAccessBeforeTheFirstFrameOfTheStartScreen()
    {
        bool? visibleWhenFactoryRan = null;
        bool? startScreenWhenFactoryRan = null;
        MainViewModel? model = null;
        var inner = CountingFactory();

        var (window, viewModel, _) = await ShowAppAsync(
            file: null,
            factory: () =>
            {
                visibleWhenFactoryRan = LastWindow?.IsVisible;
                startScreenWhenFactoryRan = model!.IsEmpty;
                return inner();
            },
            beforeShow: m =>
            {
                model = m;
                Assert.Equal(0, CoordinatorsMade);
                Assert.Equal(0, Source.FetchCalls);
            });
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(true, visibleWhenFactoryRan);
        Assert.Equal(true, startScreenWhenFactoryRan);
        Assert.Equal(1, CoordinatorsMade);
    }

    [AvaloniaFact]
    public async Task CheckRunsOnceForTwoWindows()
    {
        // The workspace shares one coordinator and one gate: two windows with their start screens ask once.
        var made = 0;
        var workspace = new Workspace(
            () => new ViewerSession(new PdfEngine()),
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => new NoFilePicker(),
            updateFactory: () =>
            {
                made++;
                return NewCoordinator();
            });
        Own(workspace);
        var first = workspace.OpenWindow();
        var second = workspace.OpenWindow();
        var firstWindow = (MainWindow)workspace.WindowOf(first);

        await WaitUntilAsync(firstWindow, () => Source.FetchCalls >= 1);
        await RunForAsync(firstWindow, 300);
        await RunForAsync((MainWindow)workspace.WindowOf(second), 100);

        Assert.Equal(1, made);
        Assert.Equal(1, Source.FetchCalls);
        Assert.Same(first.UpdateCoordinator, second.UpdateCoordinator);

        // A file handed over later, and a third window, start nothing either.
        workspace.OnExternalOpen([TestData.Pdf("hello_world.pdf")]);
        var third = workspace.OpenWindow();
        await RunForAsync(firstWindow, 300);
        Assert.NotNull(third);
        Assert.Equal(1, made);
        Assert.Equal(1, Source.FetchCalls);
    }

    [AvaloniaFact]
    public async Task OneCoordinatorDoesNotCheckTwiceEvenWhenTwoWindowsAskIt()
    {
        // Two windows without a shared gate that share the coordinator (the coordinator is the second line of defence).
        var coordinator = NewCoordinator();
        var (window, viewModel, _) = await ShowAppAsync(factory: () => coordinator);
        var (second, secondModel, _) = await ShowAppAsync("hello_world.pdf", factory: () => coordinator);

        await WaitSharpAsync(window, viewModel);
        await WaitSharpAsync(second, secondModel);
        await WaitUntilAsync(window, () => Source.FetchCalls >= 1);
        await RunForAsync(window, 300);

        Assert.Equal(1, Source.FetchCalls);
    }

    [AvaloniaFact]
    public async Task TheUpdateStatusIsNotReadInTheStartupPath()
    {
        // A corrupt update.json leaves a copy named .bad when it is read: that copy shows when the file was read.
        Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
        await File.WriteAllTextAsync(StatusPath, "{ this is not json", TestContext.Current.CancellationToken);
        var backup = StatusPath + ".bad";
        bool? readWhenMade = null;
        bool? sharpWhenMade = null;
        bool? readWhenFetched = null;
        MainViewModel? model = null;
        var inner = CountingFactory();
        Source.OnFetch = () => readWhenFetched = File.Exists(backup);

        var (window, viewModel, _) = await ShowAppAsync(
            factory: () =>
            {
                readWhenMade = File.Exists(backup);
                sharpWhenMade = model!.ActiveTab?.HasShownSharpPage;
                return inner();
            },
            beforeShow: m =>
            {
                model = m;
                Assert.False(File.Exists(backup));
            });
        await WaitSharpAsync(window, viewModel);
        await WaitUntilAsync(window, () => Source.FetchCalls == 1);

        // Not read when the window came up, nor when the coordinator was made after the first sharp page; read by the
        // job of the check, before it fetched.
        Assert.Equal(true, sharpWhenMade);
        Assert.Equal(false, readWhenMade);
        Assert.Equal(true, readWhenFetched);
        Assert.True(File.Exists(backup));
    }

    [AvaloniaFact]
    public async Task TheStatusIsKnownBeforeTheNetworkAnswers()
    {
        Source.Offer("9.9.9");
        Directory.CreateDirectory(Path.GetDirectoryName(StatusPath)!);
        var store = new UpdateStatusStore(StatusPath);
        Assert.True(store.TrySave(new UpdateStatus(DateTimeOffset.UtcNow, "9.9.9")));

        var (window, viewModel, _) = await ShowAppAsync();
        await WaitSharpAsync(window, viewModel);

        // Not due (checked a moment ago): the remembered offer shows and the network is not asked.
        await WaitUntilAsync(window, () => viewModel.UpdateCoordinator!.State.Stage == UpdateStage.Available);
        await RunForAsync(window, 200);
        Assert.Equal("9.9.9", viewModel.UpdateCoordinator!.State.Version!.Value.ToString());
        Assert.Equal(0, Source.FetchCalls);
    }
}
