using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class OpenFlowTests : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private string? _tempDirectory;

    public void Dispose()
    {
        // Windows close before their sessions are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        if (_tempDirectory is not null)
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }

        Strings.Culture = _originalCulture;
    }

    /// <summary>Every <see cref="PdfOpenError"/> in both languages.</summary>
    public static TheoryData<PdfOpenError, string> ErrorKindsAndLanguages()
    {
        var data = new TheoryData<PdfOpenError, string>();
        foreach (var error in Enum.GetValues<PdfOpenError>())
        {
            data.Add(error, "en");
            data.Add(error, "de");
        }

        return data;
    }

    [AvaloniaFact]
    public async Task OpenCommandUsesThePickerAndOpensTheFile()
    {
        var path = TestData.Pdf("hello_world.pdf");
        var picker = new FakePicker(path);
        var (window, viewModel) = Show(picker);

        Find<Button>(window, "ChooseFileButton").Command!.Execute(null);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);

        Assert.Equal(1, picker.Calls);
        Assert.Equal(path, viewModel.FullPath);
        Assert.True(Find<Control>(window, "Tab").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task CancelledPickerChangesNothing()
    {
        var picker = new FakePicker(TestData.Pdf("hello_world.pdf"));
        var (window, viewModel) = Show(picker);
        viewModel.OpenCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);

        picker.Result = null;
        // The fake picker completes synchronously, so the command has finished when Execute returns.
        viewModel.OpenCommand.Execute(null);
        Settle(window);

        Assert.Equal(2, picker.Calls);
        Assert.Equal(ViewerState.Ready, viewModel.State);
        Assert.Equal(TestData.Pdf("hello_world.pdf"), viewModel.FullPath);
    }

    [AvaloniaFact]
    public async Task StartWithAFileShowsTheTabBeforeTheDocumentIsReady()
    {
        // Program.Main starts opening before the window exists; this open waits until the test lets it go on.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new PdfEngine();
        var session = new ViewerSession(async (path, password, cancellationToken) =>
        {
            await gate.Task.ConfigureAwait(false);
            return await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        });
        _cleanup.Add(session);
        var path = TestData.Pdf("hello_world.pdf");
        var opening = session.OpenAsync(path);

        var (window, viewModel) = Show(new FakePicker(null), session);

        // The first frame: tab with the file name and an empty document area, no start screen, no card.
        Assert.Equal(ViewerState.Loading, viewModel.State);
        Assert.Equal("hello_world.pdf", viewModel.FileName);
        Assert.True(Find<Control>(window, "Tab").IsEffectivelyVisible);
        Assert.Equal("hello_world.pdf", Find<MiddleEllipsisTextBlock>(window, "TabName").Text);
        Assert.True(Find<Control>(window, "DocumentArea").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "PasswordCard").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);
        using (var frame = window.CaptureRenderedFrame())
        {
            Assert.NotNull(frame);
        }

        gate.SetResult();
        await opening;
        Settle(window);
        Assert.Equal(ViewerState.Ready, viewModel.State);
    }

    [AvaloniaFact]
    public async Task PasswordCardAppearsAcceptsThePasswordAndKeepsFocus()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.PasswordRequired);

        var card = Find<Control>(window, "PasswordCard");
        var field = Find<TextBox>(window, "PasswordField");
        Assert.True(card.IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "Tab").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "PasswordMessage").IsEffectivelyVisible);
        Assert.Same(field, window.FocusManager!.GetFocusedElement());
        Assert.Contains("encrypted_hello_world_r3.pdf", Find<TextBlock>(window, "PasswordPrompt").Text, StringComparison.Ordinal);
        Assert.Equal('•', field.PasswordChar);

        // Typed into the focused field, submitted with Enter.
        window.KeyTextInput(UserPassword);
        Settle(window);
        Assert.Equal(UserPassword, viewModel.Password);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

        // That the card and the focus stay while the password is checked: StatusBarRowIsThereOnlyWhileLoadingOrReady.
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);
        Assert.False(card.IsEffectivelyVisible);
        Assert.Equal(string.Empty, viewModel.Password);
        Assert.True(Find<Control>(window, "StatusBar").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task StatusBarRowIsThereOnlyWhileLoadingOrReady()
    {
        // Every open waits until the test releases it, so the loading states can be looked at.
        using var gate = new SemaphoreSlim(0);
        var engine = new PdfEngine();
        var session = new ViewerSession(async (path, password, cancellationToken) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        });
        _cleanup.Add(session);
        var (window, viewModel) = Show(new FakePicker(null), session);
        var row = Find<Control>(window, "StatusBarRow");
        Assert.False(row.IsEffectivelyVisible);

        // Loading reserves the row, so the viewport keeps its height when the document is ready.
        var opening = viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        Settle(window);
        Assert.Equal(ViewerState.Loading, viewModel.State);
        Assert.True(row.IsEffectivelyVisible);

        // No empty strip under the password card, also not while the password is checked.
        gate.Release();
        await opening;
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.PasswordRequired);
        var card = Find<Control>(window, "PasswordCard");
        var field = Find<TextBox>(window, "PasswordField");
        Assert.False(row.IsEffectivelyVisible);
        window.KeyTextInput(UserPassword);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Assert.Equal(ViewerState.Loading, viewModel.State);
        Assert.True(card.IsEffectivelyVisible);
        Assert.Same(field, window.FocusManager!.GetFocusedElement());
        Assert.False(row.IsEffectivelyVisible);

        gate.Release();
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);
        Assert.True(row.IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "StatusBar").IsEffectivelyVisible);

        // No empty strip under the error card either.
        var failing = viewModel.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        gate.Release();
        await failing;
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Failed);
        Assert.True(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);
        Assert.False(row.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task WrongPasswordShowsTheMessageAndKeepsTheField()
    {
        Strings.Culture = new CultureInfo("de");
        var (window, viewModel) = Show(new FakePicker(null));
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.PasswordRequired);
        var field = Find<TextBox>(window, "PasswordField");

        window.KeyTextInput("falsch");
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.PasswordIncorrect);

        var message = Find<TextBlock>(window, "PasswordMessage");
        Assert.True(Find<Control>(window, "PasswordCard").IsEffectivelyVisible);
        Assert.True(message.IsEffectivelyVisible);
        Assert.Equal("Das Passwort ist falsch. Achten Sie auf Groß- und Kleinschreibung.", message.Text);
        Assert.Contains("invalid", field.Classes);
        Assert.Equal("falsch", field.Text);
        Assert.Equal(0, Math.Min(field.SelectionStart, field.SelectionEnd));
        Assert.Equal(field.Text!.Length, Math.Max(field.SelectionStart, field.SelectionEnd));
        Assert.Same(field, window.FocusManager!.GetFocusedElement());

        // Typing replaces the selected wrong password; the right one opens the file.
        window.KeyTextInput(UserPassword);
        Settle(window);
        Assert.Equal(UserPassword, field.Text);
        viewModel.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);
        Assert.DoesNotContain("invalid", field.Classes);
    }

    [AvaloniaFact]
    public async Task CancelOnThePasswordCardClosesTheTab()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        await viewModel.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.PasswordRequired);

        Find<Button>(window, "PasswordCancelButton").Command!.Execute(null);
        Settle(window);

        Assert.Equal(ViewerState.Empty, viewModel.State);
        Assert.False(Find<Control>(window, "Tab").IsEffectivelyVisible);
        Assert.False(Find<Control>(window, "PasswordCard").IsEffectivelyVisible);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [MemberData(nameof(ErrorKindsAndLanguages))]
    public async Task ErrorCardTextMatchesEveryErrorKind(PdfOpenError error, string language)
    {
        Strings.Culture = new CultureInfo(language);
        var session = new ViewerSession((_, _, _) => Task.FromException<PdfDocument>(new PdfOpenException(error)));
        _cleanup.Add(session);
        var (window, viewModel) = Show(new FakePicker(null), session);
        var path = Path.Combine(Path.GetTempPath(), "report.pdf");

        await viewModel.OpenAsync(path);
        Settle(window);

        var errorCard = Find<Control>(window, "ErrorCard");
        var passwordCard = Find<Control>(window, "PasswordCard");
        if (error is PdfOpenError.PasswordRequired or PdfOpenError.PasswordIncorrect)
        {
            // A password problem is the password card, never the error card.
            Assert.False(errorCard.IsEffectivelyVisible);
            Assert.True(passwordCard.IsEffectivelyVisible);
            return;
        }

        // Texts of the design system, section 7.8.
        var (title, cause) = (language, error) switch
        {
            ("de", PdfOpenError.NotAPdf) => ("Diese Datei lässt sich nicht öffnen", "„report.pdf“ ist beschädigt oder keine PDF-Datei."),
            ("de", PdfOpenError.FileNotFound) => ("Diese Datei lässt sich nicht öffnen", "„report.pdf“ wurde nicht gefunden."),
            ("de", PdfOpenError.CannotRead) => ("Diese Datei lässt sich nicht öffnen", "„report.pdf“ kann nicht gelesen werden (kein Zugriff oder zu groß)."),
            ("de", PdfOpenError.UnsupportedEncryption) => ("Diese Datei lässt sich nicht öffnen", "„report.pdf“ ist mit einem Verfahren verschlüsselt, das Pagewing nicht unterstützt."),
            ("de", PdfOpenError.Unknown) => ("Diese Datei lässt sich nicht öffnen", "„report.pdf“ konnte aus einem unbekannten Grund nicht geöffnet werden."),
            ("en", PdfOpenError.NotAPdf) => ("This file cannot be opened", "“report.pdf” is damaged or not a PDF file."),
            ("en", PdfOpenError.FileNotFound) => ("This file cannot be opened", "“report.pdf” was not found."),
            ("en", PdfOpenError.CannotRead) => ("This file cannot be opened", "“report.pdf” cannot be read (no access or too large)."),
            ("en", PdfOpenError.UnsupportedEncryption) => ("This file cannot be opened", "“report.pdf” is encrypted with a method Pagewing does not support."),
            ("en", PdfOpenError.Unknown) => ("This file cannot be opened", "“report.pdf” could not be opened for an unknown reason."),
            _ => throw new InvalidOperationException($"No expected text for {error} in {language}."),
        };

        Assert.Equal(ViewerState.Failed, viewModel.State);
        Assert.True(errorCard.IsEffectivelyVisible);
        Assert.False(passwordCard.IsEffectivelyVisible);
        Assert.Equal(title, Find<TextBlock>(window, "ErrorTitle").Text);
        Assert.Equal(cause, Find<TextBlock>(window, "ErrorCause").Text);

        // The technical details are collapsed and name the full path and the error kind.
        var details = Find<Expander>(window, "ErrorDetails");
        Assert.False(details.IsExpanded);
        Assert.Equal(language == "de" ? "Technische Details" : "Technical details", details.Header);
        Assert.Contains(path, viewModel.ErrorDetailsText, StringComparison.Ordinal);
        Assert.Contains(error.ToString(), viewModel.ErrorDetailsText, StringComparison.Ordinal);
        details.IsExpanded = true;
        Settle(window);
        Assert.Equal(viewModel.ErrorDetailsText, Find<SelectableTextBlock>(window, "ErrorDetailsText").Text);
    }

    [AvaloniaFact]
    public async Task ErrorCardOffersCloseTabAndOpenOther()
    {
        var picker = new FakePicker(TestData.Pdf("hello_world.pdf"));
        var (window, viewModel) = Show(picker);
        await viewModel.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Failed);

        var close = Find<Button>(window, "ErrorCloseButton");
        var openOther = Find<Button>(window, "ErrorOpenOtherButton");
        Assert.True(close.IsEffectivelyVisible);
        Assert.True(openOther.IsEffectivelyVisible);
        Assert.Contains("primary", openOther.Classes);
        Assert.DoesNotContain("primary", close.Classes);

        // "Open another file" goes through the file dialog.
        openOther.Command!.Execute(null);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);
        Assert.Equal(1, picker.Calls);
        Assert.False(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);

        // "Close tab" returns to the start screen.
        await viewModel.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Failed);
        close.Command!.Execute(null);
        Settle(window);
        Assert.Equal(ViewerState.Empty, viewModel.State);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task DroppingAFileOpensIt()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        var path = TestData.Pdf("hello_world.pdf");

        Drop(window, await FileAsync(window, path));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);

        Assert.Equal(path, viewModel.FullPath);
    }

    [AvaloniaFact]
    public async Task DroppingSeveralFilesOpensTheFirst()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        var first = TestData.Pdf("text-basic.pdf");

        Drop(window, await FileAsync(window, first), await FileAsync(window, TestData.Pdf("hello_world.pdf")));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);

        Assert.Equal(first, viewModel.FullPath);
    }

    [AvaloniaFact]
    public async Task DroppingAFolderShowsACannotReadError()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        var folder = TempDirectory();

        Drop(window, await FolderAsync(window, folder));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Failed);

        Assert.Equal(PdfOpenError.CannotRead, viewModel.Error);
        Assert.True(Find<Control>(window, "ErrorCard").IsEffectivelyVisible);

        // Content that is not a file (text) is ignored.
        var text = new DataTransfer();
        text.Add(DataTransferItem.CreateText("C:\\not\\a\\drop.pdf"));
        DropData(window, text);
        Settle(window);
        Assert.Equal(ViewerState.Failed, viewModel.State);
        Assert.Equal(Path.GetFullPath(folder), viewModel.FullPath);
    }

    [AvaloniaFact]
    public async Task ExternalOpenReplacesTheDocument()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        var activations = 0;
        viewModel.ActivationRequested += () => activations++;
        var first = TestData.Pdf("hello_world.pdf");
        var second = TestData.Pdf("text-basic.pdf");
        await viewModel.OpenAsync(first);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);

        // As the single-instance listener does it: from a pool thread, in this process.
        await Task.Run(() => viewModel.OnExternalOpen([second]));
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready && viewModel.FullPath == second);

        Assert.Equal("text-basic.pdf", Find<MiddleEllipsisTextBlock>(window, "TabName").Text);
        Assert.Equal(1, activations);
    }

    [AvaloniaFact]
    public async Task ExternalOpenWithoutPathsOnlyActivates()
    {
        var (window, viewModel) = Show(new FakePicker(null));
        var activations = 0;
        viewModel.ActivationRequested += () => activations++;
        var path = TestData.Pdf("hello_world.pdf");
        await viewModel.OpenAsync(path);
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);
        window.WindowState = WindowState.Minimized;
        Settle(window);

        await Task.Run(() => viewModel.OnExternalOpen([]));
        await WaitUntilAsync(window, () => activations == 1);

        Assert.Equal(ViewerState.Ready, viewModel.State);
        Assert.Equal(path, viewModel.FullPath);
        Assert.NotEqual(WindowState.Minimized, window.WindowState);
    }

    [AvaloniaFact]
    public async Task CtrlOWorksAndCtrlWClosesTheDocument()
    {
        var path = TestData.Pdf("hello_world.pdf");
        var picker = new FakePicker(path);
        var (window, viewModel) = Show(picker);

        window.KeyPress(Key.O, RawInputModifiers.Control, PhysicalKey.O, "o");
        window.KeyRelease(Key.O, RawInputModifiers.Control, PhysicalKey.O, "o");
        await WaitUntilAsync(window, () => viewModel.State == ViewerState.Ready);
        Assert.Equal(1, picker.Calls);
        Assert.Equal(path, viewModel.FullPath);

        window.KeyPress(Key.W, RawInputModifiers.Control, PhysicalKey.W, "w");
        window.KeyRelease(Key.W, RawInputModifiers.Control, PhysicalKey.W, "w");
        Settle(window);
        Assert.Equal(ViewerState.Empty, viewModel.State);
        Assert.True(Find<Control>(window, "StartScreen").IsEffectivelyVisible);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void WarningSymbolInTheTabMeetsAA(string theme)
    {
        // The tab of a failed file shows the warning triangle in danger.default on surface.canvas, a new pair (icon: 3:1).
        var variant = theme == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var ratio = Wcag.Contrast(TokenTests.Token("Danger.Default", variant), TokenTests.Token("Surface.Canvas", variant));

        Assert.True(ratio >= 3, $"{theme}: danger.default on surface.canvas is {ratio:F2}:1");
    }

    private (MainWindow Window, MainViewModel ViewModel) Show(IFilePicker picker, ViewerSession? session = null)
    {
        if (session is null)
        {
            session = new ViewerSession(new PdfEngine());
            _cleanup.Add(session);
        }

        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(session, picker) { IsWindowsTitleBar = true };
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720 };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel);
    }

    private static async Task<IStorageItem> FileAsync(Window window, string path) =>
        await window.StorageProvider.TryGetFileFromPathAsync(path) ?? throw new InvalidOperationException($"No storage file for {path}.");

    private static async Task<IStorageItem> FolderAsync(Window window, string path) =>
        await window.StorageProvider.TryGetFolderFromPathAsync(path) ?? throw new InvalidOperationException($"No storage folder for {path}.");

    private static void Drop(Window window, params IStorageItem[] items)
    {
        var data = new DataTransfer();
        foreach (var item in items)
        {
            data.Add(DataTransferItem.CreateFile(item));
        }

        DropData(window, data);
    }

    private static void DropData(Window window, IDataTransfer data)
    {
        var point = new Point(window.Width / 2, window.Height / 2);
        window.DragDrop(point, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(point, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
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
            window.UpdateLayout();
        }
    }

    private string TempDirectory()
    {
        _tempDirectory ??= Directory.CreateTempSubdirectory("pagewing-app-tests-").FullName;
        return _tempDirectory;
    }

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class FakePicker(string? result) : IFilePicker
    {
        public string? Result { get; set; } = result;

        public int Calls { get; private set; }

        public Task<string?> PickPdfAsync()
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }
}
