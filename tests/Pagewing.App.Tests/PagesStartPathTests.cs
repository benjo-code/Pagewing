using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Measurement;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The start path of the page organizer (milestone 13, task 15): without use there is no plan, no history, no working copy
/// (the save engine that opens it is not made, nothing is analysed or rewritten), no dialog or card, no folder picker call
/// and no tool bar, in a normal start with the panel "Pages" open as well as in a measurement run
/// (<c>Docs/DECISIONS.md</c>, "Seiten organisieren: Messwerte und Startpfad (M13)"). The model side is also pinned by
/// <c>PagesOrganizeModelTests.NothingIsCreatedAtStartup</c> and <c>CommandCatalogTests.OrganizeCommandsRunOnlyInTheModeAndAreNotMadeEarlier</c>.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class PagesStartPathTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly List<int> _exits = [];

    public PagesStartPathTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        _output.Dispose();
        _error.Dispose();
        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task OpeningADocumentAndItsPagesPanelMakesNothingOfTheOrganizer()
    {
        var (window, viewModel, _) = Show();
        var probe = new Probe(viewModel);
        await viewModel.OpenAsync(TestData.Pdf("pages-marked.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);

        // The panel "Pages" opens after the first sharp page (the start panel): the list and the entry of the mode exist,
        // nothing of the mode does.
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages && window.GetVisualDescendants().OfType<ThumbnailList>().Any());
        probe.AssertNothingWasMade(window, tab);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "OrganizeButton");
    }

    [AvaloniaFact]
    public async Task MeasurementRunsMakeNothingOfTheOrganizer()
    {
        var (window, viewModel, view) = Show();
        var probe = new Probe(viewModel);
        var path = TestData.Pdf("pages-marked.pdf");
        using var measurement = MeasurementRun.Create(StartupOptions.Parse(["--measure-startup", path]), viewModel, view, Sink())!;

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);
        Settle(window);

        Assert.Equal([0], _exits);
        Assert.Empty(_error.ToString());
        var tab = viewModel.ActiveTab!;
        Assert.True(tab.HasShownSharpPage);

        // A measurement run opens no panel (so there is not even the entry of the mode) and builds nothing of the organizer.
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        probe.AssertNothingWasMade(window, tab);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => button.Name == "OrganizeButton");

        // A tab opened later in the run (the second file of --measure-second) is the same.
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var second = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => second.HasShownSharpPage);
        probe.AssertNothingWasMade(window, second);
    }

    private static T? FindOrNull<T>(Visual root)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault();

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

    private MeasurementOutput Sink() => new(_output, _error, _exits.Add);

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show()
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = window.GetVisualDescendants().OfType<DocumentView>().Single(control => control.Name == "DocumentView");
        return (window, viewModel, view);
    }

    /// <summary>
    /// Counts what the organizer would do if it ran: the folder picker, the analysis and the write of a plan. The save engine
    /// (made on the first use, it opens the working copy and the written file) is read by its field.
    /// </summary>
    private sealed class Probe
    {
        private static readonly FieldInfo SaveEngineField = typeof(MainViewModel).GetField("_saveEngine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel no longer has the field _saveEngine; adapt this probe.");

        private readonly MainViewModel _viewModel;
        private int _folderPicks;
        private int _analyses;
        private int _rewrites;

        public Probe(MainViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.FolderPicker = _ =>
            {
                _folderPicks++;
                return Task.FromResult<string?>(null);
            };
            var analyzer = viewModel.PageAnalyzer;
            viewModel.PageAnalyzer = (plan, document, inserted, token) =>
            {
                _analyses++;
                return analyzer(plan, document, inserted, token);
            };
            viewModel.PageRewriter = (document, engine, request, progress, token) =>
            {
                _rewrites++;
                throw new InvalidOperationException("The organizer must not write anything in the start path.");
            };
        }

        public void AssertNothingWasMade(Window window, TabViewModel tab)
        {
            // The model: no mode, no plan, no history, no selection, no inserted file, no command of the mode.
            Assert.False(tab.IsOrganizing);
            Assert.False(tab.HasPendingPageEdits);
            Assert.Null(tab.PageHistory);
            Assert.Empty(tab.SelectedPagePositions);
            Assert.Empty(tab.InsertedSources);
            Assert.False(tab.HasPageEditCommands);

            // The working copy: its engine is not made, nothing was analysed, rewritten or asked of a folder picker.
            Assert.Null(SaveEngineField.GetValue(_viewModel));
            Assert.Equal(0, _analyses);
            Assert.Equal(0, _rewrites);
            Assert.Equal(0, _folderPicks);
            Assert.Null(_viewModel.PageOutput);
            Assert.Null(_viewModel.InsertFile);
            Assert.Equal(OverlayKind.None, _viewModel.Overlay);

            // The views: no tool bar, no card, no dialog in the tree.
            Assert.Null(FindOrNull<PagesOrganizeBar>(window));
            Assert.Null(FindOrNull<InsertFileCard>(window));
            Assert.Null(FindOrNull<SplitCard>(window));
            Assert.Null(FindOrNull<ExportImagesCard>(window));
            Assert.Null(FindOrNull<ApplyPagesCard>(window));
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
