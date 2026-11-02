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
    /// document stays as it is, and the page can be redacted afterwards. It does not run the render check
    /// (<see cref="VerifyRedactionRenderAsync"/>): that one writes the whole document once, after all pages are redacted, so the analysis
    /// of a whole job redacts every page on a throwaway working copy and checks once.
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
        RunRedactionAsync(page, areas, RedactionFill.Black, RedactionImageTreatment.Solid, RedactionImageStrength.Coarse, overlay: null, apply: false, cancellationToken);

    /// <summary>
    /// The dry run with pixelating or blurring chosen for the images under the rectangles: says how many image areas would be treated and how
    /// many would get the solid fill instead (<see cref="PageRedactionImpact.ImageAreasTreated"/>, <see cref="PageRedactionImpact.ImageAreasSolid"/>),
    /// with the same decisions as <see cref="RedactPageAsync(int, IReadOnlyList{PageRect}, RedactionFill, RedactionImageTreatment, RedactionImageStrength, CancellationToken)"/>.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="areas">The rectangles in points from the top left of the displayed page.</param>
    /// <param name="treatment">What happens to the pixels of an image under a rectangle.</param>
    /// <param name="strength">How coarse pixelating and blurring work.</param>
    /// <param name="cancellationToken">Cancels between two steps of the job.</param>
    /// <returns>What would happen; <see cref="PageRedactionImpact.Refusal"/> is set when the page cannot be redacted.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist, or <paramref name="treatment"/> or <paramref name="strength"/> is not a defined value.</exception>
    /// <exception cref="RedactionException">A rectangle is not finite or has no size, or there are more than <see cref="MaxRedactionAreasPerPage"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PageRedactionImpact> AnalyzeRedactionAsync(int page, IReadOnlyList<PageRect> areas, RedactionImageTreatment treatment, RedactionImageStrength strength, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, RedactionFill.Black, treatment, strength, overlay: null, apply: false, cancellationToken);

    /// <summary>The dry run with overlay text: also says which texts are drawn, which are left out and why (<see cref="PageRedactionImpact.OverlaysNotShown"/>, <see cref="PageRedactionImpact.OverlaysNotFitting"/>), with the decisions of the call that applies.</summary>
    internal Task<PageRedactionImpact> AnalyzeRedactionAsync(int page, IReadOnlyList<PageRect> areas, RedactionImageTreatment treatment, RedactionImageStrength strength, RedactionOverlayWork? overlay, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, RedactionFill.Black, treatment, strength, overlay, apply: false, cancellationToken);

    /// <summary>
    /// Removes what is under the rectangles from the page and draws an opaque area of exactly each rectangle over it: the
    /// characters that are hit (a character counts as soon as its box touches a rectangle; invisible, covered and clipped text too),
    /// the annotations in a rectangle with their popups and replies. Text objects are split so that the characters outside stay
    /// exactly where they were; a text object whose rest cannot be rewritten is removed as a whole and listed. Every surviving
    /// character is read back from the rewritten page. Images under a rectangle get their pixels overwritten (images that are turned or
    /// sheared go as a whole), filled rectangle paths are cut around the rectangle, other paths go as a whole, and form objects with
    /// something under a rectangle are taken apart on the page first (their children become objects of the page); everything removed as a
    /// whole is listed. The page is checked with <see cref="VerifyRedactionRenderAsync"/> once all pages are done.
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
        RunRedactionAsync(page, areas, fill, RedactionImageTreatment.Solid, RedactionImageStrength.Coarse, overlay: null, apply: true, cancellationToken);

    /// <summary>
    /// As <see cref="RedactPageAsync(int, IReadOnlyList{PageRect}, RedactionFill, CancellationToken)"/>, but the pixels of an image under a
    /// rectangle can be pixelated or blurred instead of getting the fill colour: few coarse blocks that each hold the mean of their pixels
    /// (<see cref="ImageBlocks"/>; <c>PdfiumImageBlocks</c> does the writing). Pixels outside the rectangle stay bit for bit. Where the rule does not
    /// apply (the rectangle is smaller than 2 x 2 blocks of 8 pixels, the image is turned, sheared or clipped, two rectangles overlap on the same
    /// image) the area gets the solid fill and is counted in <see cref="PageRedactionImpact.ImageAreasSolid"/> with its reason. The opaque
    /// area is drawn only over the part of a rectangle that no treated image shows through; text and vector content in a rectangle are removed
    /// as always and get the solid fill.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="areas">The rectangles in points from the top left of the displayed page; parts outside the page are clipped.</param>
    /// <param name="fill">The colour of the opaque area (and of the solid fill of image areas).</param>
    /// <param name="treatment">What happens to the pixels of an image under a rectangle.</param>
    /// <param name="strength">How coarse pixelating and blurring work.</param>
    /// <param name="cancellationToken">Cancels between two steps; a canceled call may leave the document changed, which must then be discarded.</param>
    /// <returns>What was done. When <see cref="PageRedactionImpact.Refusal"/> is set, the document may have changed and must be discarded.</returns>
    /// <exception cref="RedactionTaintedException">An earlier call left the document tainted.</exception>
    /// <exception cref="InvalidOperationException">Another call of this method on the document is still running.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist, or <paramref name="treatment"/> or <paramref name="strength"/> is not a defined value.</exception>
    /// <exception cref="RedactionException">A rectangle is not finite or has no size, or there are more than <see cref="MaxRedactionAreasPerPage"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PageRedactionImpact> RedactPageAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, RedactionImageTreatment treatment, RedactionImageStrength strength, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, fill, treatment, strength, overlay: null, apply: true, cancellationToken);

    /// <summary>
    /// As the call with the treatment, and draws the overlay text of the rectangles as path objects on the opaque areas (no text, no font in the
    /// file): centred, or tiled, in the size that fits; left out over a treated image, where it does not fit or where the budget of the
    /// document is reached (the area stays filled). Nothing is drawn outside the rectangle.
    /// </summary>
    internal Task<PageRedactionImpact> RedactPageAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, RedactionImageTreatment treatment, RedactionImageStrength strength, RedactionOverlayWork? overlay, CancellationToken cancellationToken = default) =>
        RunRedactionAsync(page, areas, fill, treatment, strength, overlay, apply: true, cancellationToken);

    /// <summary>
    /// For each range of characters of a page (indexes as <see cref="GetPageTextAsync(int, CancellationToken)"/> reports them, last
    /// inclusive) says whether it shows nothing: the characters that are not generated are all drawn in the render mode "invisible"
    /// (the text layer of an OCR scan). One short <see cref="PdfPriority.Background"/> job that looks at the characters of the ranges only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    internal Task<bool[]> GetInvisibleTextAsync(int pageIndex, IReadOnlyList<(int First, int Last)> ranges, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count == 0)
        {
            return Task.FromResult(Array.Empty<bool>());
        }

        var copy = ranges.ToArray();
        return document.Thread.InvokeAsync(
            () =>
            {
                using var page = document.LoadPage(pageIndex);
                using var textPage = page.LoadTextPage();
                return PdfiumPageText.ReadInvisible(page, textPage, copy);
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>Hooks for tests of the redaction engine; <see langword="null"/> in production.</summary>
    internal PdfiumRedactionSeam? RedactionSeam { get; set; }

    /// <summary>Hooks for tests of the render check; <see langword="null"/> in production.</summary>
    internal RedactionVerifySeam? VerifySeam { get; set; }

    /// <summary>Gets how many times <see cref="VerifyRedactionRenderAsync"/> wrote this document for its check (one per call, not one per page).</summary>
    internal int RedactionDryRunWrites => Volatile.Read(ref redactionDryRunWrites);

    /// <summary>
    /// The render check of a redacted working copy (milestone 14, spec 5.5): PDFium writes a page again from its objects after any change
    /// and does not write everything it reads, so every page that was redacted is compared with the original outside the redacted
    /// rectangles. The document is written <b>once</b> for all the pages (<see cref="SaveRewriteAsync"/> into a temporary file that is
    /// deleted again), the copy is opened, and only the given pages are rendered from it (never from this working copy, whose pages hold
    /// raw edits) and from <paramref name="original"/> at <see cref="RedactionRenderCheck.Scale"/>; pixels outside the rectangles plus
    /// <see cref="RedactionRenderCheck.MarginPoints"/> may not differ by more than <see cref="RedactionRenderCheck.ChannelTolerance"/> in a
    /// channel (<see cref="RedactionRenderCheck.MaxDifferentPixels"/> pixels are allowed to).
    /// </summary>
    /// <remarks>
    /// Analysis and applying share it, each calls it once: after <see cref="RedactPageAsync"/> was run for all pages of the job on a working
    /// copy (the analysis on a throwaway copy, the application on the copy that is written), with the pages that changed. A page that fails
    /// gets <see cref="RedactionRefusalKind.PageNotRedactable"/>; the caller puts the refusal into <see cref="PageRedactionImpact.Refusal"/> of that
    /// page. A page that cannot be rendered or written fails the check, never passes it. After any refusal the caller discards the copy.
    /// </remarks>
    /// <param name="original">A document with the same pages that was not redacted (the file as it was before the working copy was made).</param>
    /// <param name="pages">The pages that were redacted, each with its rectangles; repeated pages are not allowed.</param>
    /// <param name="cancellationToken">Cancels between two steps.</param>
    /// <returns>One refusal per page that fails; empty when all pass.</returns>
    /// <exception cref="RedactionTaintedException">A redaction left this document partly redacted (it cannot be written).</exception>
    /// <exception cref="ArgumentException">The documents have a different number of pages, or a page appears twice.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page does not exist.</exception>
    /// <exception cref="RedactionException">A rectangle is not finite or has no size.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">A document is disposed.</exception>
    public Task<IReadOnlyList<RedactionRefusal>> VerifyRedactionRenderAsync(PdfDocument original, IReadOnlyList<RedactionPageAreas> pages, CancellationToken cancellationToken = default) =>
        VerifyRedactionRenderAsync(original, pages, writeWithoutPages: false, cancellationToken);

    /// <summary>
    /// As <see cref="VerifyRedactionRenderAsync(PdfDocument, IReadOnlyList{RedactionPageAreas}, CancellationToken)"/>. With
    /// <paramref name="writeWithoutPages"/> the document is written once for <see cref="RedactionVerifySeam.WrittenCopy"/> even when there
    /// is no page to compare (a plan whose pages all become images redacts nothing on the working copy, but the bytes of the whole
    /// file are still checked for page keys PDFium cannot remove); nothing is rendered then.
    /// </summary>
    /// <param name="original">A document with the same pages that was not redacted.</param>
    /// <param name="pages">The pages that were redacted, each with its rectangles; may be empty with <paramref name="writeWithoutPages"/>.</param>
    /// <param name="writeWithoutPages">Whether the document is written (and the seam called) when <paramref name="pages"/> is empty.</param>
    /// <param name="cancellationToken">Cancels between two steps.</param>
    /// <returns>One refusal per page that fails; empty when all pass.</returns>
    internal async Task<IReadOnlyList<RedactionRefusal>> VerifyRedactionRenderAsync(PdfDocument original, IReadOnlyList<RedactionPageAreas> pages, bool writeWithoutPages, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(pages);
        original.ThrowIfDisposed();
        if (original.PageCount != PageCount)
        {
            throw new ArgumentException("The original has another number of pages than the working copy.", nameof(original));
        }

        var seen = new HashSet<int>();
        foreach (var entry in pages)
        {
            ThrowIfNotAPage(entry.Page);
            if (!seen.Add(entry.Page))
            {
                throw new ArgumentException($"Page {entry.Page} appears twice.", nameof(pages));
            }

            foreach (var area in entry.Areas)
            {
                if (!double.IsFinite(area.Left) || !double.IsFinite(area.Top) || !double.IsFinite(area.Right) || !double.IsFinite(area.Bottom) || !(area.Right > area.Left) || !(area.Bottom > area.Top))
                {
                    throw new RedactionException(RedactionError.InvalidArea, "A rectangle needs finite edges and a positive size.");
                }
            }
        }

        var refusals = new List<RedactionRefusal>();
        if (pages.Count == 0 && !writeWithoutPages)
        {
            return refusals;
        }

        var path = Path.Combine(Path.GetTempPath(), $"pagewing-check-{Guid.NewGuid():N}.pdf");
        try
        {
            // The copy holds everything of the working copy, the information dictionary and the XMP included: only its owner may read it
            // on Unix (the mode applies at creation; Windows keeps the access rules of the per-user temp folder).
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 81_920 };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(path, options))
            {
                await SaveRewriteAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref redactionDryRunWrites);
            VerifySeam?.WrittenCopy?.Invoke(path);
            if (pages.Count == 0)
            {
                return refusals;
            }

            PdfDocument written;
            try
            {
                written = await new PdfEngine().OpenAsync(path, password: null, cancellationToken).ConfigureAwait(false);
            }
            catch (PdfOpenException ex)
            {
                return [.. pages.Select(p => new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [p.Page], $"The rewritten document could not be opened again for the check ({ex.Error})."))];
            }

            await using (written)
            {
                foreach (var entry in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var reason = await CheckPageAsync(original, written, entry, cancellationToken).ConfigureAwait(false);
                    if (reason is not null)
                    {
                        refusals.Add(new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [entry.Page], reason));
                    }
                }
            }

            return refusals;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A temporary file that cannot be deleted right now is the system's to clean up.
            }
            catch (UnauthorizedAccessException)
            {
                // The same.
            }
        }
    }

    private async Task<string?> CheckPageAsync(PdfDocument original, PdfDocument written, RedactionPageAreas entry, CancellationToken cancellationToken)
    {
        try
        {
            var size = await original.GetPageSizeAsync(entry.Page, cancellationToken).ConfigureAwait(false);
            var scale = RedactionRenderCheck.ScaleFor(size);
            var options = new RenderOptions { Priority = PdfPriority.Background };
            VerifySeam?.Rendering?.Invoke(original);
            var before = await original.RenderPageAsync(entry.Page, scale, options, cancellationToken).ConfigureAwait(false);
            VerifySeam?.Rendering?.Invoke(written);
            var after = await written.RenderPageAsync(entry.Page, scale, options, cancellationToken).ConfigureAwait(false);
            var result = RedactionRenderCheck.Compare(before, after, scale, entry.Areas);
            if (result.SizeDiffers)
            {
                return "The page changed its size when it was written again.";
            }

            if (result.Passes)
            {
                return null;
            }

            var box = result.Box!.Value;
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"The page looks different outside the marked areas after it is written again ({result.Different} pixels, around x {box.Left:0.#} to {box.Right:0.#} pt and y {box.Top:0.#} to {box.Bottom:0.#} pt from the top left): PDFium cannot write this page without changing it.");
        }
        catch (Exception ex) when (ex is PdfRenderException or InvalidDataException)
        {
            return "The page could not be rendered for the check, so it cannot be shown that nothing outside the marked areas changed.";
        }
    }

    /// <summary>
    /// The pixels of the images on a page that lie under a rectangle (read with <c>FPDFImageObj_GetRenderedBitmap</c> at the pixel size of
    /// each image), for the check of a redacted document: after redaction every one must be the colour of the area. Images inside form
    /// objects are read too. An image that is turned or sheared is returned whole (it is never cut at the rectangle), one that cannot be
    /// read as an entry of size 0. Call it on the document that was written and opened again, not on a working copy.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="area">The rectangle in points from the top left of the displayed page.</param>
    /// <param name="cancellationToken">Cancels before the read starts.</param>
    /// <returns>One entry per image whose bounds touch the rectangle and that has pixels under it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="RedactionException">The rectangle is not finite or has no size.</exception>
    /// <exception cref="InvalidOperationException">The page has more objects than redaction handles.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public async Task<IReadOnlyList<ImageAreaPixels>> GetImageAreaPixelsAsync(int page, PageRect area, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        if (!double.IsFinite(area.Left) || !double.IsFinite(area.Top) || !double.IsFinite(area.Right) || !double.IsFinite(area.Bottom) || !(area.Right > area.Left) || !(area.Bottom > area.Top))
        {
            throw new RedactionException(RedactionError.InvalidArea, "A rectangle needs finite edges and a positive size.");
        }

        var rectangle = new PdfiumRedactionArea(area.Left, area.Top, area.Right, area.Bottom);
        var read = await document.Thread.InvokeAsync(
            () => PdfiumRedactionRead.ReadImageAreaPixels(document, page, rectangle),
            PdfiumPriority.Background,
            cancellationToken).ConfigureAwait(false);
        return [.. read.Select(r => new ImageAreaPixels(r.Width, r.Height, r.Bgra))];
    }

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

    private async Task<PageRedactionImpact> RunRedactionAsync(int page, IReadOnlyList<PageRect> areas, RedactionFill fill, RedactionImageTreatment treatment, RedactionImageStrength strength, RedactionOverlayWork? overlay, bool apply, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(areas);
        ThrowIfNotAPage(page);
        var pdfiumTreatment = treatment switch
        {
            RedactionImageTreatment.Solid => PdfiumImageTreatment.Solid,
            RedactionImageTreatment.Pixelate => PdfiumImageTreatment.Pixelate,
            RedactionImageTreatment.Blur => PdfiumImageTreatment.Blur,
            _ => throw new ArgumentOutOfRangeException(nameof(treatment)),
        };
        var veryCoarse = strength switch
        {
            RedactionImageStrength.Coarse => false,
            RedactionImageStrength.VeryCoarse => true,
            _ => throw new ArgumentOutOfRangeException(nameof(strength)),
        };
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

        var pdfiumFill = new PdfiumRedactionFill(fill.Red, fill.Green, fill.Blue);
        if (overlay is not null && overlay.Texts.Count != copy.Length)
        {
            throw new ArgumentException("The overlay needs one text (or none) for each rectangle.", nameof(overlay));
        }

        var overlaySpec = overlay is { HasText: true } ? overlay.ToSpec(document.Thread) : null;
        if (!apply)
        {
            var dry = await document.Thread.InvokeSteppedAsync(
                () => PdfiumRedactionRun.Start(document, page, copy, pdfiumFill, apply: false, onModified: null, RedactionSeam, pdfiumTreatment, veryCoarse, overlaySpec),
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
                    RedactionSeam,
                    pdfiumTreatment,
                    veryCoarse,
                    overlaySpec),
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
        impact.ImagesOverwritten,
        impact.ImagesRemovedWhole,
        impact.PathsCropped,
        impact.PathsRemovedWhole,
        impact.FormObjectsTouched,
        impact.AnnotationsRemoved,
        [.. impact.Fallbacks.Select(f => new RedactionFallback(
            f.Kind switch
            {
                PdfiumFallbackKind.FormObjectRemovedWhole => RedactionFallbackKind.FormObjectRemovedWhole,
                PdfiumFallbackKind.ImageRemovedWhole => RedactionFallbackKind.ImageRemovedWhole,
                PdfiumFallbackKind.PathRemovedWhole => RedactionFallbackKind.PathRemovedWhole,
                _ => RedactionFallbackKind.TextObjectRemovedWhole,
            },
            page,
            f.Reason))],
        [.. impact.RemovedText],
        impact.RefusalReason is null ? null : new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [page], impact.RefusalReason),
        [.. (impact.RemovedAreas ?? []).Select(static a => new PageRect(a.Left, a.Top, a.Right, a.Bottom))],
        ImageAreasTreated: impact.ImageAreasTreated,
        ImageAreasSolid: impact.ImageAreasSolid,
        ImageSolidReasons: impact.ImageSolidReasons is { Count: > 0 } reasons ? [.. reasons.Select(static r => SolidReasonOf(r))] : null,
        OverlaysNotShown: impact.OverlaysNotShown,
        OverlaysNotFitting: impact.OverlaysNotFitting,
        OverlaysDrawn: impact.OverlaysDrawn,
        OverlaysOverLimit: impact.OverlaysOverLimit,
        OverlayPaths: impact.OverlayPaths,
        OverlaySegments: impact.OverlaySegments,
        OverlayLimitReached: impact.OverlayLimitReached);

    /// <summary>The reason an image area got the solid fill, one to one (an unknown value is a programming error and throws, it never maps to a guess).</summary>
    internal static ImageSolidReason SolidReasonOf(PdfiumImageSolidReason reason) => reason switch
    {
        PdfiumImageSolidReason.TooSmall => ImageSolidReason.TooSmall,
        PdfiumImageSolidReason.TurnedOrSheared => ImageSolidReason.TurnedOrSheared,
        PdfiumImageSolidReason.Clipped => ImageSolidReason.Clipped,
        PdfiumImageSolidReason.Overlapping => ImageSolidReason.Overlapping,
        PdfiumImageSolidReason.Unreadable => ImageSolidReason.Unreadable,
        PdfiumImageSolidReason.TooManyImages => ImageSolidReason.TooManyImages,
        _ => throw new InvalidOperationException($"Unknown reason for the solid fill of an image: {reason}."),
    };
}
