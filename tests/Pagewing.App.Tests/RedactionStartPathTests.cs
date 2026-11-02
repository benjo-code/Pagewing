using System.Globalization;
using System.Reflection;
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
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The start path of the mode "Redact" (milestone 14, task 11): without use there is no plan, no history, no command of the
/// mode, no working copy (the save engine that opens it is not made) and no read of the encryption of the document, in a
/// normal start as well as in a measurement run. The model side is also pinned by <c>RedactModelTests.NothingIsCreatedAtStartup</c>
/// and <c>CommandCatalogTests.RedactCommandsRunOnlyInTheModeAndAreNotMadeEarlier</c>.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class RedactionStartPathTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly List<int> _exits = [];

    public RedactionStartPathTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task OpeningADocumentAndItsPanelsMakesNothingOfTheMode()
    {
        var (window, viewModel, _) = Show();
        var probe = new Probe(viewModel, window);
        await viewModel.OpenAsync(TestData.Pdf("pages-marked.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);

        // The panel "Pages" opens after the first sharp page (the start panel): nothing of the mode is made by it, by the
        // catalog being asked about the commands, or by the reasons the panels would show.
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages && window.GetVisualDescendants().OfType<ThumbnailList>().Any());
        probe.AssertNothingWasMade(tab);
        foreach (var command in viewModel.Commands.All.Where(command => command.Id.StartsWith("redact.", StringComparison.Ordinal)))
        {
            _ = command.CanExecute();
        }

        _ = tab.RedactionBlockedReason;
        _ = tab.RedactionLockReason;
        _ = tab.AnnotateUnavailableReason;
        _ = tab.OrganizeUnavailableReason;
        probe.AssertNothingWasMade(tab);
    }

    [AvaloniaFact]
    public async Task MeasurementRunsMakeNothingOfTheMode()
    {
        var (window, viewModel, view) = Show();
        var probe = new Probe(viewModel, window);
        var path = TestData.Pdf("pages-marked.pdf");
        using var measurement = MeasurementRun.Create(StartupOptions.Parse(["--measure-startup", path]), viewModel, view, Sink())!;

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);
        Settle(window);

        Assert.Equal([0], _exits);
        Assert.Empty(_error.ToString());
        var tab = viewModel.ActiveTab!;
        Assert.True(tab.HasShownSharpPage);

        // A measurement run opens no panel and builds nothing of the mode.
        Assert.Equal(SidePanelKind.None, tab.SidePanel);
        probe.AssertNothingWasMade(tab);

        // A tab opened later in the run (the second file of --measure-second) is the same.
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var second = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => second.HasShownSharpPage);
        probe.AssertNothingWasMade(second);
    }

    [AvaloniaFact]
    public async Task NothingPatternRelatedExistsBeforeThePanelOpens()
    {
        var (window, viewModel, _) = Show();
        var probe = new Probe(viewModel, window);
        await viewModel.OpenAsync(TestData.Pdf("pages-marked.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages && window.GetVisualDescendants().OfType<ThumbnailList>().Any());

        // Without the panel there is no model of the search (so no kinds, no scanner, no groups) and no view of it.
        probe.AssertNothingWasMade(tab);
        Assert.False(tab.IsRedactionPanelCreated);
        Assert.Empty(window.GetVisualDescendants().OfType<RedactionPanel>());

        // The mode and its panel open the word search: the pattern part stays unused until the user switches to it.
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        Assert.False(tab.IsRedactionPanelCreated);
        tab.ToggleRedactionPanelCommand.Execute(null);
        await WaitUntilAsync(window, () => window.GetVisualDescendants().OfType<RedactionPanel>().Any());
        var search = tab.RedactionPanel;
        var scanned = 0;
        search.PatternSearcher = (_, _, _, _, _) =>
        {
            scanned++;
            return AsyncEnumerable.Empty<Pagewing.Core.Redaction.RedactionHit>();
        };
        Settle(window);
        Assert.Equal(RedactionSearchMode.Word, search.Mode);
        Assert.Equal(RedactionSearchState.Idle, search.State);
        Assert.Empty(search.Hits);
        Assert.Equal(0, scanned);

        // Even a word search never runs the scanners; only the switch to "Pattern" and "Find" do.
        search.Query = "Zoom";
        Assert.Equal(0, scanned);
        search.IsPatternMode = true;
        Assert.Equal(0, scanned);
        search.SearchCommand.Execute(null);
        await WaitUntilAsync(window, () => scanned > 0);
        Assert.Equal(1, scanned);
    }

    [AvaloniaFact]
    public async Task OverlayControlsExistOnlyInTheMode()
    {
        var (window, viewModel, _) = Show();
        var probe = new Probe(viewModel, window);
        await viewModel.OpenAsync(TestData.Pdf("pages-marked.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        await WaitUntilAsync(window, () => tab.SidePanel == SidePanelKind.Pages && window.GetVisualDescendants().OfType<ThumbnailList>().Any());

        // Outside the mode nothing of the text on the areas exists: no field, no panel, and reading the style makes nothing.
        _ = tab.RedactionOverlayStyle;
        probe.AssertNothingWasMade(tab);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), static box => box.Name is "OverlayTextBox" or "OverlayColorBox");
        Assert.Empty(window.GetVisualDescendants().OfType<RedactionPanel>());

        // The mode and its panel, without a mark: still no editor, no field, no snippet, and the settings were not touched.
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        tab.ToggleRedactionPanelCommand.Execute(null);
        await WaitUntilAsync(window, () => window.GetVisualDescendants().OfType<RedactionPanel>().Any());
        Settle(window);
        Assert.Null(tab.RedactionPanel.OverlayEditor);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), static box => box.Name is "OverlayTextBox" or "OverlayColorBox");
        Assert.Null(tab.AnnotationSettings.Current.RedactionCodes);

        // The first selected mark makes the editor and its field; the settings still hold nothing of the snippets.
        Assert.True(tab.AddAreaMark(0, new Pagewing.Core.Text.PageRect(50, 50, 150, 90)));
        Settle(window);
        Assert.NotNull(tab.RedactionPanel.OverlayEditor);
        Assert.Single(window.GetVisualDescendants().OfType<TextBox>(), static box => box.Name == "OverlayTextBox");
        Assert.Null(tab.AnnotationSettings.Current.RedactionCodes);
        Assert.Equal(1, tab.RedactionHistory!.Version);

        // Without a selected mark they go again, and with the mode.
        tab.ClearMarkSelection();
        Settle(window);
        Assert.Null(tab.RedactionPanel.OverlayEditor);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), static box => box.Name is "OverlayTextBox" or "OverlayColorBox");
        tab.SelectMarks([tab.RedactionPlan!.Marks[0].Id]);
        Settle(window);
        Assert.NotNull(tab.RedactionPanel.OverlayEditor);
        tab.DiscardRedactionsCommand.Execute(null);
        Settle(window);
        Assert.False(tab.IsRedactionPanelCreated);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBox>(), static box => box.Name is "OverlayTextBox" or "OverlayColorBox");
        Assert.Null(tab.RedactionHistory);
        Assert.Empty(tab.SelectedMarks);
    }

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
    /// Checks what the mode would make if it ran: the plan and its history, the commands, the selection, and the working
    /// copy (the save engine, made on the first use, is read by its field), and that the encryption was never asked.
    /// </summary>
    private sealed class Probe
    {
        private static readonly FieldInfo SaveEngineField = typeof(MainViewModel).GetField("_saveEngine", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel no longer has the field _saveEngine; adapt this probe.");

        private readonly MainViewModel _viewModel;
        private readonly MainWindow _window;

        public Probe(MainViewModel viewModel, MainWindow window)
        {
            _viewModel = viewModel;
            _window = window;
        }

        public void AssertNothingWasMade(TabViewModel tab)
        {
            // The model: no mode, no plan, no history, no selection, no notice, no command of the mode.
            Assert.False(tab.IsRedacting);
            Assert.False(tab.HasPendingRedactions);
            Assert.Null(tab.RedactionHistory);
            Assert.Null(tab.RedactionPlan);
            Assert.Empty(tab.SelectedMarks);
            Assert.Null(tab.RedactionNotice);
            Assert.False(tab.HasRedactionCommands);

            // The panel of the mode and its search are made when it first opens, not before.
            Assert.False(tab.IsRedactionPanelCreated);
            Assert.False(tab.IsRedactionPanelOpen);

            // Nothing that costs a job of the PDF thread: the encryption is read at the first request for the mode, not before.
            Assert.False(tab.HasReadEncryption);

            // The working copy: its engine is not made, and no card or overlay of the mode is there.
            Assert.Null(SaveEngineField.GetValue(_viewModel));
            Assert.Equal(OverlayKind.None, _viewModel.Overlay);

            // The apply flow (task 13): no preview, no question, no seam taken, and no card of it (the window makes each with its first question).
            Assert.False(tab.IsRedactionPreview);
            Assert.Null(tab.RedactionPreviewText);
            Assert.Null(_viewModel.PendingSave);
            Assert.Null(_viewModel.RedactionAnalyzer);
            Assert.Null(_viewModel.RedactionRewriter);
            Assert.DoesNotContain(_window.SaveCards, card => card is RedactApplyCard or RedactReportCard);
            Assert.Null(_window.GetVisualDescendants().OfType<RedactionToolbar>().FirstOrDefault());
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
