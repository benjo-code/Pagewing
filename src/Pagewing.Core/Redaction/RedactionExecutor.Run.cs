using System.Globalization;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>Hooks into the redaction pipeline for tests (the real work never sets them).</summary>
internal sealed class RedactionApplyHooks
{
    /// <summary>Gets the action called with the working copy right after it was opened.</summary>
    public Action<PdfDocument>? WorkingCopyOpened { get; init; }

    /// <summary>Gets the stand-in for the rectangles a page is redacted with (a test makes the engine "forget" a place); the checks keep using the plan.</summary>
    public Func<int, IReadOnlyList<Pagewing.Core.Text.PageRect>, IReadOnlyList<Pagewing.Core.Text.PageRect>>? AreasToRedact { get; init; }

    /// <summary>Gets whether the early scan of the working copy for page keys is skipped (the check of the final output stays).</summary>
    public bool SkipEarlyPageKeyScan { get; init; }

    /// <summary>Gets the action that changes the rebuilt document before it is written (a test changes a page outside the marks).</summary>
    public Func<PdfDocument, Task>? CleanBuilt { get; init; }

    /// <summary>Gets the stand-in for the read-back of the cleaned document-level data (a test lets it report items that are still there).</summary>
    public Func<PdfDocument, PageKeyScan, CancellationToken, Task<IReadOnlyList<RedactionFinding>>>? CleanupReadBack { get; init; }

    /// <summary>Gets the hooks of the saver.</summary>
    public SaveFaults? Faults { get; init; }

    /// <summary>Gets the action called with the page right before it is rendered to become an image (a test throws to make the page unreadable).</summary>
    public Action<int>? RasterPageRendering { get; init; }

    /// <summary>Gets the action called with the request, the rebuilt document and the working copy just before the saver runs.</summary>
    public Action<RewriteRequest, PdfDocument, PdfDocument>? RewriteStarting { get; init; }
}

/// <summary>
/// The rebuilt document of a redaction with everything the check of its file needs. Dispose it when the file is written or the
/// redaction failed: it closes the rebuilt document and the redacted working copy it reads from.
/// </summary>
public sealed class RedactionBuild : IAsyncDisposable
{
    private int disposed;

    internal RedactionBuild(PdfDocument clean, PdfDocument workingCopy, RedactionExpectation expectation, IReadOnlyList<PageRedactionImpact> impacts)
    {
        Clean = clean;
        WorkingCopy = workingCopy;
        Expectation = expectation;
        Impacts = impacts;
    }

    /// <summary>Gets the new document that holds the redacted pages and nothing else; write it with <see cref="Pagewing.Core.Saving.DocumentSaver"/>.</summary>
    public PdfDocument Clean { get; }

    /// <summary>Gets the redacted working copy the pages of <see cref="Clean"/> read from; it stays open (and its file stamp is checked) until this object is disposed. Never render it.</summary>
    public PdfDocument WorkingCopy { get; }

    /// <summary>Gets what the check of the written file expects.</summary>
    public RedactionExpectation Expectation { get; }

    /// <summary>Gets what redacting each marked page did, in page order.</summary>
    public IReadOnlyList<PageRedactionImpact> Impacts { get; }

    /// <summary>Closes the rebuilt document and the working copy.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await CloseQuietlyAsync(Clean).ConfigureAwait(false);
        await CloseQuietlyAsync(WorkingCopy).ConfigureAwait(false);
    }

    internal static async Task CloseQuietlyAsync(PdfDocument? document)
    {
        if (document is null)
        {
            return;
        }

        try
        {
            await document.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A document that fails to close must not hide the failure (or the result) of the redaction.
        }
    }
}

public sealed partial class RedactionExecutor
{
    // Redacting the pages takes most of the time of a build; the rest is a guess for the progress bar.
    private const double PassShare = 0.85;

    /// <summary>
    /// Works out what redacting <paramref name="plan"/> will do and whether the file may be redacted at all, on a throwaway working copy
    /// opened from the file at <paramref name="path"/> (not from the shown document, which stays valid): every marked page is
    /// redacted, the copy is written once for the render check of all pages, and the bytes PDFium wrote are scanned for page keys it
    /// cannot remove (an early answer; the final output is checked again when applying). Together with the inventory of what the cleanup
    /// removes and the rebuild loses, this is the content of the card before "Apply". Nothing is written but a temporary file that is
    /// deleted again. <b>Every caller honours <see cref="RedactionAnalysis.IsRefused"/></b>.
    /// </summary>
    /// <param name="plan">The marks.</param>
    /// <param name="path">The path of the file the plan was made for.</param>
    /// <param name="password">The password of the file, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels between the pages.</param>
    /// <exception cref="RedactionException">A mark is on a page the document does not have.</exception>
    /// <exception cref="SaveFailureException">The file at the path is not the file that was opened (<see cref="SaveFailureReason.SourceModified"/>).</exception>
    /// <exception cref="PdfOpenException">The file cannot be opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<RedactionAnalysis> AnalyzeAsync(RedactionPlan plan, string path, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var original = await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        return await AnalyzeAsync(plan, original, path, password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>As <see cref="AnalyzeAsync(RedactionPlan, string, string?, CancellationToken)"/>, with the unmodified document the page renders are compared with (the shown document, opened from <paramref name="path"/>).</summary>
    /// <param name="plan">The marks.</param>
    /// <param name="original">An unmodified document opened from <paramref name="path"/>; never a working copy.</param>
    /// <param name="path">The path of the file the plan was made for.</param>
    /// <param name="password">The password of the file, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels between the pages.</param>
    /// <exception cref="RedactionException">A mark is on a page the document does not have.</exception>
    /// <exception cref="SaveFailureException">The file at the path is not the file <paramref name="original"/> was opened from.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<RedactionAnalysis> AnalyzeAsync(RedactionPlan plan, PdfDocument original, string path, string? password, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrEmpty(path);
        EnsureMarksFit(plan, original);
        var withoutTerm = plan.Marks.Count(static m => string.IsNullOrWhiteSpace(m.Term));

        // What the plan alone decides (an overlay text with a redacted term in it) stands in the list in front of what the file decides;
        // BuildAsync asks the same rule, so the card says what the apply does.
        var planRefusal = RedactionOverlayRule.Refusal(plan);
        if (await FileRefusalAsync(original, cancellationToken).ConfigureAwait(false) is { } file)
        {
            return new RedactionAnalysis([], [], CleanupComplete: true, [], planRefusal is null ? [file] : [file, planRefusal], PageKeysChecked: false, plan.Marks.Count, withoutTerm);
        }

        var inventory = await ReadInventoryAsync(original, path, cancellationToken).ConfigureAwait(false);
        PassResult pass = new([], [], KeysChecked: false, [], OverlayBudget.Full);
        if (!plan.IsEmpty)
        {
            var working = await OpenWorkingCopyAsync(original, path, password, cancellationToken).ConfigureAwait(false);
            try
            {
                pass = await RunPassAsync(plan, original, working, progress: null, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await RedactionBuild.CloseQuietlyAsync(working).ConfigureAwait(false);
            }
        }

        var losses = await WithoutRasterPagesAsync(inventory.Losses, original, plan.RasterPages.Count > 0 ? pass.Raster : [], cancellationToken).ConfigureAwait(false);
        var overwritten = pass.Impacts.Sum(static i => i.ImagesOverwritten);
        if (overwritten > 0)
        {
            losses.Add(new RedactionLoss(RedactionLossKind.ColorSpaceBecomesRgb, overwritten));
        }

        return new RedactionAnalysis(
            pass.Impacts,
            inventory.Cleanup,
            inventory.Complete,
            [.. losses.OrderBy(static l => l.Kind)],
            planRefusal is null ? pass.Refusals : [planRefusal, .. pass.Refusals],
            pass.KeysChecked,
            plan.Marks.Count,
            withoutTerm,
            inventory.AttachmentAnnotations,
            plan.RasterPages.Count > 0 ? pass.Raster : null,
            plan.ImageTreatment);
    }

    /// <summary>
    /// The losses of the rebuild for the pages that stay pages: the internal links and form fields of a page turned into an image are
    /// counted for that page (<see cref="RasterPageInfo"/>: they are gone), so they leave the counts of the rebuild, which says they stay
    /// on the page as annotations. A loss whose count drops to nothing is left out.
    /// </summary>
    private static async Task<List<RedactionLoss>> WithoutRasterPagesAsync(IReadOnlyList<RedactionLoss> losses, PdfDocument original, IReadOnlyList<RasterPageInfo> raster, CancellationToken cancellationToken)
    {
        if (raster.Count == 0)
        {
            return [.. losses];
        }

        var pages = raster.Select(static r => r.Page).Distinct().Order().ToList();
        var links = (await original.GetInternalLinkTargetsAsync(pages, cancellationToken).ConfigureAwait(false)).Sum(static t => t.Length);
        var fields = raster.Sum(static r => r.FormFields);
        var result = new List<RedactionLoss>(losses.Count);
        foreach (var loss in losses)
        {
            var count = loss.Kind switch
            {
                RedactionLossKind.InternalLinksLoseTheirTarget => loss.Count - links,
                RedactionLossKind.FormFieldsNotFillable => loss.Count - fields,
                _ => loss.Count,
            };
            if (count > 0)
            {
                result.Add(loss with { Count = count });
            }
        }

        return result;
    }

    /// <summary>
    /// Redacts the plan on a working copy and rebuilds the pages into a new document without pages (the cleaning rebuild), ready to be
    /// written and checked by <see cref="RedactionApplier"/>; opens the unmodified document for the comparisons itself. Nothing is
    /// written. The refusals the analysis lists (<see cref="RedactionAnalysis.IsRefused"/>) are not read here: they end the build with
    /// <see cref="RedactionRefusedException"/> (page keys, a page that fails the render check) and before anything is changed
    /// (an encrypted file), so a caller that skipped the analysis still never writes a refused file.
    /// </summary>
    /// <param name="plan">The marks and raster pages; not empty.</param>
    /// <param name="path">The path of the file.</param>
    /// <param name="password">The password of the file, or <see langword="null"/>.</param>
    /// <param name="progress">Receives 0 to 1; never goes back.</param>
    /// <param name="cancellationToken">Cancels between the pages.</param>
    /// <returns>The build; the caller disposes it.</returns>
    /// <exception cref="RedactionRefusedException">The file is refused.</exception>
    /// <exception cref="SaveFailureException">The file changed (<see cref="SaveFailureReason.SourceModified"/>), or a page could not be read to count the removed text (<see cref="SaveFailureReason.RedactionFailed"/>).</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<RedactionBuild> BuildAsync(RedactionPlan plan, string path, string? password, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var original = await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        return await BuildAsync(plan, original, path, password, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>As <see cref="BuildAsync(RedactionPlan, string, string?, IProgress{double}?, CancellationToken)"/>, with the unmodified document (the shown one) the comparisons use.</summary>
    /// <param name="plan">The marks; not empty.</param>
    /// <param name="original">An unmodified document opened from <paramref name="path"/>; never a working copy.</param>
    /// <param name="path">The path of the file.</param>
    /// <param name="password">The password of the file, or <see langword="null"/>.</param>
    /// <param name="progress">Receives 0 to 1; never goes back.</param>
    /// <param name="cancellationToken">Cancels between the pages.</param>
    /// <exception cref="RedactionRefusedException">The file is refused.</exception>
    /// <exception cref="SaveFailureException">The file changed, or a page could not be read to count the removed text.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<RedactionBuild> BuildAsync(RedactionPlan plan, PdfDocument original, string path, string? password, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (plan.IsEmpty)
        {
            throw new ArgumentException("There is nothing to redact: the plan has neither a mark nor a page to turn into an image.", nameof(plan));
        }

        EnsureMarksFit(plan, original);

        // Nothing is opened, changed or written for a file that is refused as a whole.
        if (await FileRefusalAsync(original, cancellationToken).ConfigureAwait(false) is { } file)
        {
            throw new RedactionRefusedException(RedactionOverlayRule.Refusal(plan) is { } alsoRefused ? [file, alsoRefused] : [file]);
        }

        // The rule of the overlay text reads the plan only, so it is asked before a working copy is opened (nothing is changed or created).
        if (RedactionOverlayRule.Refusal(plan) is { } planRefusal)
        {
            throw new RedactionRefusedException([planRefusal]);
        }

        var working = await OpenWorkingCopyAsync(original, path, password, cancellationToken).ConfigureAwait(false);
        PdfDocument? clean = null;
        try
        {
            var pass = await RunPassAsync(plan, original, working, new Scaled(progress, 0, PassShare), cancellationToken).ConfigureAwait(false);
            if (pass.Refusals.Count > 0)
            {
                // After any refusal the working copy is only partly redacted; it is discarded, never saved.
                throw new RedactionRefusedException(pass.Refusals);
            }

            if (working.IsRedactionTainted)
            {
                throw new RedactionTaintedException();
            }

            var expectation = await ExpectationAsync(plan, original, cancellationToken).ConfigureAwait(false);
            clean = await BuildCleanAsync(plan, original, working, pass.Budget, value => progress?.Report(PassShare + ((1 - PassShare) / 2 * value)), cancellationToken).ConfigureAwait(false);
            if (Hooks?.CleanBuilt is { } built)
            {
                await built(clean).ConfigureAwait(false);
            }

            progress?.Report(PassShare + ((1 - PassShare) / 2));
            return new RedactionBuild(clean, working, expectation, pass.Impacts);
        }
        catch
        {
            await RedactionBuild.CloseQuietlyAsync(clean).ConfigureAwait(false);
            await RedactionBuild.CloseQuietlyAsync(working).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The texts the check of the result expects to be gone: the term of every mark, as real Unicode text (never the text the engine
    /// reports for a page, which has U+FFFD for characters it could not read exactly), trimmed (a selection may include the white space
    /// next to it), with the number of occurrences in the ORIGINAL document, counted by the matcher the check uses. A mark that is
    /// the same place twice counts once.
    /// </summary>
    internal static async Task<RedactionExpectation> ExpectationAsync(RedactionPlan plan, PdfDocument original, CancellationToken cancellationToken)
    {
        var candidates = new List<RemovedText>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mark in plan.Marks)
        {
            var term = mark.Term?.Trim();
            if (string.IsNullOrEmpty(term))
            {
                continue;
            }

            // The same term over the same rectangles is one place, however many marks were made of it.
            var place = string.Create(
                CultureInfo.InvariantCulture,
                $"{mark.Page}|{mark.Everywhere}|{term}|{string.Join(';', mark.Areas.Select(static a => string.Create(CultureInfo.InvariantCulture, $"{a.Left:R},{a.Top:R},{a.Right:R},{a.Bottom:R}")))}");
            if (seen.Add(place))
            {
                // The groups of an IBAN, a card number or a telephone number are laid out by the spacing of the glyphs, so their spaces are
                // not in the content stream: the raw-bytes check looks for the form without separators as well. E-MAIL IS LEFT OUT ON
                // PURPOSE (deliberate deviation from "every mark with a pattern", milestone 14.1 task 8): the dots and hyphens of an
                // address belong to it, so "johndoe@examplecom" would be a string no file holds and the check would test nothing. The
                // address itself is still searched in the bytes like any term.
                var rawForm = mark.Pattern is RedactionPatternKind.Iban or RedactionPatternKind.Card or RedactionPatternKind.Phone ? RemovedText.RawFormOf(term) : null;
                candidates.Add(new RemovedText(mark.Page, term, mark.Everywhere, 0, rawForm));
            }
        }

        IReadOnlyList<int>? rasterPages = plan.RasterPages.Count > 0 ? [.. plan.RasterPages.Order()] : null;
        var imageAreas = await ImageAreasAsync(plan, original, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new RedactionExpectation(plan, [], plan.Fill, rasterPages, imageAreas);
        }

        try
        {
            // The text of a page that becomes an image is gone in the result: its occurrences are not counted as ones that stay.
            return new RedactionExpectation(plan, await RemovedText.FromOriginalAsync(original, candidates, plan.RasterPages, cancellationToken).ConfigureAwait(false), plan.Fill, rasterPages, imageAreas);
        }
        catch (InvalidDataException ex)
        {
            // A page that cannot be read leaves the number of copies unknown: the result cannot be checked, which is never "fine".
            throw new SaveFailureException(
                SaveFailureReason.RedactionFailed,
                "The check of the redaction could not be completed.",
                ex)
            {
                Details = new RedactionCheck(
                    [new RedactionFinding(RedactionFindingKind.UnreadableStream, null, "A page of the document could not be read to count the removed text, so the result cannot be checked.")],
                    []),
            };
        }
    }

    /// <summary>
    /// For a plan that pixelates or blurs: what every image of the ORIGINAL holds under each area, reduced to the block means the result
    /// must show (<see cref="ExpectedImageArea"/>). The original is read through the same pixel box the check of the result uses, never
    /// through the working copy; the same areas in the same order as <see cref="RedactionVerifier"/> reads back (the first
    /// <see cref="RedactionVerifier.MaxPixelAreas"/> distinct ones). <see langword="null"/> for a plan that fills image areas solid.
    /// </summary>
    private static async Task<IReadOnlyList<ExpectedImageArea>?> ImageAreasAsync(RedactionPlan plan, PdfDocument original, CancellationToken cancellationToken)
    {
        if (plan.ImageTreatment == RedactionImageTreatment.Solid)
        {
            return null;
        }

        var result = new List<ExpectedImageArea>();
        var seen = new HashSet<(int, PageRect)>();
        foreach (var mark in plan.Marks)
        {
            foreach (var area in mark.Areas)
            {
                if (!seen.Add((mark.Page, area)))
                {
                    continue;
                }

                if (seen.Count > RedactionVerifier.MaxPixelAreas)
                {
                    return result;
                }

                // A page turned into an image has one image of the whole page, the area of which is plainly the fill colour.
                if (plan.RasterPages.Contains(mark.Page))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<ImageAreaPixels> parts;
                try
                {
                    parts = await original.GetImageAreaPixelsAsync(mark.Page, area, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or RedactionException)
                {
                    // The originals under an area cannot be read: what the result must show is unknown, which is never "fine".
                    throw new SaveFailureException(
                        SaveFailureReason.RedactionFailed,
                        "The check of the redaction could not be completed.",
                        ex)
                    {
                        Details = new RedactionCheck(
                            [new RedactionFinding(RedactionFindingKind.UnreadableStream, mark.Page, "The images under a marked area of the original could not be read, so the result cannot be checked against the block averages.")],
                            []),
                    };
                }

                foreach (var part in parts)
                {
                    if (part.Width <= 0 || part.Height <= 0 || part.Bgra.Length < (long)part.Width * part.Height * 4)
                    {
                        continue;
                    }

                    // A part under the size for 2 x 2 blocks gets the solid fill, so nothing is expected of it but the fill colour.
                    if (ImageBlocks.Plan(part.Width, part.Height, plan.ImageStrength) is not { } grid)
                    {
                        continue;
                    }

                    var means = ImageBlocks.BlockMeansOf(part.Bgra, part.Width, part.Height, new PixelRect(0, 0, part.Width, part.Height), grid);
                    result.Add(new ExpectedImageArea(mark.Page, area, part.Width, part.Height, means));
                }
            }
        }

        return result;
    }

    private static void EnsureMarksFit(RedactionPlan plan, PdfDocument original)
    {
        foreach (var page in plan.Pages.Concat(plan.RasterPages).Order())
        {
            if (page >= original.PageCount)
            {
                throw new RedactionException(RedactionError.InvalidArea, "A mark or a page to turn into an image is on a page the document does not have.");
            }
        }
    }

    private async Task<PdfDocument> OpenWorkingCopyAsync(PdfDocument original, string path, string? password, CancellationToken cancellationToken)
    {
        var working = await engine.OpenAsync(path, password, cancellationToken).ConfigureAwait(false);
        try
        {
            // The copy is opened from the path just now; it must be the very file the original shows, or the pages built here and the
            // checks that read the original would describe two different files.
            if (working.SourceStampAtOpen != original.SourceStampAtOpen)
            {
                throw new SaveFailureException(SaveFailureReason.SourceModified, "The file is not the one that was opened.");
            }

            Hooks?.WorkingCopyOpened?.Invoke(working);
            return working;
        }
        catch
        {
            await RedactionBuild.CloseQuietlyAsync(working).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// The pages of the plan on the working copy, then the render check of all of them in one write, then the early scan of those written
    /// bytes for page keys. After the first refusal the remaining pages are only analysed (a dry run changes nothing), so that all
    /// refusals are listed; the working copy is then tainted and must be discarded by the caller (it always is).
    /// </summary>
    private async Task<PassResult> RunPassAsync(RedactionPlan plan, PdfDocument original, PdfDocument working, Scaled? progress, CancellationToken cancellationToken)
    {
        // A page that becomes an image is never redacted here (and so never refused as not redactable): it is rendered from the original
        // when the document is built. Its marks only give the rectangles that are painted into the bitmap.
        var pages = plan.Pages.Where(p => !plan.RasterPages.Contains(p)).Order().ToArray();
        var impacts = new List<PageRedactionImpact>(pages.Length);
        var refusals = new List<RedactionRefusal>();
        var (raster, rasterRefusals) = await RasterInfosAsync(plan, original, working, cancellationToken).ConfigureAwait(false);
        refusals.AddRange(rasterRefusals);

        // The overlay text has one budget for the document: the pages redacted in place take from it by their number, then the pages that
        // become images by theirs. The analysis and applying take the same order, so both decide alike.
        var budget = OverlayBudget.Full;
        for (var i = 0; i < pages.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = pages[i];
            var areas = Hooks?.AreasToRedact is { } replace ? replace(page, plan.AreasOn(page)) : plan.AreasOn(page);
            var overlay = RedactionOverlayWork.For(plan, page, areas, budget);
            var impact = refusals.Count == 0
                ? await working.RedactPageAsync(page, areas, plan.Fill, plan.ImageTreatment, plan.ImageStrength, overlay, cancellationToken).ConfigureAwait(false)
                : await working.AnalyzeRedactionAsync(page, areas, plan.ImageTreatment, plan.ImageStrength, overlay, cancellationToken).ConfigureAwait(false);
            if (impact.Refusal is { } refusal)
            {
                refusals.Add(refusal);
            }

            impacts.Add(impact);
            budget = budget.After(impact.OverlayPaths, impact.OverlaySegments, impact.OverlayLimitReached);
            progress?.Report((i + 1) / (double)(pages.Length + 1));
        }

        var afterPages = budget;
        raster = await WithRasterOverlaysAsync(plan, original, raster, budget, cancellationToken).ConfigureAwait(false);
        if (refusals.Count > 0)
        {
            return new PassResult(impacts, refusals, KeysChecked: false, raster, afterPages);
        }

        // One write of the working copy for the render check of all pages; the same bytes give the early page key answer.
        PageKeyScan? keys = null;
        var skipScan = Hooks?.SkipEarlyPageKeyScan == true;
        working.VerifySeam = new RedactionVerifySeam
        {
            WrittenCopy = writtenPath =>
            {
                if (skipScan)
                {
                    return;
                }

                using var stream = new FileStream(writtenPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                keys = PageKeyScanner.Scan(stream, working.PageCount, cancellationToken);
            },
        };
        try
        {
            var areas = impacts.Select(i => RedactionPageAreas.For(plan.AreasOn(i.Page), i)).ToList();
            // With raster pages the copy is written even when no page is compared: the keys of a page that becomes an image are checked too.
            refusals.AddRange(await working.VerifyRedactionRenderAsync(original, areas, writeWithoutPages: plan.RasterPages.Count > 0, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            working.VerifySeam = null;
        }

        if (refusals.Count > 0)
        {
            return new PassResult(impacts, [.. refusals.OrderBy(static r => r.Pages.Count > 0 ? r.Pages[0] : -1)], KeysChecked: false, raster, afterPages);
        }

        if (keys is not null && keys.ToRefusal() is { } keyRefusal)
        {
            refusals.Add(keyRefusal);
        }

        progress?.Report(1);
        return new PassResult(impacts, refusals, KeysChecked: keys is { WasChecked: true }, raster, afterPages);
    }

    private sealed record PassResult(IReadOnlyList<PageRedactionImpact> Impacts, IReadOnlyList<RedactionRefusal> Refusals, bool KeysChecked, IReadOnlyList<RasterPageInfo> Raster, OverlayBudget Budget);

    /// <summary>Takes a part of the progress bar: 0 to 1 of the step becomes <c>start</c> to <c>start + share</c> of the whole.</summary>
    internal sealed class Scaled(IProgress<double>? target, double start, double share) : IProgress<double>
    {
        public void Report(double value) => target?.Report(start + (Math.Clamp(value, 0, 1) * share));
    }
}
