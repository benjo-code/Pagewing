using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// The text boxes of the document: creating and replacing them, and turning what PDFium reads into a
/// <see cref="TextBoxSpec"/>. See <c>Docs/DECISIONS.md</c>, entries "Textfeld: Erscheinungsbild und Entscheidung (M10)" and
/// "Textfeld: Umsetzung und Grenzen (M10)".
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Creates a text box: a FreeText whose appearance is the text set in Geist Regular (<see cref="TextBoxFont"/>), left
    /// aligned and wrapped to the box (<see cref="TextBoxLayout"/>). The box grows downwards until every line fits, at most to
    /// the bottom of the page (what reaches beyond it is cut off there); it never shrinks. Line breaks of the text are
    /// normalized. The annotation gets a new <c>/NM</c>, <c>/T</c> when <paramref name="author"/> is not blank (trimmed),
    /// <c>/M</c> and <c>/CreationDate</c>, flag Print, and what other programs need to show and edit it (<c>/Contents</c>,
    /// <c>/DA</c>, <c>/DS</c>, <c>/RC</c>). Runs with <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <returns>The new annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page, or the font size is outside 6 to 96 points.</exception>
    /// <exception cref="ArgumentException">The box is smaller than 4 points on a side or not finite, or the text is refused (<see cref="TextBoxSpec.CheckText"/>).</exception>
    /// <exception cref="PdfAnnotationLimitException">The document lists <see cref="MaxAnnotations"/> already (see <see cref="CreateDrawingAsync"/>); nothing is created.</exception>
    /// <exception cref="InvalidOperationException">No font is set (<see cref="TextBoxFont.Use"/>), or PDFium could not create the annotation; the page is left unchanged.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotation was created.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> CreateTextBoxAsync(int pageIndex, TextBoxSpec spec, string? author, CancellationToken cancellationToken = default) =>
        CreateTextBoxAsync(pageIndex, spec, author, name: null, cancellationToken);

    /// <summary>Like the public overload, but gives the text box <paramref name="name"/> as its <c>/NM</c> (undo and redo).</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or blank, or see the public overload.</exception>
    internal Task<PdfAnnotation> CreateTextBoxAsync(int pageIndex, TextBoxSpec spec, string? author, string? name, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }

        var normalized = Normalized(spec);
        name ??= NewName();
        var date = FormatPdfDate(Clock.GetUtcNow());
        var trimmedAuthor = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        return document.Thread.InvokeAsync(
            () =>
            {
                var glyphs = TextBoxFont.Table(document.Thread);
                int index;
                using (var page = document.LoadPage(pageIndex))
                {
                    EnsureRoomForOneMore(page, pageIndex, cancellationToken);
                    var (box, lines) = Lay(page, normalized, glyphs);
                    index = PdfiumTextBoxes.Create(page, box, lines, glyphs, trimmedAuthor, name, date);
                }

                try
                {
                    var created = ReadBack(pageIndex, index);
                    CountListed(pageIndex, +1);
                    return created;
                }
                catch
                {
                    TryRemove(pageIndex, index);
                    throw;
                }
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Replaces a text box by <paramref name="spec"/> (moved, resized, retyped or recoloured), in one job: the new one is
    /// created first, under the <c>/NM</c> of the old one (a new one if it had none), with its author and
    /// <c>/CreationDate</c>, <c>/M</c> now; only then is the old one deleted. If creating fails, the old one stays exactly as
    /// it was. The box grows to fit the text as for <see cref="CreateTextBoxAsync"/>. Only a text box that is
    /// <see cref="PdfAnnotation.GeometryEditable"/> (Pagewing's own, unchanged since) can be replaced. The new annotation is
    /// the last entry of the page's <c>/Annots</c>, so its index changes; the name finds it. Cancellation counts until the
    /// change starts. Runs with <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <returns>The new annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">The annotation is no text box, has replies or is not <see cref="PdfAnnotation.GeometryEditable"/>, or PDFium refused (the old one is unchanged).</exception>
    /// <exception cref="ArgumentException">See <see cref="CreateTextBoxAsync"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page of the locator is not a page of the document, or see <see cref="CreateTextBoxAsync"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the text box was replaced.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> ReplaceTextBoxAsync(AnnotationLocator locator, TextBoxSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return UpdateAnnotationAsync(locator, new AnnotationEdit(TextBox: spec), cancellationToken);
    }

    /// <summary>The spec with normalized line breaks, checked.</summary>
    private static TextBoxSpec Normalized(TextBoxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(spec.Text, nameof(spec));
        var normalized = spec with { Text = TextBoxSpec.NormalizeText(spec.Text) };
        normalized.Validate();
        return normalized;
    }

    /// <summary>
    /// The text box as PDFium creates it and its lines on the page: the box grown to the height the lines need, at most to
    /// the bottom of the page. Runs on the PDFium thread.
    /// </summary>
    private static (PdfiumTextBox Box, PdfiumTextLine[] Lines) Lay(PdfiumPage page, TextBoxSpec spec, PdfiumGlyphTable glyphs)
    {
        var layout = TextBoxLayout.Lay(spec.Text, spec.FontSize, spec.Box.Width, TextBoxFont.MetricsOf(glyphs));
        var box = spec.Box;
        var bottom = Math.Max(box.Bottom, Math.Min(box.Top + layout.ContentHeight, page.Height));
        var lines = new PdfiumTextLine[layout.Lines.Count];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = layout.Lines[i];
            lines[i] = new PdfiumTextLine(line.Text, box.Left + line.X, box.Top + line.Baseline);
        }

        return (new PdfiumTextBox((box.Left, box.Top, box.Right, bottom), spec.Text, spec.FontSize, (spec.Color.R, spec.Color.G, spec.Color.B)), lines);
    }

    /// <summary>
    /// Changes a text box (the FreeText at <paramref name="index"/>) by the edit: the whole <see cref="AnnotationEdit.TextBox"/>,
    /// or its text (<see cref="AnnotationEdit.Contents"/>) and colour. Creates the new one, then deletes the old one (see
    /// <see cref="ReplaceTextBoxAsync"/>). Runs on the PDFium thread with no handle of the page open.
    /// </summary>
    private PdfAnnotation ReplaceTextBox(int pageIndex, int index, AnnotationEdit edit, string name, string date, CancellationToken cancellationToken)
    {
        if (edit.Geometry is not null)
        {
            throw new InvalidOperationException("A text box has no drawing geometry to change.");
        }

        var glyphs = TextBoxFont.Table(document.Thread);
        int created;
        int entries;
        int old;
        using (var page = document.LoadPage(pageIndex))
        {
            var current = PdfiumAnnotations.ReadAt(page, index, cancellationToken)
                ?? throw new InvalidOperationException("The text box cannot be read, so it is not changed.");
            var read = PdfiumTextBoxes.Read(page, index);
            if (read is not { IsOwn: true } || current.HasReplies)
            {
                throw new InvalidOperationException(
                    "This text box was made or changed by another program; Pagewing cannot change it without losing its look. It can only be deleted.");
            }

            var spec = edit.TextBox ?? new TextBoxSpec(
                ToPageRect(read.Box),
                edit.Contents ?? read.Text,
                read.FontSize,
                edit.Color ?? new AnnotationColor(read.Color.R, read.Color.G, read.Color.B));
            var (box, lines) = Lay(page, Normalized(spec), glyphs);
            cancellationToken.ThrowIfCancellationRequested();
            created = PdfiumTextBoxes.Create(
                page, box, lines, glyphs, current.Author, current.Name ?? name, date, PdfiumAnnotations.ReadStringAt(page, index, "CreationDate\0"u8));
            entries = PdfiumAnnotations.CountEntries(page);
            old = index;
        }

        // From here on the new text box is on the page next to the old one: whatever fails, the new one goes again.
        int moved;
        try
        {
            using var page = document.LoadPage(pageIndex);

            // The new one is the last entry; deleting the old one (and its popups, all before it) moves it forward.
            PdfiumAnnotations.Delete(page, old, CancellationToken.None);
            moved = created - (entries - PdfiumAnnotations.CountEntries(page));
        }
        catch
        {
            TryRemoveCreated(pageIndex, created, entries);
            throw;
        }

        return ReadBack(pageIndex, moved);
    }

    private static PageRect ToPageRect((double Left, double Top, double Right, double Bottom) box) => new(box.Left, box.Top, box.Right, box.Bottom);

    /// <summary>The text box of a listed FreeText, and whether Pagewing may replace it (before the replies are taken into account).</summary>
    private static (TextBoxSpec? Spec, bool Own) ToTextBox(PdfiumTextBox? read) =>
        read is null
            ? (null, false)
            : (new TextBoxSpec(ToPageRect(read.Box), read.Text, read.FontSize, new AnnotationColor(read.Color.R, read.Color.G, read.Color.B)), read.IsOwn);

    /// <summary>Reads the text boxes of one loaded page with one conversion of the page. Runs on the PDFium thread.</summary>
    private sealed class TextBoxReader(PdfiumPage page)
    {
        private bool prepared;
        private bool hasTransform;
        private DisplayTransform transform;

        /// <summary>The text box of <paramref name="annotation"/>, or <see langword="default"/> for another kind or a damaged one.</summary>
        public (TextBoxSpec? Spec, bool Own) Read(PdfiumAnnotation annotation)
        {
            if (!PdfiumAnnotations.IsTextBox(annotation.Subtype))
            {
                return default;
            }

            if (!prepared)
            {
                prepared = true;
                hasTransform = DisplayTransform.TryCreate(page.Access(), page.Width, page.Height, out transform);
            }

            return hasTransform ? ToTextBox(PdfiumTextBoxes.Read(page, annotation.Index, transform)) : default;
        }
    }
}
