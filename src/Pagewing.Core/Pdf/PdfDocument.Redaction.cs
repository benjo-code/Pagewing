using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// Redaction of a working copy (milestone 14): finding out what a page loses under rectangles, and removing it. Both calls are for
/// a copy that is only written (see <see cref="Pagewing.Core.Pages.PageEditExecutor"/> for how copies are made) and never for a document that is
/// shown: the page edits go through raw page handles, so the document must not be rendered afterwards (write it and reopen it).
/// Each call is one stepped job on the PDFium thread (<c>PdfiumPriority.Background</c>): a page with many objects takes a while,
/// and the thread renders for other documents between the steps. See <c>Docs/DECISIONS.md</c>, entry "Schwärzen: Spike und
/// QPDF-Entscheidung (M14)".
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>The most rectangles of one page that <see cref="AnalyzeRedactionAsync"/> and <see cref="RedactPageAsync"/> take.</summary>
    public const int MaxRedactionAreasPerPage = 10_000;

    /// <summary>
    /// The dry run: what <see cref="RedactPageAsync"/> would do to the page for these rectangles, with the same decisions (the page
    /// is rewritten on a scratch copy and read back there, so the text that cannot be rewritten is known). Changes nothing: the
    /// document stays as it is, and the page can be redacted afterwards. Only text and annotations are handled so far; images,
    /// paths and form objects are not touched (their counts are 0).
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="areas">The rectangles in points from the top left of the displayed page.</param>
    /// <param name="cancellationToken">Cancels between two steps of the job.</param>
    /// <returns>What would happen; <see cref="PageRedactionImpact.Refusal"/> is set when the page cannot be redacted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="RedactionException">A rectangle is not finite or has no size, or there are more than <see cref="MaxRedactionAreasPerPage"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PageRedactionImpact> AnalyzeRedactionAsync(int page, IReadOnlyList<PageRect> areas, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, RedactionFill.Black, apply: false, cancellationToken);

    /// <summary>
    /// Removes what is under the rectangles from the page and draws an opaque area of exactly each rectangle over it: the
    /// characters that are hit (a character counts as soon as its box touches a rectangle; invisible, covered and clipped text too),
    /// the annotations in a rectangle with their popups and replies. Text objects are split so that the characters outside stay
    /// exactly where they were; a text object whose rest cannot be rewritten is removed as a whole and listed. Every surviving
    /// character is read back from the rewritten page. Only text and annotations are handled so far (see <see cref="AnalyzeRedactionAsync"/>).
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="areas">The rectangles in points from the top left of the displayed page; parts outside the page are clipped.</param>
    /// <param name="fill">The colour of the area.</param>
    /// <param name="cancellationToken">Cancels between two steps; a canceled call may leave the document changed, which must then be discarded.</param>
    /// <returns>What was done. When <see cref="PageRedactionImpact.Refusal"/> is set, the document may have changed and must be discarded.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="RedactionException">A rectangle is not finite or has no size, or there are more than <see cref="MaxRedactionAreasPerPage"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PageRedactionImpact> RedactPageAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, fill, apply: true, cancellationToken);

    private async Task<PageRedactionImpact> RunRedactionAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, bool apply, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(areas);
        ThrowIfNotAPage(page);
        if (areas.Count > MaxRedactionAreasPerPage)
        {
            throw new RedactionException(RedactionError.LimitExceeded, "More rectangles on one page than allowed.");
        }

        var copy = new PdfiumRedactionArea[areas.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var area = areas[i];
            if (!double.IsFinite(area.Left) || !double.IsFinite(area.Top) || !double.IsFinite(area.Right) || !double.IsFinite(area.Bottom) || !(area.Right > area.Left) || !(area.Bottom > area.Top))
            {
                throw new RedactionException(RedactionError.InvalidArea, "A rectangle needs finite edges and a positive size.");
            }

            copy[i] = new PdfiumRedactionArea(area.Left, area.Top, area.Right, area.Bottom);
        }

        var pdfiumFill = fill == RedactionFill.White ? PdfiumRedactionFill.White : PdfiumRedactionFill.Black;
        var impact = await document.Thread.InvokeSteppedAsync(
            () => PdfiumRedactionRun.Start(document, page, copy, pdfiumFill, apply),
            PdfiumPriority.Background,
            cancellationToken).ConfigureAwait(false);
        return ToImpact(page, impact);
    }

    private static PageRedactionImpact ToImpact(int page, PdfiumRedactionImpact impact) => new(
        page,
        impact.CharsRemoved,
        impact.TextObjectsSplit,
        impact.TextObjectsRemovedWhole,
        ImagesOverwritten: 0,
        ImagesRemovedWhole: 0,
        PathsCropped: 0,
        PathsRemovedWhole: 0,
        impact.FormObjectsTouched,
        impact.AnnotationsRemoved,
        [.. impact.Fallbacks.Select(f => new RedactionFallback(
            f.Kind == PdfiumFallbackKind.FormObjectRemovedWhole ? RedactionFallbackKind.FormObjectRemovedWhole : RedactionFallbackKind.TextObjectRemovedWhole,
            page,
            f.Reason))],
        [.. impact.RemovedText],
        impact.RefusalReason is null ? null : new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [page], impact.RefusalReason));
}
