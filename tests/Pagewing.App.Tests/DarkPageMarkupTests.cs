using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Pagewing.App.Controls;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Markups on the dark page and in print (milestone 9, task 17): the dark render paints highlights, underlines and
/// strike-outs in their colours (<c>PdfiumDarkMarkups</c>, tested pixel by pixel in <c>DarkPageRenderTests</c>); here the
/// window shows it, follows edits and the theme, keeps search hits and the selection visible on top, and hands the printing
/// system the document in memory when it has changes that are not saved. Every file is a copy in a temp folder.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DarkPageMarkupTests : PrintTestHost
{
    private static readonly Color DarkGround = Color.FromRgb(0x1E, 0x21, 0x27);
    private static readonly Color DarkText = Color.FromRgb(0xE3, 0xE5, 0xE8);
    private static readonly Color Yellow = ToColor(AnnotationPalette.ColorOf(MarkupColor.Yellow));
    private static readonly Color Blue = ToColor(AnnotationPalette.ColorOf(MarkupColor.Blue));
    private static readonly AnnotationColor DrawingRed = new(220, 30, 30);

    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pagewing-darkmarkup-" + Guid.NewGuid().ToString("N"))).FullName;

    // Added first, so it runs last: after the windows closed and the documents let go of their files.
    public DarkPageMarkupTests() => AddCleanup(new FolderDeleter(_folder));

    [AvaloniaFact]
    public async Task TheOverlayIsDrawnOnlyOnDarkPages()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var quad = await MarkAsync(window, viewModel, view, tab, "annotate.highlight", 0, 5);

        // Light: the page as PDFium draws it, the highlight multiplied into the white page; the annotations are not read for
        // the dark page.
        await WaitUntilAsync(window, () => Count(window, view, quad, color => Near(color, Yellow, 2)) > 100);
        Assert.Equal(0, tab.Session.Document!.DarkMarkupScans);

        // Dark (Ctrl+Shift+D): the highlight is a yellow tint behind the light text, not the bare dark ground.
        tab.TogglePageThemeCommand.Execute(null);
        await WaitUntilAsync(window, () => view.Canvas.PageTheme == PageTheme.Dark && Count(window, view, quad, IsYellowTint) > 100);
        Assert.True(tab.Session.Document!.DarkMarkupScans > 0);
        var tint = MostCommon(window, view, quad, IsYellowTint);
        Assert.True(Wcag.Contrast(DarkText, tint) >= 4.5, $"page text on the tint {tint} is {Wcag.Contrast(DarkText, tint):F2}:1");
        Assert.Equal(0, Count(window, view, quad.Deflate(2), color => Near(color, DarkGround, 2)));
        Assert.True(Count(window, view, quad, color => Near(color, DarkText, 3)) > 20, "the text on the highlight is gone");
    }

    [AvaloniaFact]
    public async Task OverlayFollowsAnEditAndTheThemeSwitch()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        tab.TogglePageThemeCommand.Execute(null);
        await WaitUntilAsync(window, () => view.Canvas.PageTheme == PageTheme.Dark && view.Canvas.LastSharpDestinations.Count > 0);

        // An underline made on the dark page appears in its blue at once, where PDFium alone would draw a light line.
        var quad = await MarkAsync(window, viewModel, view, tab, "annotate.underline", 7, 15);
        var line = new Rect(quad.X, quad.Bottom - (quad.Height * 0.15), quad.Width, quad.Height * 0.15);
        await WaitUntilAsync(window, () => Count(window, view, line, color => Near(color, Blue, 2)) > 50);

        // Undo takes it away again.
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && Count(window, view, line, color => Near(color, Blue, 40)) == 0);

        // Redo on the light page: PDFium's own blue line; back to dark: still blue.
        tab.TogglePageThemeCommand.Execute(null);
        tab.RedoCommand.Execute(null);
        await WaitUntilAsync(window, () => view.Canvas.PageTheme == PageTheme.Light && !tab.HasEditsInFlight
            && Count(window, view, line, color => color.B - color.R > 60 && color.B > 200) > 50);
        tab.TogglePageThemeCommand.Execute(null);
        await WaitUntilAsync(window, () => view.Canvas.PageTheme == PageTheme.Dark && view.Canvas.LastSharpDestinations.Count > 0
            && Count(window, view, line, color => Near(color, Blue, 2)) > 50);
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SearchHitOnTopOfAMarkupIsStillVisible(string theme)
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        if (theme == "Dark")
        {
            tab.TogglePageThemeCommand.Execute(null);
        }

        // "Hello, Pagewing" highlighted as a whole; then "Pagewing" found and "Hello" selected on top of it.
        var quad = await MarkAsync(window, viewModel, view, tab, "annotate.highlight", 0, 15);
        bool IsMarkup(Color color) => theme == "Dark" ? IsYellowTint(color) : Near(color, Yellow, 2);
        await WaitUntilAsync(window, () => Count(window, view, quad, IsMarkup) > 200);
        var markup = MostCommon(window, view, quad, IsMarkup);
        tab.Search.Query = "Pagewing";
        await view.SelectAsync(0, 0, 5);
        await WaitUntilAsync(window, () => tab.Search.State == SearchState.Done
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent)
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.Selection));

        using var frame = FramePixels.Snapshot(view.Canvas);
        var hit = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent).Destination;
        var selection = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Kind == HighlightKind.Selection).Destination;

        // Neither shows the bare colour of the markup inside: each has its own colour over it.
        Assert.Equal(0, CountIn(frame, hit.Deflate(3), color => Near(color, markup, 6)));
        Assert.Equal(0, CountIn(frame, selection.Deflate(3), color => Near(color, markup, 6)));

        // The current hit keeps its frame: search.on on a light page, the page foreground on a dark one.
        var frameColor = theme == "Dark" ? DarkText : TokenTests.Token("Search.On", ThemeVariant.Light);
        var band = new Rect(hit.X + 2, hit.Y - 1, hit.Width - 4, 2);
        Assert.True(CountIn(frame, band, color => Near(color, frameColor, 40)) > hit.Width / 2, "no frame on the current hit");
        Assert.True(Wcag.Contrast(frameColor, markup) >= 3, $"the frame stands out only {Wcag.Contrast(frameColor, markup):F2}:1 from the markup");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SearchHitOnTopOfADrawingIsStillVisible(string theme)
    {
        // A red line of 10 pt through "Hello, Pagewing" (milestone 10): on the dark page it is painted in its colour like a
        // markup; the search hit and the selection the view draws on top of it stay visible, each with its own colour.
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        if (theme == "Dark")
        {
            tab.TogglePageThemeCommand.Execute(null);
        }

        var document = tab.Session.Document!;
        var text = await document.GetPageTextAsync(0, TestContext.Current.CancellationToken);
        var words = text.RectsOf(0, 15);
        var (left, right) = (words.Min(r => r.Left), words.Max(r => r.Right));
        var middle = (words.Min(r => r.Top) + words.Max(r => r.Bottom)) / 2;
        await document.CreateDrawingAsync(0, DrawingGeometry.Line(new DisplayPoint(left, middle), new DisplayPoint(right, middle), 10), DrawingRed, null, null, TestContext.Current.CancellationToken);
        tab.Session.InvalidatePages(new HashSet<int> { 0 });
        var red = ToColor(DrawingRed);
        var stroke = CanvasRect(view, new PageRect(left + 2, middle - 3, right - 2, middle + 3));
        await WaitUntilAsync(window, () => (theme == "Light" || view.Canvas.PageTheme == PageTheme.Dark) && Count(window, view, stroke, color => Near(color, red, 2)) > 200);

        tab.Search.Query = "Pagewing";
        await view.SelectAsync(0, 0, 5);
        await WaitUntilAsync(window, () => tab.Search.State == SearchState.Done
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent)
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.Selection));

        using var frame = FramePixels.Snapshot(view.Canvas);
        var hit = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent).Destination;
        var selection = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Kind == HighlightKind.Selection).Destination;
        Assert.True(hit.Intersects(stroke) && selection.Intersects(stroke), "the hit or the selection does not lie on the drawing");

        // Neither shows the bare colour of the drawing inside: each has its own colour over it.
        Assert.Equal(0, CountIn(frame, hit.Deflate(3), color => Near(color, red, 6)));
        Assert.Equal(0, CountIn(frame, selection.Deflate(3), color => Near(color, red, 6)));

        // The current hit keeps its frame: search.on on a light page, the page foreground on a dark one.
        var frameColor = theme == "Dark" ? DarkText : TokenTests.Token("Search.On", ThemeVariant.Light);
        var band = new Rect(hit.X + 2, hit.Y - 1, hit.Width - 4, 2);
        Assert.True(CountIn(frame, band, color => Near(color, frameColor, 40)) > hit.Width / 2, "no frame on the current hit");
    }

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public async Task SearchHitOnTopOfASignatureIsStillVisible(string theme)
    {
        // A drawn signature in red, a line of 10 pt through "Hello, Pagewing" (milestone 12): on the dark page it is painted in its colour like a
        // markup; the search hit and the selection the view draws on top of it stay visible, each with its own colour.
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        if (theme == "Dark")
        {
            tab.TogglePageThemeCommand.Execute(null);
        }

        var document = tab.Session.Document!;
        var text = await document.GetPageTextAsync(0, TestContext.Current.CancellationToken);
        var words = text.RectsOf(0, 15);
        var (left, right) = (words.Min(r => r.Left), words.Max(r => r.Right));
        var middle = (words.Min(r => r.Top) + words.Max(r => r.Bottom)) / 2;
        await document.CreateDrawingAsync(0, DrawingGeometry.Signature([[new DisplayPoint(left, middle), new DisplayPoint(right, middle)]], 10), DrawingRed, null, null, TestContext.Current.CancellationToken);
        tab.Session.InvalidatePages(new HashSet<int> { 0 });
        var red = ToColor(DrawingRed);
        var stroke = CanvasRect(view, new PageRect(left + 2, middle - 3, right - 2, middle + 3));
        await WaitUntilAsync(window, () => (theme == "Light" || view.Canvas.PageTheme == PageTheme.Dark) && Count(window, view, stroke, color => Near(color, red, 2)) > 200);

        tab.Search.Query = "Pagewing";
        await view.SelectAsync(0, 0, 5);
        await WaitUntilAsync(window, () => tab.Search.State == SearchState.Done
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent)
            && view.Canvas.LastHighlightDestinations.Any(entry => entry.Highlight.Kind == HighlightKind.Selection));

        using var frame = FramePixels.Snapshot(view.Canvas);
        var hit = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Kind == HighlightKind.SearchCurrent).Destination;
        var selection = view.Canvas.LastHighlightDestinations.First(entry => entry.Highlight.Kind == HighlightKind.Selection).Destination;
        Assert.True(hit.Intersects(stroke) && selection.Intersects(stroke), "the hit or the selection does not lie on the drawing");

        // Neither shows the bare colour of the drawing inside: each has its own colour over it.
        Assert.Equal(0, CountIn(frame, hit.Deflate(3), color => Near(color, red, 6)));
        Assert.Equal(0, CountIn(frame, selection.Deflate(3), color => Near(color, red, 6)));

        // The current hit keeps its frame: search.on on a light page, the page foreground on a dark one.
        var frameColor = theme == "Dark" ? DarkText : TokenTests.Token("Search.On", ThemeVariant.Light);
        var band = new Rect(hit.X + 2, hit.Y - 1, hit.Width - 4, 2);
        Assert.True(CountIn(frame, band, color => Near(color, frameColor, 40)) > hit.Width / 2, "no frame on the current hit");
    }

    [AvaloniaFact]
    public async Task PrintingUnsavedMarkupsHandsTheDocumentInMemory()
    {
        // Windows prints the document in memory page by page anyway; CUPS (Linux) gets a file. With changes that are not
        // saved that file must not be the one on disk (it lacks them): the job gets no path, so CUPS gets a copy written
        // from the document in memory (PrintSupportTests.DecryptedCopyIncludesUnsavedMarkups).
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await PrintAsync(window, viewModel);
        Assert.Equal(tab.FullPath, Service.Jobs[0].Path);

        await MarkAsync(window, viewModel, view, tab, "annotate.highlight", 0, 5);
        Assert.True(tab.IsDirty);
        await PrintAsync(window, viewModel);
        Assert.Null(Service.Jobs[1].Path);
        Assert.Same(tab.Session.Document, Service.Jobs[1].Document);

        // Saved, the file has the markup again: CUPS may read it.
        tab.SaveCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.IsDirty && !tab.IsSaving);
        await PrintAsync(window, viewModel);
        Assert.Equal(tab.FullPath, Service.Jobs[2].Path);
    }

    private static Color ToColor(AnnotationColor color) => Color.FromRgb(color.R, color.G, color.B);

    /// <summary>A yellow tint over the dark ground: clearly lighter than it, red and green well above blue.</summary>
    private static bool IsYellowTint(Color color) =>
        color.R > DarkGround.R + 30 && color.G > DarkGround.G + 30 && color.R - color.B > 40 && color.G - color.B > 40 && color.R < 0xC0;

    private static bool Near(Color color, Color expected, int tolerance) =>
        Math.Abs(color.R - expected.R) <= tolerance && Math.Abs(color.G - expected.G) <= tolerance && Math.Abs(color.B - expected.B) <= tolerance;

    /// <summary>Counts the pixels of a rectangle of page 1 (canvas DIPs, see <see cref="CanvasRect"/>) that match.</summary>
    private static int Count(Window window, DocumentView view, Rect area, Func<Color, bool> predicate)
    {
        Settle(window);
        using var frame = FramePixels.Snapshot(view.Canvas);
        return CountIn(frame, area, predicate);
    }

    private static int CountIn(FramePixels frame, Rect area, Func<Color, bool> predicate)
    {
        var count = 0;
        for (var y = Math.Max(0, (int)Math.Floor(area.Y)); y < Math.Min(frame.Height, (int)Math.Ceiling(area.Bottom)); y++)
        {
            for (var x = Math.Max(0, (int)Math.Floor(area.X)); x < Math.Min(frame.Width, (int)Math.Ceiling(area.Right)); x++)
            {
                if (predicate(frame.GetColor(x, y)))
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static Color MostCommon(Window window, DocumentView view, Rect area, Func<Color, bool> predicate)
    {
        Settle(window);
        using var frame = FramePixels.Snapshot(view.Canvas);
        var counts = new Dictionary<Color, int>();
        for (var y = (int)Math.Floor(area.Y); y < (int)Math.Ceiling(area.Bottom); y++)
        {
            for (var x = (int)Math.Floor(area.X); x < (int)Math.Ceiling(area.Right); x++)
            {
                var color = frame.GetColor(x, y);
                if (predicate(color))
                {
                    counts[color] = counts.GetValueOrDefault(color) + 1;
                }
            }
        }

        return counts.MaxBy(pair => pair.Value).Key;
    }

    /// <summary>A rectangle of page 1 (PDF points from its top left) in canvas DIPs.</summary>
    private static Rect CanvasRect(DocumentView view, PageRect rect)
    {
        var layout = view.Layout!;
        var page = layout.GetPageRect(0);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        return new Rect(
            page.X - view.Canvas.Viewport.X + (rect.Left * scale),
            page.Y - view.Canvas.Viewport.Y + (rect.Top * scale),
            rect.Width * scale,
            rect.Height * scale);
    }

    /// <summary>
    /// Selects characters <paramref name="first"/> to <paramref name="last"/> of page 1 and marks them with a command of the
    /// mode (it starts); returns the markup's rectangle in canvas DIPs once it is recorded.
    /// </summary>
    private static async Task<Rect> MarkAsync(Window window, MainViewModel viewModel, DocumentView view, TabViewModel tab, string command, int first, int last)
    {
        var select = view.SelectAsync(0, first, last);
        await WaitUntilAsync(window, () => select.IsCompleted);
        Assert.NotNull(await select);
        var before = (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Count;
        viewModel.Commands.ById(command)!.Execute();
        PdfAnnotation? created = null;
        await WaitUntilAsync(window, () =>
        {
            if (tab.HasEditsInFlight || !tab.IsDirty || tab.Session.Document is not { } document)
            {
                return false;
            }

            var read = document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken);
            if (!read.Wait(TimeSpan.FromSeconds(5)) || read.Result.Count <= before)
            {
                return false;
            }

            created = read.Result[^1];
            return true;
        });
        return CanvasRect(view, created!.QuadRects[0]);
    }

    private static async Task PrintAsync(Window window, MainViewModel viewModel)
    {
        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.Print is { SelectedPrinter: not null } && viewModel.Overlay == OverlayKind.Print);
        var before = viewModel.PrintJob;
        viewModel.Print!.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PrintJob is { IsRunning: false } job && !ReferenceEquals(job, before));
    }

    private async Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

    private sealed class FolderDeleter(string folder) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A document may still hold a file for a moment; the system cleans the temp folder later.
            }
        }
    }
}
