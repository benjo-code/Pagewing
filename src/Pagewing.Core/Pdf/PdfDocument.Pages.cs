using Pagewing.Core.Pages;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// The page operations of a working copy (milestone 13): import, delete, move, rotate, blank pages and the full rewrite. They are
/// for a copy that is only written (<see cref="PageEditExecutor"/> opens it from the file of the primary source) and never for
/// a document that is shown: such a document must not be rendered or read through its page view afterwards, because the
/// per-page caches and the form environment of PDFium hang on page indices (see <c>PdfiumPageEdit</c>). Every operation is
/// a short job on the PDFium thread (<c>PdfiumPriority.Background</c>); a list of pages is cut into batches of
/// <see cref="PageEditLimits.ImportBatch"/> and the token is checked between them. <see cref="PageCount"/> follows the changes.
/// See <c>Docs/DECISIONS.md</c>, entry "Seiten organisieren: Spike und QPDF-Entscheidung (M13)".
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Copies pages of <paramref name="source"/> (which may be this document: duplicating) so that the first one gets the index
    /// <paramref name="destination"/> of this document, in batches of <see cref="PageEditLimits.ImportBatch"/> pages. The
    /// outline and the page labels of the source are not taken over, internal links lose their target, and the widgets of form
    /// fields arrive as annotations only (see the loss list of <see cref="PageEditExecutor.AnalyzeAsync"/>). For a working copy only.
    /// </summary>
    /// <param name="source">The document the pages come from; a page may repeat in <paramref name="sourceIndices"/>.</param>
    /// <param name="sourceIndices">The 0-based pages of the source, in the order they get.</param>
    /// <param name="destination">The 0-based index of this document where the first page goes: between 0 and <see cref="PageCount"/>.</param>
    /// <param name="cancellationToken">Cancels before the next batch; the batches done stay.</param>
    /// <exception cref="ArgumentOutOfRangeException">A page is not a page of <paramref name="source"/>, or <paramref name="destination"/> is out of range.</exception>
    /// <exception cref="PageEditException">The list holds more than <see cref="PageEditLimits.MaxSlots"/> pages.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused an import.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before a batch.</exception>
    /// <exception cref="ObjectDisposedException">A document is disposed.</exception>
    public Task ImportPagesAsync(PdfDocument source, IReadOnlyList<int> sourceIndices, int destination, CancellationToken cancellationToken = default) =>
        ImportPagesAsync(source, sourceIndices, destination, batchDone: null, cancellationToken);

    /// <summary>As the public overload; <paramref name="batchDone"/> is called after each batch with the number of pages imported so far.</summary>
    internal async Task ImportPagesAsync(PdfDocument source, IReadOnlyList<int> sourceIndices, int destination, Action<int>? batchDone, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sourceIndices);
        var all = Snapshot(sourceIndices);
        var sourceCount = source.PageCount;
        foreach (var index in all)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, sourceCount);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(destination);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(destination, PageCount);
        for (var offset = 0; offset < all.Length; offset += PageEditLimits.ImportBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = all[offset..Math.Min(all.Length, offset + PageEditLimits.ImportBatch)];
            var at = destination + offset;
            await document.Thread.InvokeAsync(
                () =>
                {
                    try
                    {
                        PdfiumPageEdit.Import(document, source.document, batch, at);
                    }
                    finally
                    {
                        RefreshPageCount();
                    }
                },
                PdfiumPriority.Background,
                cancellationToken).ConfigureAwait(false);
            batchDone?.Invoke(offset + batch.Length);
        }
    }

    /// <summary>
    /// Deletes pages, highest index first, in batches of <see cref="PageEditLimits.ImportBatch"/> pages. At least one page stays.
    /// For a working copy only.
    /// </summary>
    /// <param name="indices">The 0-based pages (a set: a repeated index counts once).</param>
    /// <param name="cancellationToken">Cancels before the next batch; the batches done stay.</param>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the document.</exception>
    /// <exception cref="ArgumentException">Every page would be deleted.</exception>
    /// <exception cref="PageEditException">The list holds more than <see cref="PageEditLimits.MaxSlots"/> pages.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before a batch.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task DeletePagesAsync(IReadOnlyList<int> indices, CancellationToken cancellationToken = default) =>
        DeletePagesAsync(indices, batchDone: null, cancellationToken);

    /// <summary>As the public overload; <paramref name="batchDone"/> is called after each batch.</summary>
    internal async Task DeletePagesAsync(IReadOnlyList<int> indices, Action? batchDone, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(indices);
        var count = PageCount;
        var distinct = Snapshot(indices).Distinct().OrderDescending().ToArray();
        foreach (var index in distinct)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        }

        if (distinct.Length >= count)
        {
            throw new ArgumentException("At least one page must stay.", nameof(indices));
        }

        for (var offset = 0; offset < distinct.Length; offset += PageEditLimits.ImportBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = distinct[offset..Math.Min(distinct.Length, offset + PageEditLimits.ImportBatch)];
            await document.Thread.InvokeAsync(
                () =>
                {
                    try
                    {
                        foreach (var index in batch)
                        {
                            PdfiumPageEdit.Delete(document, index);
                        }
                    }
                    finally
                    {
                        RefreshPageCount();
                    }
                },
                PdfiumPriority.Background,
                cancellationToken).ConfigureAwait(false);
            batchDone?.Invoke();
        }
    }

    /// <summary>
    /// Moves pages, in one short job, so that the first one gets the index <paramref name="destination"/> in the document
    /// without the moved pages and the others follow in the given order (a list of <c>[4, 2]</c> to 1 turns the pages 1..6 into 1, 5, 3, 2, 4, 6).
    /// For a working copy only; a longer list is moved in several calls.
    /// </summary>
    /// <param name="indices">The 0-based pages, without repeats: at most <see cref="PageEditLimits.ImportBatch"/>.</param>
    /// <param name="destination">The index of the first moved page in the result: between 0 and the page count minus the moved pages.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentException">The list is empty or longer than <see cref="PageEditLimits.ImportBatch"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An index is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused the move (a repeated page, or a destination out of range).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task MovePagesAsync(IReadOnlyList<int> indices, int destination, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count == 0 || indices.Count > PageEditLimits.ImportBatch)
        {
            throw new ArgumentException($"Between 1 and {PageEditLimits.ImportBatch} pages are moved in one call.", nameof(indices));
        }

        var batch = Snapshot(indices);
        return document.Thread.InvokeAsync(() => PdfiumPageEdit.Move(document, batch, destination), PdfiumPriority.Background, cancellationToken);
    }

    /// <summary>Sets the absolute rotation of a page (<c>/Rotate</c>). For a working copy only.</summary>
    /// <param name="index">The 0-based page.</param>
    /// <param name="degrees">0, 90, 180 or 270 (clockwise).</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist, or the rotation is not 0, 90, 180 or 270.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task SetPageRotationAsync(int index, int degrees, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return document.Thread.InvokeAsync(() => PdfiumPageEdit.SetRotation(document, index, degrees), PdfiumPriority.Background, cancellationToken);
    }

    /// <summary>Gets the absolute rotation of a page in degrees (0, 90, 180 or 270).</summary>
    /// <param name="index">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<int> GetPageRotationAsync(int index, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return document.Thread.InvokeAsync(() => PdfiumPageEdit.GetRotation(document, index), PdfiumPriority.Background, cancellationToken);
    }

    /// <summary>Inserts an empty page. For a working copy only.</summary>
    /// <param name="index">The 0-based index the new page gets: between 0 and <see cref="PageCount"/>.</param>
    /// <param name="width">The width in points: positive and finite.</param>
    /// <param name="height">The height in points: positive and finite.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index or a size is out of range.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the page.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task InsertBlankPageAsync(int index, double width, double height, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return document.Thread.InvokeAsync(
            () =>
            {
                try
                {
                    PdfiumPageEdit.InsertBlank(document, index, width, height);
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
    /// Writes the whole document to <paramref name="destination"/> (never incrementally) and keeps its encryption. The stream is
    /// written from the PDFium thread, in blocks; the caller decides where it goes (the safe replace of a file is not done here).
    /// A document that was changed by the page operations is written as it is.
    /// </summary>
    /// <exception cref="IOException">The document or the stream could not be written.</exception>
    /// <exception cref="Pagewing.Core.Redaction.RedactionTaintedException">A redaction left the document partly redacted (<see cref="IsRedactionTainted"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the save started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task SaveRewriteAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destination);
        ThrowIfRedactionTainted();
        return document.Thread.InvokeAsync(() => PdfiumPageEdit.SaveRewrite(document, destination), PdfiumPriority.Background, cancellationToken);
    }

    /// <summary>
    /// Reads where the internal links of the given pages lead (see <c>PdfiumPageEdit.ReadInternalLinkTargets</c>), in short jobs
    /// of <see cref="PageEditLimits.ImportBatch"/> pages. Used to tell which links a page edit leaves without a target.
    /// </summary>
    /// <param name="pages">The 0-based pages.</param>
    /// <param name="cancellationToken">Cancels before the next page.</param>
    /// <returns>For each given page the targets of its internal links (-1: unresolved).</returns>
    internal async Task<IReadOnlyList<int[]>> GetInternalLinkTargetsAsync(IReadOnlyList<int> pages, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var all = Snapshot(pages);
        var count = PageCount;
        foreach (var page in all)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(page);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(page, count);
        }

        var result = new int[all.Length][];
        for (var offset = 0; offset < all.Length; offset += PageEditLimits.ImportBatch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = offset;
            var last = Math.Min(all.Length, offset + PageEditLimits.ImportBatch);
            await document.Thread.InvokeAsync(
                () =>
                {
                    for (var i = first; i < last; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result[i] = PdfiumPageEdit.ReadInternalLinkTargets(document, all[i]);
                    }
                },
                PdfiumPriority.Background,
                cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Reads whether the document has page labels (one short job); see <c>PdfiumDocument.HasPageLabels</c>.</summary>
    internal Task<bool> HasPageLabelsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return document.Thread.InvokeAsync(document.HasPageLabels, PdfiumPriority.Background, cancellationToken);
    }

    /// <summary>Reads the full names of the form fields that have a widget on a page, in one short job (see <see cref="GetFormFieldsAsync"/>); none without a form.</summary>
    internal async Task<IReadOnlyList<string>> GetFormFieldNamesAsync(int pageIndex, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!HasAcroForm)
        {
            return [];
        }

        ThrowIfNotAPage(pageIndex);
        var fields = await ReadFormFieldsAsync(pageIndex, PdfiumPriority.Background, cancellationToken).ConfigureAwait(false);
        return [.. fields.Select(f => f.Key.FieldName).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Copies a list so that the PDFium thread never reads the caller's collection; a list longer than a plan can hold is refused first.</summary>
    private static int[] Snapshot(IReadOnlyList<int> list)
    {
        if (list.Count > PageEditLimits.MaxSlots)
        {
            throw new PageEditException(PageEditError.LimitExceeded, "More pages than a plan can hold.");
        }

        var copy = new int[list.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            copy[i] = list[i];
        }

        return copy;
    }

    /// <summary>Takes over the page count PDFium reports now. Runs on the PDFium thread.</summary>
    private void RefreshPageCount() => Volatile.Write(ref pageCount, document.PageCount);
}
