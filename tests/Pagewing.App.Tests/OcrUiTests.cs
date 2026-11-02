using System.Globalization;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Measurement;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The text recognition in the window (milestone 15): the hint, the card with its choices, the run with its locks, the stop and the question after it,
/// the report, and that nothing of it exists before it is used. The engine is a fake that answers from a function; the language files are a temporary
/// folder (never the real one of the user).
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class OcrUiTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("pagewing-ocr-ui-");
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly List<int> _exits = [];
    private int _created;
    private int _active;

    public OcrUiTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        _output.Dispose();
        _error.Dispose();
        Strings.Culture = _originalCulture;
        try
        {
            _work.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Still held by a window that closes late; the folder is in the temp folder.
        }
    }

    [AvaloniaFact]
    public async Task HintAppearsOnceForAScanAfterTheFirstSharpPageAndNeverStartsARun()
    {
        var (window, viewModel) = Show();
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage && tab.ShowsOcrHint);

        Assert.Contains(window.GetVisualDescendants().OfType<OcrHintBar>(), static bar => bar.IsVisible);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(tab.IsOcrRunning);
        Assert.Equal(0, _created);

        // "Not now" closes it for this document in this tab, and it does not come back.
        tab.DismissOcrHintCommand.Execute(null);
        Settle(window);
        Assert.False(tab.ShowsOcrHint);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<OcrHintBar>(), static bar => bar.IsVisible);
        await tab.EnsureOcrHintAsync();
        Assert.False(tab.ShowsOcrHint);
        Assert.Equal(0, _created);
    }

    [AvaloniaFact]
    public async Task NoHintForADocumentWithTextOrWithoutAnImage()
    {
        var (window, viewModel) = Show();
        await viewModel.OpenAsync(Copy("text-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        await tab.EnsureOcrHintAsync();
        Settle(window);
        Assert.False(tab.ShowsOcrHint);

        await viewModel.OpenAsync(Copy("ocr-mixed.pdf"));
        var mixed = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => mixed.HasShownSharpPage);
        await mixed.EnsureOcrHintAsync();
        Settle(window);
        Assert.False(mixed.ShowsOcrHint);
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<OcrHintBar>(), static bar => bar.IsVisible);
    }

    [AvaloniaFact]
    public async Task AMeasurementRunNeverLooksForAScanOrOffersTheCommand()
    {
        var (window, viewModel) = Show();
        var view = window.GetVisualDescendants().OfType<DocumentView>().Single(control => control.Name == "DocumentView");
        var path = Copy("ocr-scan.pdf");
        using var measurement = MeasurementRun.Create(StartupOptions.Parse(["--measure-startup", path]), viewModel, view, new MeasurementOutput(_output, _error, _exits.Add))!;
        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);

        var tab = viewModel.ActiveTab!;
        await tab.EnsureOcrHintAsync();
        Assert.False(viewModel.OcrFeaturesEnabled);
        Assert.False(tab.ShowsOcrHint);
        Assert.False(tab.CanRunOcr);
        Assert.Null(viewModel.Ocr);
        Assert.Equal(0, _created);
    }

    [AvaloniaFact]
    public async Task NothingOfTheFeatureIsMadeBeforeItIsUsed()
    {
        var (window, viewModel) = Show();
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage && tab.ShowsOcrHint);

        foreach (var command in viewModel.Commands.All.Where(static command => command.Id.StartsWith("ocr.", StringComparison.Ordinal)))
        {
            _ = command.CanExecute();
        }

        // The hint is shown, the card, its model, the editor and every engine are not.
        Assert.Null(viewModel.Ocr);
        Assert.DoesNotContain(window.GetVisualDescendants(), static control => control is OcrCard);
        Assert.False(tab.HasEditor);
        Assert.Equal(0, _created);
    }

    [AvaloniaFact]
    public async Task CardDefaultsAreSkipModeAutoResolutionAndRememberedLanguages()
    {
        var (window, viewModel) = Show(settingsLanguages: "deu+eng");
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);

        tab.OcrCommand.Execute(null);
        Settle(window);

        var card = viewModel.Ocr!;
        Assert.Equal(OverlayKind.Ocr, viewModel.Overlay);
        Assert.Equal(OcrCardState.Options, card.State);
        Assert.Equal(0, card.ModeIndex);
        Assert.Equal(0, card.ResolutionIndex);
        Assert.Equal(OcrPageScope.All, card.Scope);
        Assert.Equal(["deu", "eng"], card.Languages.Where(static l => l.IsSelected).Select(static l => l.Name));
        Assert.True(card.CanStart);
        Assert.Contains(window.GetVisualDescendants().OfType<OcrCard>(), static c => c.IsVisible);
        Assert.Equal(0, _created);

        // Without a remembered choice the default is the language of the interface plus English (here English: "eng").
        card.Dispose();
        viewModel.HideOverlay();
        var (window2, viewModel2) = Show(settingsLanguages: null);
        await viewModel2.OpenAsync(Copy("ocr-scan.pdf"));
        var tab2 = viewModel2.ActiveTab!;
        await WaitUntilAsync(window2, () => tab2.CanRunOcr);
        tab2.OcrCommand.Execute(null);
        Assert.Equal(["eng"], viewModel2.Ocr!.Languages.Where(static l => l.IsSelected).Select(static l => l.Name));
    }

    [AvaloniaFact]
    public async Task CardStatesWhatHappensAndNeverPromisesAccuracy()
    {
        var banned = new[] { "korrekt erkannt", "correctly recognized", "vollständig", "complete", "alle Wörter", "all words", "fehlerfrei erkannt", "flawless" };
        foreach (var culture in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(culture);
            var texts = typeof(Strings).GetProperties().Where(static p => p.Name.StartsWith("Ocr", StringComparison.Ordinal) && p.PropertyType == typeof(string))
                .Select(static p => (string)p.GetValue(null)!).ToList();
            Assert.NotEmpty(texts);
            foreach (var text in texts)
            {
                Assert.DoesNotContain(banned, phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
            }

            // What happens, and that recognition is not error-free, in both languages.
            Assert.Contains(culture == "en" ? "not error-free" : "nicht fehlerfrei", Strings.OcrWhat, StringComparison.Ordinal);
            Assert.Contains(culture == "en" ? "nothing is sent" : "nichts wird", Strings.OcrWhat, StringComparison.OrdinalIgnoreCase);
        }

        Strings.Culture = new CultureInfo("en");
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task ImportingALanguageFileAsksForTrust()
    {
        var picked = 0;
        var source = Path.Combine(_work.FullName, "frk.traineddata");
        await File.WriteAllBytesAsync(source, new byte[64], TestContext.Current.CancellationToken);
        var (window, viewModel) = Show(picker: () =>
        {
            picked++;
            return Task.FromResult<string?>(source);
        });
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;

        card.AddLanguageCommand.Execute(null);

        // First the notice about trust; no dialog yet, nothing copied.
        Assert.True(card.ImportPrompt);
        Assert.Equal(0, picked);
        Assert.False(File.Exists(Path.Combine(_work.FullName, "user", "frk.traineddata")));

        card.ChooseLanguageFileCommand.Execute(null);
        await WaitUntilAsync(window, () => card.Languages.Any(static l => l.Name == "frk"));

        Assert.Equal(1, picked);
        Assert.True(File.Exists(Path.Combine(_work.FullName, "user", "frk.traineddata")));
        Assert.True(card.Languages.Single(static l => l.Name == "frk").IsSelected);
        Assert.Contains("frk", card.ImportMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task RunLocksEditSavePrintAndOtherModesWithAVisibleReason()
    {
        using var gate = new ManualResetEventSlim();
        var (window, viewModel) = Show(behavior: call =>
        {
            gate.Wait(call.Cancellation);
            return new OcrRecognition([]);
        });
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;

        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsOcrRunning && _active > 0);

        Assert.Equal(OcrCardState.Running, card.State);
        Assert.True(tab.IsEditingFrozen);
        Assert.False(tab.CanSaveAs());
        Assert.Equal(Strings.SaveBlockedOcr, tab.SaveBlockedReason);
        Assert.False(viewModel.PrintCommand.CanExecute(null));
        Assert.False(tab.CanRunPageOutput);
        Assert.Equal(Strings.OcrBlockedRunning, tab.PageOutputBlockedReason);
        Assert.False(tab.OcrCommand.CanExecute(null));
        Assert.Equal(Strings.OcrBlockedRunning, tab.OcrBlockedReason);
        Assert.False(tab.UndoCommand.CanExecute(null));

        // Reading and moving about stay possible: the commands of the view run.
        Assert.True(tab.ZoomInCommand.CanExecute(null));
        Assert.True(tab.NextPageCommand.CanExecute(null));

        // No other overlay replaces the card while it runs.
        viewModel.ShowOverlay(OverlayKind.Help);
        Assert.Equal(OverlayKind.Ocr, viewModel.Overlay);

        gate.Set();
        await WaitUntilAsync(window, () => !tab.IsOcrRunning);
        Assert.False(tab.IsEditingFrozen);
        Assert.Null(tab.SaveBlockedReason);
    }

    [AvaloniaFact]
    public async Task EscStopsTheRunAndOffersTheFinishedPagesDefaultingToDiscard()
    {
        var (window, viewModel) = Show(behavior: call =>
        {
            if (call.Index == 0)
            {
                return new OcrRecognition([new OcrWord("Quokkaword", 100, 100, 300, 150, 90)]);
            }

            call.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            call.Cancellation.ThrowIfCancellationRequested();
            return new OcrRecognition([]);
        });
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.ProgressText.StartsWith("1 of", StringComparison.Ordinal));

        Assert.True(viewModel.CancelOcrCommand.CanExecute(null));
        viewModel.CancelOcrCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Choose);

        Assert.Contains("1 pages", card.ChooseText, StringComparison.Ordinal);
        Assert.False(tab.IsOcrRunning);
        Assert.False(tab.Editor.IsDirty);

        // Esc is "Discard": the card goes and nothing was written.
        card.HandleEscape();
        Settle(window);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.False(tab.Editor.IsDirty);
        Assert.Empty(await tab.Document!.ReadOcrLayerAsync(0, TestContext.Current.CancellationToken));
    }

    [AvaloniaFact]
    public async Task UsingTheFinishedPagesAfterAStopWritesThemAsOneUnsavedStep()
    {
        var (window, viewModel) = Show(behavior: call =>
        {
            if (call.Index == 0)
            {
                return new OcrRecognition([new OcrWord("Quokkaword", 100, 100, 300, 150, 90)]);
            }

            call.Cancellation.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            call.Cancellation.ThrowIfCancellationRequested();
            return new OcrRecognition([]);
        });
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.ProgressText.StartsWith("1 of", StringComparison.Ordinal));
        viewModel.CancelOcrCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Choose);

        card.UseFinishedCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Report);

        Assert.True(tab.Editor.IsDirty);
        Assert.True(tab.Editor.CanUndo);
        var layer = await tab.Document!.ReadOcrLayerAsync(0, TestContext.Current.CancellationToken);
        Assert.Equal(["Quokkaword"], layer.Select(static w => w.Text));
    }

    [AvaloniaFact]
    public async Task ClosingTheTabDuringARunCancelsIt()
    {
        using var gate = new ManualResetEventSlim();
        var (window, viewModel) = Show(behavior: call =>
        {
            gate.Wait(call.Cancellation);
            return new OcrRecognition([]);
        });
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => _active > 0);

        viewModel.CloseTabCommand.Execute(tab);
        await WaitUntilAsync(window, () => viewModel.Ocr is null && _active == 0);

        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.Equal(OcrCardState.Running, card.State);
    }

    [AvaloniaFact]
    public async Task ReportListsSkippedPagesLowConfidenceAndNeverSaysComplete()
    {
        var (window, viewModel) = Show(behavior: _ => new OcrRecognition([new OcrWord("Fuzzy", 100, 100, 300, 150, 30)]));
        await viewModel.OpenAsync(Copy("ocr-mixed.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;

        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Report);

        Assert.Contains("Pages recognized: 2", card.ReportSummary, StringComparison.Ordinal);
        Assert.Contains(card.ReportLines, static line => line.Contains("Pages to check", StringComparison.Ordinal) && line.Contains("turned", StringComparison.Ordinal));
        Assert.Contains("Page 1: has text of its own", card.SkippedRows);
        Assert.NotEmpty(card.ReportLinks);
        Assert.DoesNotContain(card.ReportLines.Append(card.ReportSummary), static line => line.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || line.Contains("correct", StringComparison.OrdinalIgnoreCase));
        Assert.True(tab.Editor.IsDirty);
    }

    [AvaloniaFact]
    public async Task AfterApplyingSearchFindsTheWordsWithoutReopening()
    {
        var (window, viewModel) = Show(behavior: _ => new OcrRecognition([new OcrWord("Quokkaword", 100, 100, 300, 150, 90)]));
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Report);

        var text = await tab.Document!.GetPageTextAsync(1, TestContext.Current.CancellationToken);
        Assert.Contains("Quokkaword", text.GetText(0, text.Chars.Count), StringComparison.Ordinal);
        Assert.True(tab.Editor.CanUndo);
    }

    [AvaloniaFact]
    public async Task AMissingLanguageFileEndsInTheFailedState()
    {
        var (window, viewModel) = Show(settingsLanguages: "eng");
        await viewModel.OpenAsync(Copy("ocr-scan.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;

        // The language file goes away between the choice and the start.
        File.Delete(Path.Combine(_work.FullName, "shipped", "eng.traineddata"));
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Failed);

        Assert.Contains("eng", card.FailedText, StringComparison.Ordinal);
        Assert.False(tab.IsOcrRunning);
        Assert.False(tab.Editor.IsDirty);
    }

    [AvaloniaFact]
    public async Task TheWarningBeforeSavingASignedFileNamesTheAddedText()
    {
        var (window, viewModel) = Show(behavior: _ => new OcrRecognition([new OcrWord("Added", 100, 100, 300, 150, 90)]));
        await viewModel.OpenAsync(Copy("signed-docmdp-2.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.CanRunOcr);
        tab.OcrCommand.Execute(null);
        var card = viewModel.Ocr!;
        card.ModeIndex = 2;
        card.StartCommand.Execute(null);
        await WaitUntilAsync(window, () => card.State == OcrCardState.Report);
        Assert.True(tab.HasOcrChanges);

        var warning = PendingSave.SignatureWarning(tab, tab.Session.Path!, Pagewing.Core.Pdf.DocMdpLevel.FormsOnly, formsOnly: false, ocr: tab.HasOcrChanges);

        Assert.Contains(Strings.SignatureWarningOcr, warning.Message, StringComparison.Ordinal);
        Assert.Contains("invisible text", warning.Message, StringComparison.Ordinal);
        Strings.Culture = new CultureInfo("de");
        Assert.Contains("unsichtbarer Text", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("gültig ", warning.Message.Replace("ungültig", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    private string Copy(string name)
    {
        var path = Path.Combine(_work.FullName, name);
        if (!File.Exists(path))
        {
            File.Copy(TestData.Pdf(name), path);
        }

        return path;
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(
        Func<FakeCall, OcrRecognition>? behavior = null,
        string? settingsLanguages = null,
        Func<Task<string?>>? picker = null)
    {
        var shipped = Directory.CreateDirectory(Path.Combine(_work.FullName, "shipped")).FullName;
        foreach (var name in new[] { "eng", "deu" })
        {
            var file = Path.Combine(shipped, name + ".traineddata");
            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, [1, 2, 3]);
            }
        }

        var settings = UserSettings.Detached();
        if (settingsLanguages is not null)
        {
            settings.Update(static s => s);
            settings.Update(s => s with { OcrLanguages = settingsLanguages });
        }

        var fake = new CountingFactory(behavior ?? (_ => new OcrRecognition([])), () => Interlocked.Increment(ref _created), enter: () => Interlocked.Increment(ref _active), leave: () => Interlocked.Decrement(ref _active));
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoPicker(), settings)
        {
            IsWindowsTitleBar = true,
            OcrEngineFactory = _ => fake,
            TessdataStoreProvider = () => new TessdataStore(Path.Combine(_work.FullName, "user"), shipped),
            LanguageFilePicker = picker,
        };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
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

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    private sealed class NoPicker : IFilePicker
    {
        public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSavePdfAsync(string suggestedName, string? folder) => Task.FromResult<string?>(null);
    }

    /// <summary>A fake engine that counts how many engines were made and how many calls are running.</summary>
    private sealed class CountingFactory : IOcrEngineFactory
    {
        private readonly Func<FakeCall, OcrRecognition> _behavior;
        private readonly Action _created;
        private readonly Action _enter;
        private readonly Action _leave;
        private int _calls;

        public CountingFactory(Func<FakeCall, OcrRecognition> behavior, Action created, Action enter, Action leave)
        {
            _behavior = behavior;
            _created = created;
            _enter = enter;
            _leave = leave;
        }

        public IOcrEngine Create(string languages)
        {
            _created();
            return new Engine(this);
        }

        private sealed class Engine(CountingFactory owner) : IOcrEngine
        {
            public OcrRecognition Recognize(ReadOnlySpan<byte> gray, int width, int height, int dpi, TimeSpan timeout, CancellationToken cancellationToken)
            {
                owner._enter();
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return owner._behavior(new FakeCall(Interlocked.Increment(ref owner._calls) - 1, width, height, dpi, cancellationToken));
                }
                finally
                {
                    owner._leave();
                }
            }

            public void Dispose()
            {
            }
        }
    }

    /// <summary>One call of the fake engine: which call it is (from 0 over the run) and the image it got.</summary>
    private sealed record FakeCall(int Index, int Width, int Height, int Dpi, CancellationToken Cancellation);
}
