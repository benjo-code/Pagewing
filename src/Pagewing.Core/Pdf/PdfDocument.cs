using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// An open PDF document. Every PDFium access is queued to the PDFium thread, so all members may be called
/// from any thread. After disposal every member throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class PdfDocument : IDisposable, IAsyncDisposable
{
    private readonly PdfiumDocument document;
    private readonly int pageCount;
    private readonly string pdfVersion;
    private int disposed;

    internal PdfDocument(PdfiumDocument document, int pageCount, string pdfVersion)
    {
        this.document = document;
        this.pageCount = pageCount;
        this.pdfVersion = pdfVersion;
    }

    /// <summary>Gets the number of pages.</summary>
    public int PageCount
    {
        get
        {
            ThrowIfDisposed();
            return pageCount;
        }
    }

    /// <summary>Gets the PDF version from the file header, for example "1.7"; empty when unknown.</summary>
    public string PdfVersion
    {
        get
        {
            ThrowIfDisposed();
            return pdfVersion;
        }
    }

    /// <summary>Reads the document information dictionary.</summary>
    public Task<PdfMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return document.Thread.InvokeAsync(
            () => new PdfMetadata(
                Title: document.GetMetaText("Title"),
                Author: document.GetMetaText("Author"),
                Subject: document.GetMetaText("Subject"),
                Keywords: document.GetMetaText("Keywords"),
                Creator: document.GetMetaText("Creator"),
                Producer: document.GetMetaText("Producer"),
                CreationDate: document.GetMetaText("CreationDate"),
                ModDate: document.GetMetaText("ModDate")),
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Gets the size of a page in points, after rotation and CropBox. Reads only the page dictionary, not the
    /// page content, so it stays cheap for every page of a large document.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    public Task<PdfPageSize> GetPageSizeAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, pageCount);
        return document.Thread.InvokeAsync(
            () =>
            {
                var (width, height) = document.GetPageSize(pageIndex);
                return new PdfPageSize(width, height);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Extracts the text of a page. Lines are separated by <c>\n</c> (PDFium reports <c>\r\n</c>); a page
    /// without text gives an empty string. Runs with <see cref="PdfPriority.Background"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the extraction ran.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<string> GetTextAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, pageCount);
        return document.Thread.InvokeAsync(
            () =>
            {
                using var page = document.LoadPage(pageIndex);
                using var textPage = page.LoadTextPage();
                return textPage.GetText().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Renders a whole page at <paramref name="scale"/> pixels per point (1 = 72 dpi) on white, without rotation
    /// beyond the page's own. The image size is <see cref="PdfPageSize.ToPixels"/>. The render runs in time
    /// slices on the PDFium thread, so other requests can run in between.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Invalid page index, scale or time slice.</exception>
    /// <exception cref="PdfRenderException">The image would exceed 256 Mpx, or rendering failed.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the render finished.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<RenderedImage> RenderPageAsync(int pageIndex, double scale, RenderOptions? options = null, CancellationToken cancellationToken = default) =>
        RenderAsync(pageIndex, scale, tile: null, options, cancellationToken);

    /// <summary>
    /// Renders one tile of a page: the pixels <paramref name="tile"/> of the page rendered at
    /// <paramref name="scale"/>, in the same pixel grid as <see cref="RenderPageAsync"/>.
    /// </summary>
    /// <remarks>
    /// The tile matches the corresponding area of the whole-page render up to PDFium's float rounding at the
    /// tile edges: usually by at most 1 per channel, but not bit for bit in general. Known exceptions are an
    /// edge shifted by one pixel row or column (difference up to 255 along that edge), resampled images (up to
    /// about 15), annotation appearances and rotated text at tile edges, and glyphs that reach more than 64 pixels
    /// right of their origin. Tiles are rendered with up to 64 pixels of overlap to the left, which are dropped
    /// before the image is returned; this avoids a PDFium error with glyphs cut by the left tile edge. Details:
    /// <c>Docs/DECISIONS.md</c>, entry "Kacheln mit 64 px Überlapp nach links; Kacheln sind in PDFium nicht
    /// allgemein bitgleich".
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Invalid page index, scale or time slice, or the tile is empty or not within <see cref="PdfPageSize.ToPixels"/>.
    /// </exception>
    /// <exception cref="PdfRenderException">The tile would exceed 256 Mpx, the page size in pixels exceeds the int range, or rendering failed.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the render finished.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<RenderedImage> RenderTileAsync(int pageIndex, double scale, PixelRect tile, RenderOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (tile.Width <= 0 || tile.Height <= 0 || tile.X < 0 || tile.Y < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tile), tile, "The tile must not be empty and must lie within the page.");
        }

        return RenderAsync(pageIndex, scale, new PdfiumPixelRect(tile.X, tile.Y, tile.Width, tile.Height), options, cancellationToken);
    }

    /// <summary>Closes the document. The close is queued to the PDFium thread; this call does not wait.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            document.Dispose();
        }
    }

    /// <summary>
    /// Closes the document on the PDFium thread and completes once the PDFium thread has processed the close.
    /// The native document and its file are closed when the last page, render or text-page reference is released,
    /// which may be later if work for this document is still in flight.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await document.Thread.InvokeAsync(document.Dispose).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The PDFium thread is gone (tests only); nothing can be closed on it any more.
            document.Dispose();
        }
    }

    private static async Task<RenderedImage> AwaitRender(Task<PdfiumImage> render)
    {
        try
        {
            var image = await render.ConfigureAwait(false);
            return new RenderedImage(image.Width, image.Height, image.Stride, image.Pixels);
        }
        catch (PdfiumRenderException ex)
        {
            throw new PdfRenderException(ex.Message, ex);
        }
    }

    /// <summary>Checks what can be checked without PDFium, then queues the render.</summary>
    private Task<RenderedImage> RenderAsync(int pageIndex, double scale, PdfiumPixelRect? tile, RenderOptions? options, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, pageCount);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be finite and greater than 0.");
        }

        options ??= RenderOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TimeSlice, TimeSpan.Zero, nameof(options));
        var annotations = options.Annotations;
        var slice = options.TimeSlice;
        var priority = options.Priority == PdfPriority.Background ? PdfiumPriority.Background : PdfiumPriority.Interactive;

        // The work is created on the PDFium thread: it loads the page and checks the tile against the page size.
        return AwaitRender(document.Thread.InvokeSteppedAsync(
            () => new PdfiumRenderWork(document, pageIndex, scale, tile, annotations, slice),
            priority,
            cancellationToken));
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
