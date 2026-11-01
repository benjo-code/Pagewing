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
    /// <remarks>
    /// The document counts as not writable (<see cref="IsRedactionTainted"/>, the contract is stated there) while this call is between its
    /// first change and its end, and for good when it ends in a refusal, an exception or a cancellation after it changed the page or after
    /// another page was redacted. The saving methods and further calls of this method then throw <see cref="RedactionTaintedException"/>
    /// whatever the caller did with the result. Only one call may run on a document at a time: a second one that overlaps throws
    /// <see cref="InvalidOperationException"/> (it is not queued) and changes nothing.
    /// </remarks>
    /// <exception cref="RedactionTaintedException">An earlier call left the document tainted.</exception>
    /// <exception cref="InvalidOperationException">Another call of this method on the document is still running.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="RedactionException">A rectangle is not finite or has no size, or there are more than <see cref="MaxRedactionAreasPerPage"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PageRedactionImpact> RedactPageAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, fill, apply: true, cancellationToken);

    /// <summary>Hooks for tests of the redaction engine; <see langword="null"/> in production.</summary>
    internal PdfiumRedactionSeam? RedactionSeam { get; set; }

    /// <summary>
    /// Gets whether the document must not be written: a redaction is changing it right now, or an earlier one left it only partly redacted.
    /// The contract: the flag is set while a <see cref="RedactPageAsync"/> call is between its first change to the page and its end, and
    /// it is set for good (never cleared) by any call that ends in a refusal, an exception or a cancellation after it changed the page, or
    /// after another call had redacted a page successfully, and by a successful call after an earlier call ended without a change but
    /// also without redacting its page. A document where every call ended cleanly stays writable; a document where a page was refused
    /// before anything was redacted is still the original and stays writable too. While the flag is set, <see cref="SaveRewriteAsync"/>,
    /// <see cref="SaveIncrementalAsync"/>, <see cref="SaveDecryptedCopyAsync"/> and <see cref="RedactPageAsync"/> throw
    /// <see cref="RedactionTaintedException"/>.
    /// </summary>
    public bool IsRedactionTainted => Volatile.Read(ref redactionFailed) != 0 || Volatile.Read(ref redactionInFlight) != 0;

    private void ThrowIfRedactionTainted()
    {
        if (IsRedactionTainted)
        {
            throw new RedactionTaintedException();
        }
    }

    /// <summary>Settles the books of one <see cref="RedactPageAsync"/> call (see <see cref="IsRedactionTainted"/>).</summary>
    private void EndApply(bool clean, bool modified)
    {
        if (clean)
        {
            Interlocked.Increment(ref redactionApplied);

            // An earlier page was refused without being touched: it is the one left readable now.
            if (Volatile.Read(ref redactionSkipped) != 0)
            {
                Volatile.Write(ref redactionFailed, 1);
            }
        }
        else if (modified || Volatile.Read(ref redactionApplied) > 0)
        {
            Volatile.Write(ref redactionFailed, 1);
        }
        else
        {
            Volatile.Write(ref redactionSkipped, 1);
        }

        // Only after the failure is recorded, so that the document is never seen as clean in between.
        if (modified)
        {
            Interlocked.Decrement(ref redactionInFlight);
        }

        Volatile.Write(ref redactionRunning, 0);
    }

    private async Task<PageRedactionImpact> RunRedactionAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, bool apply, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(areas);
        ThrowIfNotAPage(page);
        if (apply && IsRedactionTainted)
        {
            throw new RedactionTaintedException("An earlier redaction of this document did not finish cleanly; the document must be discarded.");
        }

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
        if (!apply)
        {
            var dry = await document.Thread.InvokeSteppedAsync(
                () => PdfiumRedactionRun.Start(document, page, copy, pdfiumFill, apply: false, onModified: null, RedactionSeam),
                PdfiumPriority.Background,
                cancellationToken).ConfigureAwait(false);
            return ToImpact(page, dry);
        }

        // One redaction at a time on a document: two overlapping ones could not tell whose change an unfinished page belongs to.
        if (Interlocked.CompareExchange(ref redactionRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another redaction of this document is still running; redact one page at a time.");
        }

        var modified = new ModifiedFlag();
        var clean = false;
        try
        {
            var impact = await document.Thread.InvokeSteppedAsync(
                () => PdfiumRedactionRun.Start(
                    document,
                    page,
                    copy,
                    pdfiumFill,
                    apply: true,
                    onModified: () =>
                    {
                        // Counted before the first change, on the PDFium thread: from here on the document must not be written.
                        if (Interlocked.Exchange(ref modified.Value, 1) == 0)
                        {
                            Interlocked.Increment(ref redactionInFlight);
                        }
                    },
                    RedactionSeam),
                PdfiumPriority.Background,
                cancellationToken).ConfigureAwait(false);
            clean = impact.RefusalReason is null;
            return ToImpact(page, impact);
        }
        finally
        {
            EndApply(clean, Volatile.Read(ref modified.Value) != 0);
        }
    }

    private sealed class ModifiedFlag
    {
        public int Value;
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
