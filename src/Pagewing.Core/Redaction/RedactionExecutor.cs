using Pagewing.Core.Pdf;

namespace Pagewing.Core.Redaction;

/// <summary>
/// Redacts a document (milestone 14): <see cref="AnalyzeAsync(RedactionPlan, string, string?, CancellationToken)"/> works out what a plan does
/// and whether the file may be redacted at all, <see cref="BuildAsync(RedactionPlan, string, string?, IProgress{double}?, CancellationToken)"/>
/// redacts a working copy and rebuilds it for <see cref="RedactionApplier"/>; the rest of this class are the parts that clean the file. A redaction always ends in a rebuild of the pages into a new
/// document without pages (<see cref="PdfEngine.CreateEmptyAsync"/>), which leaves the document-level data of the source behind;
/// what that removes and what it costs is told before anything is applied (<see cref="InventoryAsync"/>), and a file in which the
/// rebuild would leave data behind that PDFium cannot remove is refused (<see cref="PageKeyRefusal"/>, <see cref="FileRefusalAsync"/>).
/// </summary>
/// <param name="engine">The engine that opens the empty document of a rebuild.</param>
public sealed partial class RedactionExecutor(PdfEngine engine)
{
    private const int MaxCleanupDetail = 200;

    /// <summary>Gets the hooks of tests; <see langword="null"/> in production.</summary>
    internal RedactionApplyHooks? Hooks { get; init; }

    /// <summary>
    /// Tells why the file as a whole cannot be redacted: <see cref="RedactionRefusalKind.Encrypted"/> for an encrypted file (milestone
    /// 14 never decrypts silently). Nothing else is checked here; the page keys are checked on the written bytes
    /// (<see cref="PageKeyRefusal"/>).
    /// </summary>
    /// <param name="shown">The open document.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public static async Task<RedactionRefusal?> FileRefusalAsync(PdfDocument shown, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shown);
        var print = await shown.GetPrintInfoAsync(cancellationToken).ConfigureAwait(false);
        return print.IsEncrypted
            ? new RedactionRefusal(RedactionRefusalKind.Encrypted, [], "The file is encrypted; redacting it would write it without its protection, and Pagewing does not remove protection silently.")
            : null;
    }

    /// <summary>
    /// Reads what the cleanup removes (the information dictionary through PDFium, the rest by name from the bytes of the file, also
    /// inside object streams) and what the rebuild loses (internal links, form fields, signatures). Changes nothing.
    /// </summary>
    /// <param name="shown">The open document.</param>
    /// <param name="path">The path of its file; the scan opens it shared for reading.</param>
    /// <param name="cancellationToken">Cancels between the jobs and inside the scan.</param>
    /// <returns>The cleanup list and the loss list; use <see cref="ReadInventoryAsync"/> to also learn whether the file scan was complete.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public static async Task<(IReadOnlyList<CleanupItem> Cleanup, IReadOnlyList<RedactionLoss> Losses)> InventoryAsync(PdfDocument shown, string path, CancellationToken cancellationToken)
    {
        var inventory = await ReadInventoryAsync(shown, path, cancellationToken).ConfigureAwait(false);
        return (inventory.Cleanup, inventory.Losses);
    }

    /// <summary>As <see cref="InventoryAsync"/>, with the statement whether the scan of the file could read every stream.</summary>
    /// <param name="shown">The open document.</param>
    /// <param name="path">The path of its file.</param>
    /// <param name="cancellationToken">Cancels between the jobs and inside the scan.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public static async Task<RedactionInventory> ReadInventoryAsync(PdfDocument shown, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var metadata = await shown.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        var scan = await Task.Run(
            () =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                return RawContentScanner.Inventory(file, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);

        var cleanup = new List<CleanupItem>();
        AddField(cleanup, CleanupKind.Author, metadata.Author);
        AddField(cleanup, CleanupKind.Title, metadata.Title);
        AddField(cleanup, CleanupKind.Subject, metadata.Subject);
        AddField(cleanup, CleanupKind.Keywords, metadata.Keywords);
        AddField(cleanup, CleanupKind.Creator, metadata.Creator);
        AddField(cleanup, CleanupKind.Producer, metadata.Producer);
        AddCount(cleanup, CleanupKind.Xmp, scan.MetadataKeys);
        AddCount(cleanup, CleanupKind.Bookmarks, scan.OutlineItems);
        // The files of paperclip annotations come with the pages and stay; only the rest is the names tree the rebuild drops.
        AddCount(cleanup, CleanupKind.Attachments, Math.Max(0, scan.EmbeddedFiles - scan.AttachmentAnnotations));
        AddCount(cleanup, CleanupKind.JavaScript, scan.JavaScriptActions);
        AddCount(cleanup, CleanupKind.PageLabels, scan.PageLabelKeys);

        var losses = new List<RedactionLoss>();
        var targets = await shown.GetInternalLinkTargetsAsync([.. Enumerable.Range(0, shown.PageCount)], cancellationToken).ConfigureAwait(false);
        var links = targets.Sum(static t => t.Length);
        if (links > 0)
        {
            losses.Add(new RedactionLoss(RedactionLossKind.InternalLinksLoseTheirTarget, links));
        }

        var form = await shown.GetFormInfoAsync(cancellationToken).ConfigureAwait(false);
        if (form.FieldCount > 0)
        {
            losses.Add(new RedactionLoss(RedactionLossKind.FormFieldsNotFillable, form.FieldCount));
        }

        var signatures = await shown.GetSignatureInfoAsync(cancellationToken).ConfigureAwait(false);
        if (signatures.SignatureCount > 0)
        {
            losses.Add(new RedactionLoss(RedactionLossKind.SignatureInvalidated, signatures.SignatureCount));
        }

        return new RedactionInventory(cleanup, losses, scan.Complete, scan.AttachmentAnnotations);
    }

    /// <summary>
    /// Checks the bytes PDFium wrote for page keys it cannot remove (<c>/Thumb</c>, <c>/PieceInfo</c>, a page's <c>/Metadata</c>,
    /// <c>/AA</c>); see <see cref="PageKeyScanner"/>. A file that cannot be checked is refused too, with the reason
    /// <see cref="PageKeyScanner.UnverifiedReason"/>.
    /// </summary>
    /// <param name="writtenWorkingCopy">The bytes PDFium wrote for the working copy (never the original file); readable and seekable.</param>
    /// <param name="pdfiumPageCount">The number of pages PDFium reports for it.</param>
    /// <returns>The refusal, or <see langword="null"/> when the file was checked and is clean.</returns>
    public static RedactionRefusal? PageKeyRefusal(Stream writtenWorkingCopy, int pdfiumPageCount) =>
        PageKeyScanner.Refusal(writtenWorkingCopy, pdfiumPageCount);

    /// <summary>
    /// Writes the pages of the working copy into a new document without pages and the new document to <paramref name="destination"/>,
    /// as a full rewrite: the file has none of the document-level data of the source (information dictionary, XMP, bookmarks,
    /// attachments, scripts, page labels, the form). The working copy must stay open until this returns (the new document keeps
    /// reading its objects). It does not check anything: the refusals and the result check are the caller's.
    /// </summary>
    /// <param name="workingCopy">The working copy, with its redactions done.</param>
    /// <param name="destination">Where the file goes.</param>
    /// <param name="cancellationToken">Cancels between the jobs.</param>
    /// <exception cref="RedactionTaintedException">The working copy is only partly redacted.</exception>
    /// <exception cref="InvalidOperationException">The working copy is encrypted (see <see cref="FileRefusalAsync"/>); nothing is written.</exception>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task RebuildAsync(PdfDocument workingCopy, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingCopy);
        ArgumentNullException.ThrowIfNull(destination);
        if (workingCopy.IsRedactionTainted)
        {
            throw new RedactionTaintedException();
        }

        // Pages of a protected file would be written without their protection: refused before anything is written, never decrypted silently.
        if (await FileRefusalAsync(workingCopy, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            throw new InvalidOperationException(refusal.Reason);
        }

        await using var rebuilt = await engine.CreateEmptyAsync(cancellationToken).ConfigureAwait(false);
        await rebuilt.ImportPagesAsync(workingCopy, [.. Enumerable.Range(0, workingCopy.PageCount)], 0, cancellationToken).ConfigureAwait(false);
        await rebuilt.SaveRewriteAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static void AddField(List<CleanupItem> items, CleanupKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > MaxCleanupDetail)
        {
            // Never cut a surrogate pair in half.
            var cut = char.IsHighSurrogate(value[MaxCleanupDetail - 1]) ? MaxCleanupDetail - 1 : MaxCleanupDetail;
            value = value[..cut] + "…";
        }

        items.Add(new CleanupItem(kind, value));
    }

    private static void AddCount(List<CleanupItem> items, CleanupKind kind, int count)
    {
        if (count > 0)
        {
            items.Add(new CleanupItem(kind, count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}
