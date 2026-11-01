using Pagewing.Core.Redaction;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// Raster pages (milestone 14.1): a page that is one image, written into the target of a rebuild, and a count of what a page is made
/// of. Like the other page operations of a working copy these are for a document that is only written, never one that is shown.
/// Each call is one short job on the PDFium thread (<c>PdfiumPriority.Background</c>).
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Adds a page that shows the image over its whole media box and has nothing else: no text, no paths, no annotations. The page is
    /// created with the size and the <c>/Rotate</c> of <paramref name="page"/>; the pixels are checked (count and length) before
    /// anything is allocated or PDFium is asked. <see cref="PageCount"/> rises by one. For a working copy only.
    /// </summary>
    /// <param name="index">The 0-based position the new page gets: between 0 and <see cref="PageCount"/>.</param>
    /// <param name="page">The page to add.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index, a size or the rotation of the page is out of range.</exception>
    /// <exception cref="ArgumentException">The pixels are not <c>WidthPx * HeightPx * 4</c> bytes.</exception>
    /// <exception cref="RedactionException">The image has more than <see cref="RedactionLimits.MaxRasterPixels"/> pixels (<see cref="RedactionError.LimitExceeded"/>).</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the page; nothing is left of it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task AddRasterPageAsync(int index, RasterPage page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, PageCount);

        // The limit is a rule of the redaction, so it has its own exception; it comes first so that nothing is allocated for a page beyond it.
        if (page.WidthPx > 0 && page.HeightPx > 0 && (long)page.WidthPx * page.HeightPx > RedactionLimits.MaxRasterPixels)
        {
            throw new RedactionException(RedactionError.LimitExceeded, $"A raster page has at most {RedactionLimits.MaxRasterPixels:N0} pixels.");
        }

        ArgumentNullException.ThrowIfNull(page.Bgra);
        return document.Thread.InvokeAsync(
            () =>
            {
                try
                {
                    PdfiumRedactionRaster.AddPage(document, index, page.WidthPt, page.HeightPt, page.Rotation, page.WidthPx, page.HeightPx, page.Bgra);
                }
                finally
                {
                    RefreshPageCount();
                }
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Counts the annotations of a page (links and form field widgets included) without reading its page objects, so a page with a
    /// hostile number of objects can be counted too.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>The number of annotations.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidOperationException">The page cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<int> GetPageAnnotationCountAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(
            () => PdfiumRedactionRaster.CountAnnotations(document, page),
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Counts the page objects of a page by kind, and its annotations. Reads the objects of the page as they are in the document (a
    /// document that was just written and opened again, or a working copy).
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>The counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidOperationException">The page cannot be loaded, or has more objects than redaction handles.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public async Task<PageObjectSummary> GetPageObjectSummaryAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        var summary = await document.Thread.InvokeAsync(
            () => PdfiumRedactionRaster.Summarize(document, page),
            PdfiumPriority.Background,
            cancellationToken).ConfigureAwait(false);
        return new PageObjectSummary(summary.Images, summary.Paths, summary.Texts, summary.Others, summary.Annotations);
    }
}
