using Avalonia;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// The field of a text box on the page (milestone 10): with the text box tool a drag on a page opens it over the dragged box,
/// a click over a box of <see cref="TextBoxSpec.DefaultWidth"/> × <see cref="TextBoxSpec.DefaultHeight"/> points at the click
/// (moved onto the page); Enter on a selected text box of Pagewing's opens it with its text. Ctrl+Enter or leaving the field
/// applies the text (<see cref="TabViewModel.ApplyTextBoxAsync"/>, <see cref="TabViewModel.ReplaceTextBoxTextAsync"/>); a
/// blank text makes nothing; Esc drops the text. The field follows its box when the view scrolls or zooms. A press on the
/// page right after the field was left only ends it.
/// </summary>
/// <remarks>
/// Typed text is never dropped silently. While a field is open the tab counts its text as an unsaved change
/// (<see cref="TabViewModel.HasTextBoxText"/>) and applies it through <see cref="TabViewModel.FlushTextBoxDraft"/> before
/// anything that ends or blocks editing (saving, printing, the end of the mode, closing, the question before an update),
/// while the tab still takes the edit. A text that cannot be applied (a character a text box cannot show, or the tab takes
/// no edit now) keeps the field open with the reason; when the view leaves the tab, such a text stays with the tab
/// (<see cref="TabViewModel.PendingTextBoxDraft"/>) and its field opens again when the view shows the tab.
/// </remarks>
public sealed partial class DocumentView
{
    /// <summary>A drag smaller than this in either direction (points) counts as a click: the box gets the default size.</summary>
    private const double MinTextBoxDrag = 16;

    private TextBoxEditor? _textBoxEditor;

    // The field was just left by a press on the page: that press only ends it (reset after the input of the press).
    private bool _textBoxJustClosed;

    /// <summary>Gets the open field of a text box, or <see langword="null"/> (for tests).</summary>
    internal TextBoxEditor? TextBoxEditor => _textBoxEditor is { IsVisible: true } editor ? editor : null;

    /// <summary>The box of a new text box: the dragged box, or for a click the default box at the point, kept on the page.</summary>
    internal static PageRect NewTextBox(DisplayPoint anchor, DisplayPoint end, bool dragged, double pageWidth, double pageHeight)
    {
        if (dragged && Math.Abs(end.X - anchor.X) >= MinTextBoxDrag && Math.Abs(end.Y - anchor.Y) >= MinTextBoxDrag)
        {
            return new PageRect(Math.Min(anchor.X, end.X), Math.Min(anchor.Y, end.Y), Math.Max(anchor.X, end.X), Math.Max(anchor.Y, end.Y));
        }

        var width = Math.Min(TextBoxSpec.DefaultWidth, pageWidth);
        var height = Math.Min(TextBoxSpec.DefaultHeight, pageHeight);
        var left = Math.Clamp(anchor.X, 0, Math.Max(0, pageWidth - width));
        var top = Math.Clamp(anchor.Y, 0, Math.Max(0, pageHeight - height));
        return new PageRect(left, top, left + width, top + height);
    }

    /// <summary>Opens the field of a new text box on <paramref name="page"/> in <paramref name="box"/>, in the style of the tool.</summary>
    private void OpenNewTextBox(TabViewModel tab, int page, PageRect box) =>
        OpenTextBoxEditor(tab, page, box, null, string.Empty, tab.TextBoxFontSize, tab.DrawColorOf(AnnotationKind.TextBox));

    /// <summary>Enter on a selected text box: its field opens with its text, size and colour.</summary>
    private void OnTextBoxEditRequested(PdfAnnotation target)
    {
        if (_viewModel is not { } tab || target.TextBox is not { } spec)
        {
            return;
        }

        OpenTextBoxEditor(tab, target.Page, spec.Box, target, spec.Text, spec.FontSize, spec.Color);
    }

    private void OpenTextBoxEditor(TabViewModel tab, int page, PageRect box, PdfAnnotation? target, string text, double fontSize, AnnotationColor color)
    {
        CloseTextBoxEditor(apply: true);
        if (_textBoxEditor is null)
        {
            _textBoxEditor = new TextBoxEditor { Name = "TextBoxEditor" };
            _textBoxEditor.CommitRequested += OnTextBoxCommitRequested;
            _textBoxEditor.DiscardRequested += _ => CloseTextBoxEditor(apply: false);
            _textBoxEditor.TextEdited += editor => editor.Tab?.SyncFromEditor();

            // Above the pages and the scroll viewer: the field takes the keys and the pointer.
            DocumentLayers.Children.Add(_textBoxEditor);
        }

        var editor = _textBoxEditor;
        editor.Start(tab, page, box, target, text, fontSize, color);
        editor.IsVisible = true;
        tab.TextBoxFieldFlush = () => FlushTextBoxField(editor);
        tab.TextBoxFieldHasText = () => ReferenceEquals(TextBoxEditor, editor) && !string.IsNullOrWhiteSpace(editor.Text);
        tab.SyncFromEditor();
        PlaceTextBoxEditor();

        // After the input of the press or key that opened it (the view takes the focus at a release), and laid out.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (ReferenceEquals(TextBoxEditor, editor))
                {
                    editor.FocusField();
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>The field follows its box when the view scrolls or zooms.</summary>
    private void PlaceTextBoxEditor()
    {
        if (TextBoxEditor is not { } editor || PageCanvas.PagePlacement(editor.Page) is not { } placement)
        {
            return;
        }

        var (pageRect, scale) = placement;
        var top = pageRect.Y + (editor.Box.Top * scale);
        editor.Place(pageRect.TopLeft, scale, pageRect.Bottom - top);
    }

    private void OnTextBoxCommitRequested(TextBoxEditor editor, bool focusLeft)
    {
        if (!ReferenceEquals(editor, TextBoxEditor) || !FlushTextBoxField(editor))
        {
            // The text stays in the field, which says why (also when the focus left it).
            return;
        }

        if (focusLeft)
        {
            _textBoxJustClosed = true;
            Dispatcher.UIThread.Post(() => _textBoxJustClosed = false, DispatcherPriority.Background);
        }
        else
        {
            Focus();
        }
    }

    /// <summary>
    /// Applies the text of the field now: a blank text closes it and makes nothing; a text a text box cannot show, or one the
    /// tab cannot take now, stays in the field with the reason; otherwise the field closes and the text becomes the text box.
    /// </summary>
    /// <returns>Whether nothing is left in the field (see <see cref="TabViewModel.TextBoxFieldFlush"/>).</returns>
    private bool FlushTextBoxField(TextBoxEditor editor)
    {
        if (!ReferenceEquals(editor, TextBoxEditor) || editor.Tab is not { } tab)
        {
            return true;
        }

        var text = editor.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            HideTextBoxEditor(editor);
            return true;
        }

        if (TabViewModel.TextBoxProblemOf(text) is { } problem)
        {
            editor.ShowProblem(problem);
            return false;
        }

        if (!tab.CanDraw)
        {
            editor.ShowProblem(Strings.TextBoxNotNow);
            return false;
        }

        HideTextBoxEditor(editor);
        if (editor.Target is { } target)
        {
            _ = tab.ReplaceTextBoxTextAsync(target, text);
        }
        else
        {
            _ = tab.ApplyTextBoxAsync(editor.Page, editor.Box, text);
        }

        return true;
    }

    /// <summary>
    /// Closes the field: with <paramref name="apply"/> its text becomes the text box (<see cref="FlushTextBoxField"/>); a text
    /// that cannot be applied now stays with the tab as its draft (the view left the tab) and nothing is lost. Without
    /// <paramref name="apply"/> (Esc) the text is dropped.
    /// </summary>
    private void CloseTextBoxEditor(bool apply)
    {
        if (TextBoxEditor is not { Tab: { } tab } editor)
        {
            return;
        }

        if (!apply)
        {
            HideTextBoxEditor(editor);
            return;
        }

        if (!FlushTextBoxField(editor))
        {
            tab.PendingTextBoxDraft = new TextBoxDraft(editor.Page, editor.Box, editor.Target, editor.Text, editor.FontSizePoints, editor.Color);
            HideTextBoxEditor(editor);
        }
    }

    /// <summary>Hides the field and lets its tab go (the tab no longer counts its text).</summary>
    private static void HideTextBoxEditor(TextBoxEditor editor)
    {
        editor.IsVisible = false;
        if (editor.Tab is { } tab)
        {
            tab.TextBoxFieldFlush = null;
            tab.TextBoxFieldHasText = null;
            tab.SyncFromEditor();
        }
    }

    /// <summary>After a change of the tab: a field whose tab takes no edit now says so (the text stays until it can be applied or Esc drops it).</summary>
    private void CheckTextBoxEditor()
    {
        if (TextBoxEditor is { Tab: { } tab } editor && ReferenceEquals(tab, _viewModel) && !tab.CanDraw && !editor.HasProblem)
        {
            editor.ShowProblem(Strings.TextBoxNotNow);
        }
    }

    /// <summary>The view shows a tab with a text box draft it kept: its field opens again with the text and the reason.</summary>
    private void ReopenTextBoxDraft()
    {
        if (_viewModel is not { PendingTextBoxDraft: { } draft } tab)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!ReferenceEquals(_viewModel, tab) || tab.PendingTextBoxDraft != draft || _layout is null)
                {
                    return;
                }

                tab.PendingTextBoxDraft = null;
                OpenTextBoxEditor(tab, draft.Page, draft.Box, draft.Target, draft.Text, draft.FontSize, draft.Color);
                if (TextBoxEditor is { } editor)
                {
                    editor.ShowProblem(TabViewModel.TextBoxProblemOf(draft.Text) ?? Strings.TextBoxNotNow);
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>A press on the page right after the field was left by it only ends the field (no new text box, no selection).</summary>
    private bool PressEndsTextBox()
    {
        if (!_textBoxJustClosed && TextBoxEditor is null)
        {
            return false;
        }

        _textBoxJustClosed = false;
        if (TextBoxEditor is { } editor)
        {
            // The field has a text it cannot apply: the press does not leave it.
            OnTextBoxCommitRequested(editor, focusLeft: true);
        }

        return true;
    }
}
