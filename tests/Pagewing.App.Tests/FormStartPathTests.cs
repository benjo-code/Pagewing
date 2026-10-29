using System.Globalization;
using Avalonia;
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
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The start path of the forms (milestone 11, task 14): a document without a form pays nothing for them, a measurement run
/// reads and builds nothing of the form features (the budgets measure the open and the first sharp page), and nothing of the
/// form is read or built before the first sharp page or before the mode. The environment of a document with a form is made in
/// the open job and its fields are drawn with the page also in a measurement run: that is showing the page, and what the
/// measurements record (<c>Docs/DECISIONS.md</c>, "Formulare: Messwerte und Startpfad (M11)").
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class FormStartPathTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly List<int> _exits = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-formstart-" + Guid.NewGuid().ToString("N"))).FullName;

    public FormStartPathTests() => Strings.Culture = new CultureInfo("en");

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
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task OpeningADocumentWithoutAFormCreatesNoEnvironmentAndNoControls()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        var reads = CountReads(tab);

        // The panel "Pages" rendered its thumbnails too; the page, the tiles and the thumbnails never touched the form engine.
        var document = tab.Session.Document!;
        Assert.False(tab.HasForm);
        Assert.Equal(0, document.FormEnvironmentsCreated);
        Assert.Equal(0, document.FormDraws);
        Assert.Equal(0, document.FormWidgetScans);

        // Nothing is read, offered or built for it, and the commands of the mode cannot run.
        await tab.EnsureFormInfoAsync();
        Assert.Equal(0, reads.Value);
        Assert.False(tab.HasFormFields);
        Assert.False(tab.OffersFill);
        Assert.Equal(FormHint.None, tab.PendingHints);
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        Assert.Empty(tab.FieldsOfPage(0));
        Assert.False(tab.HasEditor);
        AssertNoFormControls(window, view);
    }

    [AvaloniaFact]
    public async Task MeasurementRunsCreateNoFormObjects()
    {
        var (window, viewModel, view) = Show();
        var path = TestData.Pdf("forms-basic.pdf");
        using var measurement = MeasurementRun.Create(StartupOptions.Parse(["--measure-startup", path]), viewModel, view, Sink())!;

        measurement.Start();
        await WaitUntilAsync(window, () => _exits.Count > 0);
        Settle(window);

        Assert.Equal([0], _exits);
        Assert.Empty(_error.ToString());
        var tab = viewModel.ActiveTab!;
        Assert.True(tab.HasShownSharpPage);
        var reads = CountReads(tab);

        // The page is shown with its fields (the environment of the open job, FPDF_FFLDraw with the render): that is what the
        // budgets measure. Nothing else of the form: no read for the hints, no offer, no bar, no layer, no input, no editor.
        var document = tab.Session.Document!;
        Assert.True(tab.HasForm);
        Assert.Equal(1, document.FormEnvironmentsCreated);
        Assert.True(document.FormDraws > 0, "The fields of the page are drawn also in a measurement run.");
        await tab.EnsureFormInfoAsync();
        Assert.Equal(0, reads.Value);
        Assert.False(tab.HasFormFields);
        Assert.False(tab.OffersFill);
        Assert.Equal(FormHint.None, tab.PendingHints);
        Assert.False(tab.ToggleFillCommand.CanExecute(null));
        Assert.Empty(tab.FieldsOfPage(0));
        Assert.False(tab.HasEditor);
        AssertNoFormControls(window, view);

        // A tab opened later in the run (the second file of --measure-second) is the same.
        await viewModel.OpenAsync(TestData.Pdf("forms-calc.pdf"));
        var second = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => second.HasShownSharpPage);
        var secondReads = CountReads(second);
        await second.EnsureFormInfoAsync();
        Assert.Equal(0, secondReads.Value);
        Assert.False(second.OffersFill);
        Assert.Equal(FormHint.None, second.PendingHints);
        AssertNoFormControls(window, view);
    }

    [AvaloniaFact]
    public async Task NoFormControlIsCreatedBeforeTheFirstSharpPage()
    {
        var (window, viewModel, view) = Show();
        await viewModel.OpenAsync(TestData.Pdf("forms-basic.pdf"));
        var tab = viewModel.ActiveTab!;
        var reads = CountReads(tab);

        // Up to the first sharp page: nothing read, offered or built (the window checks at every step).
        await WaitUntilAsync(window, () =>
        {
            if (tab.HasShownSharpPage)
            {
                return true;
            }

            Assert.Equal(0, reads.Value);
            Assert.False(tab.OffersFill);
            Assert.False(tab.HasFormFields);
            Assert.Equal(FormHint.None, tab.PendingHints);
            AssertNoFormControls(window, view);
            return false;
        });

        // Then the form is read once, in the background, and the offer comes with the result.
        await tab.EnsureFormInfoAsync();
        Assert.Equal(1, reads.Value);
        Assert.True(tab.HasFormFields);
        await WaitUntilAsync(window, () => FindOrNull<FormHintBar>(window)?.IsEffectivelyVisible == true);
        Assert.Equal(1, reads.Value);

        // The offer is a message only: still no toolbar, layer, input or editor.
        Assert.Null(FindOrNull<FormToolbar>(window));
        Assert.Null(view.FormOverlay);
        Assert.False(tab.HasEditor);
    }

    [AvaloniaFact]
    public async Task FormToolbarAndEditorsAreCreatedOnlyByTheMode()
    {
        var (window, viewModel, view) = Show();
        var path = System.IO.Path.Combine(_folder, "forms-basic.pdf");
        File.Copy(TestData.Pdf("forms-basic.pdf"), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.HasShownSharpPage && tab.OffersFill);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);

        // Before the mode: the offer, nothing else.
        Assert.NotNull(FindOrNull<FormHintBar>(window));
        Assert.Null(FindOrNull<FormToolbar>(window));
        Assert.Empty(window.GetVisualDescendants().OfType<FormFieldEditor>());
        Assert.Null(view.FormOverlay);
        Assert.Null(view.FormFieldEditor);
        Assert.False(tab.HasEditor);

        // The mode makes the toolbar and the layer, not yet an input or the editor.
        await tab.EnterFillAsync();
        await WaitUntilAsync(window, () => FindOrNull<FormToolbar>(window)?.IsEffectivelyVisible == true && view.FormOverlay is not null);
        Assert.Empty(window.GetVisualDescendants().OfType<FormFieldEditor>());
        Assert.Null(view.FormFieldEditor);
        Assert.False(tab.HasEditor);

        // A text field with the focus makes its input; the editor of the document comes with the first value written.
        var fields = await tab.ReadFieldsAsync(0);
        var text = fields.First(field => field.Kind == FormFieldKind.Text && TabViewModel.IsEditable(field));
        tab.FocusedField = text.Key;
        await WaitUntilAsync(window, () => view.FormFieldEditor is not null);
        Assert.Single(window.GetVisualDescendants().OfType<FormFieldEditor>());
        Assert.False(tab.HasEditor);
        Assert.True(await tab.CommitFieldAsync(text.Key.FieldName, FormFieldState.OfText("Startpfad")));
        Assert.True(tab.HasEditor);
    }

    private static void AssertNoFormControls(Window window, DocumentView view)
    {
        Assert.Null(FindOrNull<FormHintBar>(window));
        Assert.Null(FindOrNull<FormToolbar>(window));
        Assert.Empty(window.GetVisualDescendants().OfType<FormFieldEditor>());
        Assert.Null(view.FormOverlay);
        Assert.Null(view.FormFieldEditor);
    }

    /// <summary>Counts the reads of the form of the tab (<see cref="TabViewModel.FormInfoReader"/>), which still run as before.</summary>
    private static Counter CountReads(TabViewModel tab)
    {
        var counter = new Counter();
        var inner = tab.FormInfoReader;
        tab.FormInfoReader = (document, cancellationToken) =>
        {
            counter.Value++;
            return inner(document, cancellationToken);
        };
        return counter;
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

    private sealed class Counter
    {
        public int Value { get; set; }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
