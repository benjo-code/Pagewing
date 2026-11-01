using System.Globalization;
using System.Reflection;
using System.Resources;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Controls;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Marking on the page in the mode "Redact" (milestone 14, task 12a): dragging out an area with the mouse and the pen, the
/// handles and the keys, one step of the history for a drag and for a run of keys, Tab that never traps, Esc in order, the text
/// selection that becomes a mark, the drawing of the marks and their colours, the toolbar, the names for screen readers, the reason
/// shown when the mode does not start, and that the layer is the app's own and never part of a page. Every file is a copy in a temp
/// folder; settings are in memory.
/// </summary>
/// <remarks>
/// The headless platform raises mouse and touch input but has no pen: <see cref="PenInput"/> hands raw pen events of an Avalonia
/// <see cref="PenDevice"/> to the window's input, as a platform does (as in <see cref="DrawingInputTests"/>).
/// </remarks>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class RedactMarkingTests : IDisposable
{
    private const RawInputModifiers Left = RawInputModifiers.LeftMouseButton;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-redactmark-" + Guid.NewGuid().ToString("N"))).FullName;

    public RedactMarkingTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task DragCreatesAnAreaMarkAndUndoRemovesIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var version = tab.RedactionHistory!.Version;

        // While dragging the rectangle is only on the layer: the plan has nothing before the release.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(160, 330)), Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(220, 360)), Left);
        Settle(window);
        Assert.True(view.IsDraggingRedaction);
        var drag = Assert.IsType<RedactionDragView>(view.RedactionOverlay!.Drag);
        Assert.Null(drag.MarkId);
        AssertRectNear(new PageRect(100, 300, 220, 360), drag.Rect, 0.6);
        Assert.False(tab.HasPendingRedactions);

        window.MouseUp(ScreenPoint(view, window, 0, new Point(220, 360)), MouseButton.Left);
        Settle(window);

        // One area mark, one step, selected; the layer shows the plan; nothing of the document or the editor is touched.
        var mark = Assert.Single(tab.RedactionPlan!.Marks);
        Assert.Equal(RedactionKind.Area, mark.Kind);
        Assert.Equal(0, mark.Page);
        AssertRectNear(new PageRect(100, 300, 220, 360), Assert.Single(mark.Areas), 0.6);
        Assert.Equal(version + 1, tab.RedactionHistory.Version);
        Assert.Equal([mark.Id], tab.SelectedMarks);
        Assert.Same(tab.RedactionPlan, view.RedactionOverlay.Plan);
        Assert.False(view.IsDraggingRedaction);
        Assert.Null(view.RedactionOverlay.Drag);
        Assert.False(tab.HasEditor);

        // A drag from the lower right to the upper left is the same kind of rectangle.
        Drag(window, ScreenPoint(view, window, 0, new Point(400, 250)), ScreenPoint(view, window, 0, new Point(300, 170)));
        Assert.Equal(2, tab.RedactionPlan.Marks.Count);
        AssertRectNear(new PageRect(300, 170, 400, 250), Assert.Single(tab.RedactionPlan.Marks[1].Areas), 0.6);

        // A click on empty page makes nothing and clears the selection of marks.
        Click(window, ScreenPoint(view, window, 0, new Point(50, 200)));
        Assert.Equal(2, tab.RedactionPlan.Marks.Count);
        Assert.Empty(tab.SelectedMarks);

        // Undo takes the newest mark away, again and again; the layer follows.
        tab.UndoCommand.Execute(null);
        Settle(window);
        Assert.Equal(mark.Id, Assert.Single(tab.RedactionPlan.Marks).Id);
        Assert.Same(tab.RedactionPlan, view.RedactionOverlay.Plan);
        tab.UndoCommand.Execute(null);
        Settle(window);
        Assert.True(tab.RedactionPlan.IsEmpty);
        Assert.True(view.RedactionOverlay.Plan!.IsEmpty);
        tab.RedoCommand.Execute(null);
        Assert.Single(tab.RedactionPlan.Marks);
        Assert.False(tab.HasEditor);
    }

    [AvaloniaFact]
    public async Task PenDrawsAMarkAndAFingerScrolls()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var before = view.ScrollViewer.Offset;

        // The pen drags out an area, upwards as a finger would scroll; the document stays where it is.
        var pen = new PenInput(window);
        pen.Down(ScreenPoint(view, window, 0, new Point(200, 300)));
        pen.Move(ScreenPoint(view, window, 0, new Point(160, 250)));
        Assert.True(view.IsDraggingRedaction);
        pen.Up(ScreenPoint(view, window, 0, new Point(100, 200)));
        var mark = Assert.Single(tab.RedactionPlan!.Marks);
        AssertRectNear(new PageRect(100, 200, 200, 300), Assert.Single(mark.Areas), 0.6);
        Assert.Equal(before, view.ScrollViewer.Offset);

        // A finger on the page scrolls; it marks nothing and selects nothing.
        var start = ScreenPoint(view, window, 0, new Point(150, 350));
        var finger = window.TouchBegin(start);
        Settle(window);
        Assert.False(view.IsDraggingRedaction);
        for (var step = 1; step <= 6; step++)
        {
            window.TouchMove(finger, start - new Point(0, 30 * step));
            Settle(window);
        }

        window.TouchEnd(finger, start - new Point(0, 180));
        Settle(window);
        Assert.True(view.ScrollViewer.Offset.Y > before.Y + 50, $"scrolled from {before.Y} to {view.ScrollViewer.Offset.Y}");
        Assert.Single(tab.RedactionPlan.Marks);
        Assert.Null(tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task SelectionBecomesATextMarkWithItsTerm()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        tab.RedactionTool = RedactionTool.Text;
        var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
        var all = text.GetText(0, text.Chars.Count);
        Assert.False(tab.MarkSelectionCommand.CanExecute(null));
        Assert.False(viewModel.Commands.ById("redact.markSelection")!.CanExecute());

        // A word: a Text mark with the word as its term and a rectangle for its line; the selection goes.
        var first = all.IndexOf("Pagewing", StringComparison.Ordinal);
        Assert.True(first >= 0, "the test file has the word");
        tab.TextSelection = new TextSelection(new TextPosition(0, first), new TextPosition(0, first + "Pagewing".Length));
        Assert.True(tab.MarkSelectionCommand.CanExecute(null));
        Assert.True(viewModel.Commands.ById("redact.markSelection")!.CanExecute());
        await tab.MarkSelectionAsync();
        var word = Assert.Single(tab.RedactionPlan!.Marks);
        Assert.Equal(RedactionKind.Text, word.Kind);
        Assert.Equal("Pagewing", word.Term);
        Assert.False(word.Everywhere);
        Assert.Equal(0, word.Page);
        Assert.NotEmpty(word.Areas);
        Assert.All(word.Areas, area => Assert.True(area.Width > 0 && area.Height > 0));
        Assert.Null(tab.TextSelection);
        Assert.Equal([word.Id], tab.SelectedMarks);
        Assert.True(tab.RedactionHistory!.CanUndo);

        // A selection over a line end is one mark with a rectangle per line; the line end is one space in its term, and the blanks at
        // its ends are left out.
        var lineEnd = all.IndexOfAny(['\r', '\n']);
        Assert.True(lineEnd > 4, "the test file has more than one line");
        tab.TextSelection = new TextSelection(new TextPosition(0, lineEnd - 2), new TextPosition(0, Math.Min(all.Length, lineEnd + 6)));
        await tab.MarkSelectionAsync();
        var lines = tab.RedactionPlan.Marks[1];
        Assert.Equal(RedactionKind.Text, lines.Kind);
        Assert.True(lines.Areas.Count >= 2, $"{lines.Areas.Count} rectangles");
        Assert.Equal(lines.Term!.Trim(), lines.Term);
        Assert.DoesNotContain('\n', lines.Term);
        Assert.DoesNotContain('\r', lines.Term);

        // The same through the catalog (the key Ctrl+Enter), and one undo takes the mark away.
        var count = tab.RedactionPlan.Marks.Count;
        tab.TextSelection = new TextSelection(new TextPosition(0, first), new TextPosition(0, first + 4));
        viewModel.Commands.ById("redact.markSelection")!.Execute();
        await WaitUntilAsync(window, () => tab.RedactionPlan.Marks.Count == count + 1);
        Assert.Equal("Page", tab.RedactionPlan.Marks[^1].Term);
        tab.UndoCommand.Execute(null);
        Assert.Equal(count, tab.RedactionPlan.Marks.Count);

        // Only blanks: nothing is marked, and the notice says so (the selection stays).
        var space = all.IndexOf(' ', StringComparison.Ordinal);
        Assert.True(space >= 0);
        tab.TextSelection = new TextSelection(new TextPosition(0, space), new TextPosition(0, space + 1));
        await tab.MarkSelectionAsync();
        Assert.Equal(count, tab.RedactionPlan.Marks.Count);
        Assert.Equal(Strings.RedactSelectionNotMarkable, tab.RedactionNotice);
        Assert.NotNull(tab.TextSelection);
    }

    [AvaloniaFact]
    public async Task TheTextToolSelectsWithThePointerAndTheAreaToolDoesNot()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);

        // With the area tool a drag over text marks an area: no text is selected.
        Drag(window, ScreenPoint(view, window, 0, new Point(60, 60)), ScreenPoint(view, window, 0, new Point(300, 130)));
        Assert.Null(tab.TextSelection);
        Assert.Single(tab.RedactionPlan!.Marks);

        // With the text tool the same drag selects text, marks nothing, and the selection is marked on request.
        tab.UndoCommand.Execute(null);
        Assert.True(tab.RedactionPlan.IsEmpty);
        tab.ChooseRedactionToolCommand.Execute(RedactionTool.Text);
        Assert.True(tab.RedactionToolIsText);
        Assert.False(tab.RedactionToolIsArea);
        var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
        var first = text.GetText(0, text.Chars.Count).IndexOf("Pagewing", StringComparison.Ordinal);
        var begin = text.Chars[first].Box;
        var end = text.Chars[first + 7].Box;
        var line = (begin.Top + begin.Bottom) / 2;
        Drag(window, ScreenPoint(view, window, 0, new Point(begin.Left + 1, line)), ScreenPoint(view, window, 0, new Point(end.Right - 1, line)));
        Assert.True(tab.RedactionPlan.IsEmpty);
        Assert.NotNull(tab.TextSelection);
        Assert.True(tab.MarkSelectionCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task HandlesResizeAndKeyboardMoves()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await EnterAsync(window, tab);
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 300)), ScreenPoint(view, window, 0, new Point(220, 360)));
        var id = Assert.Single(tab.RedactionPlan!.Marks).Id;
        var scale = DocumentLayout.DipsPerPoint * view.Layout!.Zoom;
        var history = tab.RedactionHistory!;

        // The handles are on the frame, 4 DIPs outside the mark: the lower right corner resizes it, in one step.
        var corner = CanvasPoint(view, 0, new Point(220, 360)) + new Point(DrawingOverlay.FramePadding, DrawingOverlay.FramePadding);
        Assert.Equal(HandleKind.BottomRight, view.RedactionOverlay!.HandleAt(corner));
        Assert.Null(view.RedactionOverlay.HandleAt(CanvasPoint(view, 0, new Point(160, 330))));
        var version = history.Version;
        var from = view.Canvas.TranslatePoint(corner, window)!.Value;
        window.MouseDown(from, MouseButton.Left);
        Settle(window);
        window.MouseMove(from + new Point(scale * 20, scale * 10), Left);
        Settle(window);
        Assert.True(view.IsDraggingRedaction);
        Assert.Equal(id, view.RedactionOverlay.Drag!.MarkId);
        Assert.Equal(version, history.Version);
        window.MouseMove(from + new Point(scale * 40, scale * 20), Left);
        Settle(window);
        window.MouseUp(from + new Point(scale * 40, scale * 20), MouseButton.Left);
        Settle(window);
        AssertRectNear(new PageRect(100, 300, 260, 380), Assert.Single(tab.RedactionPlan.Marks[0].Areas), 0.8);
        Assert.Equal(version + 1, history.Version);

        // The body moves it, again in one step, and stays on the page.
        version = history.Version;
        var middle = ScreenPoint(view, window, 0, new Point(180, 340));
        Drag(window, middle, middle + new Point(scale * 30, scale * -50));
        AssertRectNear(new PageRect(130, 250, 290, 330), Assert.Single(tab.RedactionPlan.Marks[0].Areas), 0.8);
        Assert.Equal(version + 1, history.Version);
        Drag(window, ScreenPoint(view, window, 0, new Point(200, 300)), ScreenPoint(view, window, 0, new Point(-3000, -3000)));
        var kept = Assert.Single(tab.RedactionPlan.Marks[0].Areas);
        Assert.True(kept.Left >= 0 && kept.Top >= 0, $"{kept}");
        Assert.Equal(160, kept.Width, 0.8);
        tab.UndoCommand.Execute(null);
        AssertRectNear(new PageRect(130, 250, 290, 330), Assert.Single(tab.RedactionPlan.Marks[0].Areas), 0.8);

        // A drag that ends where it began is no step.
        version = history.Version;
        Drag(window, middle, middle);
        Assert.Equal(version, history.Version);

        // The arrow keys move by a point; a run of presses is one step, a pause makes the next one a step of its own.
        var start = Assert.Single(tab.RedactionPlan.Marks[0].Areas);
        tab.SelectMarks([id]);
        view.Focus();
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        AssertRectNear(new PageRect(start.Left + 2, start.Top + 1, start.Right + 2, start.Bottom + 1), Assert.Single(tab.RedactionPlan.Marks[0].Areas), 1e-6);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        AssertRectNear(new PageRect(start.Left + 3, start.Top + 1, start.Right + 3, start.Bottom + 1), Assert.Single(tab.RedactionPlan.Marks[0].Areas), 1e-6);
        tab.UndoCommand.Execute(null);
        AssertRectNear(new PageRect(start.Left + 2, start.Top + 1, start.Right + 2, start.Bottom + 1), Assert.Single(tab.RedactionPlan.Marks[0].Areas), 1e-6);
        tab.UndoCommand.Execute(null);
        AssertRectNear(start, Assert.Single(tab.RedactionPlan.Marks[0].Areas), 1e-6);

        // Shift with an arrow changes the size (the lower right corner), the mark never leaves the page or shrinks below its minimum.
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        var resized = Assert.Single(tab.RedactionPlan.Marks[0].Areas);
        Assert.Equal(start.Left, resized.Left, 1e-6);
        Assert.Equal(start.Right + 1, resized.Right, 1e-6);
        Assert.Equal(start.Bottom + 1, resized.Bottom, 1e-6);
        for (var i = 0; i < 200; i++)
        {
            tab.NudgeMarks(-1, -1, resize: true);
        }

        var smallest = Assert.Single(tab.RedactionPlan.Marks[0].Areas);
        Assert.True(smallest.Width >= 4 && smallest.Height >= 4, $"{smallest}");

        // Delete removes the selected mark in one step; with nothing selected the key does nothing; undo brings it back.
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        Assert.True(tab.RedactionPlan.IsEmpty);
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        tab.UndoCommand.Execute(null);
        Assert.Single(tab.RedactionPlan.Marks);
    }

    [AvaloniaFact]
    public async Task ATextMarkIsSelectedAndDeletedButNeverMovedOrResized()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
        var first = text.GetText(0, text.Chars.Count).IndexOf("Pagewing", StringComparison.Ordinal);
        tab.TextSelection = new TextSelection(new TextPosition(0, first), new TextPosition(0, first + 8));
        await tab.MarkSelectionAsync();
        var mark = Assert.Single(tab.RedactionPlan!.Marks);
        var area = mark.Areas[0];

        // A click on it selects it (no handles), a drag on it changes nothing, the arrow keys scroll instead of moving it.
        tab.ClearMarkSelection();
        var centre = ScreenPoint(view, window, 0, new Point((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2));
        Click(window, centre);
        Assert.Equal([mark.Id], tab.SelectedMarks);
        Assert.Null(tab.SelectedAreaMark);
        Assert.False(tab.HasMovableMarkSelection);
        Drag(window, centre, centre + new Point(60, 60));
        Assert.Equal(mark, Assert.Single(tab.RedactionPlan.Marks));
        view.Focus();
        var offset = view.ScrollViewer.Offset;
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.Equal(mark, Assert.Single(tab.RedactionPlan.Marks));
        Assert.True(view.ScrollViewer.Offset.Y >= offset.Y);

        Press(window, Key.Back, PhysicalKey.Backspace, RawInputModifiers.None);
        Assert.True(tab.RedactionPlan.IsEmpty);
    }

    [AvaloniaFact]
    public async Task TabLeavesTheOverlayWithoutATrap()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        tab.AddAreaMark(0, new PageRect(100, 300, 160, 340));
        tab.AddAreaMark(0, new PageRect(100, 100, 160, 140));
        tab.AddAreaMark(1, new PageRect(50, 50, 90, 90));
        tab.ClearMarkSelection();
        var ordered = tab.RedactionPlan!.Marks.OrderBy(mark => mark.Page).ThenBy(mark => mark.Areas[0].Top).ToArray();
        var handled = new List<bool>();
        view.AddHandler(InputElement.KeyDownEvent, (_, e) => handled.Add(e.Key == Key.Tab && e.Handled), Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        view.Focus();

        // Tab walks the marks in reading order (page, top, left); after the last the selection goes and the key is not taken,
        // so the focus moves on instead of staying in the document.
        foreach (var expected in ordered)
        {
            handled.Clear();
            Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal([expected.Id], tab.SelectedMarks);
            Assert.Contains(true, handled);
        }

        handled.Clear();
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.Empty(tab.SelectedMarks);
        Assert.DoesNotContain(true, handled);

        // Shift+Tab from nothing starts at the last mark and walks back; before the first one the selection goes as well.
        view.Focus();
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Equal([ordered[^1].Id], tab.SelectedMarks);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Equal([ordered[0].Id], tab.SelectedMarks);
        handled.Clear();
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Empty(tab.SelectedMarks);
        Assert.DoesNotContain(true, handled);

        // Without any mark Tab is never taken.
        tab.DiscardRedactionsCommand.Execute(null);
        await EnterAsync(window, tab);
        view.Focus();
        handled.Clear();
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.DoesNotContain(true, handled);
    }

    [AvaloniaFact]
    public async Task EscOrderIsMarkThenSelectionThenMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
        var first = text.GetText(0, text.Chars.Count).IndexOf("Pagewing", StringComparison.Ordinal);

        // 1. A rectangle being dragged out: Esc drops it, nothing is marked, the mode stays.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(180, 340)), Left);
        Settle(window);
        Assert.True(view.IsDraggingRedaction);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsDraggingRedaction);
        Assert.Null(view.RedactionOverlay!.Drag);
        Assert.True(tab.IsRedacting);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(200, 360)), Left);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 360)), MouseButton.Left);
        Settle(window);
        Assert.False(tab.HasPendingRedactions);

        // 2. A mark being dragged by a handle: Esc drops the drag, the mark stays as it was.
        tab.AddAreaMark(0, new PageRect(100, 300, 220, 360));
        var scale = DocumentLayout.DipsPerPoint * view.Layout!.Zoom;
        var corner = view.Canvas.TranslatePoint(CanvasPoint(view, 0, new Point(220, 360)) + new Point(DrawingOverlay.FramePadding, DrawingOverlay.FramePadding), window)!.Value;
        window.MouseDown(corner, MouseButton.Left);
        Settle(window);
        window.MouseMove(corner + new Point(scale * 30, scale * 30), Left);
        Settle(window);
        Assert.True(view.IsDraggingRedaction);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsDraggingRedaction);
        window.MouseUp(corner + new Point(scale * 30, scale * 30), MouseButton.Left);
        Settle(window);
        AssertRectNear(new PageRect(100, 300, 220, 360), Assert.Single(tab.RedactionPlan!.Marks[0].Areas), 1e-6);
        Assert.Single(tab.SelectedMarks);

        // 3. The selected mark, then 4. the text selection, then 5. the mode: which a mark that waits keeps on (a plan is applied or
        // discarded, never lost by a key), so the last Esc finds nothing to end until the mark is gone.
        tab.TextSelection = new TextSelection(new TextPosition(0, first), new TextPosition(0, first + 8));
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Empty(tab.SelectedMarks);
        Assert.NotNull(tab.TextSelection);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(tab.TextSelection);
        Assert.True(tab.IsRedacting);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(tab.IsRedacting);
        Assert.Single(tab.RedactionPlan.Marks);

        tab.UndoCommand.Execute(null);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(tab.IsRedacting);
    }

    [AvaloniaFact]
    public async Task ADragIsDroppedWhenTheTabOrTheModeChanges()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var second = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        viewModel.Tabs.Activate(first);
        Settle(window);
        await EnterAsync(window, first);

        window.MouseDown(ScreenPoint(view, window, 0, new Point(100, 300)), MouseButton.Left);
        Settle(window);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(180, 340)), Left);
        Settle(window);
        Assert.True(view.IsDraggingRedaction);
        viewModel.Tabs.Activate(second);
        Settle(window);
        Assert.False(view.IsDraggingRedaction);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(180, 340)), MouseButton.Left);
        Settle(window);
        Assert.False(first.HasPendingRedactions);
        Assert.False(second.IsRedacting);
        Assert.False(second.HasPendingRedactions);

        // A tab shown again shows its plan on the layer.
        first.AddAreaMark(0, new PageRect(10, 10, 60, 40));
        viewModel.Tabs.Activate(second);
        Settle(window);
        viewModel.Tabs.Activate(first);
        Settle(window);
        Assert.Same(first.RedactionPlan, view.RedactionOverlay!.Plan);
        viewModel.Tabs.Activate(second);
        Settle(window);
        Assert.Null(view.RedactionOverlay.Plan);
    }

    [AvaloniaFact]
    public async Task AMarkIsDrawnWhereItsRectangleIsOnEveryPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "print-mixed.pdf");
        await EnterAsync(window, tab);
        var overlay = EnsureOverlay(view);

        // Four formats, one of them rotated: the mark sits at the same place of the page as it is shown, whatever its rotation.
        var rect = new PageRect(20, 30, 120, 70);
        for (var page = 0; page < tab.PageCount; page++)
        {
            tab.GoToPage(page + 1);
            await WaitUntilAsync(window, () => tab.CurrentPage == page + 1);
            Settle(window);
            Assert.True(tab.AddAreaMark(page, rect));
        }

        tab.ClearMarkSelection();
        for (var page = 0; page < tab.PageCount; page++)
        {
            tab.GoToPage(page + 1);
            Settle(window);
            Settle(window);
            using var frame = FramePixels.Snapshot(LayersOf(view));
            var area = CanvasRect(view, page, rect);
            var edge = overlay.EdgeColor;

            // The edge is the second DIP inside the rectangle: the top and the left edge carry the token colour.
            var topEdge = new Point(area.Center.X, area.Top + (RedactionOverlay.EdgeThickness / 2));
            var leftEdge = new Point(area.Left + (RedactionOverlay.EdgeThickness / 2), area.Center.Y);
            Assert.True(Near(Pixel(frame, topEdge), edge, 8), $"page {page + 1}: top edge is {Pixel(frame, topEdge)}, expected {edge}");
            Assert.True(Near(Pixel(frame, leftEdge), edge, 8), $"page {page + 1}: left edge is {Pixel(frame, leftEdge)}, expected {edge}");

            // Outside the rectangle the page is white.
            Assert.True(Near(Pixel(frame, new Point(area.Right + 12, area.Center.Y)), Avalonia.Media.Colors.White, 8));
        }
    }

    [AvaloniaFact]
    public async Task AMarkIsNotColourAlone()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        tab.AddAreaMark(0, new PageRect(100, 300, 300, 400));
        tab.ClearMarkSelection();
        Settle(window);
        Settle(window);
        using var frame = FramePixels.Snapshot(LayersOf(view));
        var area = CanvasRect(view, 0, new PageRect(100, 300, 300, 400));
        var edge = view.RedactionOverlay!.EdgeColor;

        // Inside: the tint (a pink) and a hatch of the full edge colour across it; outside: the page. Walking along the middle
        // line, the pixels alternate between the hatch (edge colour) and the tint, so the mark is a pattern, not one colour.
        var row = area.Center.Y;
        var hatch = 0;
        var tint = 0;
        for (var x = area.Left + 6; x < area.Right - 6; x += 1)
        {
            var color = Pixel(frame, new Point(x, row));
            if (color.G < 200 && color.R > 200)
            {
                hatch++;
            }
            else if (color.R > 230 && color.G < 235 && color.G > 200)
            {
                tint++;
            }
        }

        Assert.True(hatch >= 8, $"only {hatch} hatch pixels along the line; the middle is {Pixel(frame, new Point(area.Center.X, row))}, the edge colour {edge}");
        Assert.True(tint >= 80, $"only {tint} tint pixels along the line");
        Assert.True(Near(Pixel(frame, new Point(area.Left + 1, area.Center.Y)), edge, 8), "no edge");
    }

    [AvaloniaFact]
    public async Task MarksKeepTheirColoursOnADarkPageAndTheSelectionFollowsThePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        tab.AddAreaMark(0, new PageRect(100, 300, 300, 400));
        var overlay = EnsureOverlay(view);
        Settle(window);
        var lightEdge = overlay.EdgeColor;
        var lightFrame = overlay.SelectionFrameColor;
        Assert.Equal(TokenTests.Token("Redact.Edge", ThemeVariant.Light), lightEdge);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Light), lightFrame);

        tab.PageThemeOverride = PageTheme.Dark;
        Settle(window);
        Settle(window);

        // The mark is the same red on the dark page (3:1 against both, TokenTests); its selection takes the dark theme's accent.
        Assert.Equal(lightEdge, overlay.EdgeColor);
        Assert.Equal(TokenTests.Token("Accent.Default", ThemeVariant.Dark), overlay.SelectionFrameColor);
        using var frame = FramePixels.Snapshot(LayersOf(view));
        var area = CanvasRect(view, 0, new PageRect(100, 300, 300, 400));
        Assert.True(Near(Pixel(frame, new Point(area.Center.X, area.Top + 1)), lightEdge, 24), "no edge on the dark page");
    }

    [AvaloniaFact]
    public async Task OnlyThePagesInViewAreDrawnAndScrollingIndexesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-dense.pdf");
        await EnterAsync(window, tab);
        for (var i = 0; i < 1_200; i++)
        {
            Assert.True(tab.AddAreaMark(i % tab.PageCount, new PageRect(20 + (i % 7), 20 + (i % 11), 80 + (i % 7), 60 + (i % 11))));
        }

        var overlay = EnsureOverlay(view);
        tab.ClearMarkSelection();
        Settle(window);
        Assert.Equal(1_200, overlay.Plan!.Marks.Count);
        var indexed = overlay.IndexCount;

        // Only the rectangles of the pages in view are drawn, far fewer than the plan has; a scroll indexes nothing again.
        var layout = view.Layout!;
        void AssertOnlyVisibleDrawn()
        {
            var viewport = view.Canvas.Viewport;
            var (first, last) = layout.GetVisiblePages(viewport.Y, viewport.Bottom);
            var expected = Enumerable.Range(first, last - first + 1).Sum(page => overlay.MarksOn(page).Sum(mark => mark.Areas.Count));
            Assert.True(overlay.LastDrawnRectangles <= expected, $"{overlay.LastDrawnRectangles} drawn, {expected} on the pages in view");
            Assert.True(overlay.LastDrawnRectangles < 100, $"{overlay.LastDrawnRectangles} rectangles drawn");
        }

        AssertOnlyVisibleDrawn();
        foreach (var page in new[] { 60, 120, 199, 3 })
        {
            tab.GoToPage(page + 1);
            await WaitUntilAsync(window, () => tab.CurrentPage == page + 1);
            Settle(window);
            Settle(window);
            AssertOnlyVisibleDrawn();
            Assert.Equal(indexed, overlay.IndexCount);
        }

        // A change of the plan indexes it once.
        tab.AddAreaMark(3, new PageRect(10, 10, 30, 30));
        Settle(window);
        Assert.Equal(indexed + 1, overlay.IndexCount);
    }

    [AvaloniaFact]
    public async Task OverlayIsNeverPartOfAPrintedOrRenderedPage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var document = tab.Document!;
        var ct = TestContext.Current.CancellationToken;
        var before = (await document.RenderPageAsync(0, 1, cancellationToken: ct)).Pixels.ToArray();
        var bytes = File.ReadAllBytes(tab.Session.Path!);
        var annotations = (await document.GetAnnotationsAsync(0, ct)).Count;

        await EnterAsync(window, tab);
        Drag(window, ScreenPoint(view, window, 0, new Point(100, 300)), ScreenPoint(view, window, 0, new Point(300, 380)));
        Assert.Single(tab.RedactionPlan!.Marks);
        Settle(window);

        // The layer is a sibling of the page canvas in the view, not a part of it; it takes no input and holds no document.
        var overlay = view.RedactionOverlay!;
        Assert.DoesNotContain(overlay, view.Canvas.GetVisualDescendants());
        Assert.Same(view.Canvas.GetVisualParent(), overlay.GetVisualParent());
        Assert.False(overlay.IsHitTestVisible);
        Assert.False(overlay.Focusable);
        Assert.DoesNotContain(typeof(RedactionOverlay).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public), field => typeof(PdfDocument).IsAssignableFrom(field.FieldType));

        // What PDFium renders (the pages of the view, the thumbnails, a print job all start from it) is the page without marks, and
        // so is the file on disk.
        var after = (await document.RenderPageAsync(0, 1, cancellationToken: ct)).Pixels.ToArray();
        Assert.Equal(before, after);
        Assert.Equal(bytes, File.ReadAllBytes(tab.Session.Path!));
        Assert.Equal(annotations, (await document.GetAnnotationsAsync(0, ct)).Count);
    }

    [AvaloniaFact]
    public async Task TheToolbarShowsTheCounterTheToolAndTheFill()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        Assert.Null(FindOrNull<RedactionToolbar>(window, "RedactionToolbar"));
        await EnterAsync(window, tab);
        var toolbar = FindOrNull<RedactionToolbar>(window, "RedactionToolbar")!;
        Assert.True(toolbar.IsVisible);
        Assert.Same(tab, toolbar.DataContext);
        Assert.Equal(Strings.RedactCount(0, 0), TextOf(toolbar, "CounterText"));
        Assert.Equal("No marks", TextOf(toolbar, "CounterText"));

        // The counter names marks and pages, in both languages and with the plural forms.
        tab.AddAreaMark(0, new PageRect(10, 10, 60, 40));
        Assert.Equal("1 mark on 1 page", TextOf(toolbar, "CounterText"));
        tab.AddAreaMark(0, new PageRect(10, 100, 60, 140));
        Assert.Equal("2 marks on 1 page", TextOf(toolbar, "CounterText"));
        tab.AddAreaMark(1, new PageRect(10, 10, 60, 40));
        tab.AddAreaMark(1, new PageRect(10, 100, 60, 140));
        Assert.Equal("4 marks on 2 pages", TextOf(toolbar, "CounterText"));
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("4 Markierungen auf 2 Seiten", tab.RedactionCounterText);
        Assert.Equal("1 Markierung auf 1 Seite", Strings.RedactCount(1, 1));
        Assert.Equal("Keine Markierungen", Strings.RedactCount(0, 0));
        Strings.Culture = new CultureInfo("en");

        // The tools and the fill are toggles that show their state; "Done" waits for the marks to be applied or discarded.
        var area = Button<ToggleButton>(toolbar, "AreaToolButton");
        var textTool = Button<ToggleButton>(toolbar, "TextToolButton");
        Assert.True(area.IsChecked);
        Assert.False(textTool.IsChecked);
        Assert.Contains("active", area.Classes);
        textTool.Command!.Execute(textTool.CommandParameter);
        Assert.True(textTool.IsChecked);
        Assert.False(area.IsChecked);
        Assert.Equal(RedactionTool.Text, tab.RedactionTool);
        var said = new List<string>();
        tab.RedactionHint += said.Add;
        var done = Button<Button>(toolbar, "DoneButton");
        Assert.True(done.Command!.CanExecute(null));
        done.Command.Execute(null);
        Assert.Equal([Strings.RedactMarksPending], said);
        Assert.True(tab.IsRedacting);
        var black = Button<ToggleButton>(toolbar, "FillBlackButton");
        var white = Button<ToggleButton>(toolbar, "FillWhiteButton");
        Assert.True(black.IsChecked);
        Assert.False(white.IsChecked);
        white.Command!.Execute(white.CommandParameter);
        Assert.Equal(RedactionFill.White, tab.RedactionPlan!.Fill);
        Assert.True(white.IsChecked);
        Assert.False(black.IsChecked);
        Assert.False(Button<Button>(toolbar, "MarkSelectionButton").Command!.CanExecute(null));

        // Discard drops the marks and ends the mode; the toolbar goes. There is no button that applies the marks yet.
        Button<Button>(toolbar, "DiscardButton").Command!.Execute(null);
        Settle(window);
        Assert.False(tab.IsRedacting);
        Assert.False(toolbar.IsVisible);
        Assert.Null(view.RedactionOverlay!.Plan);
        Assert.Null(FindOrNull<Button>(toolbar, "ApplyButton"));
    }

    [AvaloniaFact]
    public async Task TheToolbarStaysUsableInANarrowWindow()
    {
        var (window, viewModel, view) = Show();
        window.Width = 640;
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        Strings.Culture = new CultureInfo("de");
        await EnterAsync(window, tab);
        var toolbar = FindOrNull<RedactionToolbar>(window, "RedactionToolbar")!;
        tab.AddAreaMark(0, new PageRect(10, 10, 60, 40));
        for (var i = 0; i < 4; i++)
        {
            Settle(window);
        }

        // Everything that cannot lose its place stays in the row: the buttons, whatever else went.
        foreach (var name in new[] { "AreaToolButton", "TextToolButton", "MarkSelectionButton", "FillBlackButton", "FillWhiteButton", "UndoButton", "RedoButton", "DeleteMarksButton", "DiscardButton", "DoneButton" })
        {
            var button = Button<Control>(toolbar, name);
            Assert.True(button.IsVisible, name);
            Assert.True(button.Bounds.Width > 0, name);
        }

        var content = toolbar.GetVisualDescendants().OfType<Grid>().First(grid => grid.Name == "ToolbarContent");
        var needed = Needs(content);
        Assert.True(needed <= content.Bounds.Width + 0.5, $"the toolbar needs {needed:F0} DIPs of {content.Bounds.Width:F0}");
    }

    [AvaloniaFact]
    public async Task AutomationNamesExistForEveryButton()
    {
        foreach (var language in new[] { "en", "de" })
        {
            Strings.Culture = new CultureInfo(language);
            var (window, viewModel, view) = Show();
            var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
            await EnterAsync(window, tab);
            Assert.True(tab.AddAreaMark(0, new PageRect(100, 300, 220, 360)));
            Settle(window);
            var toolbar = FindOrNull<RedactionToolbar>(window, "RedactionToolbar")!;

            // Every button of the toolbar has a name, and so has the toolbar.
            Assert.Equal(Strings.RedactToolbarName, AutomationProperties.GetName(toolbar));
            Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(toolbar));
            var buttons = toolbar.GetVisualDescendants().OfType<Button>().ToList();
            Assert.True(buttons.Count >= 10, $"{buttons.Count} buttons");
            foreach (var button in buttons)
            {
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)), $"{language}: {button.Name} has no automation name");
            }

            // The button of the action bar, too.
            var entry = window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "RedactButton");
            Assert.Equal(Strings.CommandRedactMode, AutomationProperties.GetName(entry));

            // The selected mark and its handles are elements of the layer, named by kind and page, never by the text of a mark.
            var overlay = view.RedactionOverlay!;
            var frame = overlay.GetVisualDescendants().OfType<Border>().First(border => border.Name == "RedactionMarkFrame0");
            Assert.True(frame.IsVisible);
            Assert.Equal(Strings.RedactMarkName(1), AutomationProperties.GetName(frame));
            Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(frame));
            var handles = overlay.GetVisualDescendants().OfType<Border>().Where(border => border.Name?.StartsWith("RedactionHandle", StringComparison.Ordinal) == true).ToList();
            Assert.Equal(8, handles.Count);
            Assert.All(handles, handle =>
            {
                Assert.True(handle.IsVisible);
                Assert.Contains(Strings.RedactMarkKindName, AutomationProperties.GetName(handle), StringComparison.Ordinal);
                Assert.Contains("1", AutomationProperties.GetName(handle), StringComparison.Ordinal);
                Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(handle));
            });

            // A text mark never reads out its term; its handles do not exist.
            tab.ClearMarkSelection();
            var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
            var first = text.GetText(0, text.Chars.Count).IndexOf("Pagewing", StringComparison.Ordinal);
            tab.TextSelection = new TextSelection(new TextPosition(0, first), new TextPosition(0, first + 8));
            await tab.MarkSelectionAsync();
            Settle(window);
            Assert.DoesNotContain("Pagewing", AutomationProperties.GetName(frame), StringComparison.Ordinal);
            Assert.All(handles, handle => Assert.False(handle.IsVisible));
        }
    }

    [AvaloniaFact]
    public void TextsExistInBothLanguages()
    {
        string[] keys =
        [
            "CommandRedactMarkSelection", "RedactToolbarName", "RedactToolArea", "RedactToolAreaTip", "RedactToolText", "RedactToolTextTip",
            "RedactMarkSelection", "RedactFill", "RedactFillTip", "RedactFillBlack", "RedactFillWhite", "RedactDiscardButton", "RedactDone",
            "RedactDoneTooltip", "RedactCountNone", "RedactCountOneMark", "RedactCountOnePage", "RedactCountPages", "RedactPlanHint",
            "RedactMarkKindName", "RedactMarkNameOf", "RedactSelectionNotMarkable", "RedactCheckFailed",
        ];
        var english = new ResourceManager("Pagewing.App.Resources.Strings.en", typeof(Strings).Assembly);
        var german = new ResourceManager("Pagewing.App.Resources.Strings.de", typeof(Strings).Assembly);
        foreach (var key in keys)
        {
            var en = english.GetString(key, CultureInfo.InvariantCulture);
            var de = german.GetString(key, CultureInfo.InvariantCulture);
            Assert.False(string.IsNullOrWhiteSpace(en), $"{key} is missing in English");
            Assert.False(string.IsNullOrWhiteSpace(de), $"{key} is missing in German");

            // Every text is translated.
            Assert.NotEqual(en, de);

            // The placeholders of a format are the same in both texts.
            Assert.Equal(Placeholders(en!), Placeholders(de!));

            // No text claims a guarantee or a legal effect.
            foreach (var word in new[] { "safe", "secure", "guarantee", "legally", "sicher", "garantie", "rechtsgültig" })
            {
                Assert.DoesNotContain(word, en!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(word, de!, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The accessors read the same texts.
        Strings.Culture = new CultureInfo("de");
        Assert.Equal(german.GetString("RedactToolbarName", CultureInfo.InvariantCulture), Strings.RedactToolbarName);
        Assert.Equal("Markierung zum Schwärzen, Seite 3", Strings.RedactMarkName(3));
        Strings.Culture = new CultureInfo("en");
        Assert.Equal("Redaction mark, page 3", Strings.RedactMarkName(3));
    }

    [AvaloniaFact]
    public async Task TheBlockedReasonIsShownAfterTheFirstAttempt()
    {
        var (window, viewModel, view) = Show();
        var path = Copy("encrypted_hello_world_r3.pdf", "locked.pdf");
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab ?? throw new InvalidOperationException("No tab.");
        await WaitUntilAsync(window, () => tab.State == ViewerState.PasswordRequired);
        tab.Password = "hôtel";
        tab.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.State == ViewerState.Ready && ReferenceEquals(view.Document, tab.Session.Document) && view.Canvas.LastRequests.Count > 0);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);
        var hints = new List<string>();
        tab.RedactionHint += hints.Add;

        // Before the first attempt nothing is said: the mode is offered. The attempt reads the encryption and tells the user at once.
        Assert.Null(tab.RedactionBlockedReason);
        Assert.Empty(hints);
        var entry = window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "RedactButton");
        Assert.True(entry.Command!.CanExecute(null));
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => hints.Count > 0);
        Assert.Equal([Strings.RedactEncrypted], hints);
        Assert.False(tab.IsRedacting);

        // The hint is shown over the document (a live region), and from now on the button explains why it cannot start the mode.
        var hint = FindOrNull<Border>(window, "NoTextHint")!;
        await WaitUntilAsync(window, () => hint.IsVisible);
        Assert.Equal(Strings.RedactEncrypted, ((TextBlock)hint.Child!).Text);
        // The button stays usable: another click tells the reason again.
        Assert.True(entry.Command.CanExecute(null));
        entry.Command.Execute(null);
        Assert.Equal([Strings.RedactEncrypted, Strings.RedactEncrypted], hints);
        Assert.Equal(Strings.RedactEncrypted, AutomationProperties.GetHelpText(entry));
        Assert.Null(FindOrNull<RedactionToolbar>(window, "RedactionToolbar"));
        Assert.Null(view.RedactionOverlay);
    }

    [AvaloniaFact]
    public async Task AFailedEncryptionReadTellsTheUserAndAFreshAttemptWorks()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var hints = new List<string>();
        tab.RedactionHint += hints.Add;
        var fails = true;
        tab.EncryptionReader = (document, ct) => fails ? Task.FromException<bool>(new InvalidOperationException("no")) : Task.FromResult(false);

        // PDFium could not tell: the mode does not start (never "not encrypted" from not knowing), and the user is told.
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => hints.Count > 0);
        Assert.Equal([Strings.RedactCheckFailed], hints);
        Assert.False(tab.IsRedacting);
        Assert.Null(tab.RedactionBlockedReason);
        var hint = FindOrNull<Border>(window, "NoTextHint")!;
        await WaitUntilAsync(window, () => hint.IsVisible);
        Assert.Equal(Strings.RedactCheckFailed, ((TextBlock)hint.Child!).Text);

        // The next attempt reads again.
        fails = false;
        tab.RedactCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        Assert.Single(hints);
    }

    [AvaloniaFact]
    public async Task ARefusedMarkIsShownAsAHintAndNothingIsBuiltBeforeTheMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        // Nothing of the mode exists before it starts: no layer, no toolbar, no plan.
        Assert.Null(view.RedactionOverlay);
        Assert.Null(FindOrNull<RedactionToolbar>(window, "RedactionToolbar"));
        Assert.Null(tab.RedactionHistory);
        Assert.Null(FindOrNull<Border>(window, "NoTextHint"));

        await EnterAsync(window, tab);
        Assert.False(tab.AddAreaMark(0, new PageRect(double.NaN, 10, 20, 20)));
        var hint = FindOrNull<Border>(window, "NoTextHint")!;
        await WaitUntilAsync(window, () => hint.IsVisible);
        Assert.Equal(Strings.RedactMarkRefused, ((TextBlock)hint.Child!).Text);
    }

    [AvaloniaFact]
    public async Task EveryRefusalIsShownAgainEvenWithTheSameText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await EnterAsync(window, tab);
        var hints = new List<string>();
        tab.RedactionHint += hints.Add;

        // A refused rectangle, twice in a row: the notice string does not change, the hint comes both times.
        Assert.False(tab.AddAreaMark(0, new PageRect(double.NaN, 10, 20, 20)));
        Assert.False(tab.AddAreaMark(0, new PageRect(double.NaN, 10, 20, 20)));
        Assert.Equal([Strings.RedactMarkRefused, Strings.RedactMarkRefused], hints);

        // The hint card shows it again after it expired.
        var hint = FindOrNull<Border>(window, "NoTextHint")!;
        await WaitUntilAsync(window, () => hint.IsVisible);
        hint.IsVisible = false;
        Assert.False(tab.AddAreaMark(0, new PageRect(10, 10, double.PositiveInfinity, 20)));
        await WaitUntilAsync(window, () => hint.IsVisible);
        Assert.Equal(3, hints.Count);

        // A selection of only blanks, twice: the notice is the same, the hint comes both times.
        hints.Clear();
        var text = await tab.PageTexts!.GetAsync(0, PdfPriority.Interactive);
        var space = text.GetText(0, text.Chars.Count).IndexOf(' ', StringComparison.Ordinal);
        for (var i = 0; i < 2; i++)
        {
            tab.TextSelection = new TextSelection(new TextPosition(0, space), new TextPosition(0, space + 1));
            await tab.MarkSelectionAsync();
        }

        Assert.Equal([Strings.RedactSelectionNotMarkable, Strings.RedactSelectionNotMarkable], hints);

        // A full plan, twice: the marks-limit refusal comes both times.
        hints.Clear();
        var full = RedactionPlan.Empty.WithRange(Enumerable.Range(0, RedactionLimits.MaxMarks)
            .Select(_ => new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, [new PageRect(1, 1, 5, 5)], null, false)));
        tab.RedactionHistory!.Do(full);
        Assert.False(tab.AddAreaMark(0, new PageRect(10, 10, 20, 20)));
        Assert.False(tab.AddAreaMark(0, new PageRect(10, 10, 20, 20)));
        Assert.Equal([Strings.RedactMarksLimit, Strings.RedactMarksLimit], hints);

        // Clearing the notice says nothing.
        hints.Clear();
        tab.DismissRedactionNoticeCommand.Execute(null);
        Assert.Empty(hints);
    }

    [AvaloniaFact]
    public async Task TheModeButtonOfTheActionBarFollowsTheMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        Settle(window);
        var entry = window.GetVisualDescendants().OfType<ToggleButton>().First(button => button.Name == "RedactButton");
        Settle(window);
        Assert.True(entry.Command!.CanExecute(null));
        Assert.NotEqual(true, entry.IsChecked);
        entry.Command.Execute(null);
        await WaitUntilAsync(window, () => tab.IsRedacting);
        Assert.True(entry.IsChecked);
        Assert.Contains("active", entry.Classes);
        Assert.Contains("Ctrl+Shift+P", ToolTip.GetTip(entry)!.ToString(), StringComparison.Ordinal);

        // With marks the click does not end the mode (apply or discard them first) and says so; without, it ends it.
        tab.AddAreaMark(0, new PageRect(10, 10, 60, 40));
        var said = new List<string>();
        tab.RedactionHint += said.Add;
        Assert.True(entry.Command.CanExecute(null));
        Assert.True(entry.IsEnabled, "an active entry is never greyed out");
        entry.Command.Execute(null);
        Assert.True(tab.IsRedacting);
        Assert.Equal([Strings.RedactMarksPending], said);
        tab.UndoCommand.Execute(null);
        Assert.True(entry.Command.CanExecute(null));
        entry.Command.Execute(null);
        Assert.False(entry.IsChecked);
    }

    private static string Placeholders(string text) =>
        string.Join(',', System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order(StringComparer.Ordinal));

    private static T Button<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name) ?? throw new InvalidOperationException(name + " is missing");

    private static string TextOf(Visual root, string name) => Button<TextBlock>(root, name).Text ?? string.Empty;

    private static double Needs(Control content)
    {
        content.InvalidateMeasure();
        foreach (var part in content.GetVisualDescendants().OfType<Layoutable>())
        {
            part.InvalidateMeasure();
        }

        content.Measure(Size.Infinity);
        return content.DesiredSize.Width - content.Margin.Left - content.Margin.Right;
    }

    private static RedactionOverlay EnsureOverlay(DocumentView view) =>
        view.RedactionOverlay ?? throw new InvalidOperationException("The mode did not make its layer.");

    /// <summary>The visual that holds the page canvas and the layers over it (the pixels of the marks are read from it).</summary>
    private static Visual LayersOf(DocumentView view) => view.Canvas.GetVisualParent() ?? throw new InvalidOperationException("No layers.");

    private static Avalonia.Media.Color Pixel(FramePixels frame, Point canvasPoint)
    {
        // The canvas and the layers share their origin: a canvas point is a point of the snapshot of the layers.
        return frame.GetColor((int)Math.Floor(canvasPoint.X), (int)Math.Floor(canvasPoint.Y));
    }

    private static bool Near(Avalonia.Media.Color a, Avalonia.Media.Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static void AssertRectNear(PageRect expected, PageRect actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
            && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance,
            $"{expected} vs {actual}");

    /// <summary>Starts the mode of the tab through its command and waits until it is on and its toolbar shows.</summary>
    private static async Task EnterAsync(Window window, TabViewModel tab)
    {
        if (!tab.IsRedacting)
        {
            tab.RedactCommand.Execute(null);
            await WaitUntilAsync(window, () => tab.IsRedacting);
        }

        Settle(window);
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

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings? settings = null)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, FindOrNull<DocumentView>(window, "DocumentView")!);
    }

    /// <summary>Opens a copy of a test file (in the temp folder) in a new tab and waits for its first sharp page.</summary>
    private async Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        await viewModel.OpenAsync(Copy(name, Guid.NewGuid().ToString("N")[..8] + "-" + name));
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
    /// platform implementation as a platform with a pen does (the headless platform has mouse and touch only). Private API of
    /// Avalonia, reached by reflection; a renamed member makes this throw instead of passing silently.
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
