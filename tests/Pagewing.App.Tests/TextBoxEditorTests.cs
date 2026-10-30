using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pagewing.App.Commands;
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
/// The text box tool (milestone 10): the field on the page that a drag or click of the tool opens (Ctrl+Enter or leaving it
/// applies the text, Esc drops it, Ctrl+Z undoes its text only, a blank text makes nothing, a text with characters a text
/// box cannot show keeps it open with the reason), Enter on a selected text box, moving it with the select tool, the
/// command and the toolbar. Every file is a copy in a temp folder; settings are in memory.
/// </summary>
[Collection("Localization")]
public sealed class TextBoxEditorTests : IDisposable
{
    private const RawInputModifiers Left = RawInputModifiers.LeftMouseButton;
    private const RawInputModifiers Ctrl = RawInputModifiers.Control;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly CultureInfo _originalCulture = Strings.Culture;
    private readonly List<IDisposable> _cleanup = [];
    private readonly string _folder = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pagewing-textbox-" + Guid.NewGuid().ToString("N"))).FullName;

    public TextBoxEditorTests() => Strings.Culture = new CultureInfo("en");

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
    public async Task CtrlEnterCommits()
    {
        var settings = UserSettings.Detached();
        settings.Update(current => current with { AuthorName = "Ada" });
        var (window, viewModel, view) = Show(settings);
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        var editor = OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        Assert.Equal(0, editor.Page);
        AssertRectNear(new PageRect(60, 80, 260, 120), editor.Box, 0.5);
        Assert.True(editor.Field.IsKeyboardFocusWithin);
        Assert.False(tab.HasEditor);

        window.KeyTextInput("Grüße aus Köln – 5 €");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);

        Assert.Null(view.TextBoxEditor);
        var box = Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1));
        Assert.Equal("Grüße aus Köln – 5 €", box.Contents);
        Assert.Equal("Ada", box.Author);
        Assert.Equal(16, box.TextBox!.FontSize);
        Assert.Equal(DrawPalette.ColorOf(DrawColor.Red), box.TextBox.Color);
        Assert.Equal(60, box.TextBox.Box.Left, 0.5);
        Assert.Equal(80, box.TextBox.Box.Top, 0.5);
        Assert.Equal(260, box.TextBox.Box.Right, 0.5);
        Assert.True(box.GeometryEditable);
        await WaitUntilAsync(window, () => tab.IsDirty && tab.CanUndo);
        Assert.Equal(AnnotationKind.TextBox, tab.ActiveTool);
    }

    [AvaloniaFact]
    public async Task EmptyTextCreatesNothing()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("   ");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);

        Assert.Null(view.TextBoxEditor);
        Settle(window);
        Assert.False(tab.HasEditsInFlight);
        Assert.False(tab.IsDirty);
        Assert.Empty(await TextBoxesAsync(tab));
    }

    [AvaloniaFact]
    public async Task EscDiscards()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Dropped");
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);

        Assert.Null(view.TextBoxEditor);
        Assert.True(tab.IsAnnotating);
        Settle(window);
        Assert.False(tab.IsDirty);
        Assert.Empty(await TextBoxesAsync(tab));
    }

    [AvaloniaFact]
    public async Task FocusLossCommits()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Left behind");
        view.Focus();
        Settle(window);

        Assert.Null(view.TextBoxEditor);
        Assert.Equal("Left behind", Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1)).Contents);
    }

    [AvaloniaFact]
    public async Task CtrlZInTheFieldUndoesTextOnly()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("First");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        await WaitForTextBoxesAsync(window, tab, count: 1);
        await WaitUntilAsync(window, () => tab.CanUndo);

        var editor = OpenByDrag(window, view, new Point(60, 200), new Point(260, 240));
        window.KeyTextInput("abc");
        Settle(window);
        Assert.Equal("abc", editor.Text);
        Press(window, Key.Z, PhysicalKey.Z, Ctrl);

        // The field undid its typing; the document still has its text box and its step.
        Assert.NotEqual("abc", editor.Text);
        Assert.Same(editor, view.TextBoxEditor);
        Assert.True(tab.CanUndo);
        Assert.Equal("First", Assert.Single(await TextBoxesAsync(tab)).Contents);
    }

    [AvaloniaFact]
    public async Task UnsupportedCharactersKeepTheFieldOpenWithTheReason()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);

        var editor = OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Ωmega 😀");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);

        Assert.Same(editor, view.TextBoxEditor);
        Assert.True(editor.HasProblem);
        Assert.Contains("Ω", editor.ProblemText.Text, StringComparison.Ordinal);
        Assert.Contains("😀", editor.ProblemText.Text, StringComparison.Ordinal);

        // Leaving the field does not drop the text either; typing hides the reason, Esc drops it.
        view.Focus();
        Settle(window);
        Assert.Same(editor, view.TextBoxEditor);
        Assert.Empty(await TextBoxesAsync(tab));
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        editor.FocusField();
        Settle(window);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);
        Assert.Null(view.TextBoxEditor);
        Assert.False(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task AClickOpensADefaultBoxOnThePage()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var size = view.Layout!.GetPageSize(0);

        Click(window, ScreenPoint(view, window, 0, new Point(50, 60)));
        var editor = view.TextBoxEditor!;
        AssertRectNear(new PageRect(50, 60, 50 + TextBoxSpec.DefaultWidth, 60 + TextBoxSpec.DefaultHeight), editor.Box, 0.5);
        Press(window, Key.Escape, PhysicalKey.Escape, RawInputModifiers.None);

        // Near the right and bottom edges the box moves onto the page.
        Click(window, ScreenPoint(view, window, 0, new Point(size.Width - 5, size.Height - 5)));
        editor = view.TextBoxEditor!;
        Assert.Equal(size.Width, editor.Box.Right, 0.5);
        Assert.Equal(size.Height, editor.Box.Bottom, 0.5);
    }

    [AvaloniaFact]
    public async Task EnterOnASelectedTextBoxEditsItsTextAsOneStep()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Before");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        await WaitForTextBoxesAsync(window, tab, count: 1);

        tab.SelectToolCommand.Execute(null);
        Click(window, ScreenPoint(view, window, 0, new Point(100, 95)));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Kind == AnnotationKind.TextBox);
        Assert.True(tab.CanMoveSelection);
        view.Focus();
        Settle(window);

        Press(window, Key.Enter, PhysicalKey.Enter, RawInputModifiers.None);
        var editor = view.TextBoxEditor!;
        Assert.Equal("Before", editor.Text);
        Assert.NotNull(editor.Target);
        editor.Field.SelectAll();
        window.KeyTextInput("After");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        var after = Assert.Single(await TextBoxesAsync(tab));
        Assert.Equal("After", after.Contents);
        Assert.Equal(tab.SelectedAnnotation?.Name, after.Name);

        // One step: undo brings the old text back.
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal("Before", Assert.Single(await TextBoxesAsync(tab)).Contents);
    }

    [AvaloniaFact]
    public async Task TheSelectToolMovesAndResizesATextBox()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Move me");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        var created = Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1));

        tab.SelectToolCommand.Execute(null);
        Click(window, ScreenPoint(view, window, 0, new Point(150, 100)));
        await WaitUntilAsync(window, () => tab.SelectedAnnotation?.Kind == AnnotationKind.TextBox);

        Drag(window, ScreenPoint(view, window, 0, new Point(150, 100)), ScreenPoint(view, window, 0, new Point(190, 160)));
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        var moved = Assert.Single(await TextBoxesAsync(tab));
        Assert.Equal(created.Name, moved.Name);
        Assert.Equal(100, moved.TextBox!.Box.Left, 1);
        Assert.Equal(140, moved.TextBox.Box.Top, 1);
        Assert.Equal("Move me", moved.Contents);

        // The toolbar size of a selected text box changes its font size (one step).
        tab.SetSelectionWidthCommand.Execute(StrokeWidth.Thick);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal(24, Assert.Single(await TextBoxesAsync(tab)).TextBox!.FontSize);
        await WaitUntilAsync(window, () => tab.CanUndo);
        tab.UndoCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal(16, Assert.Single(await TextBoxesAsync(tab)).TextBox!.FontSize);
    }

    [AvaloniaFact]
    public async Task TheCommandAndTheToolbarOfferTheTextBox()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");

        var command = new CommandCatalog(viewModel).ById("annotate.textbox")!;
        Assert.Equal(new KeyGesture(Key.T, KeyModifiers.Control | KeyModifiers.Shift), command.Gesture);
        tab.TextBoxCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsAnnotating && tab.ActiveTool == AnnotationKind.TextBox);

        var toolbar = window.GetVisualDescendants().OfType<AnnotationToolbar>().Single();
        var tool = toolbar.GetVisualDescendants().OfType<Control>().Single(c => c.Name == "TextBoxTool");
        Assert.True(((Controls.ActionToggleButton)tool).IsChecked);
        Assert.Equal(Strings.TextBoxSizeName(StrokeWidth.Medium), Avalonia.Automation.AutomationProperties.GetName(toolbar.WidthButtonOf(StrokeWidth.Medium)));
        Assert.True(toolbar.WidthButtonOf(StrokeWidth.Medium).IsChecked);

        // A size step is the font size of the tool.
        toolbar.WidthButtonOf(StrokeWidth.Thin).Command!.Execute(StrokeWidth.Thin);
        Assert.Equal(12, tab.TextBoxFontSize);

        // With a drawing tool the same buttons are stroke widths again.
        tab.SetToolCommand.Execute(AnnotationKind.Rectangle);
        Settle(window);
        Assert.Equal(Strings.StrokeWidthName(StrokeWidth.Medium), Avalonia.Automation.AutomationProperties.GetName(toolbar.WidthButtonOf(StrokeWidth.Medium)));
    }

    [AvaloniaFact]
    public async Task TheCommentsPanelListsATextBoxWithItsText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Listed text");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        await WaitForTextBoxesAsync(window, tab, count: 1);

        tab.SidePanel = SidePanelKind.Comments;
        await WaitUntilAsync(window, () => tab.Comments.Items.OfType<CommentItemViewModel>().Any(i => i.IsTextBox));
        var item = tab.Comments.Items.OfType<CommentItemViewModel>().Single(i => i.IsTextBox);
        Assert.Equal(Strings.ToolTextBox, item.KindText);
        Assert.Equal("Listed text", item.Comment);
        Assert.Null(item.LockReason);
    }

    [AvaloniaFact]
    public async Task CtrlSWhileTheFieldHasFocusSavesTheTypedText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Saved with Ctrl+S");
        Settle(window);

        // The open field is an unsaved change: Save runs, applies the text first and writes it.
        Assert.True(tab.IsDirty);
        Press(window, Key.S, PhysicalKey.S, Ctrl);
        await WaitUntilAsync(window, () => !tab.IsSaving && !tab.IsDirty && view.TextBoxEditor is null);

        await using var saved = await new PdfEngine().OpenAsync(tab.FullPath!, cancellationToken: TestContext.Current.CancellationToken);
        var box = Assert.Single(await saved.GetAnnotationsAsync(0, TestContext.Current.CancellationToken), a => a.Kind == AnnotationKind.TextBox);
        Assert.Equal("Saved with Ctrl+S", box.Contents);
    }

    [AvaloniaFact]
    public async Task ModeEndWithTheFieldOpenAppliesTheText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Kept at the end of the mode");
        Settle(window);

        Press(window, Key.K, PhysicalKey.K, Ctrl | RawInputModifiers.Shift);

        Assert.False(tab.IsAnnotating);
        Assert.Null(view.TextBoxEditor);
        Assert.Equal("Kept at the end of the mode", Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1)).Contents);
    }

    [AvaloniaFact]
    public async Task PrintWithTheFieldOpenAppliesTheText()
    {
        var (window, viewModel, view) = Show();
        viewModel.PrintServiceFactory = () => new FakePrintService();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Printed");
        Settle(window);

        Press(window, Key.P, PhysicalKey.P, Ctrl);
        await WaitUntilAsync(window, () => viewModel.Overlay == OverlayKind.Print);

        Assert.Null(view.TextBoxEditor);
        Assert.Equal("Printed", Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1)).Contents);
    }

    [AvaloniaFact]
    public async Task CloseTabWithTheFieldOpenDoesNotLoseTheText()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Do not lose me");
        Settle(window);

        // Ctrl+W applies the text and asks about the change, as for any other one; "Cancel" keeps the tab with the text box.
        Press(window, Key.W, PhysicalKey.W, Ctrl);
        await WaitUntilAsync(window, () => viewModel.PendingSave is { Kind: PendingSaveKind.SaveChanges });
        Assert.Contains(tab, viewModel.Tabs);
        Assert.True(viewModel.PendingSave!.Resolve(PendingSaveChoice.Cancel));
        Settle(window);
        Assert.Contains(tab, viewModel.Tabs);
        Assert.Equal("Do not lose me", Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1)).Contents);
        Assert.True(tab.IsDirty);
    }

    [AvaloniaFact]
    public async Task RefusedApplyKeepsTheFieldAndShowsAHint()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        var editor = OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Not now");
        Settle(window);

        // While the tab takes no edit (here: the lock of an update), the field stays open with the text and says why.
        tab.IsEditingFrozen = true;
        Settle(window);
        Assert.Same(editor, view.TextBoxEditor);
        Assert.True(editor.HasProblem);
        Assert.Equal(Strings.TextBoxNotNow, editor.ProblemText.Text);
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        Assert.Same(editor, view.TextBoxEditor);
        Assert.Equal("Not now", editor.Text);
        Assert.Empty(await TextBoxesAsync(tab));
        Assert.True(tab.AsksBeforeClosing);

        // Once the tab takes edits again, Ctrl+Enter applies it.
        tab.IsEditingFrozen = false;
        editor.FocusField();
        Settle(window);
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        Assert.Equal("Not now", Assert.Single(await WaitForTextBoxesAsync(window, tab, count: 1)).Contents);
    }

    [AvaloniaFact]
    public async Task AFieldTheViewLeavesWithoutApplyingComesBackWithTheTab()
    {
        var (window, viewModel, view) = Show();
        var first = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, first);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Ωmega");
        Settle(window);

        // Another tab: the text cannot be applied (a character a text box cannot show), so it stays with its tab.
        await OpenCopyAsync(window, viewModel, view, "text-basic.pdf");
        Assert.Null(view.TextBoxEditor);
        Assert.Equal("Ωmega", first.PendingTextBoxDraft?.Text);
        Assert.True(first.AsksBeforeClosing);

        // Back on the tab, its field opens again with the text and the reason.
        viewModel.Tabs.Activate(first);
        await WaitUntilAsync(window, () => view.TextBoxEditor is not null);
        Assert.Equal("Ωmega", view.TextBoxEditor!.Text);
        Assert.True(view.TextBoxEditor.HasProblem);
        Assert.Null(first.PendingTextBoxDraft);
    }

    [AvaloniaFact]
    public async Task PanelEditorNamesTheUnsupportedCharacters()
    {
        var (window, viewModel, view) = Show();
        var tab = await OpenCopyAsync(window, viewModel, view, "text-rich.pdf");
        await BeginAsync(window, tab);
        OpenByDrag(window, view, new Point(60, 80), new Point(260, 120));
        window.KeyTextInput("Panel text");
        Press(window, Key.Enter, PhysicalKey.Enter, Ctrl);
        await WaitForTextBoxesAsync(window, tab, count: 1);

        tab.SidePanel = SidePanelKind.Comments;
        await WaitUntilAsync(window, () => tab.Comments.Items.Any(i => i.IsTextBox));
        var comments = tab.Comments;
        comments.SelectedItem = comments.Items.Single(i => i.IsTextBox);
        Settle(window);

        // The comment of a text box is its text: a character it cannot show is named under the field, nothing is applied.
        comments.CommentDraft = "Ωmega";
        comments.CommitCommentCommand.Execute(null);
        Settle(window);
        Assert.NotNull(comments.CommentProblem);
        Assert.Contains("Ω", comments.CommentProblem, StringComparison.Ordinal);
        var problem = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CommentProblemText");
        Assert.True(problem.IsEffectivelyVisible);
        Assert.Equal(comments.CommentProblem, Avalonia.Automation.AutomationProperties.GetName(problem));
        Assert.Equal("Panel text", Assert.Single(await TextBoxesAsync(tab)).Contents);

        // An empty text says so as well; typing clears the reason, a good text is applied.
        comments.CommentDraft = string.Empty;
        comments.CommitCommentCommand.Execute(null);
        Assert.Equal(Strings.TextBoxEmpty, comments.CommentProblem);
        comments.CommentDraft = "New text";
        Assert.Null(comments.CommentProblem);
        comments.CommitCommentCommand.Execute(null);
        await WaitUntilAsync(window, () => !tab.HasEditsInFlight);
        Assert.Equal("New text", Assert.Single(await TextBoxesAsync(tab)).Contents);
    }

    private static TextBoxEditor OpenByDrag(Window window, DocumentView view, Point from, Point to)
    {
        Drag(window, ScreenPoint(view, window, 0, from), ScreenPoint(view, window, 0, to));
        Settle(window);
        return view.TextBoxEditor ?? throw new InvalidOperationException("The field of the text box did not open.");
    }

    private static void AssertRectNear(PageRect expected, PageRect actual, double tolerance) =>
        Assert.True(
            Math.Abs(expected.Left - actual.Left) <= tolerance && Math.Abs(expected.Top - actual.Top) <= tolerance
            && Math.Abs(expected.Right - actual.Right) <= tolerance && Math.Abs(expected.Bottom - actual.Bottom) <= tolerance,
            $"{expected} vs {actual}");

    private static async Task BeginAsync(Window window, TabViewModel tab)
    {
        tab.BeginAnnotateCommand.Execute(null);
        await WaitUntilAsync(window, () => tab.IsAnnotating);
        tab.SetToolCommand.Execute(AnnotationKind.TextBox);
        Assert.Equal(AnnotationKind.TextBox, tab.ActiveTool);
        Settle(window);
    }

    private static async Task<PdfAnnotation[]> TextBoxesAsync(TabViewModel tab) =>
        (await tab.Session.Document!.GetAnnotationsAsync(0, TestContext.Current.CancellationToken))
            .Where(annotation => annotation.Kind == AnnotationKind.TextBox)
            .ToArray();

    private static async Task<PdfAnnotation[]> WaitForTextBoxesAsync(Window window, TabViewModel tab, int count)
    {
        PdfAnnotation[] found = [];
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            Settle(window);
            if (!tab.HasEditsInFlight && tab.Session.Document is not null)
            {
                found = await TextBoxesAsync(tab);
                if (found.Length == count)
                {
                    return found;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"The page has {found.Length} text boxes, not {count}.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

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

    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Settle(window);
        window.MouseMove(from + ((to - from) / 2), Left);
        Settle(window);
        window.MouseMove(to, Left);
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

    private (MainWindow Window, MainViewModel ViewModel, DocumentView View) Show(UserSettings? settings = null)
    {
        var viewModel = new MainViewModel(() => new ViewerSession(new PdfEngine()), new NoFilePicker(), settings ?? UserSettings.Detached()) { IsWindowsTitleBar = true };
        _cleanup.Add(viewModel);
        var window = new MainWindow { DataContext = viewModel, Width = 1024, Height = 720, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light };
        _cleanup.Add(new Closer(window));
        window.Show();
        Settle(window);
        return (window, viewModel, window.GetVisualDescendants().OfType<DocumentView>().First(v => v.Name == "DocumentView"));
    }

    private async Task<TabViewModel> OpenCopyAsync(Window window, MainViewModel viewModel, DocumentView view, string name)
    {
        var path = System.IO.Path.Combine(_folder, Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.Copy(TestData.Pdf(name), path);
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
}
