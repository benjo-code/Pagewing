using System.Globalization;
using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;

namespace Pagewing.Core.Pdf;

/// <summary>
/// The image signatures of the document (a stamp with one image). A drawn signature is an ink with the subject "Signature" and
/// goes through <see cref="CreateDrawingAsync(int, DrawingGeometry, AnnotationColor, string?, string?, CancellationToken)"/> and
/// <see cref="ReplaceDrawingAsync"/>. See <c>Docs/DECISIONS.md</c>, entry "Sichtbare Unterschrift: Erscheinungsbild und
/// Entscheidung (M12)".
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Creates an image signature: a stamp with the pixels of <paramref name="image"/> (longer side scaled down to what PDFium
    /// writes, at most 1,200 pixels), in the largest box of the image's aspect ratio inside <paramref name="box"/>, moved onto the
    /// page or scaled down if it does not fit. The annotation gets a new <c>/NM</c>, <c>/T</c> when <paramref name="author"/> is not
    /// blank (trimmed), <c>/M</c> and <c>/CreationDate</c>, flag Print and Pagewing's mark. It is not a digital signature. Runs with
    /// <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <param name="pageIndex">The 0-based page.</param>
    /// <param name="box">The wanted box in page points from the top left.</param>
    /// <param name="image">The pixels; not empty.</param>
    /// <param name="author">The author or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the job until the annotation is created.</param>
    /// <returns>The new annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page.</exception>
    /// <exception cref="ArgumentException">The image is empty, or the box is not finite or has no width or height.</exception>
    /// <exception cref="PdfAnnotationLimitException">The document lists <see cref="MaxAnnotations"/> already; nothing is created.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the annotation or its appearance; the page is left unchanged.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotation was created.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> CreateSignatureImageAsync(int pageIndex, PageRect box, SignatureImage image, string? author, CancellationToken cancellationToken = default) =>
        CreateSignatureImageAsync(pageIndex, box, image, author, name: null, cancellationToken);

    /// <summary>
    /// Like the public overload, but gives the stamp <paramref name="name"/> as its <c>/NM</c> instead of a new one (when not
    /// <see langword="null"/>), so that undo and redo find it again under the name it had.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or blank, or see the public overload.</exception>
    internal Task<PdfAnnotation> CreateSignatureImageAsync(int pageIndex, PageRect box, SignatureImage image, string? author, string? name, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        ArgumentNullException.ThrowIfNull(image);
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }

        if (!IsUsable(box))
        {
            throw new ArgumentException("The box needs a width and a height.", nameof(box));
        }

        if (image.IsEmpty)
        {
            throw new ArgumentException("The image shows nothing.", nameof(image));
        }

        var stored = SignatureCutout.FitWithin(image, PdfiumSignatures.MaxImageSide);
        name ??= NewName();
        var date = FormatPdfDate(Clock.GetUtcNow());
        var trimmedAuthor = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        return document.Thread.InvokeAsync(
            () =>
            {
                int index;
                using (var page = document.LoadPage(pageIndex))
                {
                    EnsureRoomForOneMore(page, pageIndex, cancellationToken);
                    var fitted = SignatureBoxes.Fit(box, (double)stored.Width / stored.Height, page.Width, page.Height);
                    index = PdfiumSignatures.CreateImage(
                        document, page, (fitted.Left, fitted.Top, fitted.Right, fitted.Bottom), stored.Width, stored.Height, stored.Bgra, trimmedAuthor, name, date);
                }

                return FinishCreatedImage(pageIndex, index);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Reads the pixels of an image signature of Pagewing, one to one (what undo of a deletion creates it again from). The locator
    /// finds it by name if it has one, else by index.
    /// </summary>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">The annotation is not an image signature of Pagewing, or its image cannot be read.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page of the locator is not a page of the document.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the image was read.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<SignatureImage> ReadSignatureImageAsync(AnnotationLocator locator, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(locator.Page);
        return document.Thread.InvokeAsync(
            () =>
            {
                using var page = document.LoadPage(locator.Page);
                var index = PdfiumAnnotations.Find(page, locator.Name, locator.Index, cancellationToken);
                if (index < 0)
                {
                    throw new PdfAnnotationNotFoundException(string.Create(CultureInfo.InvariantCulture, $"Page {locator.Page + 1} has no such annotation."));
                }

                if (!PdfiumSignatures.IsOwnImage(document, page, index) || PdfiumSignatures.ReadImage(document, page, index) is not var (width, height, bgra))
                {
                    throw new InvalidOperationException("The annotation is not an image signature of Pagewing.");
                }

                return new SignatureImage(width, height, bgra);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Changes an own image signature on the page: a new <paramref name="edit"/> geometry (a rectangle) moves and scales it to the
    /// largest box of its aspect ratio inside, around the centre of the wanted box, onto the page; a new comment replaces
    /// <c>/Contents</c>. The colour of an image cannot change. <c>/M</c> becomes <paramref name="date"/>. Runs on the PDFium thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">A text box or a colour is given, or PDFium refused a change.</exception>
    /// <exception cref="ArgumentException">The geometry is no rectangle.</exception>
    private static void UpdateSignatureImage(PdfiumPage page, int index, AnnotationEdit edit, string name, string date, PageRect current)
    {
        if (edit.TextBox is not null)
        {
            throw new InvalidOperationException("Only a text box has a text box to change.");
        }

        if (edit.Color is not null)
        {
            throw new InvalidOperationException("The colour of an image signature cannot be changed.");
        }

        if (edit.Geometry is { } geometry && geometry.Kind != AnnotationKind.Rectangle)
        {
            throw new ArgumentException("An image signature is moved and scaled with a rectangle.", nameof(edit));
        }

        if (edit.Contents is not null)
        {
            PdfiumAnnotations.Update(page, index, edit.Contents, null, name, date);
        }

        if (edit.Geometry is { } wanted)
        {
            var fitted = SignatureBoxes.Fit(wanted.Box, current.Width / current.Height, page.Width, page.Height);
            PdfiumSignatures.SetBox(page, index, (fitted.Left, fitted.Top, fitted.Right, fitted.Bottom), date);
        }
    }

    /// <summary>
    /// Checks that a just created stamp has its appearance and reads it back; whatever fails, it is removed again before the error
    /// goes on. Runs on the PDFium thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">PDFium did not write the appearance.</exception>
    private PdfAnnotation FinishCreatedImage(int pageIndex, int index)
    {
        try
        {
            using (var page = document.LoadPage(pageIndex))
            {
                if (!PdfiumAnnotations.HasAppearance(page, index))
                {
                    throw new InvalidOperationException("PDFium did not write the appearance of the annotation.");
                }
            }

            var created = ReadBack(pageIndex, index);
            CountListed(pageIndex, +1);
            return created;
        }
        catch
        {
            TryRemove(pageIndex, index);
            throw;
        }
    }

    private static bool IsUsable(PageRect box) =>
        double.IsFinite(box.Left) && double.IsFinite(box.Top) && double.IsFinite(box.Right) && double.IsFinite(box.Bottom)
        && box.Right > box.Left && box.Bottom > box.Top;

    /// <summary>
    /// Tells which stamps of a page are Pagewing's own image signatures, with a budget of pixels across the page
    /// (<see cref="PdfiumSignatures.PixelBudget"/>; past it a stamp counts as foreign). Runs on the PDFium thread.
    /// </summary>
    private sealed class SignatureReader(PdfiumDocument document, PdfiumPage page)
    {
        private long budget = PdfiumSignatures.PixelBudget;

        /// <summary>Whether <paramref name="annotation"/> is a stamp that no reply answers and that is an own image signature.</summary>
        public bool IsOwnImage(PdfiumAnnotation annotation) =>
            annotation.Subtype == FpdfConstants.AnnotStamp && !annotation.HasReplies && PdfiumSignatures.IsOwnImage(document, page, annotation.Index, ref budget);
    }
}
