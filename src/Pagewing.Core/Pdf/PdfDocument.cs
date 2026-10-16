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

    /// <summary>Gets the size of a page in points, after rotation and CropBox.</summary>
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
                using var page = document.LoadPage(pageIndex);
                return new PdfPageSize(page.Width, page.Height);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>Closes the document. The close is queued to the PDFium thread; this call does not wait.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            document.Dispose();
        }
    }

    /// <summary>Closes the document on the PDFium thread and completes once it (and its file) is closed.</summary>
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

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
}
