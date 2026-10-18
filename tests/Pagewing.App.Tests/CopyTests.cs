using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class CopyTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly System.Globalization.CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public CopyTests() => Strings.Culture = new System.Globalization.CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [Fact]
    public void WindowsHtmlEnvelopeHasByteOffsetsThatPointAtTheFragment()
    {
        const string fragment = "<p>Grüße &amp; Überschrift</p>\n";
        var bytes = ClipboardHtml.WrapForWindows(fragment);

        // UTF-8 without a byte order mark and without a trailing NUL.
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.NotEqual(0, bytes[^1]);

        var all = Encoding.UTF8.GetString(bytes);
        var headerEnd = all.IndexOf("<html>", StringComparison.Ordinal);
        var lines = all[..headerEnd].Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Equal("Version:0.9", lines[0]);
        var offsets = new Dictionary<string, int>();
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(':');
            Assert.Equal(10, parts[1].Length);
            offsets[parts[0]] = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        Assert.Equal(["StartHTML", "EndHTML", "StartFragment", "EndFragment"], lines.Skip(1).Select(line => line.Split(':')[0]));

        // Byte offsets: the umlauts are two bytes each, so the character count would be wrong.
        var utf8Fragment = Encoding.UTF8.GetBytes(fragment);
        Assert.NotEqual(fragment.Length, utf8Fragment.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(all[..headerEnd]), offsets["StartHTML"]);
        Assert.Equal(bytes.Length, offsets["EndHTML"]);
        Assert.Equal(utf8Fragment.Length, offsets["EndFragment"] - offsets["StartFragment"]);
        Assert.Equal(fragment, Encoding.UTF8.GetString(bytes, offsets["StartFragment"], offsets["EndFragment"] - offsets["StartFragment"]));

        Assert.Equal("<html><body>\r\n<!--StartFragment-->", Encoding.UTF8.GetString(bytes, offsets["StartHTML"], offsets["StartFragment"] - offsets["StartHTML"]));
        Assert.Equal("<!--EndFragment-->\r\n</body></html>", Encoding.UTF8.GetString(bytes, offsets["EndFragment"], offsets["EndHTML"] - offsets["EndFragment"]));
    }

    [AvaloniaFact]
    public async Task CtrlCCopiesProseAndHtml()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var selection = await SelectAsync(window, view, 0, 0, text.Chars.Count);
        FocusDocument(window, view);

        Press(window, Key.C, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => clipboard.Calls.Count > 0);

        var call = Assert.Single(clipboard.Calls);
        var slices = TextFlow.SlicesOf(selection!.Value, _ => text);
        Assert.Equal(TextFlow.Plain(slices), call.Plain);
        Assert.StartsWith("Pagewing Handbuch\n\nPagewing ist ein schneller PDF-Viewer", call.Plain, StringComparison.Ordinal);
        Assert.Contains("\n\n", call.Plain, StringComparison.Ordinal);
        Assert.Equal(TextFlow.Html(slices), call.Html);
        Assert.Contains("<h1>Pagewing Handbuch</h1>", call.Html, StringComparison.Ordinal);
        Assert.Contains("<b>fett</b>", call.Html, StringComparison.Ordinal);
        Assert.Contains("<i>kursiv</i>", call.Html, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CtrlShiftCCopiesRawTextWithLineBreaksAndNoHtml()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var selection = await SelectAsync(window, view, 0, 0, text.Chars.Count);
        FocusDocument(window, view);

        Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
        await WaitUntilAsync(window, () => clipboard.Calls.Count > 0);

        var call = Assert.Single(clipboard.Calls);
        Assert.Null(call.Html);
        Assert.Equal(TextFlow.Raw(TextFlow.SlicesOf(selection!.Value, _ => text)), call.Plain);

        // The PDF's own line breaks: the word that is hyphenated at a line end stays so.
        Assert.Contains("Win-\ndows", call.Plain, StringComparison.Ordinal);
        Assert.StartsWith("Pagewing Handbuch\nPagewing ist", call.Plain, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CopyWithoutASelectionLeavesTheClipboardAlone()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        await TextAsync(window, tab, 0);
        FocusDocument(window, view);
        Assert.Null(tab.TextSelection);

        Press(window, Key.C, RawInputModifiers.Control);
        Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
        await view.CopySelectionAsync(raw: false);
        await view.CopySelectionAsync(raw: true);

        // An empty selection (a caret) and one of spaces only change nothing either.
        tab.TextSelection = new TextSelection(new TextPosition(0, 3), new TextPosition(0, 3));
        await view.CopySelectionAsync(raw: false);
        Assert.Empty(clipboard.Calls);
    }

    [AvaloniaFact]
    public async Task CtrlCInTheSearchFieldCopiesTheFieldNotThePdf()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        await SelectAsync(window, view, 0, 0, text.Chars.Count);

        Press(window, Key.F, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => window.FocusManager!.GetFocusedElement() is TextBox { Name: "SearchField" });
        Assert.True(tab.HasTextSelection);

        Press(window, Key.C, RawInputModifiers.Control);
        Press(window, Key.C, RawInputModifiers.Control | RawInputModifiers.Shift);
        await Task.Delay(50);
        Settle(window);
        Assert.Empty(clipboard.Calls);
        Assert.True(tab.HasTextSelection);
    }

    [AvaloniaFact]
    public async Task CopyAcrossPagesLoadsTheMissingPages()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "thousand-pages.pdf");
        var source = tab.Session.EnsurePageTexts()!;

        // More pages than the text source keeps, and far from what the view reads ahead.
        const int last = 40;
        Assert.True(last + 1 > PageTextSource.MaxCachedPages);
        Assert.False(source.TryGet(20, out _));
        var asked = new List<int>();
        view.ReadText = (texts, page, priority) =>
        {
            asked.Add(page);
            return texts.GetAsync(page, priority);
        };
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(last, 5));
        FocusDocument(window, view);

        Press(window, Key.C, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => clipboard.Calls.Count > 0);

        var call = Assert.Single(clipboard.Calls);
        Assert.Equal(Enumerable.Range(0, last + 1), asked.Where(page => page <= last).Distinct().Order());

        // The same text as TextFlow makes of all pages read one by one.
        var pages = new Dictionary<int, PageText>();
        for (var page = 0; page <= last; page++)
        {
            var read = source.GetAsync(page);
            await WaitUntilAsync(window, () => read.IsCompleted);
            pages[page] = await read;
        }

        var slices = TextFlow.SlicesOf(tab.TextSelection!.Value, page => pages[page]);
        Assert.Equal(TextFlow.Plain(slices), call.Plain);
        Assert.Equal(TextFlow.Html(slices), call.Html);
        Assert.True(slices.Count > 30, $"{slices.Count} slices");
    }

    [AvaloniaFact]
    public async Task CopyAcrossTwoPagesJoinsThemLikeTheTextFlowDoes()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var first = await TextAsync(window, tab, 0);
        var second = await TextAsync(window, tab, 1);
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(1, second.Chars.Count));
        FocusDocument(window, view);

        Press(window, Key.C, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => clipboard.Calls.Count > 0);

        var call = Assert.Single(clipboard.Calls);
        var slices = TextFlow.SlicesOf(tab.TextSelection!.Value, page => page == 0 ? first : second);
        Assert.Equal(2, slices.Count);
        Assert.Equal(TextFlow.Plain(slices), call.Plain);
        Assert.Contains("Pagewing Handbuch", call.Plain, StringComparison.Ordinal);
        Assert.Contains("Seite zwei", call.Plain, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task CopyDoesNotBlockTheUiWhileThePagesAreRead()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        await SelectAsync(window, view, 0, 0, text.Chars.Count);
        FocusDocument(window, view);

        var gate = new TaskCompletionSource();
        var reads = 0;
        view.ReadText = async (texts, page, priority) =>
        {
            reads++;
            await gate.Task;
            return await texts.GetAsync(page, priority);
        };

        // Returns at once with the clipboard untouched; a second chord while the first runs is ignored.
        Press(window, Key.C, RawInputModifiers.Control);
        Press(window, Key.C, RawInputModifiers.Control);
        Assert.Empty(clipboard.Calls);
        Assert.Equal(1, reads);

        gate.SetResult();
        await WaitUntilAsync(window, () => clipboard.Calls.Count > 0);
        await Task.Delay(50);
        Settle(window);
        Assert.Single(clipboard.Calls);
    }

    [AvaloniaFact]
    public async Task AClipboardFailureDoesNotCrash()
    {
        var (window, viewModel, view, clipboard) = Show();
        var tab = await OpenAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        await SelectAsync(window, view, 0, 0, text.Chars.Count);
        view.ClipboardRetryDelay = TimeSpan.FromMilliseconds(1);

        // A clipboard that another program holds for a moment: the second try gets through.
        clipboard.FailTimes = 1;
        await view.CopySelectionAsync(raw: false);
        Assert.Equal(2, clipboard.Attempts);
        Assert.Single(clipboard.Calls);

        // One that stays locked: one more try, then the copy ends without an exception.
        clipboard.Attempts = 0;
        clipboard.Calls.Clear();
        clipboard.FailTimes = int.MaxValue;
        await view.CopySelectionAsync(raw: false);
        Assert.Equal(2, clipboard.Attempts);
        Assert.Empty(clipboard.Calls);

        // Other failures of the platform are swallowed the same way.
        clipboard.Attempts = 0;
        clipboard.Failure = new System.ComponentModel.Win32Exception(5);
        await view.CopySelectionAsync(raw: true);
        Assert.Equal(2, clipboard.Attempts);

        // The view still copies afterwards.
        clipboard.Failure = new IOException("Clipboard is locked.");
        clipboard.FailTimes = 0;
        await view.CopySelectionAsync(raw: true);
        Assert.Single(clipboard.Calls);
    }

    private static void FocusDocument(Window window, DocumentView view)
    {
        view.Focus();
        Settle(window);
        Assert.True(view.IsFocused);
    }

    /// <summary>Selects characters through the view; waits until the selection is there.</summary>
    private static async Task<TextSelection?> SelectAsync(Window window, DocumentView view, int page, int first, int last)
    {
        var task = view.SelectAsync(page, first, last);
        await WaitUntilAsync(window, () => task.IsCompleted);
        Settle(window);
        return await task;
    }

    /// <summary>Waits until the text of a page is there (read through the session, as the view does after the first sharp page).</summary>
    private static async Task<PageText> TextAsync(Window window, TabViewModel tab, int page)
    {
        var source = tab.Session.EnsurePageTexts() ?? throw new InvalidOperationException("No document.");
        var task = source.GetAsync(page);
        await WaitUntilAsync(window, () => task.IsCompleted);
        return await task;
    }

    private static void Press(Window window, Key key, RawInputModifiers modifiers)
    {
        var physical = key == Key.C ? PhysicalKey.C : PhysicalKey.F;
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Settle(window);
    }

    private (Window Window, MainViewModel ViewModel, DocumentView View, RecordingClipboard Clipboard) Show()
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        var view = Find<DocumentView>(window, "DocumentView");
        var clipboard = new RecordingClipboard();
        view.Clipboard = clipboard;
        return (window, viewModel, view, clipboard);
    }

    /// <summary>Opens a file in a new tab, waits until the view shows it and closes the panel the window opens by itself.</summary>
    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
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
                throw new TimeoutException("The view did not reach the expected state.");
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

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name} in the window.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    /// <summary>A clipboard that records what it is given and can fail a number of times first.</summary>
    private sealed class RecordingClipboard : IClipboardService
    {
        public List<(string Plain, string? Html)> Calls { get; } = [];

        public int Attempts { get; set; }

        public int FailTimes { get; set; }

        public Exception Failure { get; set; } = new IOException("Clipboard is locked.");

        public Task SetTextAsync(string plainText, string? htmlFragment)
        {
            Attempts++;
            if (Attempts <= FailTimes)
            {
                throw Failure;
            }

            Calls.Add((plainText, htmlFragment));
            return Task.CompletedTask;
        }
    }
}
