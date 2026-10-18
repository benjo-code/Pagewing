using System.Runtime.CompilerServices;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Text;

/// <summary>The outcome of searching one page, with the progress of the whole search.</summary>
/// <param name="PageIndex">The 0-based index of the page that was searched.</param>
/// <param name="Hits">The hits on that page in text order; empty for a page without a hit or a damaged page.</param>
/// <param name="PagesDone">How many pages have been searched so far, this one included.</param>
/// <param name="PageCount">The number of pages of the document.</param>
/// <param name="LimitReached">Whether the search stops after this page because <see cref="DocumentSearch.MaxHits"/> is reached.</param>
public readonly record struct SearchPageResult(int PageIndex, IReadOnlyList<SearchHit> Hits, int PagesDone, int PageCount, bool LimitReached);

/// <summary>Searches a whole document page by page.</summary>
public static class DocumentSearch
{
    /// <summary>The number of hits after which a search stops.</summary>
    public const int MaxHits = 10_000;

    /// <summary>
    /// Searches the pages one request at a time, from <paramref name="startPage"/> to the end, then from page 0 up to
    /// <c>startPage - 1</c>. Yields one result per page (also pages without hits). Stops after the page that reaches
    /// <see cref="MaxHits"/>; that page's hits are cut to the limit and the result has
    /// <see cref="SearchPageResult.LimitReached"/> set. A page whose search fails with
    /// <see cref="InvalidDataException"/> is skipped: it yields an empty result.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPage"/> is not a page of the document.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async IAsyncEnumerable<SearchPageResult> SearchAsync(
        PdfDocument document,
        string query,
        SearchOptions options,
        int startPage,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(query);
        var pageCount = document.PageCount;
        ArgumentOutOfRangeException.ThrowIfNegative(startPage);
        if (pageCount > 0)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(startPage, pageCount);
        }

        var total = 0;
        for (var done = 1; done <= pageCount; done++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageIndex = (startPage + done - 1) % pageCount;

            IReadOnlyList<SearchHit> hits;
            try
            {
                hits = await document.SearchPageAsync(pageIndex, query, options, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                hits = [];
            }

            var limitReached = total + hits.Count >= MaxHits;
            if (total + hits.Count > MaxHits)
            {
                hits = hits.Take(MaxHits - total).ToArray();
            }

            total += hits.Count;
            yield return new SearchPageResult(pageIndex, hits, done, pageCount, limitReached);
            if (limitReached)
            {
                yield break;
            }
        }
    }
}
