using System.Globalization;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
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

// The first launch with several files: the first one opens early (Program.Main), the others behind it once its first
// sharp tile is drawn.
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
    public async Task MoreFilesOpenBehindTheFirstTabAfterItsFirstSharpTileAndTouchNothingOfIt()
    {
        var more = StartupOptions.Parse([TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")]).MorePaths;
        var (workspace, viewModel, first) = StartLikeProgram(TestData.Pdf("hello_world.pdf"), more);
        var view = ViewOf(workspace, viewModel);

        // What the document view of the first tab shows when its first sharp tile is drawn (the handler of the test
        // runs after the one that starts the opening, which only posts a Background job).
        var tilesWhenDrawn = new Dictionary<TileKey, Bitmap>();
        var tabsWhenDrawn = -1;
        var tileEvents = 0;
        PdfDocument? document = null;
        DocumentLayout? layout = null;
        view.FirstSharpTileDrawn += () =>
        {
            tileEvents++;
            tabsWhenDrawn = viewModel.Tabs.Count;
            document = view.Document;
            layout = view.Layout;
            foreach (var key in view.Canvas.Bitmaps.Keys.Where(key => !key.IsPreview))
            {
                view.Canvas.Bitmaps.TryGet(key, out var bitmap);
                tilesWhenDrawn[key] = bitmap;
            }
        };
        var dataContextChanges = 0;
        view.PropertyChanged += (_, e) => dataContextChanges += e.Property == StyledElement.DataContextProperty ? 1 : 0;
        var activeChanges = 0;
        viewModel.Tabs.ActiveChanged += () => activeChanges++;
        viewModel.PropertyChanged += (_, e) => activeChanges += e.PropertyName == nameof(MainViewModel.ActiveTab) ? 1 : 0;

        // Shown with the early session as its only tab: nothing else is part of the startup path or of the first frame.
        Assert.Same(first, Assert.Single(viewModel.Tabs));
        Dispatcher.UIThread.RunJobs();
        Assert.Same(first, Assert.Single(viewModel.Tabs));

        await WaitUntilAsync(workspace, () => viewModel.Tabs.Count == 3);

        // The sharp tile was drawn before the others were added; they lie in the order of the command line, behind it.
        Assert.Equal(1, tabsWhenDrawn);
        Assert.Equal(
            [TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")],
            viewModel.Tabs.Select(tab => tab.Session.Path));
        Assert.Same(first, viewModel.Tabs[0]);
        await WaitUntilAsync(workspace, () => viewModel.Tabs.All(tab => tab.State == ViewerState.Ready));
        Assert.Equal(3, Volatile.Read(ref _opens));

        // The first tab stayed active all the time: the active tab never changed, so the document view was not
        // disconnected and connected again (no new data context, same document and layout, same tiles, nothing drawn anew).
        Assert.Same(first, viewModel.ActiveTab);
        Assert.True(first.IsActive);
        Assert.False(viewModel.Tabs[1].IsActive);
        Assert.False(viewModel.Tabs[2].IsActive);
        Assert.Equal(0, activeChanges);
        Assert.Equal(0, dataContextChanges);
        Assert.Equal(1, tileEvents);
        Assert.Same(first, view.DataContext);
        Assert.NotNull(document);
        Assert.Same(document, view.Document);
        Assert.Same(layout, view.Layout);
        Assert.NotEmpty(tilesWhenDrawn);
        foreach (var (key, bitmap) in tilesWhenDrawn)
        {
            Assert.True(view.Canvas.Bitmaps.TryGet(key, out var now));
            Assert.Same(bitmap, now);
        }
    }

    [AvaloniaFact]
    public async Task MoreFilesOpenAtOnceWhenTheFirstFileFailsAndTheFailedTabStaysActive()
    {
        var missing = Path.Combine(Path.GetTempPath(), "pagewing-missing-first.pdf");
        var (workspace, viewModel, first) = StartLikeProgram(missing, [TestData.Pdf("hello_world.pdf")]);

        // The error card is what the window shows: no sharp tile will come, so nothing waits for one.
        await WaitUntilAsync(workspace, () => viewModel.Tabs.Count == 2 && viewModel.Tabs[1].State == ViewerState.Ready);

        Assert.Equal(ViewerState.Failed, first.State);
        Assert.Same(first, viewModel.Tabs[0]);
        Assert.Same(first, viewModel.ActiveTab);
        Assert.Equal(TestData.Pdf("hello_world.pdf"), viewModel.Tabs[1].Session.Path);
    }

    [AvaloniaFact]
    public async Task MoreFilesOpenWhenTheFirstTabIsClosedBeforeItsFirstSharpTile()
    {
        var (workspace, viewModel, first) = StartLikeProgram(
            TestData.Pdf("hello_world.pdf"), [TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")]);

        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitUntilAsync(workspace, () => viewModel.Tabs.Count == 2);

        // Without a tab the first of them is the active one, the next one lies behind it.
        Assert.DoesNotContain(first, viewModel.Tabs);
        Assert.Equal(
            [TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf")],
            viewModel.Tabs.Select(tab => tab.Session.Path));
        Assert.Same(viewModel.Tabs[0], viewModel.ActiveTab);
    }

    [AvaloniaFact]
    public async Task AtMostTwentyTabsIncludingTheFirstAndAFailingFileGetsItsOwnErrorTab()
    {
        var missing = Enumerable.Range(0, 24).Select(i => Path.Combine(Path.GetTempPath(), $"pagewing-missing-{i}.pdf")).ToList();
        var args = new[] { TestData.Pdf("hello_world.pdf") }.Concat(missing).ToArray();
        var options = StartupOptions.Parse(args);
        var (workspace, viewModel, first) = StartLikeProgram(options.Paths[0], options.MorePaths);

        await WaitUntilAsync(workspace, () => viewModel.Tabs.Count > 1);
        await WaitUntilAsync(workspace, () => viewModel.Tabs.All(tab => tab.State != ViewerState.Loading));

        Assert.Equal(MainViewModel.MaxFilesPerOpen, viewModel.Tabs.Count);
        Assert.Same(first, viewModel.ActiveTab);
        Assert.Equal(ViewerState.Ready, first.State);
        Assert.All(viewModel.Tabs.Skip(1), tab => Assert.Equal(ViewerState.Failed, tab.State));
        Assert.Equal(missing.Take(MainViewModel.MaxFilesPerOpen - 1), viewModel.Tabs.Skip(1).Select(tab => tab.Session.Path));
    }

    [AvaloniaFact]
    public async Task NoMoreFilesChangesNothing()
    {
        var (workspace, viewModel, first) = StartLikeProgram(TestData.Pdf("hello_world.pdf"), []);
        var view = ViewOf(workspace, viewModel);

        await WaitUntilAsync(workspace, () => first.State == ViewerState.Ready && view.Canvas.Bitmaps.Keys.Any(key => !key.IsPreview));

        Assert.Same(first, Assert.Single(viewModel.Tabs));
        Assert.Equal(1, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public async Task AWindowClosedBeforeTheBackgroundJobOpensNothing()
    {
        var (workspace, viewModel, _) = StartLikeProgram(TestData.Pdf("hello_world.pdf"), [TestData.Pdf("text-basic.pdf")]);
        var window = (MainWindow)workspace.WindowOf(viewModel);

        // The first sharp tile starts the job; the window closes before the job runs.
        window.DocumentView.FirstSharpTileDrawn += () => window.Close();
        await WaitUntilAsync(workspace, () => workspace.Windows.Count == 0);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(workspace.Windows);
        Assert.Equal(1, Volatile.Read(ref _opens));
    }

    [AvaloniaFact]
    public async Task OpeningInTheBackgroundNeverChangesTheActiveTab()
    {
        var workspace = CreateWorkspace();
        var viewModel = workspace.OpenWindow();
        await viewModel.OpenAsync(TestData.Pdf("hello_world.pdf"));
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        viewModel.ActivateTabCommand.Execute(viewModel.Tabs[0]);
        var chosen = viewModel.ActiveTab;
        var changes = 0;
        viewModel.Tabs.ActiveChanged += () => changes++;
        viewModel.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(MainViewModel.ActiveTab) ? 1 : 0;

        // New files lie at the end, in their order; a file that is already open is skipped, not activated.
        await viewModel.OpenInBackgroundAsync([TestData.Pdf("vector-shapes.pdf"), TestData.Pdf("text-basic.pdf"), TestData.Pdf("thousand-pages.pdf")]);

        Assert.Equal(
            [TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf"), TestData.Pdf("vector-shapes.pdf"), TestData.Pdf("thousand-pages.pdf")],
            viewModel.Tabs.Select(tab => tab.Session.Path));
        Assert.Same(chosen, viewModel.ActiveTab);
        Assert.Equal(0, changes);

        // Without a tab the first new tab has to be the active one; the others lie behind it, in their order.
        var empty = workspace.OpenWindow();
        await empty.OpenInBackgroundAsync([TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf")]);
        Assert.Equal([TestData.Pdf("hello_world.pdf"), TestData.Pdf("text-basic.pdf")], empty.Tabs.Select(tab => tab.Session.Path));
        Assert.Same(empty.Tabs[0], empty.ActiveTab);
        Assert.False(empty.Tabs[1].IsActive);
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
    /// file becomes the first tab, and the window opens the other files behind it once the first sharp tile is drawn.
    /// </summary>
    private (Workspace Workspace, MainViewModel ViewModel, TabViewModel First) StartLikeProgram(string firstPath, IReadOnlyList<string> more)
    {
        var workspace = CreateWorkspace();
        var session = CreateSession();
        _ = session.OpenAsync(firstPath);
        var first = new TabViewModel(session);
        var viewModel = workspace.OpenWindow(first, (window, model) => App.OpenMoreFilesAfterFirstSharpTile((MainWindow)window, model, first, more));
        return (workspace, viewModel, first);
    }

    private static DocumentView ViewOf(Workspace workspace, MainViewModel viewModel) =>
        ((MainWindow)workspace.WindowOf(viewModel)).DocumentView;

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
            for (var i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                foreach (var viewModel in workspace.Windows)
                {
                    workspace.WindowOf(viewModel).UpdateLayout();
                }
            }

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
