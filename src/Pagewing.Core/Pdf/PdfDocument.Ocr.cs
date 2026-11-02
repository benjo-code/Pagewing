using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>What <see cref="PdfDocument.AddOcrLayerAsync"/> wrote.</summary>
/// <param name="Written">The words written.</param>
/// <param name="Skipped">The words left out because the font of the layer lacks one of their characters (counted, never replaced).</param>
public readonly record struct AddOcrLayerResult(int Written, int Skipped);

/// <summary>
/// Text recognition (milestone 15): what a page is made of, a page rendered for the engine, and the invisible text layer. Each call is one
/// short job on the PDFium thread (<c>PdfiumPriority.Background</c>), so rendering for the view goes on between them. The layer is written into
/// the document in memory, like an annotation; saving it is the saving of the document (<c>DocumentSaver</c>). See <c>Docs/DECISIONS.md</c>, entries
/// "OCR: Textebene, Spike (M15, Aufgabe 1)" and "OCR: PDFium-Zugang (M15, Aufgabe 6)".
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Counts the text of a page (without whitespace and without Pagewing's own layer), tells whether the layer is there and reads the resolution and
    /// the coverage of the largest image. Reads the page; changes nothing.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PageOcrInfo> GetOcrPageInfoAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(
            () =>
            {
                var info = PdfiumOcrLayer.ReadInfo(document, page);
                return new PageOcrInfo(info.TextCharacters, info.HasOwnLayer, info.ImageDpi, info.ImageCoverage);
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Renders a page for the engine: as displayed (rotation and crop box applied), light, without annotations and form fields, at a resolution.
    /// The image is <see cref="OcrResolution"/>'s size for the page (each side rounded up). Run as time slices at background priority.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="dpi">The resolution in pixels per inch.</param>
    /// <param name="cancellationToken">Cancels before the render finishes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist or the resolution is not positive.</exception>
    /// <exception cref="PdfRenderException">Rendering failed or the image would exceed the limit of the renderer.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<RenderedImage> RenderPageForOcrAsync(int page, int dpi, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpi);
        return RenderPageAsync(
            page,
            dpi / 72.0,
            new RenderOptions { Annotations = false, Priority = PdfPriority.Background, PageTheme = PageTheme.Light },
            cancellationToken);
    }

    /// <summary>
    /// Writes the words of a page as Pagewing's invisible text layer. A word with a character the font lacks is left out and counted. A word
    /// without a text or without a box with an area is dropped. The layer comes with the mark that makes it ours; the page does not change visibly.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="words">The words in points from the top left of the displayed page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="ArgumentException">There are more words than a page takes.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused (nothing is left of the layer on the page then).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<AddOcrLayerResult> AddOcrLayerAsync(int page, IReadOnlyList<OcrPageWord> words, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        ArgumentNullException.ThrowIfNull(words);
        var usable = words.Where(static w => w.IsUsable).Select(static w => new PdfiumOcrWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, w.Confidence)).ToArray();
        return document.Thread.InvokeAsync(
            () =>
            {
                if (usable.Length == 0)
                {
                    return new AddOcrLayerResult(0, 0);
                }

                var (written, skipped) = PdfiumOcrLayer.Add(document, page, usable, TextBoxFont.ReadData, static text => text.All(TextBoxCharacters.IsSupported));
                return new AddOcrLayerResult(written, skipped);
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>Takes Pagewing's layer out of a page. Invisible text of other origin stays.</summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>How many text objects were removed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<int> RemoveOcrLayerAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(() => PdfiumOcrLayer.Remove(document, page), PdfiumPriority.Background, cancellationToken);
    }

    /// <summary>
    /// Reads Pagewing's layer of a page: the words in the order of the page, with boxes in points from the top left of the displayed page. The
    /// confidence is not kept in the file and reads as 0.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<IReadOnlyList<OcrPageWord>> ReadOcrLayerAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(
            () => (IReadOnlyList<OcrPageWord>)[.. PdfiumOcrLayer.Read(document, page).Select(static w => new OcrPageWord(w.Text, w.Left, w.Top, w.Right, w.Bottom, w.Confidence))],
            PdfiumPriority.Background,
            cancellationToken);
    }
}
