using Avalonia;
using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;

namespace Pagewing.App.Views;

/// <summary>
/// The field of a text box on the page (milestone 10): with the text box tool a drag on a page opens it over the dragged box,
/// a click over a box of <see cref="TextBoxSpec.DefaultWidth"/> × <see cref="TextBoxSpec.DefaultHeight"/> points at the click
/// (moved onto the page); Enter on a selected text box of Pagewing's opens it with its text. Ctrl+Enter or leaving the field
/// applies the text (<see cref="TabViewModel.ApplyTextBoxAsync"/>, <see cref="TabViewModel.ReplaceTextBoxTextAsync"/>); a
/// blank text makes nothing; a text with characters a text box cannot show keeps the field open with the reason. Esc drops
/// the text. Another tab, document or the end of the mode closes the field and applies a text it can apply. The field
/// follows its box when the view scrolls or zooms. A press on the page right after the field was left only ends it.
/// </summary>
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

            // Above the pages and the scroll viewer: the field takes the keys and the pointer.
            DocumentLayers.Children.Add(_textBoxEditor);
        }

        var editor = _textBoxEditor;
        editor.Start(tab, page, box, target, text, fontSize, color);
        editor.IsVisible = true;
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
        if (!ReferenceEquals(editor, TextBoxEditor))
        {
            return;
        }

        if (TabViewModel.TextBoxProblemOf(editor.Text) is { } problem)
        {
            // Never changed silently: the field stays and says why (also when the focus left it).
            editor.ShowProblem(problem);
            return;
        }

        if (focusLeft)
        {
            _textBoxJustClosed = true;
            Dispatcher.UIThread.Post(() => _textBoxJustClosed = false, DispatcherPriority.Background);
        }

        CloseTextBoxEditor(apply: true);
        if (!focusLeft)
        {
            Focus();
        }
    }

    /// <summary>
    /// Closes the field: with <paramref name="apply"/> its text becomes the text box (if it can; a text it cannot apply is
    /// dropped only when the field must go, as with another tab), otherwise it is dropped.
    /// </summary>
    private void CloseTextBoxEditor(bool apply)
    {
        if (TextBoxEditor is not { Tab: { } tab } editor)
        {
            return;
        }

        var text = editor.Text;
        editor.IsVisible = false;
        if (apply && TabViewModel.TextBoxProblemOf(text) is null)
        {
            if (editor.Target is { } target)
            {
                _ = tab.ReplaceTextBoxTextAsync(target, text);
            }
            else
            {
                _ = tab.ApplyTextBoxAsync(editor.Page, editor.Box, text);
            }
        }
    }

    /// <summary>After a change of the tab: the field closes (applying what it can) when the mode ended or the tab cannot take an edit.</summary>
    private void CheckTextBoxEditor()
    {
        if (TextBoxEditor is { Tab: { } tab } && (!ReferenceEquals(tab, _viewModel) || !tab.IsAnnotating || !tab.CanDraw))
        {
            CloseTextBoxEditor(apply: true);
        }
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
