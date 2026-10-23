using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DocumentInfoTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];

    public DocumentInfoTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
        // Windows close before their view models (and the sessions of their tabs) are disposed.
        for (var i = _cleanup.Count - 1; i >= 0; i--)
        {
            _cleanup[i].Dispose();
        }

        Strings.Culture = _originalCulture;
    }

    [AvaloniaFact]
    public async Task InfoButtonOpensThePopupWithTheFileData()
    {
        var path = WritePdf(
            [(595, 842), (842, 595)],
            "/Title (Jahresbericht 2026) /Author (Erika Mustermann) /CreationDate (D:20260315093000+01'00')");
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, path);
        var bar = Find<StatusBar>(window, "StatusBar");
        var button = Find<Button>(window, "DocumentInfoButton");

        // Right in the status bar, right of the layout button, with the icon, the label, the tooltip and the name.
        var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), bar)!.Value.X;
        Assert.True(button.IsEffectivelyVisible);
        Assert.True(button.TranslatePoint(default, window)!.Value.X > Find<Button>(window, "LayoutButton").TranslatePoint(default, window)!.Value.X);
        Assert.Equal(bar.Bounds.Width - 8, right, 1.0);
        Assert.Same(Resource("Pw.Icon.InfoCircle"), Find<StrokeIcon>(window, "DocumentInfoIcon").Data);
        Assert.Equal("Document info", Find<TextBlock>(window, "DocumentInfoText").Text);
        Assert.Equal("Document info", ToolTip.GetTip(button) as string);
        Assert.Equal("Document info", AutomationProperties.GetName(button));

        // A click opens the popup above the status bar.
        Assert.Null(bar.InfoPopup);
        Click(window, button);
        var popup = Assert.IsType<Popup>(bar.InfoPopup);
        Assert.True(popup.IsOpen);
        var content = Assert.IsType<DocumentInfoPopup>(popup.Child);
        await WaitUntilAsync(window, () => Labels(content).Contains("Created"));
        var length = new FileInfo(path).Length;
        Assert.Equal(
            [
                ("File", Path.GetFileName(path)),
                ("Folder", Path.GetDirectoryName(path)!),
                ("Pages", "2"),
                ("Page size", "A4 · 210 × 297 mm"),
                ("PDF version", "1.4"),
                ("File size", DocumentInfoViewModel.FormatFileSize(length, new CultureInfo("en"))),
                ("Title", "Jahresbericht 2026"),
                ("Author", "Erika Mustermann"),
                ("Created", "3/15/2026"),
            ],
            ShownRows(content));
        Assert.Equal(1, tab.CurrentPage);
        Assert.True(content.PointToScreen(new Point(0, content.Bounds.Height)).Y <= button.PointToScreen(default).Y, "the popup lies above the button");

        // The card: radius.overlay, surface.raised, 1 px border.subtle; labels text.secondary, values text.primary.
        var card = Find<Border>(content, "Card");
        Assert.Equal(new CornerRadius(10), card.CornerRadius);
        Assert.Same(Resource("Pw.Brush.Surface.Raised"), card.Background);
        Assert.Same(Resource("Pw.Brush.Border.Subtle"), card.BorderBrush);
        Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.All(Texts(content, "InfoLabel"), label => Assert.Same(Resource("Pw.Brush.Text.Secondary"), label.Foreground));
        Assert.All(Texts(content, "InfoValue"), value => Assert.Same(Resource("Pw.Brush.Text.Primary"), value.Foreground));
        Assert.Equal("Document info", AutomationProperties.GetName(content));

        // German texts and the short date of the culture.
        popup.IsOpen = false;
        Settle(window);
        Strings.Culture = new CultureInfo("de");
        Click(window, button);
        content = Assert.IsType<DocumentInfoPopup>(popup.Child);
        await WaitUntilAsync(window, () => Labels(content).Contains("Erstellt"));
        Assert.Equal(
            ["Datei", "Ordner", "Seiten", "Seitenformat", "PDF-Version", "Dateigröße", "Titel", "Autor", "Erstellt"],
            Labels(content));
        Assert.Equal("15.03.2026", ShownRows(content)[^1].Value);
        Assert.Equal(DocumentInfoViewModel.FormatFileSize(length, new CultureInfo("de")), ShownRows(content)[5].Value);
        popup.IsOpen = false;
        Settle(window);
    }

    [AvaloniaFact]
    public async Task PopupShowsTheFormatOfTheCurrentPage()
    {
        var path = WritePdf([(595, 842), (842, 595), (612, 792)], null);
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, path);
        var bar = Find<StatusBar>(window, "StatusBar");
        Click(window, Find<Button>(window, "DocumentInfoButton"));
        var content = Assert.IsType<DocumentInfoPopup>(bar.InfoPopup!.Child);
        Assert.Equal("A4 · 210 × 297 mm", Value(content, "Page size"));

        // Another current page changes the format while the popup is open.
        tab.GoToPage(2);
        Settle(window);
        Assert.True(bar.InfoPopup.IsOpen);
        Assert.Equal("A4 · 297 × 210 mm", Value(content, "Page size"));
        tab.GoToPage(3);
        Settle(window);
        Assert.Equal("Letter · 216 × 279 mm", Value(content, "Page size"));
        bar.InfoPopup.IsOpen = false;
        Settle(window);

        // wide-page.pdf is wider than high and no paper format: its millimetres in landscape.
        await OpenTabAsync(window, viewModel, view, "wide-page.pdf");
        Click(window, Find<Button>(window, "DocumentInfoButton"));
        content = Assert.IsType<DocumentInfoPopup>(bar.InfoPopup.Child);
        Assert.Equal("212 × 42 mm", Value(content, "Page size"));
        bar.InfoPopup.IsOpen = false;
        Settle(window);
    }

    [AvaloniaFact]
    public async Task MissingMetadataIsLeftOut()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "hello_world.pdf");
        var bar = Find<StatusBar>(window, "StatusBar");
        var button = Find<Button>(window, "DocumentInfoButton");

        // No title, author or creation date: their lines do not appear, not even empty.
        Click(window, button);
        var content = Assert.IsType<DocumentInfoPopup>(bar.InfoPopup!.Child);
        var info = Assert.IsType<DocumentInfoViewModel>(content.DataContext);
        await WaitUntilAsync(window, () => info.IsLoaded);
        Assert.Equal(["File", "Folder", "Pages", "Page size", "PDF version", "File size"], Labels(content));
        Assert.DoesNotContain(ShownRows(content), row => string.IsNullOrWhiteSpace(row.Value));
        bar.InfoPopup.IsOpen = false;
        Settle(window);

        // A read that fails shows what is known without it, and nothing is thrown into the UI.
        tab.InfoReader = (_, _, _) => Task.FromException<DocumentInfo>(new InvalidDataException("damaged"));
        Click(window, button);
        content = Assert.IsType<DocumentInfoPopup>(bar.InfoPopup.Child);
        info = Assert.IsType<DocumentInfoViewModel>(content.DataContext);
        await WaitUntilAsync(window, () => info.IsLoaded);
        Assert.True(bar.InfoPopup.IsOpen);
        Assert.Equal(["File", "Folder", "Pages", "Page size", "PDF version"], Labels(content));
        Assert.Equal("hello_world.pdf", Value(content, "File"));
        bar.InfoPopup.IsOpen = false;
        Settle(window);
    }

    [AvaloniaFact]
    public async Task InfoIsNotReadBeforeThePopupOpens()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var bar = Find<StatusBar>(window, "StatusBar");
        var reads = 0;
        tab.InfoReader = (document, path, cancellationToken) =>
        {
            reads++;
            return DocumentInfoReader.ReadAsync(document, path, cancellationToken);
        };

        // Opening another document in the tab reads nothing, and neither popup nor its content exist.
        await tab.OpenAsync(TestData.Pdf("outline.pdf"));
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && tab.HasShownSharpPage);
        Settle(window);
        Assert.Equal(0, reads);
        Assert.Null(bar.InfoPopup);
        Assert.Empty(bar.GetLogicalDescendants().OfType<Popup>());
        Assert.Empty(window.GetVisualDescendants().OfType<DocumentInfoPopup>());

        // The popup reads it when it opens, and again each time it opens.
        Click(window, Find<Button>(window, "DocumentInfoButton"));
        var content = Assert.IsType<DocumentInfoPopup>(bar.InfoPopup!.Child);
        await WaitUntilAsync(window, () => Labels(content).Contains("Title"));
        Assert.Equal(1, reads);
        Assert.Equal("Pagewing Lesezeichen", Value(content, "Title"));
        bar.InfoPopup.IsOpen = false;
        Settle(window);
        Assert.Null(bar.InfoPopup.Child);
        Click(window, Find<Button>(window, "DocumentInfoButton"));
        await WaitUntilAsync(window, () => reads == 2);
        bar.InfoPopup.IsOpen = false;
        Settle(window);
    }

    [AvaloniaFact]
    public async Task EscapeClosesThePopupAndReturnsTheFocus()
    {
        var (window, viewModel, view) = Show();
        await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var bar = Find<StatusBar>(window, "StatusBar");
        var button = Find<Button>(window, "DocumentInfoButton");

        // By keyboard: Enter on the button opens the popup and moves the focus into it.
        button.Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter);
        var popup = bar.InfoPopup!;
        Assert.True(popup.IsOpen);
        var content = Assert.IsType<DocumentInfoPopup>(popup.Child);
        Assert.True(content.IsKeyboardFocusWithin, "the focus is in the popup");
        Press(window, Key.Tab, PhysicalKey.Tab);
        Assert.True(popup.IsOpen);
        Assert.True(content.IsKeyboardFocusWithin, "Tab keeps the focus in the popup");

        // Esc closes it, and the focus is back on the button.
        Press(window, Key.Escape, PhysicalKey.Escape);
        Assert.False(popup.IsOpen);
        Assert.Null(popup.Child);
        Assert.True(button.IsFocused);

        // A click beside the popup closes it as well, with the focus back on the button.
        Click(window, button);
        Assert.True(popup.IsOpen);
        var canvas = view.Canvas;
        var outside = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 3), window)!.Value;
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Settle(window);
        Assert.False(popup.IsOpen);
        Assert.True(button.IsFocused);

        // A click on the button of the open popup only closes it.
        Click(window, button);
        Assert.True(popup.IsOpen);
        Click(window, button);
        Assert.False(popup.IsOpen);
    }

    [Fact]
    public void FileSizeIsFormattedForTheCulture()
    {
        var en = new CultureInfo("en");
        var de = new CultureInfo("de");

        // Below 1 KB in bytes; then KB, MB and GB of 1024, one decimal below 10, whole numbers from 10 on.
        Assert.Equal("0 B", DocumentInfoViewModel.FormatFileSize(0, en));
        Assert.Equal("1023 B", DocumentInfoViewModel.FormatFileSize(1023, en));
        Assert.Equal("1 KB", DocumentInfoViewModel.FormatFileSize(1024, en));
        Assert.Equal("1.5 KB", DocumentInfoViewModel.FormatFileSize(1536, en));
        Assert.Equal("1,5 KB", DocumentInfoViewModel.FormatFileSize(1536, de));
        Assert.Equal("9.9 KB", DocumentInfoViewModel.FormatFileSize(10_188, en));
        Assert.Equal("10 KB", DocumentInfoViewModel.FormatFileSize(10_200, en));
        Assert.Equal("152 KB", DocumentInfoViewModel.FormatFileSize(156_000, en));
        Assert.Equal("1022 KB", DocumentInfoViewModel.FormatFileSize(1_047_000, de));

        // A value that would round to 1024 moves on to the next unit.
        Assert.Equal("1 MB", DocumentInfoViewModel.FormatFileSize(1_048_575, en));
        Assert.Equal("2.4 MB", DocumentInfoViewModel.FormatFileSize(2_500_000, en));
        Assert.Equal("2,4 MB", DocumentInfoViewModel.FormatFileSize(2_500_000, de));
        Assert.Equal("48 MB", DocumentInfoViewModel.FormatFileSize(50_000_000, de));
        Assert.Equal("3.5 GB", DocumentInfoViewModel.FormatFileSize(3_758_096_384, en));
        Assert.Equal("2048 GB", DocumentInfoViewModel.FormatFileSize(2_199_023_255_552, en));
    }

    [AvaloniaFact]
    public async Task PopupShowsNothingForALoadingOrFailedTab()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var bar = Find<StatusBar>(window, "StatusBar");
        var button = Find<Button>(window, "DocumentInfoButton");

        // A file that fails to open closes the open popup; the button goes with the status bar.
        Click(window, button);
        var popup = bar.InfoPopup!;
        Assert.True(popup.IsOpen);
        await tab.OpenAsync(TestData.Pdf("not-a-pdf.pdf"));
        Settle(window);
        Assert.True(tab.IsFailed);
        Assert.False(popup.IsOpen);
        Assert.Null(popup.Child);
        Assert.False(button.IsEffectivelyVisible);
        bar.OpenDocumentInfo();
        Settle(window);
        Assert.False(popup.IsOpen);

        // A result that arrives after the document changed is dropped.
        await tab.OpenAsync(TestData.Pdf("outline.pdf"));
        Settle(window);
        var pending = new TaskCompletionSource<DocumentInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.InfoReader = (_, _, _) => pending.Task;
        using (var info = new DocumentInfoViewModel(tab))
        {
            var load = info.LoadAsync();
            await tab.OpenAsync(TestData.Pdf("text-basic.pdf"));
            Settle(window);
            pending.SetResult(new DocumentInfo("x.pdf", "y", 9, "1.7", 1, "Stale", "Stale", null));
            await load;
            Assert.DoesNotContain(info.Rows, row => row.Value == "Stale");
            Assert.False(info.IsLoaded);
        }

        // While a file loads the button is not there, and the popup does not open.
        var never = new TaskCompletionSource<PdfDocument>();
        _cleanup.Add(new Canceller(never));
        var (loadingWindow, loadingViewModel, _) = Show(() => new ViewerSession((_, _, _) => never.Task));
        _ = loadingViewModel.OpenAsync(TestData.Pdf("text-basic.pdf"));
        Settle(loadingWindow);
        var loadingTab = loadingViewModel.ActiveTab!;
        Assert.Equal(ViewerState.Loading, loadingTab.State);
        var loadingBar = Find<StatusBar>(loadingWindow, "StatusBar");
        // The status bar was never shown: its controls are in its name scope, not yet in the visual tree.
        Assert.False(loadingBar.FindControl<Button>("DocumentInfoButton")!.IsEffectivelyVisible);
        loadingBar.OpenDocumentInfo();
        Settle(loadingWindow);
        Assert.Null(loadingBar.InfoPopup);
    }

    [AvaloniaFact]
    public async Task LabelDisappearsBelow760Pixels()
    {
        var (window, viewModel, view) = Show();
        await OpenTabAsync(window, viewModel, view, "text-basic.pdf");
        var button = Find<Button>(window, "DocumentInfoButton");
        var text = Find<TextBlock>(window, "DocumentInfoText");
        var icon = Find<StrokeIcon>(window, "DocumentInfoIcon");
        Assert.True(text.IsEffectivelyVisible);

        window.Width = 760;
        Settle(window);
        Assert.True(text.IsEffectivelyVisible);

        // Below 760 px only the icon stays, with its tooltip and name, still at the right edge.
        window.Width = 700;
        Settle(window);
        var bar = Find<StatusBar>(window, "StatusBar");
        Assert.False(text.IsEffectivelyVisible);
        Assert.True(icon.IsEffectivelyVisible);
        Assert.Equal("Document info", ToolTip.GetTip(button) as string);
        Assert.Equal("Document info", AutomationProperties.GetName(button));
        Assert.Equal(bar.Bounds.Width - 8, button.TranslatePoint(new Point(button.Bounds.Width, 0), bar)!.Value.X, 1.0);

        window.Width = 900;
        Settle(window);
        Assert.True(text.IsEffectivelyVisible);
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(Func<ViewerSession>? sessions = null, double width = 1024)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(sessions ?? (() => new ViewerSession(new PdfEngine())), new NoFilePicker()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = width, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
    }

    /// <summary>Opens a test file (a name in testdata, or a full path) in a new tab and waits until the view shows it.</summary>
    private static async Task<TabViewModel> OpenTabAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(Path.IsPathRooted(name) ? name : TestData.Pdf(name));
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);

        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        return tab;
    }

    /// <summary>
    /// Writes an uncompressed PDF 1.4 with one empty page per size (points) and the given entries of the information
    /// dictionary (none for <see langword="null"/>) to a temporary file, deleted after the test.
    /// </summary>
    private string WritePdf(IReadOnlyList<(int Width, int Height)> sizes, string? info)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', sizes.Select((_, i) => $"{3 + i} 0 R"))}] /Count {sizes.Count} >>",
        };
        foreach (var (width, height) in sizes)
        {
            objects.Add(FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] >>"));
        }

        if (info is not null)
        {
            objects.Add($"<< {info} >>");
        }

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        var infoEntry = info is not null ? $" /Info {objects.Count} 0 R" : string.Empty;
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{infoEntry} >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(Path.GetTempPath(), $"pagewing-info-{Guid.NewGuid():N}.pdf");
        File.WriteAllText(path, pdf.ToString(), Encoding.ASCII);

        // Disposed last: after the windows and sessions that have the file open.
        _cleanup.Insert(0, new FileDeleter(path));
        return path;
    }

    /// <summary>The lines the popup shows, label and value, in their order on screen.</summary>
    private static List<(string Label, string Value)> ShownRows(DocumentInfoPopup content)
    {
        (TopLevel.GetTopLevel(content) as Layoutable)?.UpdateLayout();
        var labels = Texts(content, "InfoLabel");
        var values = Texts(content, "InfoValue");
        Assert.Equal(labels.Count, values.Count);
        return labels.Zip(values, (label, value) => (label.Text ?? string.Empty, value.Text ?? string.Empty)).ToList();
    }

    private static List<string> Labels(DocumentInfoPopup content) => ShownRows(content).Select(row => row.Label).ToList();

    private static string? Value(DocumentInfoPopup content, string label) =>
        ShownRows(content).Where(row => row.Label == label).Select(row => row.Value).SingleOrDefault();

    private static List<TextBlock> Texts(Visual root, string name) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(text => text.Name == name).ToList();

    private static object? Resource(string key) => Application.Current!.FindResource(ThemeVariant.Light, key);

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey)
    {
        window.KeyPress(key, RawInputModifiers.None, physicalKey, null);
        window.KeyRelease(key, RawInputModifiers.None, physicalKey, null);
        Settle(window);
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

    private static T Find<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().SingleOrDefault(control => control.Name == name)
        ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    private sealed class Canceller(TaskCompletionSource<PdfDocument> source) : IDisposable
    {
        public void Dispose() => source.TrySetCanceled();
    }

    private sealed class FileDeleter(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Still open somewhere: the temporary folder keeps it.
            }
        }
    }
}
