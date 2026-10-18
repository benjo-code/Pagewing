using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The first launch with several files: the first one opens early (Program.Main), the others after the first frame.
// The windows read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class StartupTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly PdfEngine _engine = new();
    private int _opens;

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task MoreFilesOpenAsTabsAfterTheFirstFrameAndTheFirstStaysActive()
    {
        var more = StartupOptions.Parse([TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")]).MorePaths;
        var (workspace, viewModel, first) = StartLikeProgram(TestData.Pdf("hello_world.pdf"), more);

        // The window is shown with the early session as its only tab: nothing else is part of the startup path.
        Assert.Same(first, Assert.Single(viewModel.Tabs));
        Assert.Equal(1, Volatile.Read(ref _opens));

        // The job of the first frame (Background priority) opens the others behind it, in the order of the command line.
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(
            [TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")],
            viewModel.Tabs.Select(tab => tab.Session.Path));
        Assert.Same(first, viewModel.Tabs[0]);
        Assert.Same(first, viewModel.ActiveTab);
        Assert.True(first.IsActive);
        Assert.False(viewModel.Tabs[1].IsActive);
        await WaitUntilAsync(workspace, () => viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));
        Assert.Equal(3, Volatile.Read(ref _opens));
        Assert.Same(first, viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task AtMostTwentyTabsIncludingTheFirstAndAFailingFileGetsItsOwnErrorTab()
    {
        var missing = Enumerable.Range(0, 24).Select(i => Path.Combine(Path.GetTempPath(), $"pagewing-missing-{i}.pdf")).ToList();
        var args = new[] { TestData.Pdf("hello_world.pdf") }.Concat(missing).ToArray();
        var options = StartupOptions.Parse(args);
        var (workspace, viewModel, first) = StartLikeProgram(options.Paths[0], options.MorePaths);

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(MainViewModel.MaxFilesPerOpen, viewModel.Tabs.Count);
        await WaitUntilAsync(workspace, () => viewModel.Tabs.All(tab => tab.State != ViewerState.Loading));

        Assert.Same(first, viewModel.ActiveTab);
        Assert.Equal(ViewerState.Ready, first.State);
        Assert.All(viewModel.Tabs.Skip(1), tab => Assert.Equal(ViewerState.Failed, tab.State));
        Assert.Equal(missing.Take(MainViewModel.MaxFilesPerOpen - 1), viewModel.Tabs.Skip(1).Select(tab => tab.Session.Path));
    }

    [AvaloniaFact]
    public async Task NoMoreFilesChangesNothing()
    {
        var (workspace, viewModel, first) = StartLikeProgram(TestData.Pdf("hello_world.pdf"), []);

        Dispatcher.UIThread.RunJobs();
        await WaitUntilAsync(workspace, () => first.State == ViewerState.Ready);

        Assert.Same(first, Assert.Single(viewModel.Tabs));
        Assert.Equal(1, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public void AWindowClosedBeforeTheFirstFrameJobOpensNothing()
    {
        var (workspace, viewModel, _) = StartLikeProgram(TestData.Pdf("hello_world.pdf"), [TestData.Pdf("text-basic.pdf")]);

        workspace.WindowOf(viewModel).Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(workspace.Windows);
        Assert.Equal(1, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public async Task OpeningInTheBackgroundKeepsTheActiveTabWhateverItIs()
    {
        var workspace = CreateWorkspace();
        var viewModel = workspace.OpenWindow();
        await viewModel.OpenAsync(TestData.Pdf("hello_world.pdf"));
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var chosen = viewModel.ActiveTab;
        Assert.Equal(TestData.Pdf("text-basic.pdf"), chosen!.Session.Path);

        await viewModel.OpenInBackgroundAsync([TestData.Pdf("vector-shapes.pdf")]);

        Assert.Equal(3, viewModel.Tabs.Count);
        Assert.Same(chosen, viewModel.ActiveTab);

        // Without a tab there is nothing to keep: the new tab is active, as with any open.
        var empty = workspace.OpenWindow();
        await empty.OpenInBackgroundAsync([TestData.Pdf("hello_world.pdf")]);
        Assert.Same(empty.Tabs[0], empty.ActiveTab);
    }

    [Fact]
    public void ApplicationStartupCarriesTheMorePathsAndNoneByDefault()
    {
        using var session = CreateSession();

        Assert.Empty(new AppStartup(session, null).MorePaths);
        Assert.Equal(["b.pdf"], new AppStartup(session, null, MorePaths: ["b.pdf"]).MorePaths);
    }

    /// <summary>
    /// What <see cref="App"/> and <see cref="Program.Main"/> do with a command line: the early session of the first
    /// file becomes the first tab, and the window opens the other files after its first frame.
    /// </summary>
    private (Workspace Workspace, MainViewModel ViewModel, TabViewModel First) StartLikeProgram(string firstPath, IReadOnlyList<string> more)
    {
        var workspace = CreateWorkspace();
        var session = CreateSession();
        _ = session.OpenAsync(firstPath);
        var first = new TabViewModel(session);
        var viewModel = workspace.OpenWindow(first, (window, model) => App.OpenMoreFilesAfterFirstFrame((MainWindow)window, model, more));
        return (workspace, viewModel, first);
    }

    private Workspace CreateWorkspace()
    {
        // The windows do not activate when they are shown, so only the tests activate them.
        var workspace = new Workspace(
            CreateSession,
            () => new MainWindow { Width = 1024, Height = 720, ShowActivated = false },
            _ => new NoFilePicker());
        _cleanup.Add(workspace);
        return workspace;
    }

    private ViewerSession CreateSession() =>
        new((path, password, cancellationToken) =>
        {
            Interlocked.Increment(ref _opens);
            return _engine.OpenAsync(path, password, cancellationToken);
        });

    private static async Task WaitUntilAsync(Workspace workspace, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The windows did not reach the expected state.");
            }

            await Task.Delay(10);
        }
    }
}
