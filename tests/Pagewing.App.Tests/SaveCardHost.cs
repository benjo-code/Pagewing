using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// What the tests of the save cards and of closing with changes share (<see cref="SaveCardsTests"/>,
/// <see cref="CloseWithChangesTests"/>): windows with a manual clock for the lock of the dangerous choices, copies of the
/// test files in a temp folder, a highlight to make a tab dirty, and the clicks and keys. Settings and history in memory
/// or in the temp folder, never the user's.
/// </summary>
internal sealed class SaveCardHost : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private static readonly AnnotationColor Yellow = new(255, 230, 0);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public SaveCardHost()
    {
        Strings.Culture = new CultureInfo("en");
        Folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-savecards-" + Guid.NewGuid().ToString("N"))).FullName;
    }

    /// <summary>Gets the temp folder of the copies.</summary>
    public string Folder { get; }

    /// <summary>Gets the clock of every window of the host (moves only when told to).</summary>
    public PrintClock Clock { get; } = new();

    /// <summary>Gets the print service of the windows (never the system's).</summary>
    public FakePrintService Print { get; } = new();

    /// <summary>Gets the file picker of the windows: its save dialog returns <see cref="SavePicker.Result"/>.</summary>
    public SavePicker Picker { get; } = new();

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    /// <summary>Adds something the host disposes at the end (before what was added earlier).</summary>
    public void Own(IDisposable disposable) => _cleanup.Add(disposable);

    public (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(RecentFilesService? recent = null)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), Picker, UserSettings.Detached(), recent)
        {
            IsWindowsTitleBar = true,
            Clock = Clock,
            PrintServiceFactory = () => Print,
        };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Copies a test file into the temp folder (under <paramref name="copyName"/> when given).</summary>
    public string Copy(string name, string? copyName = null)
    {
        var path = Path.Combine(Folder, copyName ?? name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    /// <summary>Opens <paramref name="path"/> in a new tab and waits for its first sharp page.</summary>
    public static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string path)
    {
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

    /// <summary>Opens a copy of a test file in a new tab (see <see cref="OpenAsync"/>).</summary>
    public async Task<(TabViewModel Tab, string Path)> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name, string? copyName = null)
    {
        var path = Copy(name, copyName);
        return (await OpenAsync(window, viewModel, view, path), path);
    }

    /// <summary>A highlight on <paramref name="page"/>; waits until the tab shows it as unsaved.</summary>
    public static async Task MarkAsync(Window window, TabViewModel tab, int page = 0)
    {
        var created = await tab.Editor.ApplyMarkupAsync(page, AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], Yellow, null, null);
        Assert.NotNull(created);
        await WaitUntilAsync(window, () => tab.IsDirty && tab.CanUndo);
    }

    /// <summary>Waits until the card of the window shows a question of <paramref name="kind"/>, and returns the card.</summary>
    public static async Task<T> WaitForCardAsync<T>(Window window, MainViewModel viewModel, PendingSaveKind kind)
        where T : SaveCard
    {
        T? card = null;
        await WaitUntilAsync(window, () =>
            viewModel.PendingSave is { } question && question.Kind == kind
            && (card = window.GetVisualDescendants().OfType<T>().FirstOrDefault(found => found.IsVisible)) is not null
            && ReferenceEquals(card.Pending, question)
            && card.ButtonOf(question.DefaultChoice) is { IsFocused: true });
        return card!;
    }

    /// <summary>Lets the lock of the dangerous choices pass on the manual clock.</summary>
    public void PassTheLock(Window window)
    {
        Clock.Advance(PendingSave.ArmingDelay);
        Settle(window);
    }

    /// <summary>A click with the left button in the middle of <paramref name="control"/>.</summary>
    public static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    public static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    public static async Task WaitUntilAsync(Window window, Func<bool> condition)
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

    public static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }

    public static T Find<T>(Visual root, string name)
        where T : Control =>
        TryFind<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    public static T? TryFind<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    /// <summary>The buttons of <paramref name="card"/> that are shown, from left to right, with their text.</summary>
    public static IReadOnlyList<string> ShownButtons(SaveCard card) =>
        card.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible)
            .OrderBy(button => button.TranslatePoint(default, card)!.Value.X)
            .Select(button => (string)button.Content!)
            .ToList();

    /// <summary>A file picker whose open dialog is cancelled and whose save dialog returns <see cref="Result"/>.</summary>
    internal sealed class SavePicker : IFilePicker
    {
        public string? Result { get; set; }

        public int Calls { get; private set; }

        public Task<string?> PickPdfAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickSavePdfAsync(string suggestedName, string? folder)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    // At the end of a test nothing is asked any more (a window with changes would stay open otherwise).
    private sealed class Closer(MainWindow window) : IDisposable
    {
        public void Dispose() => window.CloseWithoutAsking();
    }
}
