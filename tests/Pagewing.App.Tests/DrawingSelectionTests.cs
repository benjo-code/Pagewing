using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Selecting, moving and resizing drawings (milestone 10, task 12): the select tool picks the topmost selectable annotation
/// under a click, a drag moves the selection or one of its handles (one step of the history on release), the keyboard walks,
/// nudges, deletes and opens the comment of the selection. Every file is a copy in a temp folder; settings are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class DrawingSelectionTests : IDisposable
{
    private const RawInputModifiers Left = RawInputModifiers.LeftMouseButton;
    private const RawInputModifiers LeftShift = RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly AnnotationKind[] DrawingKinds =
        [AnnotationKind.Ink, AnnotationKind.Rectangle, AnnotationKind.Ellipse, AnnotationKind.Line, AnnotationKind.Arrow];

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-select-" + Guid.NewGuid().ToString("N"))).FullName;

    public DrawingSelectionTests() => Strings.Culture = new CultureInfo("en");

    public void Dispose()
    {
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
    public async Task ClickSelectsTopmostEditable()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var highlight = await MarkupAsync(window, tab, new PageRect(100, 200, 300, 220));
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        var line = await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(80, 250), new DisplayPoint(320, 250), 2));
        ChooseSelectTool(window, tab);

        // Where the line crosses the outline of the rectangle: the later drawing.
        await ClickAndWaitAsync(window, view, tab, new Point(121, 250), line.Name);

        // The outline of the rectangle away from the line, then inside it on the highlight (the inside is not the shape).
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 210), highlight.Name);
        var frame = FindOrNull<Border>(window, "SelectionFrame");
        Assert.NotNull(frame);
        Assert.True(frame.IsVisible);
        Assert.Equal("Highlight selected, page 1", AutomationProperties.GetName(frame));
        Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(frame));

        // A click on nothing clears the selection; nothing was changed.
        var version = tab.EditVersion;
        Click(window, ScreenPoint(view, window, 0, new Point(450, 360)));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is null);
        Assert.False(FindOrNull<Border>(window, "SelectionFrame") is { IsVisible: true });
        Assert.Equal(version, tab.EditVersion);
    }

    [AvaloniaFact]
    public async Task DragMovesAsOneUndoStep()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        var version = tab.EditVersion;

        // Several moves of the pointer (pressed inside the frame, away from the handles): only the preview follows; the
        // document changes once, on release.
        window.MouseDown(ScreenPoint(view, window, 0, new Point(150, 200)), MouseButton.Left);
        Settle(window);
        foreach (var point in new[] { new Point(160, 204), new Point(180, 214), new Point(200, 230) })
        {
            window.MouseMove(ScreenPoint(view, window, 0, point), Left);
            Settle(window);
        }

        Assert.True(view.IsMovingSelection);
        Assert.Equal(version, tab.EditVersion);
        AssertRectNear(new PageRect(120, 180, 280, 300), (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box, 0.01);
        AssertRectNear(new PageRect(170, 210, 330, 330), view.DrawingOverlay!.SelectionPreview!.Box, 0.6);

        window.MouseUp(ScreenPoint(view, window, 0, new Point(200, 230)), MouseButton.Left);
        Settle(window);
        Assert.False(view.IsMovingSelection);
        var moved = await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(170, 210, 330, 330));
        Assert.Equal(version + 1, tab.EditVersion);
        Assert.Equal(rectangle.Name, tab.SelectedAnnotation?.Name);
        AssertRectNear(moved.Drawing!.Box, tab.SelectedAnnotation!.Drawing!.Box, 0.01);

        // One undo puts it back; a second one takes the rectangle away (the move was one step).
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(120, 180, 280, 300));
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && !tab.CanUndo);
        Assert.Empty(await DrawingsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task DragBeyondThePageEdgeIsClamped()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(60, 180, 160, 260), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(110, 181), rectangle.Name);

        // Far beyond the left edge and above the page: the box stops at the edges.
        Drag(window, ScreenPoint(view, window, 0, new Point(85, 200)), ScreenPoint(view, window, 0, new Point(-65, 39)));
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(0, 180 - 161, 100, 260 - 161));

        // Again: the left edge stays where the page ends (only the vertical part of the drag moves it).
        var box = (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box;
        Drag(window, ScreenPoint(view, window, 0, new Point(30, box.Top + 20)), ScreenPoint(view, window, 0, new Point(-40, box.Top + 60)));
        var clamped = await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(0, box.Top + 40, 100, box.Bottom + 40));
        Assert.Equal(0, clamped.Drawing!.Box.Left, 3);
    }

    [AvaloniaFact]
    public async Task ArrowKeysMoveByOnePointAndTenWithShift()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        var version = tab.EditVersion;
        var offset = view.ScrollViewer.Offset;

        // Right: one point, shown at once, made after the pause; the document does not scroll.
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        AssertRectNear(new PageRect(121, 180, 281, 300), tab.SelectionGeometry!.Box, 1e-6);
        Assert.Equal(offset, view.ScrollViewer.Offset);
        Assert.Equal(version, tab.EditVersion);
        Assert.True(tab.AsksBeforeClosing);
        clock.Advance(TabViewModel.NudgeDelay);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(121, 180, 281, 300));
        Assert.Equal(version + 1, tab.EditVersion);

        // Shift+Down: ten points, its own step.
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
        AssertRectNear(new PageRect(121, 190, 281, 310), tab.SelectionGeometry!.Box, 1e-6);
        clock.Advance(TabViewModel.NudgeDelay);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(121, 190, 281, 310));
        Assert.Equal(version + 2, tab.EditVersion);

        // Left and Up the same way back.
        Press(window, Key.Left, PhysicalKey.ArrowLeft, RawInputModifiers.Shift);
        Press(window, Key.Up, PhysicalKey.ArrowUp, RawInputModifiers.None);
        clock.Advance(TabViewModel.NudgeDelay);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(111, 189, 271, 309));
        Assert.Equal(offset, view.ScrollViewer.Offset);
    }

    [AvaloniaFact]
    public async Task NudgesWithin400msAreOneUndoStep()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        var version = tab.EditVersion;

        // Three presses 300 ms apart: the pause never reaches 400 ms, nothing is made yet.
        for (var i = 0; i < 3; i++)
        {
            Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Settle(window);
        }

        Assert.Equal(version, tab.EditVersion);
        Assert.Equal(120, (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box.Left, 3);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(123, 180, 283, 300));
        Assert.Equal(version + 1, tab.EditVersion);

        // Ending the mode makes a waiting nudge at once, as its own step.
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        tab.EndAnnotateCommand.Execute(null);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(124, 180, 284, 300));
        Assert.Equal(version + 2, tab.EditVersion);
    }

    [AvaloniaFact]
    public async Task TabWalksTheAnnotationsOfThePageInReadingOrder()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var low = await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(100, 340), new DisplayPoint(200, 350), 2));
        var topRight = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(300, 150, 360, 190), 2));
        var topLeft = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 150, 160, 190), 2));
        var middle = await MarkupAsync(window, tab, new PageRect(100, 250, 300, 266));
        ChooseSelectTool(window, tab);
        view.Focus();
        Settle(window);

        // From no selection Tab takes the first in reading order (top to bottom, then left to right).
        string?[] order = [topLeft.Name, topRight.Name, middle.Name, low.Name];
        foreach (var name in order)
        {
            Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
            await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == name);
        }

        // Shift+Tab goes back.
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == middle.Name);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == topRight.Name);

        // The keys stayed in the document meanwhile.
        Assert.True(view.IsFocused);
    }

    [AvaloniaFact]
    public async Task TabLeavesTheViewAfterTheLastAnnotation()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var first = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 150, 160, 190), 2));
        var last = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 300, 160, 340), 2));
        ChooseSelectTool(window, tab);

        // Tab past the last annotation: the selection goes and the key is not taken, so the focus leaves the document.
        view.Focus();
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == first.Name);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == last.Name);
        Assert.True(view.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.Null(tab.SelectedAnnotation);
        Assert.False(view.IsKeyboardFocusWithin);

        // Shift+Tab before the first one does the same, the other way.
        view.Focus();
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == last.Name);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == first.Name);
        Assert.True(view.IsFocused);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Null(tab.SelectedAnnotation);
        Assert.False(view.IsKeyboardFocusWithin);
        Assert.True(tab.IsAnnotating);
    }

    [AvaloniaFact]
    public async Task TabAfterAMoveOfTheLastAnnotationLeavesTheView()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await BeginAsync(window, tab);
        var first = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 150, 160, 190), 2));
        var last = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 300, 160, 340), 2));
        ChooseSelectTool(window, tab);
        view.Focus();
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == first.Name);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == last.Name);

        // The last one is nudged and the nudge is made: the history moved, so the list of the page is read again. That read
        // is held back here, so Tab comes while the list is cold (the worst case of a quick Tab after a change).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = tab.AnnotationReader;
        tab.AnnotationReader = async (document, page, cancellationToken) =>
        {
            await gate.Task;
            return await reader(document, page, cancellationToken);
        };
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        clock.Advance(TabViewModel.NudgeDelay);
        await WaitForBoxAsync(window, tab, last.Name, new PageRect(101, 300, 161, 340));
        Assert.False(tab.IsPageRead(0));

        // Tab now: past the last annotation once the page is read, the selection goes and the focus leaves the document
        // (no new round through the page).
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        gate.SetResult();
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is null && !view.IsKeyboardFocusWithin);
        Settle(window);
        Assert.Null(tab.SelectedAnnotation);
        Assert.False(view.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task TabWithAColdCacheDoesNotSwallowTheKey()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        ChooseSelectTool(window, tab);
        TaskCompletionSource? gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = tab.AnnotationReader;
        tab.AnnotationReader = async (document, page, cancellationToken) =>
        {
            if (gate is { } waiting)
            {
                await waiting.Task;
            }

            return await reader(document, page, cancellationToken);
        };

        // A page without anything to select, not read yet: the key waits for the read, then the focus leaves the document.
        view.Focus();
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.True(view.IsFocused);
        gate.SetResult();
        await WaitUntilAsync(window, () => !view.IsKeyboardFocusWithin);
        Assert.Null(tab.SelectedAnnotation);

        // A drawing is made with another tool (the select tool reads nothing meanwhile); back in the select tool the list of
        // the page is cold again: the first Tab selects the first annotation once the page is read.
        gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(100, 150, 160, 190), 2));
        ChooseSelectTool(window, tab);
        view.Focus();
        Settle(window);
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.None);
        Assert.True(view.IsFocused);
        Assert.Null(tab.SelectedAnnotation);
        gate.SetResult();
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == rectangle.Name);
        Assert.True(view.IsFocused);

        // Shift+Tab from it (the list is read now): before the first one, the focus leaves at once.
        Press(window, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
        Assert.Null(tab.SelectedAnnotation);
        Assert.False(view.IsKeyboardFocusWithin);
    }

    [AvaloniaFact]
    public async Task SmallShapeCanBeMovedByItsBody()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var small = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(200, 200, 212, 210), 1));
        var line = await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(300, 300), new DisplayPoint(312, 300), 1));
        ChooseSelectTool(window, tab);

        // A box smaller than a handle: a press in its middle moves it and keeps its size.
        await ClickAndWaitAsync(window, view, tab, new Point(206, 200), small.Name);
        Drag(window, ScreenPoint(view, window, 0, new Point(206, 205)), ScreenPoint(view, window, 0, new Point(236, 225)));
        await WaitForBoxAsync(window, tab, small.Name, new PageRect(230, 220, 242, 230));

        // The middle of a short line moves the whole line; its ends stay apart as they were.
        await ClickAndWaitAsync(window, view, tab, new Point(306, 300), line.Name);
        Drag(window, ScreenPoint(view, window, 0, new Point(306, 300)), ScreenPoint(view, window, 0, new Point(306, 320)));
        var moved = await WaitForDrawingAsync(window, tab, line.Name, a => Math.Abs(a.Drawing!.Strokes[0][0].Y - 320) < 0.6);
        AssertNear(new DisplayPoint(300, 320), moved.Drawing!.Strokes[0][0], 0.6);
        AssertNear(new DisplayPoint(312, 320), moved.Drawing.Strokes[0][1], 0.6);
    }

    [AvaloniaFact]
    public async Task SmallShapeCanStillBeResizedFromOutsideItsBox()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var small = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(200, 200, 212, 210), 1));
        var line = await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(300, 300), new DisplayPoint(312, 300), 1));
        ChooseSelectTool(window, tab);
        var scale = DocumentLayout.DipsPerPoint * view.Layout!.Zoom;

        // Beside the bottom right corner, outside the box: the corner handle; the box grows to the right and down.
        await ClickAndWaitAsync(window, view, tab, new Point(206, 200), small.Name);
        var corner = HandleCentre(window, "SelectionHandleBottomRight");
        Drag(window, corner + new Point(6, 6), corner + new Point(6 + (20 * scale), 6 + (10 * scale)));
        await WaitForBoxAsync(window, tab, small.Name, new PageRect(200, 200, 232, 220));

        // Beyond the end of the short line: its end handle; the start stays.
        await ClickAndWaitAsync(window, view, tab, new Point(306, 300), line.Name);
        var end = HandleCentre(window, "SelectionHandleEnd");
        Drag(window, end + new Point(5, 0), end + new Point(5 + (30 * scale), 0));
        var longer = await WaitForDrawingAsync(window, tab, line.Name, a => Math.Abs(a.Drawing!.Strokes[0][1].X - 342) < 0.6);
        AssertNear(new DisplayPoint(300, 300), longer.Drawing!.Strokes[0][0], 0.01);
    }

    [AvaloniaFact]
    public async Task DeleteKeyDeletes()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        var line = await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(80, 350), new DisplayPoint(320, 350), 2));
        ChooseSelectTool(window, tab);

        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);

        // While a card asks about the tab the selection stays, but no key and no drag changes anything.
        tab.IsAskedAbout = true;
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        Drag(window, ScreenPoint(view, window, 0, new Point(150, 181)), ScreenPoint(view, window, 0, new Point(190, 220)));
        Assert.False(view.IsMovingSelection);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == rectangle.Name);
        Assert.Null(tab.SelectionGeometry is { } shown && !RectNear(rectangle.Drawing!.Box, shown.Box, 0.01) ? shown : null);
        Assert.Equal(2, (await DrawingsAsync(tab, 0)).Length);
        tab.IsAskedAbout = false;

        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is null);
        var left = await WaitForDrawingsAsync(window, tab, 0, count: 1);
        Assert.Equal(line.Name, Assert.Single(left).Name);

        // Backspace does the same; undo brings both back.
        await ClickAndWaitAsync(window, view, tab, new Point(200, 350), line.Name);
        Press(window, Key.Back, PhysicalKey.Backspace, RawInputModifiers.None);
        await WaitForDrawingsAsync(window, tab, 0, count: 0);
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        await WaitForDrawingsAsync(window, tab, 0, count: 2);
    }

    [AvaloniaFact]
    public async Task EnterOpensTheComment()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(80, 350), new DisplayPoint(320, 350), 2));
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);

        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        Assert.Equal(SidePanelKind.Comments, tab.SidePanel);
        await WaitUntilAsync(window, () => tab.Comments.SelectedItem?.Annotation.Name == rectangle.Name);
        await WaitUntilAsync(window, () => FocusedElement(window) is TextBox { Name: "CommentBox" });

        // The comment typed there belongs to the drawing.
        var box = (TextBox)FocusedElement(window)!;
        box.Text = "Check this";
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.Control);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.Comments.SelectedItem?.Annotation.Contents == "Check this");
        Assert.Equal("Check this", (await DrawingAsync(tab, rectangle.Name)).Contents);
    }

    [AvaloniaFact]
    public async Task LockedForeignDrawingOnlyOffersCommentAndDelete()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "drawings-foreign.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await BeginAsync(window, tab);
        ChooseSelectTool(window, tab);
        var foreign = (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Single(a => a.Name == "ink-0001");
        Assert.True(foreign.Editable);
        Assert.False(foreign.GeometryEditable);
        var stroke = foreign.Drawing!.Strokes[0];
        var on = new Point((stroke[0].X + stroke[1].X) / 2, (stroke[0].Y + stroke[1].Y) / 2);

        await ClickAndWaitAsync(window, view, tab, on, foreign.Name);
        Assert.False(tab.CanMoveSelection);
        Assert.False(string.IsNullOrEmpty(tab.SelectionLockReason));
        Assert.True(FindOrNull<Border>(window, "SelectionFrame") is { IsVisible: true });
        Assert.Empty(Handles(window));

        // Neither a drag nor the arrows move it, and nothing is changed.
        Drag(window, ScreenPoint(view, window, 0, on), ScreenPoint(view, window, 0, on + new Point(40, 30)));
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        clock.Advance(TabViewModel.NudgeDelay);
        Settle(window);
        Assert.False(view.IsMovingSelection);
        Assert.Equal(0, tab.EditVersion);
        Assert.Equal(foreign.Drawing.Box, (await DrawingAsync(tab, foreign.Name)).Drawing!.Box);

        // Its comment opens and can be edited.
        await ClickAndWaitAsync(window, view, tab, on, foreign.Name);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntilAsync(window, () => FocusedElement(window) is TextBox { Name: "CommentBox", IsEffectivelyVisible: true });

        // Delete removes it.
        view.Focus();
        Settle(window);
        Press(window, Key.Delete, PhysicalKey.Delete, RawInputModifiers.None);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && tab.SelectedAnnotation is null);
        var names = (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Select(a => a.Name);
        Assert.DoesNotContain("ink-0001", names);
    }

    [AvaloniaFact]
    public async Task MarkupIsSelectableButNotMovable()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var highlight = await MarkupAsync(window, tab, new PageRect(100, 200, 300, 220));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 210), highlight.Name);
        Assert.False(tab.CanMoveSelection);
        Assert.Empty(Handles(window));
        Assert.True(FindOrNull<Border>(window, "SelectionFrame") is { IsVisible: true });
        var version = tab.EditVersion;

        // A drag changes nothing; the arrows scroll as without a selection.
        Drag(window, ScreenPoint(view, window, 0, new Point(200, 210)), ScreenPoint(view, window, 0, new Point(260, 260)));
        Assert.False(view.IsMovingSelection);
        var offset = view.ScrollViewer.Offset;
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.True(view.ScrollViewer.Offset.Y > offset.Y);
        Settle(window);
        Assert.Equal(version, tab.EditVersion);
        var markup = Assert.Single(await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken), a => a.Name == highlight.Name);
        Assert.Equal(highlight.Bounds, markup.Bounds);
    }

    [AvaloniaFact]
    public async Task HandleNamesSayWhatIsSelected()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        var line = await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(60, 340), new DisplayPoint(300, 340), 2));
        ChooseSelectTool(window, tab);

        // Each handle names itself, the kind of the selection and its page, like the frame does.
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        Assert.Equal("Rectangle selected, page 1", AutomationProperties.GetName(FindOrNull<Border>(window, "SelectionFrame")!));
        foreach (var handle in Handles(window))
        {
            var kind = Enum.Parse<HandleKind>(handle.Name!["SelectionHandle".Length..]);
            Assert.Equal($"{Strings.SelectionHandleName(kind)}, Rectangle, page 1", AutomationProperties.GetName(handle));
        }

        // Another selection: the names follow it.
        await ClickAndWaitAsync(window, view, tab, new Point(180, 340), line.Name);
        var start = Assert.Single(Handles(window), handle => handle.Name == "SelectionHandleStart");
        Assert.Equal($"{Strings.SelectionHandleStart}, Line, page 1", AutomationProperties.GetName(start));

        // In German.
        Strings.Culture = new CultureInfo("de");
        tab.ClearSelection();
        Settle(window);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        var corner = Assert.Single(Handles(window), handle => handle.Name == "SelectionHandleTopLeft");
        Assert.Equal($"{Strings.SelectionHandleTopLeft}, Rechteck, Seite 1", AutomationProperties.GetName(corner));
        Assert.Equal("Rechteck ausgewählt, Seite 1", AutomationProperties.GetName(FindOrNull<Border>(window, "SelectionFrame")!));
    }

    [AvaloniaFact]
    public async Task HandlesHaveA24DipHitArea()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);

        // Eight handles with names, each 24 DIPs; the frame and the handles stand out against the page (3:1).
        var handles = Handles(window);
        Assert.Equal(8, handles.Count);
        Assert.All(handles, handle =>
        {
            Assert.Equal(24, handle.Bounds.Width, 3);
            Assert.Equal(24, handle.Bounds.Height, 3);
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(handle)));
            Assert.Equal(AccessibilityView.Content, AutomationProperties.GetAccessibilityView(handle));
        });
        Assert.Equal($"{Strings.SelectionHandleBottomRight}, Rectangle, page 1", AutomationProperties.GetName(handles.Single(h => h.Name == "SelectionHandleBottomRight")));
        var overlay = view.DrawingOverlay!;
        Assert.True(Wcag.Contrast(overlay.SelectionFrameColor, Colors.White) >= 3);
        Assert.True(Wcag.Contrast(overlay.SelectionHaloColor, overlay.SelectionFrameColor) >= 3);

        // On a dark page the frame takes the tokens of the dark theme and keeps 3:1 against that ground.
        Assert.True(window.TryFindResource("Pw.Color.Page.DarkBackground", Avalonia.Styling.ThemeVariant.Light, out var ground));
        var darkGround = Assert.IsType<Color>(ground);
        view.Canvas.PageTheme = PageTheme.Dark;
        Assert.True(Wcag.Contrast(overlay.SelectionFrameColor, darkGround) >= 3);
        Assert.True(Wcag.Contrast(overlay.SelectionHaloColor, overlay.SelectionFrameColor) >= 3);
        view.Canvas.PageTheme = PageTheme.Light;
        Settle(window);

        // A press 11 DIPs beside the middle of the bottom right handle still takes it: the box grows to the right only.
        var corner = HandleCentre(window, "SelectionHandleBottomRight");
        Drag(window, corner + new Point(11, 0), corner + new Point(11 + 40, 0));
        var scale = DocumentLayout.DipsPerPoint * view.Layout!.Zoom;
        var resized = await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(120, 180, 280 + (40 / scale), 300));
        Assert.Equal(300, resized.Drawing!.Box.Bottom, 1);

        // 14 DIPs away (outside the handle and the frame) it is a click beside the drawing: the selection goes, nothing moves.
        corner = HandleCentre(window, "SelectionHandleBottomRight");
        Drag(window, corner + new Point(14, 14), corner + new Point(54, 14));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation is null);
        AssertRectNear(resized.Drawing.Box, (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box, 0.01);
    }

    [AvaloniaFact]
    public async Task SelectionClearsWhenTheModeEnds()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);

        // Another tool clears it as well.
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        Settle(window);
        Assert.Null(tab.SelectedAnnotation);

        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        tab.EndAnnotateCommand.Execute(null);
        Settle(window);
        Assert.Null(tab.SelectedAnnotation);
        Assert.False(FindOrNull<Border>(window, "SelectionFrame") is { IsVisible: true });
        Assert.Empty(Handles(window));

        // Outside the mode the arrows scroll again.
        view.Focus();
        var offset = view.ScrollViewer.Offset;
        Press(window, Key.Down, PhysicalKey.ArrowDown, RawInputModifiers.None);
        Assert.True(view.ScrollViewer.Offset.Y > offset.Y);
    }

    [AvaloniaFact]
    public async Task NothingIsReadBeforeTheModeStarts()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "drawings-foreign.pdf");
        var reads = 0;
        var reader = tab.AnnotationReader;
        tab.AnnotationReader = (document, page, cancellationToken) =>
        {
            reads++;
            return reader(document, page, cancellationToken);
        };

        // Clicks and the keys of the selection in the reading view read no annotation and make nothing.
        view.Focus();
        Click(window, ScreenPoint(view, window, 0, new Point(100, 100)));
        foreach (var (key, physical) in new[] { (Key.Tab, PhysicalKey.Tab), (Key.Right, PhysicalKey.ArrowRight), (Key.Delete, PhysicalKey.Delete), (Key.Enter, PhysicalKey.Enter) })
        {
            view.Focus();
            Press(window, key, physical, RawInputModifiers.None);
        }

        Assert.Equal(0, reads);
        Assert.Null(tab.SelectedAnnotation);
        Assert.False(tab.HasEditor);
        Assert.Null(view.DrawingOverlay);

        // Nor does the mode with a markup tool.
        await BeginAsync(window, tab);
        Click(window, ScreenPoint(view, window, 0, new Point(100, 100)));
        Assert.Equal(0, reads);

        // The select tool reads the page when it is needed.
        ChooseSelectTool(window, tab);
        Click(window, ScreenPoint(view, window, 0, new Point(100, 100)));
        await WaitUntilAsync(window, () => reads > 0);
    }

    [AvaloniaFact]
    public async Task EscOrderGestureDragSelectionThenMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));

        // A stroke of a drawing tool first.
        tab.SetToolCommand.Execute(AnnotationKind.Ink);
        window.MouseDown(ScreenPoint(view, window, 0, new Point(320, 320)), MouseButton.Left);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(360, 340)), Left);
        Settle(window);
        Assert.True(view.IsDrawing);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsDrawing);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(360, 340)), MouseButton.Left);
        Settle(window);
        Assert.True(tab.IsAnnotating);

        // Then a drag of the selection: it goes without a trace, the selection stays.
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        var version = tab.EditVersion;
        window.MouseDown(ScreenPoint(view, window, 0, new Point(150, 200)), MouseButton.Left);
        window.MouseMove(ScreenPoint(view, window, 0, new Point(190, 240)), Left);
        Settle(window);
        Assert.True(view.IsMovingSelection);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(view.IsMovingSelection);
        Assert.Null(view.DrawingOverlay!.SelectionPreview);
        window.MouseUp(ScreenPoint(view, window, 0, new Point(190, 240)), MouseButton.Left);
        Settle(window);
        Assert.Equal(rectangle.Name, tab.SelectedAnnotation?.Name);
        Assert.Equal(version, tab.EditVersion);

        // Then the selection, then the mode.
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(tab.SelectedAnnotation);
        Assert.True(tab.IsAnnotating);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.False(tab.IsAnnotating);
        AssertRectNear(new PageRect(120, 180, 280, 300), (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box, 0.01);
    }

    [AvaloniaFact]
    public async Task SelectionFollowsTheNameAfterAReplace()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        await DrawAsync(window, tab, DrawingGeometry.Line(new DisplayPoint(80, 350), new DisplayPoint(320, 350), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        var index = tab.SelectedAnnotation!.Index;

        Drag(window, ScreenPoint(view, window, 0, new Point(150, 200)), ScreenPoint(view, window, 0, new Point(170, 210)));
        var moved = await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(140, 190, 300, 310));

        // The replaced rectangle is the last one of the page now; the selection is it, under its name, with its new shape.
        Assert.NotEqual(index, moved.Index);
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Index == moved.Index);
        Assert.Equal(rectangle.Name, tab.SelectedAnnotation!.Name);
        AssertRectNear(moved.Drawing!.Box, tab.SelectedAnnotation.Drawing!.Box, 0.01);

        // A move right after uses the new shape, not the old one.
        Drag(window, ScreenPoint(view, window, 0, new Point(170, 215)), ScreenPoint(view, window, 0, new Point(180, 215)));
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(150, 190, 310, 310));
    }

    [AvaloniaFact]
    public async Task ArrowEndpointHandleMovesOnlyThatEnd()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var arrow = await DrawAsync(window, tab, DrawingGeometry.Arrow(new DisplayPoint(100, 300), new DisplayPoint(250, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(170, 300), arrow.Name);

        var handles = Handles(window);
        Assert.Equal(["SelectionHandleEnd", "SelectionHandleStart"], handles.Select(h => h.Name).Order(StringComparer.Ordinal));
        Assert.Equal($"{Strings.SelectionHandleEnd}, Arrow, page 1", AutomationProperties.GetName(handles.Single(h => h.Name == "SelectionHandleEnd")));

        // The tip goes down by 50 points; the tail stays, the head is built again for the new direction.
        var tip = HandleCentre(window, "SelectionHandleEnd");
        var scale = DocumentLayout.DipsPerPoint * view.Layout!.Zoom;
        Drag(window, tip, tip + new Point(0, 50 * scale));
        var changed = await WaitForDrawingAsync(window, tab, arrow.Name, a => Math.Abs(a.Drawing!.Strokes[0][1].Y - 350) < 0.6);
        var shaft = changed.Drawing!.Strokes[0];
        AssertNear(new DisplayPoint(100, 300), shaft[0], 0.01);
        AssertNear(new DisplayPoint(250, 350), shaft[1], 0.6);
        Assert.Equal(AnnotationKind.Arrow, changed.Kind);
        Assert.Equal(2, changed.Drawing.Strokes.Count);

        // With Shift the end snaps to 45 degrees from the other end.
        var start = HandleCentre(window, "SelectionHandleStart");
        Drag(window, start, start + new Point(-40 * scale, 3 * scale), LeftShift);
        var snapped = await WaitForDrawingAsync(window, tab, arrow.Name, a => Math.Abs(a.Drawing!.Strokes[0][0].X - 100) > 1);
        var line = snapped.Drawing!.Strokes[0];
        var angle = Math.Atan2(line[0].Y - line[1].Y, line[0].X - line[1].X) * 180 / Math.PI;
        Assert.Equal(0, Math.IEEERemainder(angle, 45), 1);
        AssertNear(new DisplayPoint(250, 350), line[1], 0.6);
    }

    [AvaloniaFact]
    public async Task KeysDoNotActWhileATextBoxHasFocus()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);
        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        await WaitUntilAsync(window, () => FocusedElement(window) is TextBox { Name: "CommentBox" });
        var version = tab.EditVersion;

        // In the comment field the keys belong to the field.
        foreach (var (key, physical) in new[] { (Key.Right, PhysicalKey.ArrowRight), (Key.Delete, PhysicalKey.Delete), (Key.Back, PhysicalKey.Backspace), (Key.Tab, PhysicalKey.Tab) })
        {
            if (FocusedElement(window) is not TextBox)
            {
                break;
            }

            Press(window, key, physical, RawInputModifiers.None);
        }

        clock.Advance(TabViewModel.NudgeDelay);
        Settle(window);
        Assert.Equal(version, tab.EditVersion);
        Assert.Equal(rectangle.Name, tab.SelectedAnnotation?.Name);
        AssertRectNear(new PageRect(120, 180, 280, 300), (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box, 0.01);
    }

    [AvaloniaFact]
    public async Task NudgeIsNotLostWhenPrintingStarts()
    {
        var (window, viewModel, view) = Show();
        var service = new FakePrintService { SystemDialog = true, DialogTicket = new PrintTicket(new PrintSettings { Printer = "Plotter" }) };
        var gate = new TaskCompletionSource();
        service.OnPrint = (_, _, _) => gate.Task;
        viewModel.PrintServiceFactory = () => service;
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var clock = new PrintClock();
        tab.SelectionClock = clock;
        await BeginAsync(window, tab);
        var rectangle = await DrawAsync(window, tab, DrawingGeometry.Rectangle(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), rectangle.Name);

        // A nudge waits for the keys to rest when printing starts: it is made first.
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        viewModel.PrintCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsPrinting);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(121, 180, 281, 300));
        gate.SetResult();
        await WaitUntilAsync(window, () => !tab.IsPrinting);

        // A nudge whose pause ends while the tab takes no edit (here a card asks about it) stays, shown and counted, and is
        // made once the tab takes edits again.
        Press(window, Key.Right, PhysicalKey.ArrowRight, RawInputModifiers.None);
        tab.IsAskedAbout = true;
        clock.Advance(TabViewModel.NudgeDelay);
        Settle(window);
        AssertRectNear(new PageRect(122, 180, 282, 300), tab.SelectionGeometry!.Box, 1e-6);
        Assert.True(tab.AsksBeforeClosing);
        Assert.Equal(121, (await DrawingAsync(tab, rectangle.Name)).Drawing!.Box.Left, 1);
        tab.IsAskedAbout = false;
        clock.Advance(TabViewModel.NudgeDelay);
        await WaitForBoxAsync(window, tab, rectangle.Name, new PageRect(122, 180, 282, 300));
    }

    [AvaloniaFact]
    public async Task UndoAfterMoveRestoresThePosition()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var ellipse = await DrawAsync(window, tab, DrawingGeometry.Ellipse(new PageRect(120, 180, 280, 300), 2));
        ChooseSelectTool(window, tab);
        await ClickAndWaitAsync(window, view, tab, new Point(200, 181), ellipse.Name);

        Drag(window, ScreenPoint(view, window, 0, new Point(150, 200)), ScreenPoint(view, window, 0, new Point(210, 240)));
        await WaitForBoxAsync(window, tab, ellipse.Name, new PageRect(180, 220, 340, 340));

        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitForBoxAsync(window, tab, ellipse.Name, new PageRect(120, 180, 280, 300));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Drawing is { } drawing && RectNear(new PageRect(120, 180, 280, 300), drawing.Box, 0.01));
        Assert.Equal(ellipse.Name, tab.SelectedAnnotation!.Name);

        await WaitUntilAsync(window, () => tab.CanRedo);
        tab.RedoCommand.Execute(null);
        await WaitForBoxAsync(window, tab, ellipse.Name, new PageRect(180, 220, 340, 340));
    }

    private static void AssertNear(DisplayPoint expected, DisplayPoint actual, double tolerance) =>
        Assert.True(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance, $"{expected} vs {actual}");

    private static bool RectNear(PageRect expected, PageRect actual, double tolerance) =>
        Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
        && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance;

    private static void AssertRectNear(PageRect expected, PageRect actual, double tolerance) =>
        Assert.True(RectNear(expected, actual, tolerance), $"{expected} vs {actual}");

    /// <summary>Starts the mode of the tab through its command; waits until it is on.</summary>
    private static async Task BeginAsync(Window window, TabViewModel tab)
    {
        if (!tab.IsAnnotating)
        {
            tab.BeginAnnotateCommand.Execute(null);
            await WaitUntilAsync(window, () => tab.IsAnnotating);
        }

        Settle(window);
    }

    private static void ChooseSelectTool(Window window, TabViewModel tab)
    {
        if (!tab.IsSelectTool)
        {
            tab.SelectToolCommand.Execute(null);
        }

        Assert.True(tab.IsSelectTool);
        Settle(window);
    }

    /// <summary>Makes a drawing on page 1 through the tab, as a stroke of a drawing tool would, and returns it as read.</summary>
    private static async Task<PdfAnnotation> DrawAsync(Window window, TabViewModel tab, DrawingGeometry geometry)
    {
        var before = (await DrawingsAsync(tab, 0)).Select(a => a.Name).ToHashSet();
        await tab.ApplyDrawingAsync(0, geometry);
        var after = await WaitUntilCountAsync(window, tab, before.Count + 1);
        return after.Single(a => !before.Contains(a.Name));
    }

    private static async Task<PdfAnnotation[]> WaitUntilCountAsync(Window window, TabViewModel tab, int count) =>
        await WaitForDrawingsAsync(window, tab, 0, count);

    /// <summary>Makes a highlight over one line of page 1 through the editor and returns it as read.</summary>
    private static async Task<PdfAnnotation> MarkupAsync(Window window, TabViewModel tab, PageRect line)
    {
        var created = await tab.Editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [line], new AnnotationColor(255, 230, 0), null, null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        return created!;
    }

    private static async Task<PdfAnnotation[]> DrawingsAsync(TabViewModel tab, int page) =>
        (await tab.Session.Document!.GetAnnotationsAsync(page, TestContext.Current.CancellationToken))
            .Where(annotation => DrawingKinds.Contains(annotation.Kind))
            .ToArray();

    private static async Task<PdfAnnotation> DrawingAsync(TabViewModel tab, string? name) =>
        (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).Single(a => a.Name == name);

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

    /// <summary>Waits until the drawing called <paramref name="name"/> has <paramref name="box"/> (within half a point) and returns it.</summary>
    private static Task<PdfAnnotation> WaitForBoxAsync(Window window, TabViewModel tab, string? name, PageRect box) =>
        WaitForDrawingAsync(window, tab, name, a => a.Drawing is { } drawing && RectNear(box, drawing.Box, 0.6));

    private static async Task<PdfAnnotation> WaitForDrawingAsync(Window window, TabViewModel tab, string? name, Func<PdfAnnotation, bool> condition)
    {
        PdfAnnotation? found = null;
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (!tab.HasEditsInFlight && tab.Session.Document is not null)
            {
                found = (await tab.Session.Document.GetAnnotationsAsync(0, TestContext.Current.CancellationToken)).SingleOrDefault(a => a.Name == name);
                if (found is not null && condition(found))
                {
                    return found;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The drawing {name} did not get there: {found?.Drawing?.Box}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private static async Task ClickAndWaitAsync(Window window, DocumentView view, TabViewModel tab, Point pagePoint, string? name)
    {
        Click(window, ScreenPoint(view, window, 0, pagePoint));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Name == name);
        Settle(window);
    }

    private static List<Border> Handles(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Name?.StartsWith("SelectionHandle", StringComparison.Ordinal) == true && b.IsEffectivelyVisible).ToList();

    /// <summary>The window point of the middle of a handle.</summary>
    private static Point HandleCentre(Window window, string name)
    {
        var handle = Handles(window).Single(h => h.Name == name);
        return handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)!.Value;
    }

    private static IInputElement? FocusedElement(Window window) => window.FocusManager?.GetFocusedElement();

    /// <summary>The window point of a point on a page (PDF points from the top left of the page as displayed).</summary>
    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var canvas = new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
        return view.Canvas.TranslatePoint(canvas, window)!.Value;
    }

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

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
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
}
