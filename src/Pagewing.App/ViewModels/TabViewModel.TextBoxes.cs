using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The text box tool of the mode "Comment" (milestone 10): a drag on a page (or a click, for a box of
/// <see cref="TextBoxSpec.DefaultWidth"/> × <see cref="TextBoxSpec.DefaultHeight"/>) opens a field on the page
/// (<c>Views/TextBoxEditor</c>), and its text becomes a text box in the colour and size of the tool's style (the width step of
/// the style is the size: <see cref="TextBoxSpec.FontSizeOf"/>). Enter on a selected text box of Pagewing's opens the field
/// with its text again. A text with characters a text box cannot show (<see cref="TextBoxCharacters"/>) is never made or
/// changed: the field stays open and says which ones.
/// </summary>
public sealed partial class TabViewModel
{
    private RelayCommand? _textBoxCommand;

    /// <summary>Ctrl+Shift+T: starts the mode with the text box tool, or turns it on in the mode.</summary>
    public ICommand TextBoxCommand => _textBoxCommand ??= DrawCommand(AnnotationKind.TextBox);

    /// <summary>
    /// Raised on the UI thread when a selected text box of Pagewing's is to be edited (Enter): the document view opens its
    /// field on the page with the text.
    /// </summary>
    internal event Action<PdfAnnotation>? TextBoxEditRequested;

    /// <summary>Gets the font size of the text box tool in points (its style).</summary>
    internal double TextBoxFontSize => TextBoxSpec.FontSizeOf(StyleOf(AnnotationKind.TextBox).Width);

    /// <summary>
    /// Says why a text cannot become a text box, or <see langword="null"/> when it can; a blank text is no problem (it makes
    /// nothing).
    /// </summary>
    /// <param name="text">The text of the field.</param>
    /// <returns>The hint for the field, or <see langword="null"/>.</returns>
    internal static string? TextBoxProblemOf(string text)
    {
        var check = TextBoxSpec.CheckText(text ?? string.Empty);
        return check.Problem switch
        {
            TextBoxTextProblem.UnsupportedCharacters => string.Format(
                Strings.Culture, CompositeFormat.Parse(Strings.TextBoxUnsupportedCharacters), string.Join(" ", check.Unsupported.Select(Shown))),
            TextBoxTextProblem.TooLong => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.TextBoxTooLong), TextBoxSpec.MaxTextLength),
            TextBoxTextProblem.TooManyLines => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.TextBoxTooManyLines), TextBoxSpec.MaxLines),
            _ => null,
        };

        // A tab or another control character is named by its code, as it has no glyph to show.
        static string Shown(string character) =>
            character.Length == 1 && char.IsControl(character[0]) ? $"U+{(int)character[0]:X4}" : character;
    }

    /// <summary>
    /// Makes a text box of the field's text on <paramref name="page"/> in <paramref name="box"/>, in the colour and size of
    /// the tool and by the author of the settings, after every drawing queued before it. A blank text or one
    /// <see cref="TextBoxProblemOf"/> refuses makes nothing (the field checks first). Runs in the background through the
    /// editor; from the call on the tab counts as having changes. A text box the document has no room for or that PDFium
    /// refuses changes nothing; <see cref="DrawingRefused"/> says why.
    /// </summary>
    /// <returns>Completes when the text box was made, refused or dropped; it never fails.</returns>
    internal Task ApplyTextBoxAsync(int page, PageRect box, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_disposed || !CanDraw || TextBoxSpec.CheckText(text).Problem != TextBoxTextProblem.None)
        {
            return Task.CompletedTask;
        }

        var spec = new TextBoxSpec(box, TextBoxSpec.NormalizeText(text), TextBoxFontSize, DrawColorOf(AnnotationKind.TextBox));
        var author = AnnotationSettings.Current.AuthorName;
        var run = Track(ApplyTextBoxCoreAsync(_documentGeneration, page, spec, _drawingTail, author));
        _drawingTail = run;
        return run;
    }

    /// <summary>
    /// Gives a text box of Pagewing's a new text (the field of Enter): one step of the history; nothing when the text stays,
    /// is blank or refused (the field checks first), or the tab takes no edit now.
    /// </summary>
    /// <returns>Completes when the change was made or refused; it never fails.</returns>
    internal Task ReplaceTextBoxTextAsync(PdfAnnotation target, string text)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(text);
        var normalized = TextBoxSpec.NormalizeText(text);
        if (target is not { GeometryEditable: true, TextBox: { } spec } || !CanDraw || WaitsForReplaceWithoutName(target)
            || TextBoxSpec.CheckText(normalized).Problem != TextBoxTextProblem.None || normalized == spec.Text)
        {
            return Task.CompletedTask;
        }

        var locator = new AnnotationLocator(target.Page, target.Index, target.Name);
        return RunEditAsync(async editor =>
        {
            try
            {
                var changed = await editor.ReplaceTextBoxAsync(locator, spec with { Text = normalized });
                if (_selectedAnnotation is { } now && SameAnnotation(now, target))
                {
                    SelectedAnnotation = changed;
                }
            }
            catch (Exception ex) when (ex is not (ObjectDisposedException or OperationCanceledException or PdfAnnotationNotFoundException))
            {
                DrawingRefused?.Invoke(Strings.DrawingChangeFailed);
            }
        });
    }

    /// <summary>Enter on a selected text box of Pagewing's: the view opens its field (<see cref="TextBoxEditRequested"/>).</summary>
    /// <returns>Whether the selection is such a text box.</returns>
    private bool RequestTextBoxEdit()
    {
        if (_selectedAnnotation is not { Kind: AnnotationKind.TextBox, GeometryEditable: true } selected || !CanDraw)
        {
            return false;
        }

        FlushNudge();
        TextBoxEditRequested?.Invoke(_selectedAnnotation ?? selected);
        return true;
    }

    private async Task ApplyTextBoxCoreAsync(int generation, int page, TextBoxSpec spec, Task before, string? author)
    {
        try
        {
            // Counted before the first await, down again in the finally (Track); undo waits for it like for a drawing.
            _pendingEdits++;
            _drawingsInFlight++;
            RaiseStepCanExecuteChanged();
            try
            {
                await before;
            }
            catch (Exception)
            {
                // The drawings before it never fail; only their end matters.
            }

            if (_disposed || generation != _documentGeneration)
            {
                return;
            }

            PdfAnnotation? created;
            try
            {
                created = await Editor.ApplyTextBoxAsync(page, spec, author);
            }
            catch (PdfAnnotationLimitException)
            {
                DrawingRefused?.Invoke(string.Format(Strings.Culture, CompositeFormat.Parse(Strings.DrawingLimitReached), PdfDocument.MaxAnnotations));
                return;
            }
            catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                DrawingRefused?.Invoke(Strings.DrawingFailed);
                return;
            }

            if (created is not null)
            {
                _comments?.SelectWhenListed(created);
            }
        }
        finally
        {
            _pendingEdits--;
            _drawingsInFlight--;
            RaiseStepCanExecuteChanged();
            SyncFromEditor();
        }
    }
}
