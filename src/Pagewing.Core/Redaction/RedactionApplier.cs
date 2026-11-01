using System.Globalization;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.Core.Redaction;

/// <summary>What <see cref="RedactionApplier"/> writes.</summary>
/// <param name="Plan">The marks and the pages to turn into images, made for the document the session has open; not empty.</param>
/// <param name="TargetPath">The file to write: the path of the open document ("save in the original file") or another one ("Save as"). An existing file is replaced.</param>
/// <param name="OverwriteChangedOriginal">Whether the user confirmed overwriting another file that now stands at the path of the original. The app never sets it.</param>
/// <param name="Rewritten">
/// Called on the calling thread with the checked, reopened document right before the session adopts it, or <see langword="null"/>.
/// The caller uses it to learn that the document that is about to arrive in the session is this one. Must not throw.
/// </param>
/// <param name="ReplaceExisting">
/// Whether a file that stands at <paramref name="TargetPath"/> when the checked file is moved into place is replaced (the default).
/// With <see langword="false"/> nothing is ever replaced: such a file ends in <see cref="SaveFailureReason.TargetExists"/> and stays
/// as it is (command line without <c>--force</c>).
/// </param>
public sealed record RedactionApplyRequest(
    RedactionPlan Plan,
    string TargetPath,
    bool OverwriteChangedOriginal = false,
    Action<PdfDocument>? Rewritten = null,
    bool ReplaceExisting = true);

/// <summary>The result of <see cref="RedactionApplier"/>: the file is written and passed its check.</summary>
/// <param name="Saved">The result of the rewrite; its document belongs to the session when <paramref name="Adopted"/> is <see langword="true"/>, otherwise to the caller (the command line) who disposes it.</param>
/// <param name="Adopted">Whether the session took the written file as its document (it did not when it was closed or opened another file meanwhile; the file is written all the same). Always <see langword="false"/> without a session.</param>
/// <param name="Check">The check the file passed: no findings, and the lines of what it did not check (the report states them).</param>
public sealed record RedactionApplyResult(SaveResult Saved, bool Adopted, RedactionCheck Check);

/// <summary>
/// Applies a redaction plan (milestone 14, the pipeline of the spec, 5.3 to 5.5): redacts a working copy opened from the <em>file</em>
/// (never the shown document), rebuilds the pages into a new document without pages, writes that in full through the check of
/// <see cref="DocumentSaver.RewriteAsync(PdfDocument, PdfEngine, RewriteRequest, IProgress{double}?, CancellationToken)"/> and, with
/// a session, lets the session adopt the checked file. The check of the written file is the part that decides: besides the standard
/// fingerprints of the pages it runs <see cref="RedactionVerifier"/>, the scan for page keys PDFium cannot remove and the render
/// comparison with the original outside the marks (see <see cref="RedactionFindingKind"/>); a finding ends in
/// <see cref="SaveFailureReason.RedactionFailed"/> with the target unchanged. Any failure or cancellation before the target is replaced
/// leaves the target byte for byte as it was, removes the temporary file, and keeps the plan and the session as they were.
/// </summary>
public static class RedactionApplier
{
    // Where the time goes, only a guess for the progress bar: redacting the pages, then writing and checking the file.
    private const double BuildShare = 0.5;

    /// <summary>
    /// Applies <paramref name="request"/>. Call it from a pool thread, never from the UI thread: it reads file stamps and the adoption
    /// closes the old document.
    /// </summary>
    /// <remarks>
    /// The caller ran <see cref="RedactionExecutor.AnalyzeAsync(RedactionPlan, string, string?, CancellationToken)"/> and showed the
    /// user what it lists, and honoured <see cref="RedactionAnalysis.IsRefused"/>; a refusal that is found here nevertheless is
    /// <see cref="RedactionRefusedException"/>. The working copy is built from the file at the path of the open document; it must be
    /// the file the session shows, which is checked before anything is written: a file that was replaced or rewritten since it was
    /// opened ends in <see cref="SaveFailureReason.SourceModified"/>, with no way to overwrite it. Encrypted files are refused.
    /// </remarks>
    /// <param name="session">The session whose open document the plan was made for.</param>
    /// <param name="engine">Opens the working copy and the written file.</param>
    /// <param name="request">What to write.</param>
    /// <param name="progress">Receives 0 to 1; never goes back.</param>
    /// <param name="cancellationToken">Cancels until the target is replaced; afterwards it has no effect.</param>
    /// <returns>The result and whether the session adopted the written file.</returns>
    /// <exception cref="InvalidOperationException">The session has no open document.</exception>
    /// <exception cref="ArgumentException">The plan is empty: no mark and no page to turn into an image.</exception>
    /// <exception cref="RedactionException">A mark is on a page the document does not have.</exception>
    /// <exception cref="RedactionRefusedException">The file is refused (nothing was written).</exception>
    /// <exception cref="SaveFailureException">The check found something (<see cref="SaveFailureReason.RedactionFailed"/>, <see cref="SaveFailureException.Details"/> is the <see cref="RedactionCheck"/>) or the rewrite failed; the target is unchanged.</exception>
    /// <exception cref="RedactionAdoptException">The file is written, but the session could not take it over.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    public static Task<RedactionApplyResult> ApplyAsync(
        ViewerSession session,
        PdfEngine engine,
        RedactionApplyRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken) =>
        ApplyAsync(session, engine, request, progress, hooks: null, cancellationToken);

    internal static async Task<RedactionApplyResult> ApplyAsync(
        ViewerSession session,
        PdfEngine engine,
        RedactionApplyRequest request,
        IProgress<double>? progress,
        RedactionApplyHooks? hooks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = session.SnapshotForSave() ?? throw new InvalidOperationException("The session has no open document.");
        var shown = snapshot.Document;
        var monotonic = new Monotonic(progress);
        return await WriteCoreAsync(
            engine,
            shown,
            snapshot.Path,
            snapshot.OpenedStamp,
            snapshot.Password,
            request,
            monotonic,
            hooks,
            async result =>
            {
                // From here on the file is written, whatever happens to the session: a failure now is told apart from a failed write.
                try
                {
                    try
                    {
                        request.Rewritten?.Invoke(result.Saved.Reopened);
                    }
                    catch (Exception)
                    {
                        // The notification must not keep the checked file from being adopted; the caller promised not to throw.
                    }

                    var adopted = await session.AdoptRewrittenDocumentAsync(result.Saved.Reopened, request.TargetPath, replacing: shown).ConfigureAwait(false);
                    monotonic.Report(1);
                    return result with { Adopted = adopted };
                }
                catch (Exception ex)
                {
                    await RedactionBuild.CloseQuietlyAsync(result.Saved.Reopened).ConfigureAwait(false);
                    throw new RedactionAdoptException(request.TargetPath, ex);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="request"/> for a caller that has no <see cref="ViewerSession"/> (the command line): the same working
    /// copy, the same checks and the same checked full rewrite, but nothing adopts the result. The caller owns
    /// <see cref="SaveResult.Reopened"/> of the result and disposes it. <see cref="RedactionApplyRequest.Rewritten"/> is not called.
    /// </summary>
    /// <param name="engine">Opens the working copy and the written file.</param>
    /// <param name="shown">The document the plan was made for, opened from <paramref name="path"/>; it is only read.</param>
    /// <param name="path">The full path <paramref name="shown"/> was opened from.</param>
    /// <param name="password">The password of the document, or <see langword="null"/>.</param>
    /// <param name="openedStamp">The stamp at <paramref name="path"/> taken when the document was opened, or <see langword="null"/> to skip the check that another file stands there (only relevant when the target is <paramref name="path"/>).</param>
    /// <param name="request">What to write.</param>
    /// <param name="progress">Receives 0 to 1; never goes back.</param>
    /// <param name="cancellationToken">Cancels until the target is replaced; afterwards it has no effect.</param>
    /// <returns>The result; its document is open and belongs to the caller; <see cref="RedactionApplyResult.Adopted"/> is <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException">The plan is empty: no mark and no page to turn into an image.</exception>
    /// <exception cref="RedactionException">A mark is on a page the document does not have.</exception>
    /// <exception cref="RedactionRefusedException">The file is refused (nothing was written).</exception>
    /// <exception cref="SaveFailureException">The check found something or the rewrite failed; the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    public static Task<RedactionApplyResult> WriteAsync(
        PdfEngine engine,
        PdfDocument shown,
        string path,
        string? password,
        FileStamp? openedStamp,
        RedactionApplyRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return WriteCoreAsync(engine, shown, path, openedStamp, password, request, new Monotonic(progress), hooks: null, static result => Task.FromResult(result), cancellationToken);
    }

    private static async Task<RedactionApplyResult> WriteCoreAsync(
        PdfEngine engine,
        PdfDocument shown,
        string path,
        FileStamp? openedStamp,
        string? password,
        RedactionApplyRequest request,
        IProgress<double> progress,
        RedactionApplyHooks? hooks,
        Func<RedactionApplyResult, Task<RedactionApplyResult>> written,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrEmpty(request.TargetPath);
        var executor = new RedactionExecutor(engine) { Hooks = hooks };

        // The working copy is redacted, checked page by page, and rebuilt into a new document; after any refusal or exception
        // inside, it is discarded (never saved).
        await using var build = await executor.BuildAsync(request.Plan, shown, path, password, new RedactionExecutor.Scaled(progress, 0, BuildShare), cancellationToken).ConfigureAwait(false);

        // What the written file must look like comes from the unmodified shown document, never from the working copy: its page
        // caches and the form environment hang on page indices and it is only read through raw pages.
        var expected = await ExpectedFingerprintsAsync(shown, request.Plan, cancellationToken).ConfigureAwait(false);
        RedactionCheck? passed = null;

        // Every document the rebuilt one reads from is listed: the shown document and the working copy the pages were imported from
        // (a different handle on the same path). Each is compared handle against handle before, during and after the write.
        var rewrite = new RewriteRequest(
            path,
            request.TargetPath,
            openedStamp,
            password,
            expected,
            [shown, build.WorkingCopy],
            request.OverwriteChangedOriginal,
            request.ReplaceExisting,
            async (context, token) => passed = await CheckAsync(shown, build, context, hooks, token).ConfigureAwait(false),
            RequiresListedSources: true);
        hooks?.RewriteStarting?.Invoke(rewrite, build.Clean, build.WorkingCopy);
        var saved = await DocumentSaver.RewriteAsync(build.Clean, engine, rewrite, new RedactionExecutor.Scaled(progress, BuildShare, 1 - BuildShare), hooks?.Faults, cancellationToken).ConfigureAwait(false);
        if (passed is null)
        {
            // The saver replaced the target without the hook having run to its end: never report a check that did not happen.
            await RedactionBuild.CloseQuietlyAsync(saved.Reopened).ConfigureAwait(false);
            throw new SaveFailureException(SaveFailureReason.RedactionFailed, "The check of the written file could not be completed.");
        }

        return await written(new RedactionApplyResult(saved, Adopted: false, passed)).ConfigureAwait(false);
    }

    /// <summary>
    /// The fingerprints the standard check compares: every page as the shown document has it (size and rotation always, the text of the
    /// pages the standard check reads), except the pages that are redacted on purpose, where only size and rotation are compared. The
    /// text of a redacted page is covered by the check of the redaction itself. A page that becomes an image has the size the user sees
    /// (after rotation and crop box, which the original reports) and no rotation: it is a new page of that size, drawn upright.
    /// </summary>
    private static async Task<IReadOnlyList<PageFingerprint>> ExpectedFingerprintsAsync(PdfDocument shown, RedactionPlan plan, CancellationToken cancellationToken)
    {
        var count = shown.PageCount;
        var expected = new PageFingerprint[count];
        Array.Fill(expected, PageEditExecutor.Unchecked);
        var sampled = PageEditExecutor.SampledPages(count);
        var read = await PageEditExecutor.FingerprintsAsync(shown, sampled, cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < sampled.Count; i++)
        {
            expected[sampled[i]] = plan.RasterPages.Contains(sampled[i])
                ? read[i] with { Rotation = 0, CompareText = false }
                : plan.Pages.Contains(sampled[i]) ? read[i] with { CompareText = false } : read[i];
        }

        return expected;
    }

    /// <summary>
    /// The check of the written file (the hook of the saver): the verifier, the scan for page keys on the written bytes (strict: a
    /// cross-reference stream is not read) and the comparison of the rendered pages with the original outside the marks. Throws a
    /// <see cref="SaveFailureException"/> with the whole <see cref="RedactionCheck"/> as <see cref="SaveFailureException.Details"/> when
    /// anything is found; returns the check, which is clean, otherwise.
    /// </summary>
    private static async Task<RedactionCheck> CheckAsync(PdfDocument shown, RedactionBuild build, RewriteCheckContext context, RedactionApplyHooks? hooks, CancellationToken cancellationToken)
    {
        var reopened = context.Reopened;
        RedactionCheck verified;
        List<RedactionFinding> extra = [];
        await using (var file = new FileStream(context.TemporaryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan))
        {
            verified = await RedactionVerifier.VerifyAsync(reopened, file, build.Expectation, cancellationToken).ConfigureAwait(false);
            file.Position = 0;
            var keys = PageKeyScanner.Scan(file, reopened.PageCount, allowCrossReferenceStream: false, cancellationToken: cancellationToken);
            extra.AddRange(KeyFindings(keys));

            // What the cleaning rebuild removed at the document level is read back from the written file: "cleaned" in the report
            // means this read found nothing, never that the rebuild was meant to.
            if (keys.WasChecked)
            {
                extra.AddRange(hooks?.CleanupReadBack is { } replaced
                    ? await replaced(reopened, keys, cancellationToken).ConfigureAwait(false)
                    : await CleanupReadBack.ReadAsync(reopened, keys, cancellationToken).ConfigureAwait(false));
            }
        }

        extra.AddRange(await RenderFindingsAsync(shown, reopened, build, cancellationToken).ConfigureAwait(false));
        var rasterPages = build.Expectation.RasterPages?.Count ?? 0;
        var check = new RedactionCheck(
            [.. verified.Findings, .. extra],
            [.. verified.NotChecked, .. RedactionReportText.ApplyNotCheckedFor(rasterPages)],
            [.. verified.Items, .. RedactionReportText.ApplyNotCheckedItemsFor(rasterPages)]);
        if (!check.IsClean)
        {
            throw new SaveFailureException(SaveFailureReason.VerificationFailed, "The check of the redacted file found a problem.") { Details = check };
        }

        return check;
    }

    private static IEnumerable<RedactionFinding> KeyFindings(PageKeyScan scan)
    {
        if (!scan.WasChecked)
        {
            yield return new RedactionFinding(RedactionFindingKind.PageKeysStillPresent, null, "The page keys of the written file could not be checked, so it cannot be shown that no page still carries a thumbnail, private data, metadata or actions.");
            yield break;
        }

        foreach (var finding in scan.Findings)
        {
            yield return new RedactionFinding(
                RedactionFindingKind.PageKeysStillPresent,
                finding.Page,
                string.Create(CultureInfo.InvariantCulture, $"The page still carries {string.Join(", ", finding.Keys)}, which PDFium cannot remove."));
        }
    }

    private static async Task<IReadOnlyList<RedactionFinding>> RenderFindingsAsync(PdfDocument shown, PdfDocument reopened, RedactionBuild build, CancellationToken cancellationToken)
    {
        var findings = new List<RedactionFinding>();

        // The rebuilt file has no form: PDFium draws the fields of the original through its form environment and nothing of them in
        // the result, so for a document with a form the annotations (fields included) are left out of both renders.
        var options = new RenderOptions { Priority = PdfPriority.Background, Annotations = !shown.HasAcroForm };
        foreach (var impact in build.Impacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var areas = RedactionPageAreas.For(build.Expectation.Plan.AreasOn(impact.Page), impact).Areas;
            try
            {
                var size = await shown.GetPageSizeAsync(impact.Page, cancellationToken).ConfigureAwait(false);
                var scale = RedactionRenderCheck.ScaleFor(size);
                var before = await shown.RenderPageAsync(impact.Page, scale, options, cancellationToken).ConfigureAwait(false);
                var after = await reopened.RenderPageAsync(impact.Page, scale, options, cancellationToken).ConfigureAwait(false);
                var result = RedactionRenderCheck.Compare(before, after, scale, areas);
                if (result.SizeDiffers)
                {
                    findings.Add(new RedactionFinding(RedactionFindingKind.PageChangedOutsideAreas, impact.Page, "The page has another size in the written file."));
                }
                else if (!result.Passes)
                {
                    var box = result.Box!.Value;
                    findings.Add(new RedactionFinding(
                        RedactionFindingKind.PageChangedOutsideAreas,
                        impact.Page,
                        string.Create(CultureInfo.InvariantCulture, $"The page looks different outside the marked areas ({result.Different} pixels, around x {box.Left:0.#} to {box.Right:0.#} pt and y {box.Top:0.#} to {box.Bottom:0.#} pt from the top left).")));
                }
            }
            catch (Exception ex) when (ex is PdfRenderException or InvalidDataException)
            {
                findings.Add(new RedactionFinding(RedactionFindingKind.PageChangedOutsideAreas, impact.Page, "The page could not be rendered for the comparison, so it cannot be shown that nothing outside the marked areas changed."));
            }
        }

        return findings;
    }

    /// <summary>Reports only increasing values (a retry of a step must never move the bar back).</summary>
    private sealed class Monotonic(IProgress<double>? target) : IProgress<double>
    {
        private readonly object gate = new();
        private double last;

        public void Report(double value)
        {
            if (target is null)
            {
                return;
            }

            double clamped;
            lock (gate)
            {
                clamped = Math.Clamp(value, 0, 1);
                if (clamped <= last)
                {
                    return;
                }

                last = clamped;
            }

            target.Report(clamped);
        }
    }
}
