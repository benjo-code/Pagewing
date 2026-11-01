using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Viewing;

namespace Pagewing.Core.Pages;

/// <summary>What <see cref="PageEditApplier.ApplyAsync"/> writes.</summary>
/// <param name="Plan">The plan, made for the document the session has open.</param>
/// <param name="TargetPath">The file to write: the path of the open document ("save in the original file") or another one ("Save as"). An existing file is replaced.</param>
/// <param name="InsertedFiles">The file of each inserted source the plan uses (empty for a plan of the open document alone).</param>
/// <param name="OverwriteChangedOriginal">Whether the user confirmed overwriting another file that now stands at the path of the original.</param>
/// <param name="ReplaceExisting">
/// Whether a file that stands at <paramref name="TargetPath"/> when the checked file is moved into place is replaced (the
/// default). With <see langword="false"/> nothing is ever replaced: such a file ends in <see cref="SaveFailureReason.TargetExists"/>
/// and stays as it is (command line without <c>--force</c>, split without overwrite).
/// </param>
/// <param name="Rewritten">
/// Called on the calling thread with the checked, reopened document right before the session adopts it, or
/// <see langword="null"/>. The caller uses it to learn that the document that is about to arrive in the session is this
/// one. Must not throw.
/// </param>
public sealed record PageApplyRequest(
    PageEditPlan Plan,
    string TargetPath,
    IReadOnlyDictionary<PageSourceId, PageSourceFile> InsertedFiles,
    bool OverwriteChangedOriginal = false,
    Action<PdfDocument>? Rewritten = null,
    bool ReplaceExisting = true);

/// <summary>The result of <see cref="PageEditApplier.ApplyAsync"/>.</summary>
/// <param name="Saved">The result of the rewrite; its document belongs to the session when <paramref name="Adopted"/> is <see langword="true"/> and is closed otherwise.</param>
/// <param name="Adopted">Whether the session took the written file as its document (it did not when it was closed or opened another file meanwhile; the file is written all the same).</param>
public sealed record PageApplyResult(SaveResult Saved, bool Adopted);

/// <summary>
/// The page changes were written and checked, but the session could not take the written file as its document (or something
/// else failed after the write). The file at <see cref="TargetPath"/> is the rewritten one; the session may still show the old
/// document, so the caller must not treat the page changes as unapplied.
/// </summary>
public sealed class PageApplyAdoptException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PageApplyAdoptException()
        : this(string.Empty, null)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PageApplyAdoptException(string message)
        : this(string.Empty, null)
    {
        _ = message;
    }

    /// <summary>Creates the exception for a written file.</summary>
    /// <param name="targetPath">The path of the rewritten file.</param>
    /// <param name="inner">What failed after the write.</param>
    public PageApplyAdoptException(string targetPath, Exception? inner)
        : base("The page changes were written, but the document could not be taken over.", inner)
    {
        TargetPath = targetPath;
    }

    /// <summary>Gets the path of the file that was written.</summary>
    public string TargetPath { get; }
}

/// <summary>Hooks into <see cref="PageEditApplier"/> for tests.</summary>
internal sealed class PageApplyHooks
{
    /// <summary>Gets the executor used to build the working copy (tests give one that reports the copy it opened).</summary>
    public PageEditExecutor? Executor { get; init; }

    /// <summary>Gets the action called with every inserted file the applier opened for the fingerprints.</summary>
    public Action<PdfDocument>? SourceOpened { get; init; }
}

/// <summary>
/// Applies a page plan to the document a session has open (milestone 13): builds the working copy from the file (never from
/// the shown document), works out what the result must look like from the sources and the plan, rewrites it in full through
/// the check of <see cref="DocumentSaver.RewriteAsync(PdfDocument, PdfEngine, RewriteRequest, IProgress{double}?, CancellationToken)"/>
/// and lets the session adopt the checked file (<see cref="ViewerSession.AdoptRewrittenDocumentAsync"/>). The other side of
/// <see cref="Annotations.AnnotationEditor"/>'s save for page changes: any failure or cancellation before the target is replaced leaves
/// the target byte for byte as it was and the session with its document.
/// </summary>
public static class PageEditApplier
{
    // Building the copy and checking the file take about this share of the time each; only a guess for the progress bar.
    private const double BuildShare = 0.3;

    /// <summary>
    /// Applies <paramref name="request"/>. Call it from a pool thread, never from the UI thread: it reads file stamps and
    /// the adoption closes the old document.
    /// </summary>
    /// <remarks>
    /// The caller must have run <see cref="PageEditExecutor.AnalyzeAsync"/> for this plan and honoured
    /// <see cref="PageEditReport.IsRefused"/> (a refused plan must never be written) and shown the user what the report lists;
    /// this method does not read the report again. The working copy is built from the <em>file</em> at the path of the open
    /// document; it must be the file the session shows, which is checked before anything is written: a file that was replaced
    /// or rewritten since it was opened ends in <see cref="SaveFailureReason.SourceModified"/>, with no way to overwrite it.
    /// </remarks>
    /// <param name="session">The session whose open document the plan was made for.</param>
    /// <param name="engine">Opens the working copy, the inserted files and the written file.</param>
    /// <param name="request">What to write.</param>
    /// <param name="progress">Receives 0 to 1 (from the PDFium thread); never goes back.</param>
    /// <param name="cancellationToken">Cancels until the target is replaced; afterwards it has no effect.</param>
    /// <param name="rewriter">Replaces the rewrite (tests make it fail in ways the saver does not); <see langword="null"/> uses <see cref="DocumentSaver"/>.</param>
    /// <returns>The result and whether the session adopted the written file.</returns>
    /// <exception cref="InvalidOperationException">The session has no open document.</exception>
    /// <exception cref="PageEditException">The plan does not fit the document, a limit is exceeded or a source has no file.</exception>
    /// <exception cref="PdfOpenException">A file cannot be opened.</exception>
    /// <exception cref="InvalidDataException">A page of a source is damaged.</exception>
    /// <exception cref="SaveFailureException">The rewrite failed or the file is not the one that was opened; the target is unchanged.</exception>
    /// <exception cref="PageApplyAdoptException">The file is written, but the session could not take it over.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    public static Task<PageApplyResult> ApplyAsync(
        ViewerSession session,
        PdfEngine engine,
        PageApplyRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        Func<PdfDocument, PdfEngine, RewriteRequest, IProgress<double>?, CancellationToken, Task<SaveResult>>? rewriter = null) =>
        ApplyAsync(session, engine, request, progress, rewriter, hooks: null, cancellationToken);

    internal static async Task<PageApplyResult> ApplyAsync(
        ViewerSession session,
        PdfEngine engine,
        PageApplyRequest request,
        IProgress<double>? progress,
        Func<PdfDocument, PdfEngine, RewriteRequest, IProgress<double>?, CancellationToken, Task<SaveResult>>? rewriter,
        PageApplyHooks? hooks,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = session.SnapshotForSave() ?? throw new InvalidOperationException("The session has no open document.");
        var shown = snapshot.Document;
        return await WriteCoreAsync(
            engine,
            shown,
            snapshot.Path,
            snapshot.OpenedStamp,
            snapshot.Password,
            request,
            progress,
            rewriter,
            hooks,
            async result =>
            {
                // From here on the file is written, whatever happens to the session: a failure now is told apart from a failed write.
                try
                {
                    try
                    {
                        request.Rewritten?.Invoke(result.Reopened);
                    }
                    catch (Exception)
                    {
                        // The notification must not keep the checked file from being adopted; the caller promised not to throw.
                    }

                    var adopted = await session.AdoptRewrittenDocumentAsync(result.Reopened, request.TargetPath, replacing: shown).ConfigureAwait(false);
                    progress?.Report(1);
                    return new PageApplyResult(result, adopted);
                }
                catch (Exception ex)
                {
                    await CloseQuietlyAsync(result.Reopened).ConfigureAwait(false);
                    throw new PageApplyAdoptException(request.TargetPath, ex);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes <paramref name="request"/> for a caller that has no <see cref="ViewerSession"/> (the command line): the same
    /// working copy, the same expected fingerprints and the same checked full rewrite as the session overload of
    /// <see cref="ApplyAsync(ViewerSession, PdfEngine, PageApplyRequest, IProgress{double}?, CancellationToken, Func{PdfDocument, PdfEngine, RewriteRequest, IProgress{double}?, CancellationToken, Task{SaveResult}}?)"/>,
    /// but nothing adopts the result. The caller owns <see cref="SaveResult.Reopened"/> and disposes it.
    /// </summary>
    /// <remarks>
    /// Same contract as the session overload: the caller ran <see cref="PageEditExecutor.AnalyzeAsync"/> and honoured
    /// <see cref="PageEditReport.IsRefused"/> first. <see cref="PageApplyRequest.Rewritten"/> is not called.
    /// </remarks>
    /// <param name="engine">Opens the working copy, the inserted files and the written file.</param>
    /// <param name="shown">The document the plan was made for, opened from <paramref name="path"/>.</param>
    /// <param name="path">The full path <paramref name="shown"/> was opened from.</param>
    /// <param name="password">The password of the document, or <see langword="null"/>; reopens the written file.</param>
    /// <param name="openedStamp">The stamp at <paramref name="path"/> taken when the document was opened, or <see langword="null"/> to skip the check that another file stands there (only relevant when the target is <paramref name="path"/>).</param>
    /// <param name="request">What to write.</param>
    /// <param name="progress">Receives 0 to 1 (from the PDFium thread); never goes back.</param>
    /// <param name="cancellationToken">Cancels until the target is replaced; afterwards it has no effect.</param>
    /// <returns>The result; its document is open and belongs to the caller.</returns>
    /// <exception cref="PageEditException">The plan does not fit the document, a limit is exceeded or a source has no file.</exception>
    /// <exception cref="PdfOpenException">A file cannot be opened.</exception>
    /// <exception cref="InvalidDataException">A page of a source is damaged.</exception>
    /// <exception cref="SaveFailureException">The rewrite failed or the file is not the one that was opened; the target is unchanged.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled; the target is unchanged.</exception>
    public static Task<SaveResult> WriteAsync(
        PdfEngine engine,
        PdfDocument shown,
        string path,
        string? password,
        FileStamp? openedStamp,
        PageApplyRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentException.ThrowIfNullOrEmpty(path);
        return WriteCoreAsync(
            engine,
            shown,
            path,
            openedStamp,
            password,
            request,
            progress,
            rewriter: null,
            hooks: null,
            static result => Task.FromResult(result),
            cancellationToken);
    }

    private static async Task<TResult> WriteCoreAsync<TResult>(
        PdfEngine engine,
        PdfDocument shown,
        string path,
        FileStamp? openedStamp,
        string? password,
        PageApplyRequest request,
        IProgress<double>? progress,
        Func<PdfDocument, PdfEngine, RewriteRequest, IProgress<double>?, CancellationToken, Task<SaveResult>>? rewriter,
        PageApplyHooks? hooks,
        Func<SaveResult, Task<TResult>> written,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Plan);
        ArgumentException.ThrowIfNullOrEmpty(request.TargetPath);
        ArgumentNullException.ThrowIfNull(request.InsertedFiles);
        if (shown.PageCount != request.Plan.PrimaryPageCount)
        {
            throw new PageEditException(PageEditError.OutOfRange, "The document has another number of pages than the plan was made for.");
        }

        var usage = PlanUsage.Of(request.Plan);
        usage.EnsureFilesAreGiven(request.InsertedFiles.Keys, request.InsertedFiles.Count);
        var sources = new Dictionary<PageSourceId, PdfDocument>();
        PdfDocument? working = null;
        try
        {
            // What the result must look like comes from the sources, opened for reading only (their page caches are the
            // truth about the files), never from the working copy that is changed.
            // Pages of an encrypted source in an unencrypted result would be written without their protection. The analysis
            // refuses that first (PageLoss.Blocks); this is the same refusal for a caller that did not consult the report.
            var primaryEncrypted = usage.InsertedIds.Count == 0
                || (await shown.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted;
            foreach (var id in usage.InsertedIds)
            {
                var file = request.InsertedFiles[new PageSourceId(id)];
                var source = await file.OpenAsync(engine, cancellationToken).ConfigureAwait(false);
                sources[new PageSourceId(id)] = source;
                hooks?.SourceOpened?.Invoke(source);
                if (!primaryEncrypted && (await source.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false)).IsEncrypted)
                {
                    throw new SaveFailureException(SaveFailureReason.EncryptedNotSupported, "An encrypted file cannot be inserted into a document that is not encrypted.")
                    {
                        FilePath = file.Path,
                    };
                }
            }

            var expected = await PageEditExecutor.ExpectedFingerprintsAsync(request.Plan, shown, sources, cancellationToken).ConfigureAwait(false);
            working = await (hooks?.Executor ?? new PageEditExecutor(engine))
                .BuildAsync(request.Plan, new PageSourceFile(path, password), request.InsertedFiles, new Scaled(progress, 0, BuildShare), cancellationToken)
                .ConfigureAwait(false);

            // The working copy was opened from the path just now: it must be the very file the session shows. Otherwise another
            // program replaced or rewrote it, and the pages built here (and the fingerprints, which come from the shown
            // document) would describe two different files. The same dead end as the saver's, with no override.
            // An inserted source is opened once for the fingerprints here and once by the executor; both check it against the
            // stamp taken when the file was inserted (PageSourceFile.OpenAsync), so a file that changed since is refused by name.
            if (working.SourceStampAtOpen != shown.SourceStampAtOpen)
            {
                throw new SaveFailureException(SaveFailureReason.SourceModified, "The file is not the one that was opened.");
            }

            // The shown document and the inserted files must still be the files that were read: a file changed meanwhile
            // would give pages the fingerprints do not describe.
            IReadOnlyList<PdfDocument> unchanged = [shown, .. sources.Values];
            var rewrite = new RewriteRequest(
                path,
                request.TargetPath,
                openedStamp,
                password,
                expected,
                unchanged,
                request.OverwriteChangedOriginal,
                request.ReplaceExisting);
            var writing = new Scaled(progress, BuildShare, 1 - BuildShare);
            var result = rewriter is null
                ? await DocumentSaver.RewriteAsync(working, engine, rewrite, writing, cancellationToken).ConfigureAwait(false)
                : await rewriter(working, engine, rewrite, writing, cancellationToken).ConfigureAwait(false);
            return await written(result).ConfigureAwait(false);
        }
        finally
        {
            await CloseQuietlyAsync(working).ConfigureAwait(false);
            foreach (var source in sources.Values)
            {
                await CloseQuietlyAsync(source).ConfigureAwait(false);
            }
        }
    }

    // A document that fails to close must not hide the failure (or the result) of the apply.
    private static async Task CloseQuietlyAsync(PdfDocument? document)
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
            // Nothing more to do with it.
        }
    }

    /// <summary>Takes a part of the progress bar: 0 to 1 of the step becomes <c>start</c> to <c>start + share</c> of the whole.</summary>
    private sealed class Scaled(IProgress<double>? target, double start, double share) : IProgress<double>
    {
        public void Report(double value) => target?.Report(start + (Math.Clamp(value, 0, 1) * share));
    }
}
