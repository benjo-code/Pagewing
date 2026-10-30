using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
using Pagewing.App.Controls;
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
/// The mode "Comment" (milestone 9, task 16): starting and ending it, marking on the release of the mouse, the commands of
/// the catalog, the edit controls of the "Comments" panel, the colours per kind and the startup path. Every file is a copy in
/// a temp folder (nothing is written beside the test data); settings and history are in memory.
/// </summary>
// The window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class AnnotateModeTests : IDisposable
{
    private const RawInputModifiers None = RawInputModifiers.None;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;
    private const RawInputModifiers CtrlShift = RawInputModifiers.Control | RawInputModifiers.Shift;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-annotate-" + Guid.NewGuid().ToString("N"))).FullName;

    public AnnotateModeTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task ReleasingTheMouseWithAToolAppliesTheMarkup()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { AuthorName = "Ada" });
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        Assert.Equal("Pagewing", text.GetText(0, 8));
        await BeginAsync(window, tab, AnnotationKind.Highlight);

        // From the left half of "P" to the right half of "g": "Pagewing" becomes a yellow highlight by Ada.
        Drag(window, ScreenPoint(view, window, 0, LeftHalf(text.Chars[0].Box)), ScreenPoint(view, window, 0, RightHalf(text.Chars[7].Box)));

        var annotation = Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Highlight, annotation.Kind);
        Assert.Equal(MarkupColor.Yellow, AnnotationPalette.Match(annotation.Color!.Value));
        Assert.Equal("Ada", annotation.Author);
        AssertRectNear(Assert.Single(text.RectsOf(0, 8)), Assert.Single(annotation.QuadRects), 0.5);

        // The selection became the markup: it is gone; the tab has a change to save; the mode stays with its tool.
        Assert.Null(tab.TextSelection);
        await WaitUntilAsync(window, () => tab.IsDirty && tab.CanUndo);
        Assert.True(tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Highlight, tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task WithoutAToolSelectionOnlySelects()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var clipboard = new RecordingClipboard();
        view.Clipboard = clipboard;
        await BeginAsync(window, tab, tool: null);

        Drag(window, ScreenPoint(view, window, 0, LeftHalf(text.Chars[0].Box)), ScreenPoint(view, window, 0, RightHalf(text.Chars[7].Box)));

        // A selection, no markup: Ctrl+C copies it as it does outside the mode.
        Assert.Equal(new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8)), tab.TextSelection);
        view.Focus();
        Press(window, Key.C, PhysicalKey.C, Ctrl);
        await WaitUntilAsync(window, () => clipboard.Text == "Pagewing");
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
        Assert.Empty(await MarkupsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task SelectionAcrossTwoPagesCreatesOneAnnotationPerPage()
    {
        var (window, viewModel, view) = Show(height: 900);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        tab.Zoom = 0.5;
        Settle(window);
        var first = await TextAsync(window, tab, 0);
        var second = await TextAsync(window, tab, 1);
        await WaitUntilAsync(window, () => view.Layout!.IsShown(1) && view.Canvas.LastRequests.Count > 0);
        await BeginAsync(window, tab, AnnotationKind.Underline);

        // From the last word of page 1 to the first word of page 2, with the mouse.
        var lastWordStart = LastWordStart(first);
        Drag(
            window,
            ScreenPoint(view, window, 0, LeftHalf(first.Chars[lastWordStart].Box)),
            ScreenPoint(view, window, 1, RightHalf(second.Chars[3].Box)));

        var onFirst = Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1));
        var onSecond = Assert.Single(await AnnotationsAsync(window, tab, 1, count: 1));
        Assert.Equal(AnnotationKind.Underline, onFirst.Kind);
        Assert.Equal(AnnotationKind.Underline, onSecond.Kind);
        Assert.Equal(MarkupColor.Blue, AnnotationPalette.Match(onSecond.Color!.Value));
        Assert.Null(tab.TextSelection);

        // One step per page on the history: two undos remove both.
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.CanUndo && !tab.Editor.IsBusy);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.CanUndo && !tab.Editor.IsBusy);
        Assert.Empty(await MarkupsAsync(tab, 0));
        Assert.Empty(await MarkupsAsync(tab, 1));
    }

    [AvaloniaFact]
    public async Task SelectionWithoutGeometryCreatesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);

        // The blank between "Pagewing" and "Handbuch": nothing to mark, nothing changes, nothing is said.
        Assert.True(char.IsWhiteSpace(text.GetText(8, 9), 0));
        tab.TextSelection = new TextSelection(new TextPosition(0, 8), new TextPosition(0, 9));
        viewModel.Commands.ById("annotate.highlight")!.Execute();
        await WaitUntilAsync(window, () => tab.IsAnnotating && !tab.HasEditsInFlight);

        Assert.Null(tab.TextSelection);
        Assert.False(tab.IsDirty);
        Assert.Empty(await MarkupsAsync(tab, 0));
        Assert.Null(viewModel.PendingSave);

        // The blanks at the ends of a selection are left out: " Handbuch " marks exactly "Handbuch".
        tab.TextSelection = new TextSelection(new TextPosition(0, 8), new TextPosition(0, 18));
        viewModel.Commands.ById("annotate.highlight")!.Execute();
        var annotation = Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1));
        AssertRectNear(Assert.Single(text.RectsOf(9, 17)), Assert.Single(annotation.QuadRects), 0.5);
    }

    [AvaloniaFact]
    public async Task AClickWithoutDragAppliesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        await BeginAsync(window, tab, AnnotationKind.Highlight);

        // A click on a letter, and a press that moves less than 4 DIP: no markup.
        var point = ScreenPoint(view, window, 0, Centre(text.Chars[3].Box));
        Click(window, point);
        window.MouseDown(point, MouseButton.Left);
        Settle(window);
        window.MouseMove(point + new Point(2, 1), RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseUp(point + new Point(2, 1), MouseButton.Left);
        Settle(window);

        Assert.False(tab.HasEditsInFlight);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task DoubleAndTripleClickMarkTheWordAndTheLine()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        // Long enough that a loaded machine cannot let the wait for a third click run out between the clicks and the
        // checks below (the dispatcher only runs timers inside Click and the waits); the word then comes after this delay.
        view.MultiClickMarkupDelay = TimeSpan.FromMilliseconds(1000);
        await BeginAsync(window, tab, AnnotationKind.StrikeOut);

        // A double click marks its word once no third click came.
        var word = ScreenPoint(view, window, 0, Centre(text.Chars[12].Box));
        Click(window, word, clicks: 2);
        Assert.True(view.IsMarkupPending);
        var marked = Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1));
        AssertRectNear(Assert.Single(text.RectsOf(9, 17)), Assert.Single(marked.QuadRects), 0.5);

        // Undo it; then a triple click marks the line, not the word as well.
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.CanUndo && !tab.Editor.IsBusy);
        // Elsewhere on the line (farther than a double click may move), so the clicks do not continue the double click above.
        Click(window, ScreenPoint(view, window, 0, Centre(text.Chars[3].Box)), clicks: 3);
        var line = Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1));
        var (start, end) = text.LineAt(12);
        AssertRectNear(Assert.Single(TabViewModel.MarkupLinesOf(text, start, end)), Assert.Single(line.QuadRects), 0.5);
        // No word waits to be marked after the third click: nothing more comes.
        Assert.False(view.IsMarkupPending);
        Assert.Single(await MarkupsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task ADragFromALinkSelectsWithAToolAndFollowsNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        var link = text.Links.First(candidate => DocumentView.IsClickable(candidate) && candidate.Target is UriTarget);
        var box = link.Rects[0];
        var startChar = text.CharAt((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
        Assert.True(startChar >= 0);
        await BeginAsync(window, tab, AnnotationKind.Highlight);

        // Pressed on the link, dragged along its line: a markup, no card asking about the address.
        var from = ScreenPoint(view, window, 0, new Point(box.Left + 2, (box.Top + box.Bottom) / 2));
        var to = ScreenPoint(view, window, 0, new Point(box.Right - 2, (box.Top + box.Bottom) / 2));
        Drag(window, from, to);
        Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1, kind: AnnotationKind.Highlight));
        Assert.Null(viewModel.PendingLink);

        // A plain click on the link still asks first (the card), it never opens anything. In the middle of the link, farther
        // from the press of the drag than a double click may move, so it is a click of its own.
        Assert.True(box.Width * view.Layout!.Zoom * DocumentLayout.DipsPerPoint / 2 > 8, "the link is wide enough");
        await WaitUntilAsync(window, () => view.PendingTextRequests == 0);
        Click(window, ScreenPoint(view, window, 0, new Point((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2)));
        await WaitUntilAsync(window, () => viewModel.PendingLink is not null);
        viewModel.CancelLinkCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task EscEndsTheModeButFirstClearsTheSelection()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, tool: null);
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));
        view.Focus();
        Settle(window);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Null(tab.TextSelection);
        Assert.True(tab.IsAnnotating);

        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.IsAnnotating);
        Assert.False(Find<AnnotationToolbar>(window, "AnnotationToolbar").IsVisible);
        Assert.True(view.IsFocused);
    }

    [AvaloniaFact]
    public async Task EscOrderIsCardsThenSelectionThenMode()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var search = tab.Search;
        search.Query = "Pagewing";
        await WaitUntilAsync(window, () => search.State == SearchState.Done && search.Hits.Count == 4);
        await BeginAsync(window, tab, AnnotationKind.Highlight);
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));

        // The help card is open and the keys are back in the document: Esc closes the card (the window's Esc), nothing else.
        Press(window, Key.F1, PhysicalKey.F1, None);
        Assert.Equal(OverlayKind.Help, viewModel.Overlay);
        view.Focus();
        Settle(window);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal(OverlayKind.None, viewModel.Overlay);
        Assert.True(tab.HasTextSelection);
        Assert.True(tab.IsAnnotating);

        // Then the selection, then the mode, then (as before the mode) the search.
        view.Focus();
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.HasTextSelection);
        Assert.True(tab.IsAnnotating);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.IsAnnotating);
        Assert.True(search.IsActive);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(search.IsActive);

        // In the toolbar the same order: a selection first, then the mode; the keys go back to the document.
        await BeginAsync(window, tab, AnnotationKind.Highlight);
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));
        var toolbar = Find<AnnotationToolbar>(window, "AnnotationToolbar");
        Find<Button>(toolbar, "DoneButton").Focus(NavigationMethod.Tab);
        Settle(window);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.HasTextSelection);
        Assert.True(tab.IsAnnotating);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.False(tab.IsAnnotating);
        Assert.True(view.IsFocused);
    }

    [AvaloniaFact]
    public async Task CommandWithSelectionStartsTheModeAndAppliesAtOnce()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        await SelectAsync(window, view, 0, 9, 17);
        Assert.False(tab.IsAnnotating);

        // Ctrl+Shift+U with "Handbuch" selected: the mode with the underline, and the underline at once.
        view.Focus();
        Press(window, Key.U, PhysicalKey.U, CtrlShift);
        var annotation = Assert.Single(await AnnotationsAsync(window, tab, 0, count: 1));
        Assert.Equal(AnnotationKind.Underline, annotation.Kind);
        AssertRectNear(Assert.Single(text.RectsOf(9, 17)), Assert.Single(annotation.QuadRects), 0.5);
        Assert.True(tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Underline, tab.ActiveTool);
        Assert.Null(tab.TextSelection);
        Assert.True(Find<AnnotationToolbar>(window, "AnnotationToolbar").IsEffectivelyVisible);

        // In the mode the key works the same: Ctrl+Shift+X strikes out the next selection.
        await SelectAsync(window, view, 0, 0, 8);
        Press(window, Key.X, PhysicalKey.X, CtrlShift);
        await AnnotationsAsync(window, tab, 0, count: 2);
        Assert.Equal(AnnotationKind.StrikeOut, tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task CommandWithoutSelectionStartsTheModeWithThatTool()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var catalog = viewModel.Commands;
        bool Offered(string id) => catalog.ById(id) is { } command && command.CanExecute() && (command.Offered?.Invoke() ?? true);
        Assert.All(["annotate.mode", "annotate.highlight", "annotate.underline", "annotate.strikeout"], id => Assert.True(Offered(id), id));

        view.Focus();
        Press(window, Key.H, PhysicalKey.H, CtrlShift);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.Equal(AnnotationKind.Highlight, tab.ActiveTool);
        Assert.False(tab.HasEditor);

        // Ctrl+Shift+K ends it (and starts it again without a tool).
        Press(window, Key.K, PhysicalKey.K, CtrlShift);
        Assert.False(tab.IsAnnotating);
        Assert.Null(tab.ActiveTool);
        Press(window, Key.K, PhysicalKey.K, CtrlShift);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.Null(tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task ReadingViewShowsNoEditingTools()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "annotations-foreign.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(0) is { IsEffectivelyVisible: true });

        // Reading: no toolbar, no edit controls; the button "Comment" is the way in, nothing else in the panel acts.
        Assert.Null(FindOrNull<AnnotationToolbar>(window, "AnnotationToolbar"));
        Assert.Null(FindOrNull<CommentEditor>(window, "CommentEditor"));
        Assert.False(comments.IsEditing);
        var content = Find<CommentsPanel>(window, "CommentsContent");
        var button = Find<ActionToggleButton>(content, "AnnotateButton");
        Assert.True(button.IsEffectivelyVisible);
        Assert.Equal(Strings.AnnotateModeName, Avalonia.Automation.AutomationProperties.GetName(button));
        Assert.False(button.IsChecked);
        Assert.DoesNotContain(content.GetVisualDescendants().OfType<Control>(), control => control.IsEffectivelyVisible
            && control is TextBox or CheckBox or ToggleSwitch && control.FindAncestorOfType<ComboBox>() is null);
        Assert.All(list.GetRealizedContainers(), container => Assert.False(Find<TextBlock>(container, "CommentReadOnly").IsEffectivelyVisible));

        // In the mode: toolbar, edit controls, and "View only" on the entries Pagewing does not edit.
        Click(window, button);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        Assert.True(button.IsChecked);
        Assert.Contains("active", button.Classes);
        Assert.True(Find<AnnotationToolbar>(window, "AnnotationToolbar").IsEffectivelyVisible);
        Assert.True(Find<CommentEditor>(window, "CommentEditor").IsEffectivelyVisible);
        Assert.True(comments.IsEditing);
        var ink = comments.Items.First(item => item.Kind == AnnotationKind.Other);
        Assert.True(ink.IsViewOnlyInMode);
        Assert.DoesNotContain(comments.Items, item => item.Annotation.Editable && item.IsViewOnlyInMode);

        // "Done" ends it: everything of the mode goes again.
        Click(window, Find<Button>(window, "DoneButton"));
        Assert.False(tab.IsAnnotating);
        Assert.False(Find<AnnotationToolbar>(window, "AnnotationToolbar").IsVisible);
        Assert.Null(FindOrNull<CommentEditor>(window, "CommentEditor"));
        Assert.False(ink.IsViewOnlyInMode);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task CommentFieldKeepsItsOwnUndo()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, _) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentBox"));
        Assert.True(tab.CanUndo);

        // Typed into the comment field; Ctrl+Z undoes the typing, not the markup.
        var box = Find<TextBox>(window, "CommentBox");
        box.Focus();
        Settle(window);
        window.KeyTextInput("Note");
        Settle(window);
        Assert.Equal("Note", comments.CommentDraft);
        Press(window, Key.Z, PhysicalKey.Z, Ctrl);
        Assert.NotEqual("Note", box.Text);
        Assert.True(tab.CanUndo);
        Assert.Single(await MarkupsAsync(tab, 0));
    }

    [AvaloniaFact]
    public async Task CtrlEnterCommitsTheCommentAndEscDiscardsIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentBox"));

        var box = Find<TextBox>(window, "CommentBox");
        box.Focus();
        Settle(window);
        window.KeyTextInput("First line");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        await WaitUntilAsync(window, () => comments.SelectedItem?.Annotation.Contents == "First line");
        var annotation = Assert.Single(await MarkupsAsync(tab, 0));
        Assert.Equal("First line", annotation.Contents);
        Assert.Equal("First line", comments.SelectedItem!.Comment);

        // Esc drops what was typed: the comment stays, the keys go back to the entry.
        box = Find<TextBox>(window, "CommentBox");
        box.Focus();
        box.CaretIndex = box.Text!.Length;
        Settle(window);
        window.KeyTextInput(" and more");
        Settle(window);
        Assert.Equal("First line and more", comments.CommentDraft);
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.Equal("First line", comments.CommentDraft);
        Assert.True(tab.IsAnnotating);
        await WaitUntilAsync(window, () => list.ContainerFromIndex(list.SelectedIndex) is { IsKeyboardFocusWithin: true });
        Assert.Equal("First line", Assert.Single(await MarkupsAsync(tab, 0)).Contents);

        // An empty comment removes it.
        comments.CommentDraft = string.Empty;
        comments.CommitCommentCommand.Execute(null);
        await WaitUntilAsync(window, () => comments.SelectedItem is { Comment: null } && !tab.HasEditsInFlight);
        Assert.True(string.IsNullOrEmpty(Assert.Single(await MarkupsAsync(tab, 0)).Contents));
    }

    [AvaloniaFact]
    public async Task DeleteFromThePanelAndUndoRestoresIt()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, _) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentDeleteButton"));
        var name = comments.SelectedItem!.Annotation.Name;

        var delete = Find<Button>(window, "CommentDeleteButton");
        Assert.Equal(Strings.CommentDelete, Avalonia.Automation.AutomationProperties.GetName(delete));
        Click(window, delete);
        await WaitUntilAsync(window, () => comments.Items.Count == 0 && !tab.HasEditsInFlight);
        Assert.Empty(await MarkupsAsync(tab, 0));
        Assert.Null(comments.SelectedItem);

        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => comments.Items.Count == 1);
        Assert.Equal(name, Assert.Single(await MarkupsAsync(tab, 0)).Name);
    }

    [AvaloniaFact]
    public async Task ChangingTheColourFromThePanelUpdatesTheAnnotation()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, _) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentColorButton"));
        Assert.Equal(MarkupColor.Yellow, comments.SelectedColor);

        // The menu offers the five colours by name, the colour of the markup with a check mark; Green changes it.
        var button = Find<Button>(window, "CommentColorButton");
        Click(window, button);
        var menu = Assert.IsType<MenuFlyout>(button.Flyout);
        await WaitUntilAsync(window, () => menu.IsOpen && menu.Items.Count == 5);
        var entries = menu.Items.Cast<MenuItem>().ToArray();
        Assert.Equal(AnnotationPalette.All.Select(Strings.ColorName), entries.Select(entry => (string)entry.Header!));
        Assert.Equal(["Yellow", "Green", "Blue", "Pink", "Orange"], entries.Select(entry => (string)entry.Header!));
        Assert.Single(entries, entry => ((Panel)entry.Icon!).Children.OfType<StrokeIcon>().Any());
        entries[1].Command!.Execute(entries[1].CommandParameter);
        menu.Hide();
        await WaitUntilAsync(window, () => comments.SelectedColor == MarkupColor.Green && !tab.HasEditsInFlight);
        var annotation = Assert.Single(await MarkupsAsync(tab, 0));
        Assert.Equal(MarkupColor.Green, AnnotationPalette.Match(annotation.Color!.Value));

        // Undo brings the old colour back.
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => comments.SelectedColor == MarkupColor.Yellow);
    }

    [AvaloniaFact]
    public async Task ModeIsUnavailableWhenTheDocumentForbidsAnnotations()
    {
        var (window, viewModel, view) = Show();
        var path = Copy("encrypted_hello_world_r3.pdf");
        await viewModel.OpenAsync(path);
        var tab = viewModel.ActiveTab!;
        await WaitUntilAsync(window, () => tab.IsPasswordPrompt && tab.State == ViewerState.PasswordRequired);
        tab.Password = "hôtel";
        tab.SubmitPasswordCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsReady && ReferenceEquals(view.Document, tab.Session.Document));
        await AutoOpenedPanel.CloseAsync(window, viewModel, view);
        await WaitUntilAsync(window, () => tab.HasShownSharpPage);

        // Not known before the first request: the button is there; asking reads the permissions and refuses with a reason.
        await OpenPanelAsync(window, viewModel, tab);
        var button = Find<ActionToggleButton>(window, "AnnotateButton");
        Assert.True(button.IsEffectivelyVisible);
        Click(window, button);
        await WaitUntilAsync(window, () => !tab.CanAnnotate);
        Assert.False(tab.IsAnnotating);
        Assert.False(button.IsEffectivelyVisible);
        var reason = Find<TextBlock>(window, "AnnotateUnavailable");
        Assert.True(reason.IsEffectivelyVisible);
        Assert.Equal(Strings.AnnotateForbidden, reason.Text);
        Assert.Null(FindOrNull<AnnotationToolbar>(window, "AnnotationToolbar"));

        // Nothing of the mode runs now: neither the keys nor the palette offer it.
        Assert.All(
            ["annotate.mode", "annotate.highlight", "annotate.underline", "annotate.strikeout"],
            id => Assert.False(viewModel.Commands.ById(id)!.CanExecute(), id));
        Strings.Culture = new CultureInfo("de");
        Assert.Equal("Dieses Dokument erlaubt keine Kommentare: Seine Berechtigungen verbieten es, welche hinzuzufügen.", tab.AnnotateUnavailableReason);
    }

    [AvaloniaFact]
    public async Task NothingIsReadOrCreatedBeforeTheFirstSharpPage()
    {
        // A tab without a view never shows a sharp page: the mode cannot start, and nothing is read for it.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached());
        _cleanup.Add(viewModel);
        await viewModel.OpenAsync(Copy("text-rich.pdf"));
        var tab = viewModel.ActiveTab!;
        var reads = 0;
        tab.PermissionReader = (document, cancellationToken) =>
        {
            reads++;
            return document.MayAnnotateAsync(cancellationToken);
        };
        var deadline = DateTime.UtcNow + Timeout;
        while (!tab.IsReady && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(tab.IsReady);
        Assert.False(tab.HasShownSharpPage);
        tab.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 8));
        foreach (var id in new[] { "annotate.mode", "annotate.highlight", "annotate.underline", "annotate.strikeout" })
        {
            Assert.False(viewModel.Commands.ById(id)!.CanExecute(), id);
            viewModel.Commands.ById(id)!.Execute();
        }

        tab.BeginAnnotateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(tab.IsAnnotating);
        Assert.Equal(0, reads);
        Assert.False(tab.HasEditor);
        Assert.False(tab.IsCommentsCreated);
        Assert.Equal(Strings.AnnotateNotReady, tab.AnnotateUnavailableReason);

        // With a window: the document is shown, but until the mode is asked for nothing of it exists or is read.
        var (window, shown, view) = Show();
        var other = await OpenCopyAsync(window, shown, view, "text-basic.pdf");
        var otherReads = 0;
        other.PermissionReader = (document, cancellationToken) =>
        {
            otherReads++;
            return document.MayAnnotateAsync(cancellationToken);
        };
        Assert.Null(FindOrNull<AnnotationToolbar>(window, "AnnotationToolbar"));
        Assert.False(other.HasEditor);
        Assert.Equal(0, otherReads);
        Assert.Null(other.AnnotateUnavailableReason);

        // The permissions are read once per document, at the first request.
        other.ToggleAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => other.IsAnnotating);
        other.ToggleAnnotateCommand.Execute(null);
        other.ToggleAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => other.IsAnnotating);
        Assert.Equal(1, otherReads);
        Assert.False(other.HasEditor);
    }

    [AvaloniaFact]
    public async Task ChosenColourIsRememberedPerKind()
    {
        var settings = UserSettings.Detached();
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, AnnotationKind.Underline);
        Assert.Equal(MarkupColor.Blue, tab.ActiveColor);

        tab.SetColorCommand.Execute(MarkupColor.Green);
        Assert.Equal(MarkupColor.Green, settings.Current.UnderlineColor);
        Assert.Equal(MarkupColor.Yellow, settings.Current.HighlightColor);
        Assert.Equal(MarkupColor.Pink, settings.Current.StrikeOutColor);

        tab.SetToolCommand.Execute(AnnotationKind.Highlight);
        Assert.Equal(MarkupColor.Yellow, tab.ActiveColor);
        tab.SetColorCommand.Execute(MarkupColor.Orange);
        tab.SetToolCommand.Execute(AnnotationKind.Underline);
        Assert.Equal(MarkupColor.Green, tab.ActiveColor);
        Assert.Equal(MarkupColor.Orange, settings.Current.HighlightColor);

        // The same tool again turns it off (only selecting); the colours show the last tool, and a colour turns it on again.
        tab.SetToolCommand.Execute(AnnotationKind.Underline);
        Assert.Null(tab.ActiveTool);
        Assert.Equal(MarkupColor.Green, tab.ActiveColor);
        tab.SetColorCommand.Execute(MarkupColor.Pink);
        Assert.Equal(AnnotationKind.Underline, tab.ActiveTool);
        Assert.Equal(MarkupColor.Pink, settings.Current.UnderlineColor);

        // Another tab of the app has the same colours; a markup takes the colour of its kind.
        var second = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        await BeginAsync(window, second, AnnotationKind.Underline);
        Assert.Equal(MarkupColor.Pink, second.ActiveColor);
        second.TextSelection = new TextSelection(new TextPosition(0, 0), new TextPosition(0, 5));
        viewModel.Commands.ById("annotate.underline")!.Execute();
        var annotation = Assert.Single(await AnnotationsAsync(window, second, 0, count: 1));
        Assert.Equal(MarkupColor.Pink, AnnotationPalette.Match(annotation.Color!.Value));
    }

    [AvaloniaFact]
    public async Task ModeBelongsToTheTab()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, first, AnnotationKind.StrikeOut);
        var toolbar = Find<AnnotationToolbar>(window, "AnnotationToolbar");
        Assert.True(toolbar.IsEffectivelyVisible);
        Assert.True(view.Margin.Top > 0);

        // The second tab reads: no toolbar, the document at the top of its area.
        var second = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        Assert.False(second.IsAnnotating);
        Assert.False(toolbar.IsVisible);
        Assert.Equal(0, view.Margin.Top);

        // Back to the first: its mode and tool are still there.
        viewModel.Tabs.Activate(first);
        Settle(window);
        Assert.True(first.IsAnnotating);
        Assert.Equal(AnnotationKind.StrikeOut, first.ActiveTool);
        Assert.True(toolbar.IsEffectivelyVisible);
        Assert.Same(first, toolbar.DataContext);
        Assert.True(toolbar.SwatchOf(MarkupColor.Pink).IsChecked);

        // Another document in the tab ends its mode.
        await first.OpenAsync(Copy("text-basic.pdf", "other.pdf"));
        await WaitUntilAsync(window, () => first.IsReady && first.FileName == "other.pdf");
        Assert.False(first.IsAnnotating);
        Assert.False(toolbar.IsVisible);
    }

    [AvaloniaFact]
    public async Task ClosingRightAfterAMarkupStillAsks()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var text = await TextAsync(window, tab, 0);
        await BeginAsync(window, tab, AnnotationKind.Highlight);

        // Released and closed in the same turn of the dispatcher: the markup is on its way, not on the history yet.
        var from = ScreenPoint(view, window, 0, LeftHalf(text.Chars[0].Box));
        var to = ScreenPoint(view, window, 0, RightHalf(text.Chars[7].Box));
        window.MouseDown(from, MouseButton.Left);
        Settle(window);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseUp(to, MouseButton.Left);
        Assert.True(tab.HasEditsInFlight || tab.AsksBeforeClosing);
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges });
        Assert.Contains(tab, viewModel.Tabs);
        Assert.Single(await MarkupsAsync(tab, 0));
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Settle(window);
        Assert.Contains(tab, viewModel.Tabs);

        // The same for an operation the editor took a moment ago, in a clean tab: right after the call it is busy (the
        // PDFium thread may even have recorded it already; the Core test IsBusyFromTheCallUntilTheStepIsRecorded holds the
        // thread to show the busy state alone). Either way closing asks.
        var second = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        var apply = second.Editor.ApplyMarkupAsync(0, AnnotationKind.Highlight, [new PageRect(72, 72, 220, 90)], AnnotationPalette.ColorOf(MarkupColor.Yellow), null, null);
        Assert.True(second.Editor.IsBusy || second.Editor.IsDirty);
        Assert.True(second.AsksBeforeClosing);
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges } question && ReferenceEquals(question.Tab, second));
        Assert.NotNull(await apply);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Settle(window);
        Assert.Contains(second, viewModel.Tabs);

        // Without a change in flight and without changes a tab closes at once.
        var clean = await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        Assert.False(clean.AsksBeforeClosing);
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitUntilAsync(window, () => !viewModel.Tabs.Contains(clean));
        Assert.Null(viewModel.PendingSave);
    }

    [AvaloniaFact]
    public async Task AMarkupOnARotatedPageSitsOnTheSelectedText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "rotated-page.pdf");
        var text = await TextAsync(window, tab, 0);
        var start = text.GetText(0, text.Chars.Count).IndexOf("Rotated", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var expected = Assert.Single(text.RectsOf(start, start + 7));
        await SelectAsync(window, view, 0, start, start + 7);
        viewModel.Commands.ById("annotate.highlight")!.Execute();
        await AnnotationsAsync(window, tab, 0, count: 1);
        await WaitUntilAsync(window, () => tab.IsDirty && !tab.HasEditsInFlight);

        // Saved and opened again: the quadrilateral covers the selected word (0.5 pt).
        Assert.True(await viewModel.SaveActiveTabAsync());
        await WaitUntilAsync(window, () => !tab.IsDirty && !tab.IsSaving);
        await using var reopened = await new PdfEngine().OpenAsync(tab.FullPath!, cancellationToken: TestContext.Current.CancellationToken);
        var annotation = Assert.Single(await reopened.GetAnnotationsAsync(0, TestContext.Current.CancellationToken));
        AssertRectNear(expected, Assert.Single(annotation.QuadRects), 0.5);
        AssertRectNear(expected, annotation.Bounds, 0.5);
    }

    [AvaloniaFact]
    public void CommandsHaveTitlesInBothLanguagesAndTheirShortcuts()
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), UserSettings.Detached());
        _cleanup.Add(viewModel);
        var catalog = viewModel.Commands;
        var expected = new (string Id, string English, string German, string Gesture)[]
        {
            ("annotate.mode", "Start or end commenting", "Kommentieren beginnen oder beenden", "Strg+Umschalt+K"),
            ("annotate.highlight", "Highlight", "Hervorheben", "Strg+Umschalt+H"),
            ("annotate.underline", "Underline", "Unterstreichen", "Strg+Umschalt+U"),
            ("annotate.strikeout", "Strike out", "Durchstreichen", "Strg+Umschalt+X"),
        };

        foreach (var (id, english, german, gesture) in expected)
        {
            var command = catalog.ById(id)!;
            Assert.True(command.Listed);
            Assert.Equal(CommandCatalog.EditGroup, command.Group);
            Strings.Culture = new CultureInfo("en");
            Assert.Equal(english, command.Title());
            Strings.Culture = new CultureInfo("de");
            Assert.Equal(german, command.Title());
            Assert.Equal(gesture, GestureText.Of(command.Gesture!));
        }

        // The Esc of the mode is not a key of the window: popups, menus and a dragged tab keep their Esc.
        Assert.DoesNotContain(catalog.All, command => command.Id.StartsWith("annotate.", StringComparison.Ordinal) && command.Gestures.Any(gesture => gesture.Key == Key.Escape));
    }

    [AvaloniaFact]
    public async Task OverlappingEditsSettleWithoutSpinning()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, tool: null);

        // Two edits in flight, the second behind the first; their continuations come back to the UI thread, the gates open
        // on a pool thread (as the PDFium thread finishes an operation). The wait must end once both did.
        var first = new TaskCompletionSource();
        var second = new TaskCompletionSource();
        var a = tab.RunEditAsync(_ => first.Task);
        var b = tab.RunEditAsync(async _ =>
        {
            await first.Task;
            await second.Task;
        });
        Assert.True(tab.HasEditsInFlight);
        Assert.True(tab.AsksBeforeClosing);
        var settled = tab.WhenEditsSettledAsync();
        Assert.False(settled.IsCompleted);

        await Task.Run(() =>
        {
            first.SetResult();
            second.SetResult();
        });
        await WaitUntilAsync(window, () => settled.IsCompleted);
        Assert.True(a.IsCompletedSuccessfully && b.IsCompletedSuccessfully);
        Assert.False(tab.HasEditsInFlight);
        Assert.False(tab.AsksBeforeClosing);
    }

    [AvaloniaFact]
    public async Task AnEditThatThrowsAtOnceLeavesNothingPending()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab, tool: null);

        var thrown = tab.RunEditAsync(_ => throw new InvalidOperationException("Refused."));
        Assert.True(thrown.IsCompletedSuccessfully);
        Assert.False(tab.HasEditsInFlight);
        Assert.False(tab.AsksBeforeClosing);
        Assert.False(tab.IsDirty);
        Assert.True(tab.WhenEditsSettledAsync().IsCompleted);

        // Not stuck asking: the tab closes at once.
        viewModel.CloseActiveTabCommand.Execute(null);
        await WaitUntilAsync(window, () => !viewModel.Tabs.Contains(tab));
        Assert.Null(viewModel.PendingSave);
    }

    [AvaloniaFact]
    public async Task ClosingWithTwoOverlappingEditsAsksAndKeepsBoth()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, _) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null);
        Assert.True(await viewModel.SaveActiveTabAsync());
        await WaitUntilAsync(window, () => !tab.IsDirty && !tab.IsSaving && comments.SelectedItem is not null);

        // A colour and a delete in one turn, then closing right away: the close waits for both and asks. The editor holds both
        // operations at the gate until the test lets them go, so "in flight" does not depend on how fast the PDFium thread is.
        var editor = tab.Editor;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.BeforeOperation = () => gate.Task;
        try
        {
            comments.SetItemColorCommand.Execute(MarkupColor.Green);
            comments.DeleteCommand.Execute(null);
            Assert.True(tab.HasEditsInFlight);
            viewModel.CloseActiveTabCommand.Execute(null);

            // The close waits: no question and no closed tab while an edit is held.
            Settle(window);
            Assert.True(tab.HasEditsInFlight);
            Assert.Null(viewModel.PendingSave);
            Assert.Contains(tab, viewModel.Tabs);
        }
        finally
        {
            editor.BeforeOperation = null;
            gate.TrySetResult();
        }

        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges });
        Assert.False(tab.HasEditsInFlight);
        Assert.Empty(await MarkupsAsync(tab, 0));
        Assert.True(tab.CanUndo);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Settle(window);
        Assert.Contains(tab, viewModel.Tabs);

        // Both steps are on the history: undo the delete (the green markup comes back), then the colour.
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => comments.Items.Count == 1 && !tab.HasEditsInFlight);
        Assert.Equal(MarkupColor.Green, AnnotationPalette.Match(Assert.Single(await MarkupsAsync(tab, 0)).Color!.Value));
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight && comments.Items.Count == 1
            && comments.Items[0].Annotation.Color is { } color && AnnotationPalette.Match(color) == MarkupColor.Yellow);
    }

    [AvaloniaFact]
    public async Task EscAnywhereInThePanelEndsTheModeButTheFieldKeepsItsOwn()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, _) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentDeleteButton"));

        // The comment field: Esc drops what was typed, the mode stays.
        var box = Find<TextBox>(window, "CommentBox");
        box.Focus();
        Settle(window);
        window.KeyTextInput("x");
        Press(window, Key.Escape, PhysicalKey.Escape, None);
        Assert.True(tab.IsAnnotating);

        // The filter, the button "Comment", the button "Delete": Esc ends the mode from each of them.
        foreach (var name in new[] { "FilterBox", "AnnotateButton", "CommentDeleteButton" })
        {
            if (!tab.IsAnnotating)
            {
                await BeginAsync(window, tab, tool: null);
                await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentDeleteButton"));
            }

            Find<Control>(window, name).Focus(NavigationMethod.Tab);
            Settle(window);
            Press(window, Key.Escape, PhysicalKey.Escape, None);
            Assert.False(tab.IsAnnotating, name);
        }

        // A waiting address goes first, also with the keys in the panel or in the toolbar: Esc drops it, the mode stays.
        await BeginAsync(window, tab, tool: null);
        foreach (var name in new[] { "FilterBox", "DoneButton" })
        {
            Find<Control>(window, name).Focus(NavigationMethod.Tab);
            Settle(window);
            tab.FollowLink(new PageLink([new PageRect(0, 0, 10, 10)], new UriTarget("https://example.org/docs")));
            Settle(window);
            Assert.NotNull(viewModel.PendingLink);
            Press(window, Key.Escape, PhysicalKey.Escape, None);
            Assert.Null(viewModel.PendingLink);
            Assert.True(tab.IsAnnotating, name);
        }

        // The button "Comment" names its shortcut.
        var button = Find<ActionToggleButton>(window, "AnnotateButton");
        Assert.Equal("Start or end commenting (Ctrl+Shift+K)", ToolTip.GetTip(button));
        Assert.Equal("Ctrl+Shift+K", Avalonia.Automation.AutomationProperties.GetAcceleratorKey(button));
    }

    [AvaloniaFact]
    public async Task PanelTabOrderIsButtonFilterListEditor()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        var (comments, list) = await OpenPanelAsync(window, viewModel, tab);
        await MarkWithCommandAsync(window, view, tab, viewModel);
        await WaitUntilAsync(window, () => comments.SelectedItem is not null && IsShown(window, "CommentDeleteButton"));

        Find<Control>(window, "AnnotateButton").Focus(NavigationMethod.Tab);
        Settle(window);
        var order = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            Press(window, Key.Tab, PhysicalKey.Tab, None);
            var focused = window.FocusManager!.GetFocusedElement() as Control;
            order.Add(ReferenceEquals(focused?.FindAncestorOfType<ListBox>(includeSelf: true), list) ? "CommentList" : focused?.Name ?? "none");
        }

        Assert.Equal(["FilterBox", "CommentList", "CommentBox", "CommentColorButton", "CommentDeleteButton"], order);
    }

    private static void AssertRectNear(PageRect expected, PageRect actual, double tolerance)
    {
        Assert.True(Math.Abs(expected.Left - actual.Left) <= tolerance, $"left {expected.Left} vs {actual.Left}");
        Assert.True(Math.Abs(expected.Top - actual.Top) <= tolerance, $"top {expected.Top} vs {actual.Top}");
        Assert.True(Math.Abs(expected.Right - actual.Right) <= tolerance, $"right {expected.Right} vs {actual.Right}");
        Assert.True(Math.Abs(expected.Bottom - actual.Bottom) <= tolerance, $"bottom {expected.Bottom} vs {actual.Bottom}");
    }

    /// <summary>The first character of the last word of a page that is not generated (the start of a selection to the next page).</summary>
    private static int LastWordStart(PageText text)
    {
        var end = text.Chars.Count;
        while (end > 0 && (text.Chars[end - 1].IsGenerated || char.IsWhiteSpace(text.Chars[end - 1].Text, 0)))
        {
            end--;
        }

        var start = end - 1;
        while (start > 0 && !text.Chars[start - 1].IsGenerated && !char.IsWhiteSpace(text.Chars[start - 1].Text, 0))
        {
            start--;
        }

        return start;
    }

    /// <summary>Starts the mode of the tab through its command and chooses <paramref name="tool"/>; waits until the toolbar shows.</summary>
    private static async Task BeginAsync(Window window, TabViewModel tab, AnnotationKind? tool)
    {
        if (!tab.IsAnnotating)
        {
            tab.BeginAnnotateCommand.Execute(null);
            await WaitUntilAsync(window, () => tab.IsAnnotating && FindOrNull<AnnotationToolbar>(window, "AnnotationToolbar") is { IsEffectivelyVisible: true });
        }

        if (tool is { } kind && tab.ActiveTool != kind)
        {
            tab.SetToolCommand.Execute(kind);
        }

        Assert.Equal(tool, tab.ActiveTool);
    }

    /// <summary>Highlights "Handbuch" on page 1 with the command (the mode starts) and waits until it is recorded.</summary>
    private static async Task MarkWithCommandAsync(Window window, DocumentView view, TabViewModel tab, MainViewModel viewModel)
    {
        await SelectAsync(window, view, 0, 9, 17);
        viewModel.Commands.ById("annotate.highlight")!.Execute();
        await AnnotationsAsync(window, tab, 0, count: 1);
        await WaitUntilAsync(window, () => tab.IsDirty && tab.CanUndo && !tab.HasEditsInFlight);
    }

    /// <summary>
    /// Waits until a page has <paramref name="count"/> markups (of <paramref name="kind"/>, if given; links and other
    /// annotations of the file do not count) and returns them.
    /// </summary>
    private static async Task<PdfAnnotation[]> AnnotationsAsync(Window window, TabViewModel tab, int page, int count, AnnotationKind? kind = null)
    {
        PdfAnnotation[] found = [];
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (!tab.HasEditsInFlight && tab.Session.Document is { } document)
            {
                var annotations = await document.GetAnnotationsAsync(page, TestContext.Current.CancellationToken);
                found = annotations.Where(annotation => kind is { } wanted ? annotation.Kind == wanted : annotation.Kind != AnnotationKind.Other).ToArray();
                if (found.Length == count)
                {
                    return found;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Page {page} has {found.Length} markups, not {count}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The highlights, underlines and strike-outs of a page as the document has them now.</summary>
    private static async Task<PdfAnnotation[]> MarkupsAsync(TabViewModel tab, int page) =>
        (await tab.Session.Document!.GetAnnotationsAsync(page, TestContext.Current.CancellationToken))
            .Where(annotation => annotation.Kind != AnnotationKind.Other)
            .ToArray();

    private static async Task<(CommentsViewModel Comments, ListBox List)> OpenPanelAsync(Window window, MainViewModel viewModel, TabViewModel tab)
    {
        if (tab.SidePanel != SidePanelKind.Comments)
        {
            viewModel.ToggleSidePanelCommand.Execute(SidePanelKind.Comments);
        }

        var comments = tab.Comments;
        await WaitUntilAsync(window, () => !comments.IsLoading && (IsShown(window, "CommentList") || IsShown(window, "CommentsEmptyHint")));
        return (comments, Find<ListBox>(window, "CommentList"));
    }

    private static async Task<TextSelection?> SelectAsync(Window window, DocumentView view, int page, int first, int last)
    {
        var task = view.SelectAsync(page, first, last);
        await WaitUntilAsync(window, () => task.IsCompleted);
        Settle(window);
        return await task;
    }

    private static async Task<PageText> TextAsync(Window window, TabViewModel tab, int page)
    {
        var source = tab.Session.EnsurePageTexts() ?? throw new InvalidOperationException("No document.");
        var task = source.GetAsync(page);
        await WaitUntilAsync(window, () => task.IsCompleted);
        return await task;
    }

    private static Point LeftHalf(PageRect box) => new(box.Left + (box.Width / 4), (box.Top + box.Bottom) / 2);

    private static Point RightHalf(PageRect box) => new(box.Right - (box.Width / 4), (box.Top + box.Bottom) / 2);

    private static Point Centre(PageRect box) => new((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);

    /// <summary>The window point of a point on a page (PDF points from the top left of the page as displayed).</summary>
    private static Point ScreenPoint(DocumentView view, Window window, int page, Point pagePoint)
    {
        var layout = view.Layout!;
        var rect = layout.GetPageRect(page);
        var scale = DocumentLayout.DipsPerPoint * layout.Zoom;
        var canvasPoint = new Point(rect.X - view.Canvas.Viewport.X + (pagePoint.X * scale), rect.Y - view.Canvas.Viewport.Y + (pagePoint.Y * scale));
        return view.Canvas.TranslatePoint(canvasPoint, window)!.Value;
    }

    private static Point CenterOf(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

    private static void Click(Window window, Control control) => Click(window, CenterOf(window, control));

    private static void Click(Window window, Point point, int clicks = 1)
    {
        Settle(window);

        // Without settling in between, so the clicks come within the double-click time.
        for (var i = 0; i < clicks; i++)
        {
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
        }

        Settle(window);
    }

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Settle(window);
        window.MouseMove(from + ((to - from) / 2), RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Settle(window);
        window.MouseUp(to, MouseButton.Left);
        Settle(window);
    }

    private static void Press(Window window, Key key, PhysicalKey physicalKey, RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, physicalKey, null);
        window.KeyRelease(key, modifiers, physicalKey, null);
        Settle(window);
    }

    private static bool IsShown(Window window, string name) =>
        window.GetVisualDescendants().OfType<Control>().Any(control => control.Name == name && control.IsEffectivelyVisible);

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
        FindOrNull<T>(root, name) ?? throw new InvalidOperationException($"No {typeof(T).Name} named {name}.");

    private static T? FindOrNull<T>(Visual root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private string Copy(string fixture, string? name = null)
    {
        var path = System.IO.Path.Combine(_folder, name ?? fixture);
        File.Copy(TestData.Pdf(fixture), path);
        return path;
    }

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings? settings = null, double height = 720)
    {
        // Forced, so the layout is the same on Windows and Linux runners.
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = height, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, Find<DocumentView>(window, "DocumentView"));
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

    private sealed class RecordingClipboard : IClipboardService
    {
        public string? Text { get; private set; }

        public Task SetTextAsync(string plainText, string? htmlFragment)
        {
            Text = plainText;
            return Task.CompletedTask;
        }
    }
}
