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
/// The text of an open field is an unsaved change (<see cref="HasTextBoxText"/>) and is applied by
/// <see cref="FlushTextBoxDraft"/> before anything that ends or blocks editing (save, print, end of the mode, close, the
/// question before an update), while the tab still takes it; what cannot be applied then stays in the field with the reason,
/// or with the tab as <see cref="PendingTextBoxDraft"/> when the view leaves it. Typed text is never dropped silently.
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

    /// <summary>
    /// Gets or sets how the document view applies the text of the open text box field of this tab now (set while a field
    /// is open, else <see langword="null"/>): returns <see langword="true"/> when nothing is left in the field (applied,
    /// blank, or no field), <see langword="false"/> when the text stays in the field because it cannot be applied now (a
    /// character a text box cannot show, or the tab takes no edit at the moment); the field then says why.
    /// </summary>
    internal Func<bool>? TextBoxFieldFlush { get; set; }

    /// <summary>Gets or sets whether the open field of this tab has text (set by the document view while a field is open).</summary>
    internal Func<bool>? TextBoxFieldHasText { get; set; }

    /// <summary>
    /// Gets or sets the text of a field the view had to close (it left the tab) and could not apply: the view opens the field
    /// with it again when it shows the tab. It counts as an unsaved change.
    /// </summary>
    internal TextBoxDraft? PendingTextBoxDraft { get; set; }

    /// <summary>Gets whether a text box text waits in an open field or as a kept draft: an unsaved change (<see cref="AsksBeforeClosing"/>).</summary>
    internal bool HasTextBoxText => !_disposed && (TextBoxFieldHasText?.Invoke() == true || PendingTextBoxDraft is not null);

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

    /// <summary>
    /// Applies the text of the open text box field now, while the tab still takes edits: called before everything that would
    /// end or block editing (saving, printing, the end of the mode, closing, the question before an update; with
    /// <see cref="FlushNudge"/>). Text that cannot be applied stays in the field, which says why.
    /// </summary>
    /// <returns>Whether nothing is left in a field.</returns>
    internal bool FlushTextBoxDraft() => _disposed || TextBoxFieldFlush?.Invoke() != false;

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

/// <summary>The text of a text box field that could not be applied when the view left its tab (see <see cref="TabViewModel.PendingTextBoxDraft"/>).</summary>
/// <param name="Page">The 0-based page of the box.</param>
/// <param name="Box">The box in page points.</param>
/// <param name="Target">The text box the field changes, or <see langword="null"/> for a new one.</param>
/// <param name="Text">The typed text.</param>
/// <param name="FontSize">The font size in points.</param>
/// <param name="Color">The colour.</param>
internal sealed record TextBoxDraft(int Page, PageRect Box, PdfAnnotation? Target, string Text, double FontSize, AnnotationColor Color);
