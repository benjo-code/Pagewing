using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
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
/// Drawing with the mouse and the pen (milestone 10, task 11): the gesture of a drawing tool on one page, its preview on
/// the drawing layer, the annotation made on release, Shift to snap, what discards a stroke (Esc, a lost capture, another
/// tab, tool or document) and what a finger does (it scrolls). Every file is a copy in a temp folder; settings are in memory.
/// </summary>
/// <remarks>
/// The headless platform raises mouse and touch input (<see cref="HeadlessWindowExtensions.TouchBegin"/>) but has no pen:
/// <see cref="PenInput"/> hands raw pen events of an Avalonia <see cref="PenDevice"/> to the window's input, as a platform does.
/// </remarks>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DrawingInputTests : IDisposable
{
    private const RawInputModifiers Left = RawInputModifiers.LeftMouseButton;
    private const RawInputModifiers LeftShift = RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly AnnotationKind[] DrawingKinds =
        [AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-drawing-" + Guid.NewGuid().ToString("N"))).FullName;

    public DrawingInputTests() => Strings.Culture = new CultureInfo("en");

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
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A document may still hold a file for a moment; the system cleans the temp folder later.
        }
    }

    [AvaloniaFact]
    public async Task DragWithInkMakesOneAnnotationOnRelease()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { AuthorName = "Ada" });
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // While dragging the stroke is only on the drawing layer: PDFium does nothing before the release.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        foreach (var point in new[] { new Point(130, 310), new Point(160, 330), new Point(200, 320) })
        {
            window.MouseMove(ScreenPoint(view, window, 0, point), Left);
            Settle(window);
        }

        Assert.True(view.IsDrawing);
        Assert.True(view.DrawingOverlay!.HasPreview);
        Assert.False(tab.HasEditor);
        Assert.Empty(await DrawingsAsync(tab, 0));

        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 320)), MouseButton.Left);
        Settle(window);

        // One freehand annotation of one stroke, from the first to the last point, in the style of the tool, by Ada.
        var ink = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Ink, ink.Kind);
        Assert.Equal("Ada", ink.Author);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Red), ink.Color);
        var drawing = ink.Drawing!;
        Assert.Equal(StrokeWidths.PointsOf(StrokeWidth.Medium), drawing.Width, 3);
        var stroke = Assert.Single(drawing.Strokes);
        AssertNear(new DisplayPoint(100, 300), stroke[0], 0.5);
        AssertNear(new DisplayPoint(200, 320), stroke[^1], 0.5);
        Assert.InRange(drawing.Box.Bottom, 320, 330.5);

        // The preview is gone, the tab has a change, the tool stays on.
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay.HasPreview);
        await WaitUntilAsync(window, () => tab.IsDirty && tab.CanUndo);
        Assert.True(tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Ink, tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task ClickWithoutDragMakesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // A click with the pen tool, and with the rectangle a press that moves less than a click may.
        var point = ScreenPoint(view, window, 0, new Point(120, 200));
        Click(window, point);
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);
        window.MouseDown(point, MouseButton.Left);
        Settle(window);
        window.MouseMove(point + new Point(2, 1), Left);
        Settle(window);
        window.MouseUp(point + new Point(2, 1), MouseButton.Left);
        Settle(window);

        Assert.False(view.IsDrawing);
        Assert.False(tab.HasEditsInFlight);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task EscDuringTheStrokeDiscardsIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(160, 330)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);

        // Esc ends the stroke, not the mode; the rest of the drag draws nothing.
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay!.HasPreview);
        Assert.True(tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Ink, tab.ActiveTool);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(200, 340)), Left);
        Settle(window);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 340)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);

        // Nothing is stuck: the next drag draws.
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 200)), ScreenPoint(view, window, 0, new Point(180, 220)));
        Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
    }

    [AvaloniaFact]
    public async Task SecondEscLeavesTheMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Rectangle);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(200, 380)), Left);
        Settle(window);

        // The first Esc takes the shape, the second the mode (as without a stroke).
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsDrawing);
        Assert.True(tab.IsAnnotating);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(tab.IsAnnotating);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 380)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditor);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task CaptureLostDiscardsIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        IPointer? pointer = null;
        view.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, handledEventsToo: true);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(160, 330)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        Assert.Same(view, pointer!.Captured);

        // Something else takes the pointer (a menu, the system): the stroke goes.
        pointer.Capture(null);
        Settle(window);
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay!.HasPreview);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(200, 340)), Left);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 340)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditor);

        Drag(window, ScreenPoint(view, window, 0, new Point(100, 200)), ScreenPoint(view, window, 0, new Point(180, 220)));
        Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
    }

    [AvaloniaFact]
    public async Task TabSwitchDiscardsIt()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var second = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        viewModel.Tabs.Activate(first);
        Settle(window);
        await BeginAsync(window, first, AnnotationKind.Ink);

        // Another tab in the middle of a stroke: nothing on either.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(160, 330)), Left);
        Settle(window);
        viewModel.Tabs.Activate(second);
        Settle(window);
        Assert.False(view.IsDrawing);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 340)), MouseButton.Left);
        Settle(window);
        Assert.False(first.HasEditor);
        Assert.False(second.HasEditor);

        // Another tool in the middle of a stroke: nothing either.
        viewModel.Tabs.Activate(first);
        Settle(window);
        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(160, 330)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        first.SetToolCommand.Execute(AnnotationKind.Rectangle);
        Settle(window);
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay!.HasPreview);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 340)), MouseButton.Left);
        Settle(window);
        Assert.False(first.HasEditor);

        // Back to drawing: one shape.
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 200)), ScreenPoint(view, window, 0, new Point(180, 260)));
        Assert.Equal(AnnotationKind.Rectangle, Assert.Single(await WaitForDrawingsAsync(window, first, 0, count: 1)).Kind);
    }

    [AvaloniaFact]
    public async Task TouchDoesNotDrawButScrolls()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        var before = view.ScrollViewer.Offset.Y;

        // A finger drags upwards over the page: the document scrolls, nothing is drawn, nothing is selected.
        var start = ScreenPoint(view, window, 0, new Point(150, 350));
        var finger = window.TouchBegin(start);
        Settle(window);
        Assert.False(view.IsDrawing);
        for (var step = 1; step <= 6; step++)
        {
            window.TouchMove(finger, start - new Point(0, 30 * step));
            Settle(window);
        }

        window.TouchEnd(finger, start - new Point(0, 180));
        Settle(window);

        Assert.True(view.ScrollViewer.Offset.Y > before + 50, $"scrolled from {before} to {view.ScrollViewer.Offset.Y}");
        Assert.Null(view.DrawingOverlay);
        Assert.Null(tab.TextSelection);
        Assert.False(tab.HasEditor);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task PenDraws()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        var pen = new PenInput(window);
        var before = view.ScrollViewer.Offset;

        // Upwards, as a finger would scroll: the pen draws, the document stays where it is.
        pen.Down(ScreenPoint(view, window, 0, new Point(100, 300)));
        pen.Move(ScreenPoint(view, window, 0, new Point(140, 250)));
        pen.Move(ScreenPoint(view, window, 0, new Point(180, 200)));
        Assert.True(view.IsDrawing);
        pen.Up(ScreenPoint(view, window, 0, new Point(220, 150)));

        var ink = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Ink, ink.Kind);
        AssertNear(new DisplayPoint(100, 300), ink.Drawing!.Strokes[0][0], 0.5);
        AssertNear(new DisplayPoint(220, 150), ink.Drawing.Strokes[0][^1], 0.5);
        Assert.False(view.IsDrawing);
        Assert.Equal(before, view.ScrollViewer.Offset);
    }

    [AvaloniaFact]
    public async Task ShiftSnapsALineTo45Degrees()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Line);

        // 31 degrees with Shift: a line at 45 degrees of the same length.
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 300)), ScreenPoint(view, window, 0, new Point(200, 360)), LeftShift);
        var line = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Line, line.Kind);
        Assert.True(ArrowGeometry.TryRecognize(line.Drawing!.Strokes, out var start, out var end, out var head));
        Assert.False(head);
        AssertNear(new DisplayPoint(100, 300), start, 0.5);
        var length = Math.Sqrt((100 * 100) + (60 * 60));
        AssertNear(new DisplayPoint(100 + (length / Math.Sqrt(2)), 300 + (length / Math.Sqrt(2))), end, 0.5);

        // Without Shift an arrow points where the pointer was released.
        tab.SetToolCommand.Execute(AnnotationKind.Arrow);
        Drag(window, ScreenPoint(view, window, 0, new Point(300, 150)), ScreenPoint(view, window, 0, new Point(400, 170)));
        var arrow = (await WaitForDrawingsAsync(window, tab, 0, count: 2)).Single(annotation => annotation.Kind == AnnotationKind.Arrow);
        Assert.True(ArrowGeometry.TryRecognize(arrow.Drawing!.Strokes, out start, out end, out head));
        Assert.True(head);
        AssertNear(new DisplayPoint(300, 150), start, 0.5);
        AssertNear(new DisplayPoint(400, 170), end, 0.5);
    }

    [AvaloniaFact]
    public async Task ShiftMakesASquare()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Rectangle);

        Drag(window, ScreenPoint(view, window, 0, new Point(100, 100)), ScreenPoint(view, window, 0, new Point(200, 140)), LeftShift);
        var square = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Rectangle, square.Kind);
        AssertRectNear(new PageRect(100, 100, 200, 200), square.Drawing!.Box, 0.5);

        // The oval with Shift is a circle; without Shift the box of the drag.
        tab.SetToolCommand.Execute(AnnotationKind.Ellipse);
        Drag(window, ScreenPoint(view, window, 0, new Point(300, 100)), ScreenPoint(view, window, 0, new Point(340, 180)), LeftShift);
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 250)), ScreenPoint(view, window, 0, new Point(200, 290)));
        var ellipses = (await WaitForDrawingsAsync(window, tab, 0, count: 3)).Where(annotation => annotation.Kind == AnnotationKind.Ellipse).ToArray();
        Assert.Contains(ellipses, e => RectNear(new PageRect(300, 100, 380, 180), e.Drawing!.Box, 0.5));
        Assert.Contains(ellipses, e => RectNear(new PageRect(100, 250, 200, 290), e.Drawing!.Box, 0.5));
    }

    [AvaloniaFact]
    public async Task StrokeIsOnThePageUnderThePointerOnARotatedPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "rotated-page.pdf");
        await BeginAsync(window, tab, AnnotationKind.Rectangle);
        var size = view.Layout!.GetPageSize(0);
        Assert.True(size.Height > size.Width, "the page is shown upright (100 x 200)");

        // Where the pointer was on the page as shown, also after reading it back in PDF space.
        Drag(window, ScreenPoint(view, window, 0, new Point(20, 40)), ScreenPoint(view, window, 0, new Point(80, 150)));
        var shape = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        AssertRectNear(new PageRect(20, 40, 80, 150), shape.Drawing!.Box, 0.5);

        // A drag that leaves the page keeps drawing on it, up to its edge.
        Drag(window, ScreenPoint(view, window, 0, new Point(50, 120)), ScreenPoint(view, window, 0, new Point(size.Width + 60, size.Height + 60)));
        var shapes = await WaitForDrawingsAsync(window, tab, 0, count: 2);
        Assert.Contains(shapes, s => RectNear(new PageRect(50, 120, size.Width, size.Height), s.Drawing!.Box, 0.5));
    }

    [AvaloniaFact]
    public async Task NothingIsCreatedBeforeTheFirstSharpPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // As before the first sharp page of a document: a drag makes no gesture, no layer and no annotation.
        tab.HasShownSharpPage = false;
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 300)), ScreenPoint(view, window, 0, new Point(200, 340)));
        Assert.False(view.IsDrawing);
        Assert.Null(view.DrawingOverlay);
        Assert.False(tab.HasEditor);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task NothingHappensWhileTheEditIsNotRunnable()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // While the tab saves, and while a card of the save flow asks about it: a press starts nothing.
        tab.IsSaving = true;
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 300)), ScreenPoint(view, window, 0, new Point(200, 340)));
        Assert.False(view.IsDrawing);
        Assert.Null(view.DrawingOverlay);
        tab.IsSaving = false;
        tab.IsAskedAbout = true;
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 300)), ScreenPoint(view, window, 0, new Point(200, 340)));
        Assert.False(view.IsDrawing);
        Assert.Null(view.DrawingOverlay);
        Assert.False(tab.HasEditor);

        // A card that comes in the middle of a stroke takes it.
        tab.IsAskedAbout = false;
        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(160, 330)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        tab.IsAskedAbout = true;
        Settle(window);
        Assert.False(view.IsDrawing);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 340)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditor);
        tab.IsAskedAbout = false;

        // Free again: a drag draws.
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 200)), ScreenPoint(view, window, 0, new Point(180, 220)));
        Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
    }

    [AvaloniaFact]
    public async Task TextSelectionDoesNotStartWithADrawingTool()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var texts = tab.Session.EnsurePageTexts()!;
        var read = texts.GetAsync(0);
        await WaitUntilAsync(window, () => read.IsCompleted);
        var text = await read;
        await BeginAsync(window, tab, AnnotationKind.Rectangle);

        // Over the text: the crosshair, and a drag draws instead of selecting.
        var from = ScreenPoint(view, window, 0, new Point(text.Chars[0].Box.Left + 1, text.Chars[0].Box.Top + 1));
        var to = ScreenPoint(view, window, 0, new Point(text.Chars[7].Box.Right - 1, text.Chars[7].Box.Bottom - 1));
        window.MouseMove(from);
        Settle(window);
        Assert.Same(view.DrawCursor, view.ScrollViewer.Cursor);
        Drag(window, from, to);
        Assert.Null(tab.TextSelection);
        Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));

        // Without a drawing tool the same drag selects again, with the I-beam.
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);
        Assert.Null(tab.ActiveTool);
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        window.MouseMove(from);
        Settle(window);
        Assert.NotSame(view.DrawCursor, view.ScrollViewer.Cursor);
        Drag(window, from, to);
        Assert.NotNull(tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task TheLimitIsReportedAndTheGestureEnds()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenAsync(window, viewModel, view, WriteFullPdf());
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // The document lists as many annotations as Pagewing reads: the stroke is not added, and a hint says why.
        Drag(window, ScreenPoint(view, window, 0, new Point(40, 40)), ScreenPoint(view, window, 0, new Point(200, 120)));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        var hint = await WaitForHintAsync(window);
        Assert.Equal(string.Format(Strings.Culture, CompositeFormat.Parse(Strings.DrawingLimitReached), PdfDocument.MaxAnnotations), ((TextBlock)hint.Child!).Text);
        Assert.Contains("10,000", ((TextBlock)hint.Child!).Text, StringComparison.Ordinal);
        Assert.False(tab.IsDirty);
        Assert.False(tab.CanUndo);
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay!.HasPreview);

        // The next press starts a stroke of its own.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(40, 200)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(120, 220)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        Assert.True(view.DrawingOverlay.HasPreview);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(120, 220)), MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task TheModeStartsNoOverlayWork()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        // Reading, then the mode with a drawing tool: no drawing layer yet.
        Assert.Null(view.DrawingOverlay);
        await BeginAsync(window, tab, AnnotationKind.Ink);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 300)));
        Settle(window);
        Assert.Null(view.DrawingOverlay);
        Assert.False(tab.HasEditor);

        // The first press makes it, once.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(150, 300)), MouseButton.Left);
        Settle(window);
        var overlay = view.DrawingOverlay;
        Assert.NotNull(overlay);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(150, 300)), MouseButton.Left);
        Settle(window);
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 200)), ScreenPoint(view, window, 0, new Point(180, 220)));
        Assert.Same(overlay, view.DrawingOverlay);
    }

    [AvaloniaFact]
    public async Task OverlayInvalidatesOnlyTheStrokeRectangle()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        var zoom = view.Layout!.Zoom * DocumentLayout.DipsPerPoint;
        var pen = Math.Max(1, StrokeWidths.PointsOf(StrokeWidth.Medium) * zoom);
        var margin = (pen / 2) + 2;

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(120, 310)), Left);
        Settle(window);
        var overlay = view.DrawingOverlay!;
        var count = overlay.InvalidationCount;

        // A move to a new point: the rectangle of the stroke so far and the new segment, not the whole layer.
        window.MouseMove(ScreenPoint(view, window, 0, new Point(140, 300)), Left);
        Settle(window);
        Assert.True(overlay.InvalidationCount > count);
        var expected = CanvasRect(view, 0, new PageRect(100, 300, 140, 310)).Inflate(margin);
        AssertRectNear(expected, overlay.LastInvalidatedRect, 0.5);
        Assert.True(overlay.LastInvalidatedRect.Width * overlay.LastInvalidatedRect.Height < overlay.Bounds.Width * overlay.Bounds.Height / 50);

        // The pixels of the stroke are on the layer in the colour of the tool, the rest of the layer is transparent.
        using (var pixels = FramePixels.Snapshot(overlay))
        {
            var onStroke = CanvasPoint(view, 0, new Point(110, 305));
            var color = pixels.GetColor((int)onStroke.X, (int)onStroke.Y);
            Assert.True(IsStrokeRed(color), $"stroke pixel {color}");
        }

        window.MouseUp(ScreenPoint(view, window, 0, new Point(140, 300)), MouseButton.Left);
        Settle(window);
        await WaitForDrawingsAsync(window, tab, 0, count: 1);
    }

    [AvaloniaFact]
    public async Task PreviewIsClippedToTheView()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        // Zoomed in and scrolled into the page, so the page covers the whole view, also its rounded top left corner.
        tab.Zoom = 2;
        Settle(window);
        view.ScrollViewer.Offset = new Vector(300, 400);
        Settle(window);
        await BeginAsync(window, tab, AnnotationKind.Ink);
        var canvas = view.Canvas;
        var corner = canvas.TranslatePoint(default, window)!.Value;
        var bottom = canvas.TranslatePoint(new Point(0, canvas.Bounds.Height), window)!.Value.Y;
        var centre = canvas.TranslatePoint(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2), window)!.Value;
        Assert.True(canvas.CornerRadius.TopLeft >= 8, "the view has a rounded top left corner");
        Assert.True(canvas.PagePointAt(new Point(1, 1)) is { } hit && hit.Position.X > 0 && hit.Position.Y > 0, "the page lies under the corner");

        // From the middle of the view into its corner: the stroke is drawn up to the rounded edge, not into the corner.
        var into = corner + new Point(1, 1);
        window.MouseDown(centre, MouseButton.Left);
        Settle(window);
        window.MouseMove(centre + ((into - centre) / 2), Left);
        Settle(window);
        window.MouseMove(into, Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        using (var frame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            var scaling = window.RenderScaling;
            Avalonia.Media.Color At(Point point) => frame.GetColor((int)(point.X * scaling), (int)(point.Y * scaling));
            var inside = At(corner + new Point(30, 30 * (centre.Y - corner.Y) / (centre.X - corner.X)));
            Assert.True(IsStrokeRed(inside), $"inside the view {inside}");
            Assert.False(IsStrokeRed(At(into)), $"in the rounded corner {At(into)}");
        }

        // On down past the bottom of the view (the page goes on below it): nothing beyond the view is redrawn or painted.
        var below = new Point(centre.X, window.Bounds.Height - 2);
        window.MouseMove(below, Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        var overlay = view.DrawingOverlay!;
        Assert.True(overlay.LastInvalidatedRect.Bottom <= overlay.Bounds.Height + 0.01, $"{overlay.LastInvalidatedRect} in {overlay.Bounds}");
        Assert.True(overlay.LastInvalidatedRect.Top >= -0.01 && overlay.LastInvalidatedRect.Left >= -0.01, $"{overlay.LastInvalidatedRect}");
        using (var frame = new FramePixels(window.CaptureRenderedFrame()!))
        {
            var scaling = window.RenderScaling;
            foreach (var y in new[] { bottom + 3, below.Y })
            {
                var outside = frame.GetColor((int)(below.X * scaling), (int)(y * scaling));
                Assert.False(IsStrokeRed(outside), $"below the view at {y}: {outside}");
            }
        }

        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        window.MouseUp(below, MouseButton.Left);
        Settle(window);
    }

    [AvaloniaFact]
    public async Task ScrollDuringTheDragKeepsTheAnchorOnThePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 250)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 280)), Left);
        Settle(window);
        var before = view.DrawingOverlay!.LastInvalidatedRect;
        var offset = view.ScrollViewer.Offset;

        // The view scrolls by 100 DIPs while the button is held (the keys, the scroll bar, a jump): the stroke moves with
        // its page, its points stay where they were on the page.
        view.ScrollViewer.Offset = offset + new Vector(0, 100);
        Settle(window);
        Assert.Equal(offset.Y + 100, view.ScrollViewer.Offset.Y, 3);
        Assert.True(view.IsDrawing);
        var after = view.DrawingOverlay.LastInvalidatedRect;
        Assert.Equal(before.Top - 100, after.Top, 1);
        Assert.Equal(before.Left, after.Left, 1);

        window.MouseMove(ScreenPoint(view, window, 0, new Point(200, 300)), Left);
        Settle(window);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 300)), MouseButton.Left);
        Settle(window);
        var stroke = Assert.Single(Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1)).Drawing!.Strokes);
        AssertNear(new DisplayPoint(100, 250), stroke[0], 0.5);
        AssertNear(new DisplayPoint(200, 300), stroke[^1], 0.5);
    }

    [AvaloniaFact]
    public async Task ZoomDuringTheDragKeepsTheAnchorOnThePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Line);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 200)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 220)), Left);
        Settle(window);
        var before = view.DrawingOverlay!.LastInvalidatedRect;

        // Zoomed in while the button is held (Ctrl+Plus, the status bar): the shape grows with its page.
        var zoom = tab.Zoom;
        tab.ZoomInCommand.Execute(null);
        Settle(window);
        Assert.True(tab.Zoom > zoom);
        Assert.True(view.IsDrawing);
        var after = view.DrawingOverlay.LastInvalidatedRect;
        var expected = CanvasRect(view, 0, new PageRect(100, 200, 150, 220));
        Assert.True(after.Contains(expected), $"{expected} in {after}");
        Assert.True(after.Width > before.Width * tab.Zoom / zoom * 0.9, $"{before} to {after}");

        window.MouseMove(ScreenPoint(view, window, 0, new Point(200, 240)), Left);
        Settle(window);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 240)), MouseButton.Left);
        Settle(window);
        var line = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        Assert.True(ArrowGeometry.TryRecognize(line.Drawing!.Strokes, out var start, out var end, out _));
        AssertNear(new DisplayPoint(100, 200), start, 0.5);
        AssertNear(new DisplayPoint(200, 240), end, 0.5);
    }

    [AvaloniaFact]
    public async Task WindowDeactivationDiscardsTheStroke()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        window.Activate();
        await WaitUntilAsync(window, () => window.IsActive);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 250)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 280)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);

        // The window loses the activation in the middle of a stroke (Alt+Tab, minimised): the stroke goes. The headless
        // platform reports a deactivation only when a window is hidden (activating another window does not deactivate it).
        window.Hide();
        await WaitUntilAsync(window, () => !window.IsActive);
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay!.HasPreview);
        window.Show();
        window.Activate();
        await WaitUntilAsync(window, () => window.IsActive);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 300)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditor);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task LayoutChangeDiscardsTheStroke()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Rectangle);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 250)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(150, 280)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);

        // Two pages side by side in the middle of a stroke: the page moves under it, the stroke goes.
        tab.SetLayoutModeCommand.Execute(PageLayoutMode.TwoPages);
        Settle(window);
        Assert.False(view.IsDrawing);
        Assert.False(view.DrawingOverlay!.HasPreview);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(160, 290)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasEditor);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task ReleaseOfAZigZagStrokeDoesNotBlockTheUiThread()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // The worst case of the thinning (the amplitude grows with every point, so every round of Douglas-Peucker peels one
        // point): 20,000 points, the most a stroke collects. On the UI thread this took over a second (DECISIONS, "Zeichnen:
        // Messwerte (M10)"); on release the view only hands the points to the tab.
        var origin = ScreenPoint(view, window, 0, new Point(0, 0));
        var scale = (ScreenPoint(view, window, 0, new Point(100, 0)).X - origin.X) / 100;
        static DisplayPoint PagePoint(int i) => new(20 + (i * 0.02), 250 + ((i % 2 == 0 ? 1 : -1) * (1 + (i * 0.005))));
        Point Screen(int i) => origin + new Point(PagePoint(i).X * scale, PagePoint(i).Y * scale);
        window.MouseDown(Screen(0), MouseButton.Left);
        Settle(window);
        window.MouseMove(Screen(1), Left);
        Settle(window);
        Assert.True(view.IsDrawing);

        // The points go straight into the preview (20,000 pointer events through the headless window take minutes); the
        // release adds the last one.
        var preview = view.StrokePreview!;
        for (var i = 2; i < DocumentView.MaxStrokePoints - 1; i++)
        {
            preview.Add(PagePoint(i));
        }

        Assert.Equal(DocumentView.MaxStrokePoints - 1, preview.Points.Count);

        // No clock (it would flake under load): the thinning waits on a gate, so the release can only return, and the UI
        // thread settle, while it is still running or not yet started, and it must run on a pool thread.
        var uiThread = Environment.CurrentManagedThreadId;
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        var reducedOn = 0;
        var onPool = false;
        var reduced = false;
        tab.FreehandReducer = points =>
        {
            reducedOn = Environment.CurrentManagedThreadId;
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            entered.Set();
            gate.Wait(Timeout);
            var result = TabViewModel.ReduceFreehand(points);
            Volatile.Write(ref reduced, true);
            return result;
        };

        window.MouseUp(Screen(DocumentView.MaxStrokePoints - 1), MouseButton.Left);
        Settle(window);
        Assert.False(view.IsDrawing);
        Assert.False(Volatile.Read(ref reduced));
        await WaitUntilAsync(window, () => entered.IsSet);
        Assert.NotEqual(uiThread, reducedOn);
        Assert.True(onPool);
        Assert.False(Volatile.Read(ref reduced));
        gate.Set();

        // The stroke is made all the same, in the background.
        var ink = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Ink, ink.Kind);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task FreehandReductionRunsOffTheUiThread()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        var uiThread = Environment.CurrentManagedThreadId;
        int? reducedOn = null;
        var onPool = false;
        tab.FreehandReducer = points =>
        {
            reducedOn = Environment.CurrentManagedThreadId;
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            return TabViewModel.ReduceFreehand(points);
        };

        await tab.ApplyFreehandAsync(0, [new DisplayPoint(100, 300), new DisplayPoint(130, 310), new DisplayPoint(160, 330), new DisplayPoint(200, 320)]);

        Assert.NotNull(reducedOn);
        Assert.NotEqual(uiThread, reducedOn);
        Assert.True(onPool);
        var ink = Assert.Single(await WaitForDrawingsAsync(window, tab, 0, count: 1));

        // The width is the style's: the one place a drawing gets it from.
        Assert.Equal(StrokeWidths.PointsOf(StrokeWidth.Medium), ink.Drawing!.Width, 3);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Red), ink.Color);
    }

    [AvaloniaFact]
    public async Task TwoQuickStrokesAreAppliedInReleaseOrder()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);

        // The first stroke is slow to reduce (it waits for the gate), the second and a shape released after them are not.
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            if (points.Count > 3)
            {
                entered.Set();
                gate.Wait(Timeout);
            }

            return TabViewModel.ReduceFreehand(points);
        };

        var first = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100), new DisplayPoint(140, 140)]);
        await WaitUntilAsync(window, () => entered.IsSet);
        var second = tab.ApplyFreehandAsync(0, [new DisplayPoint(300, 100), new DisplayPoint(330, 140), new DisplayPoint(360, 100)]);
        var third = tab.ApplyDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(300, 300, 400, 360), 2));

        // Nothing overtook the first stroke: the editor has not been asked for anything yet, but the tab has a change.
        for (var i = 0; i < 20; i++)
        {
            Settle(window);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(tab.HasEditor);
        Assert.True(tab.AsksBeforeClosing);

        gate.Set();
        await Task.WhenAll(first, second, third);
        var drawings = await WaitForDrawingsAsync(window, tab, 0, count: 3);
        Assert.Equal([AnnotationKind.Ink, AnnotationKind.Ink, AnnotationKind.Rectangle], drawings.Select(d => d.Kind));
        AssertRectNear(new PageRect(50, 100, 140, 140), drawings[0].Drawing!.Box, 0.5);
        AssertRectNear(new PageRect(300, 100, 360, 130), drawings[1].Drawing!.Box, 0.5);

        // The history has them in the same order: undo takes the rectangle first.
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        Assert.Equal(2, (await WaitForDrawingsAsync(window, tab, 0, count: 2)).Length);
        Assert.DoesNotContain(await DrawingsAsync(tab, 0), d => d.Kind == AnnotationKind.Rectangle);
    }

    [AvaloniaFact]
    public async Task StrokeIsDroppedWhenTheDocumentChangesBeforeTheReductionFinishes()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            entered.Set();
            gate.Wait(Timeout);
            return TabViewModel.ReduceFreehand(points);
        };

        var stroke = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100)]);
        await WaitUntilAsync(window, () => entered.IsSet);

        // Another file in the tab while the points are still being reduced: the stroke belongs to the old one and goes without
        // an error, a hint or a change in the history of the new one.
        var hints = new List<string>();
        tab.DrawingRefused += hints.Add;
        await tab.OpenAsync(Copy("text-basic.pdf", "other.pdf"));
        await WaitUntilAsync(window, () => tab.Session.Document is not null && tab.HasShownSharpPage);
        gate.Set();
        await stroke;
        Settle(window);

        Assert.Empty(hints);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.False(tab.CanUndo);
        Assert.False(tab.HasEditsInFlight);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task StrokeIsDroppedWhenTheTabIsDisposedBeforeTheReductionFinishes()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var other = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            entered.Set();
            gate.Wait(Timeout);
            return TabViewModel.ReduceFreehand(points);
        };

        var stroke = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100)]);
        await WaitUntilAsync(window, () => entered.IsSet);
        viewModel.Tabs.Activate(other);
        Settle(window);
        tab.Dispose();
        gate.Set();

        // It ends without an exception, and nothing is asked of the editor of a tab that is gone.
        await stroke;
        Settle(window);
        Assert.False(tab.HasEditor);
    }

    [AvaloniaFact]
    public async Task EmptyFreehandStrokeStillCreatesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        var hints = new List<string>();
        tab.DrawingRefused += hints.Add;

        // One point, and points on one spot: no stroke, no history, no hint.
        await tab.ApplyFreehandAsync(0, [new DisplayPoint(100, 100)]);
        await tab.ApplyFreehandAsync(0, [new DisplayPoint(100, 100), new DisplayPoint(100, 100), new DisplayPoint(100, 100)]);
        Settle(window);

        Assert.Empty(hints);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.False(tab.CanUndo);
        Assert.False(tab.AsksBeforeClosing);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task UndoRightAfterAReleaseRemovesThatStroke()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        using var gate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            gate.Wait(Timeout);
            return TabViewModel.ReduceFreehand(points);
        };

        // The history is empty and the stroke is still being reduced: Ctrl+Z is possible, and it waits for the stroke.
        var stroke = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100)]);
        Assert.False(tab.CanUndo);
        Assert.True(tab.UndoCommand.CanExecute(null));
        tab.UndoCommand.Execute(null);
        Settle(window);
        Assert.False(tab.HasEditor);

        gate.Set();
        await stroke;
        await WaitUntilAsync(window, () => tab.EditVersion >= 2 && !tab.HasEditsInFlight);

        // The stroke was made and then undone: nothing on the page, redo brings it back.
        Assert.Empty(await DrawingsAsync(tab, 0));
        Assert.True(tab.CanRedo);
        Assert.False(tab.CanUndo);
        Assert.False(tab.UndoCommand.CanExecute(null));

        // With a change before it, Ctrl+Z takes the stroke and leaves the change before.
        tab.FreehandReducer = null;
        await tab.ApplyFreehandAsync(0, [new DisplayPoint(300, 300), new DisplayPoint(330, 340), new DisplayPoint(360, 300)]);
        using var secondGate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            secondGate.Wait(Timeout);
            return TabViewModel.ReduceFreehand(points);
        };
        var version = tab.EditVersion;
        var second = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100)]);
        tab.UndoCommand.Execute(null);
        secondGate.Set();
        await second;
        await WaitUntilAsync(window, () => tab.EditVersion >= version + 2 && !tab.HasEditsInFlight);
        var left = Assert.Single(await DrawingsAsync(tab, 0));
        AssertRectNear(new PageRect(300, 300, 360, 330), left.Drawing!.Box, 0.5);
        Assert.True(tab.CanRedo);
        Assert.True(tab.CanUndo);
    }

    [AvaloniaFact]
    public async Task RedoWaitsForTheStrokesInFlightToo()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        using var gate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            gate.Wait(Timeout);
            return TabViewModel.ReduceFreehand(points);
        };

        // Undo and redo pressed while the stroke is in flight: the stroke is made, undone and made again, in that order.
        var stroke = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100)]);
        Assert.True(tab.RedoCommand.CanExecute(null));
        tab.UndoCommand.Execute(null);
        tab.RedoCommand.Execute(null);
        Settle(window);
        Assert.False(tab.HasEditor);

        gate.Set();
        await stroke;
        await WaitUntilAsync(window, () => tab.EditVersion >= 3 && !tab.HasEditsInFlight);
        Assert.Single(await DrawingsAsync(tab, 0));
        Assert.True(tab.CanUndo);
        Assert.False(tab.CanRedo);
    }

    [AvaloniaFact]
    public async Task AFailedReductionDoesNotLetLaterStrokesOvertakeEarlierOnes()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Ink);
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        tab.FreehandReducer = points =>
        {
            if (points[0].X < 100)
            {
                entered.Set();
                gate.Wait(Timeout);
            }
            else if (points[0].X < 250)
            {
                throw new InvalidOperationException("The reduction of the second stroke failed.");
            }

            return TabViewModel.ReduceFreehand(points);
        };
        var hints = new List<string>();
        tab.DrawingRefused += hints.Add;

        // A is held, B fails at once, C is fine: neither the hint of B nor C comes before A.
        var a = tab.ApplyFreehandAsync(0, [new DisplayPoint(50, 100), new DisplayPoint(80, 140), new DisplayPoint(110, 100)]);
        await WaitUntilAsync(window, () => entered.IsSet);
        var b = tab.ApplyFreehandAsync(0, [new DisplayPoint(200, 100), new DisplayPoint(230, 140), new DisplayPoint(260, 100)]);
        var c = tab.ApplyFreehandAsync(0, [new DisplayPoint(300, 100), new DisplayPoint(330, 140), new DisplayPoint(360, 100)]);
        for (var i = 0; i < 20; i++)
        {
            Settle(window);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.False(tab.HasEditor);
        Assert.Empty(hints);

        gate.Set();
        await Task.WhenAll(a, b, c);
        var drawings = await WaitForDrawingsAsync(window, tab, 0, count: 2);
        Assert.True(drawings[0].Drawing!.Box.Left < 100 && drawings[1].Drawing!.Box.Left > 250, "A is made before C");
        Assert.Equal([Strings.DrawingFailed], hints);
    }

    private static bool IsStrokeRed(Avalonia.Media.Color color) => color.R > 150 && color.G < 100 && color.B < 100;

    private static void AssertNear(DisplayPoint expected, DisplayPoint actual, double tolerance)
    {
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance, $"{expected} vs {actual}");
    }

    private static bool RectNear(PageRect expected, PageRect actual, double tolerance) =>
        Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
        && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance;

    private static void AssertRectNear(PageRect expected, PageRect actual, double tolerance) =>
        Assert.True(RectNear(expected, actual, tolerance), $"{expected} vs {actual}");

    private static void AssertRectNear(Rect expected, Rect actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance
            && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance,
            $"{expected} vs {actual}");

    /// <summary>Starts the mode of the tab through its command and chooses <paramref name="tool"/>; waits until the toolbar shows.</summary>
    private static async Task BeginAsync(Window window, TabViewModel tab, AnnotationKind tool)
    {
        if (!tab.IsAnnotating)
        {
            tab.BeginAnnotateCommand.Execute(null);
            await WaitUntilAsync(window, () => tab.IsAnnotating);
        }

        tab.SetToolCommand.Execute(tool);
        Assert.Equal(tool, tab.ActiveTool);
        Settle(window);
    }

    /// <summary>The drawings of a page as the document has them now.</summary>
    private static async Task<PdfAnnotation[]> DrawingsAsync(TabViewModel tab, int page) =>
        (await tab.Session.Document!.GetAnnotationsAsync(page, TestContext.Current.CancellationToken))
            .Where(annotation => DrawingKinds.Contains(annotation.Kind))
            .ToArray();

    /// <summary>Waits until a page has <paramref name="count"/> drawings and returns them.</summary>
    private static async Task<PdfAnnotation[]> WaitForDrawingsAsync(Window window, TabViewModel tab, int page, int count)
    {
        PdfAnnotation[] found = [];
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (!tab.HasEditsInFlight && tab.Session.Document is not null)
            {
                found = await DrawingsAsync(tab, page);
                if (found.Length == count)
                {
                    return found;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Page {page} has {found.Length} drawings, not {count}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Border> WaitForHintAsync(Window window)
    {
        Border? hint = null;
        await WaitUntilAsync(window, () => (hint = FindOrNull<Border>(window, "NoTextHint")) is { IsVisible: true });
        return hint!;
    }

    /// <summary>The window point of a point on a page (PDF points from the top left of the page as displayed).</summary>
    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint) =>
        view.Canvas.TranslatePoint(CanvasPoint(view, page, pagePoint), window)!.Value;

    /// <summary>The canvas (viewport) point of a point on a page.</summary>
    private static Point CanvasPoint(DocumentView view, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        return new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
    }

    private static Rect CanvasRect(DocumentView view, int page, PageRect rect) =>
        new(CanvasPoint(view, page, new Point(rect.Left, rect.Top)), CanvasPoint(view, page, new Point(rect.Right, rect.Bottom)));

    private static void Click(Window window, Point point)
    {
        Settle(window);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Settle(window);
    }

    private static void Drag(Window window, Point from, Point to, RawInputModifiers held = Left)
    {
        var modifiers = held & ~RawInputModifiers.LeftMouseButton;
        window.MouseDown(from, MouseButton.Left, modifiers);
        Settle(window);
        window.MouseMove(from + ((to - from) / 2), held);
        Settle(window);
        window.MouseMove(to, held);
        Settle(window);
        window.MouseUp(to, MouseButton.Left, modifiers);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
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

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private string Copy(string fixture, string? name = null)
    {
        var path = System.IO.Path.Combine(_folder, name ?? fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    /// <summary>A page of 300 x 300 points that lists as many annotations (notes) as Pagewing reads, in the temp folder.</summary>
    private string WriteFullPdf()
    {
        var notes = string.Concat(Enumerable.Repeat("<< /Subtype /Text /Rect [280 280 290 290] /F 2 >> ", PdfDocument.MaxAnnotations));
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [{notes}] >>",
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = pdf.Length;
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = System.IO.Path.Combine(_folder, "full.pdf");
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(pdf.ToString()));
        return path;
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings? settings = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, FindOrNull<DocumentView>(window, "DocumentView")!);
    }

    /// <summary>Opens a copy of a test file (in the temp folder) in a new tab and waits for its first sharp page.</summary>
    private Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name) =>
        OpenAsync(window, viewModel, view, Copy(name, Guid.NewGuid().ToString("N")[..8] + "-" + name));

    private static async Task<TabViewModel> OpenAsync(Window window, MainViewModel viewModel, DocumentView view, string path)
    {
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No active tab after opening.");
        await WaitUntilAsync(window, () => ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        return tab;
    }

    private sealed class Closer(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    /// <summary>
    /// A pen on the headless window: raw pen events of an Avalonia <see cref="PenDevice"/>, handed to the input of the window's
    /// platform implementation as a platform with a pen does (the headless platform has mouse and touch only). The pen device,
    /// the raw event and the input of the platform are private API of Avalonia (internal in its reference assemblies, public
    /// at run time), so they are reached by reflection; a renamed member makes this throw instead of passing silently.
    /// </summary>
    private sealed class PenInput(Window window)
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly IInputDevice _device = (IInputDevice)(Activator.CreateInstance(typeof(PenDevice), Members, null, [false], null)
            ?? throw new InvalidOperationException("No pen device."));

        private ulong _timestamp = 1_000;

        public void Down(Point point) => Raise(RawPointerEventType.LeftButtonDown, point, RawInputModifiers.None);

        public void Move(Point point) => Raise(RawPointerEventType.Move, point, RawInputModifiers.LeftMouseButton);

        public void Up(Point point) => Raise(RawPointerEventType.LeftButtonUp, point, RawInputModifiers.None);

        private void Raise(RawPointerEventType type, Point point, RawInputModifiers modifiers)
        {
            var impl = window.PlatformImpl ?? throw new InvalidOperationException("The window is closed.");
            var root = impl.GetType().GetProperty("InputRoot", Members)?.GetValue(impl) ?? throw new InvalidOperationException("No input root.");
            var input = (Action<RawInputEventArgs>)(impl.GetType().GetProperty("Input", Members)?.GetValue(impl) ?? throw new InvalidOperationException("No input."));
            var constructor = typeof(RawPointerEventArgs).GetConstructor(
                Members,
                [typeof(IInputDevice), typeof(ulong), typeof(IInputRoot), typeof(RawPointerEventType), typeof(Point), typeof(RawInputModifiers)])
                ?? throw new InvalidOperationException("No constructor of the raw pointer event.");
            _timestamp += 16;
            input((RawInputEventArgs)constructor.Invoke([_device, _timestamp, root, type, point, modifiers]));
            Settle(window);
        }
    }
}
