using System.Globalization;
using System.Text;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Saving;

/// <summary>What <see cref="DocumentSaver.RewriteAsync(PdfDocument, PdfEngine, RewriteRequest, IProgress{double}?, CancellationToken)"/> writes and checks.</summary>
/// <param name="OriginalPath">The file the page edit was made for (the file the working copy was opened from).</param>
/// <param name="TargetPath">
/// The file to write: <paramref name="OriginalPath"/> for "save in the original file", another path for "Save As". An existing
/// target is replaced (the caller asks the user first), unless it is read-only.
/// </param>
/// <param name="OriginalStamp">
/// The stamp at the path of the original (<see cref="FileStamp.Of"/>) taken when the document was opened, or
/// <see langword="null"/> to skip the <see cref="SaveFailureReason.OriginalChanged"/> check.
/// </param>
/// <param name="Password">The password of the document, to reopen the written file; never written anywhere, <see cref="ToString"/> does not show it.</param>
/// <param name="ExpectedPages">
/// The fingerprint of every page of the result, in order (<see cref="PageEditExecutor.FingerprintsAsync(PdfDocument, CancellationToken)"/>
/// computes them for source documents); the written file must have exactly these pages (all of them up to 50, then a sample).
/// </param>
/// <param name="UnchangedSources">
/// Documents opened from the files the result was built from (for example the inserted files, and the shown document):
/// each must still be the file that was opened, checked handle against handle like the working copy itself.
/// </param>
/// <param name="OverwriteChangedOriginal">
/// Whether the user confirmed overwriting the path of the original although another file now stands there. It never overrides
/// <see cref="SaveFailureReason.SourceModified"/>.
/// </param>
/// <param name="ReplaceExisting">
/// Whether a file that stands at the target when the checked file is moved into place is replaced (the default; the caller
/// has asked the user). With <see langword="false"/> the move never replaces: a file that exists at that moment, even one
/// that was created by another program while the file was written, ends in <see cref="SaveFailureReason.TargetExists"/>, with
/// that file untouched and the temporary file removed.
/// </param>
/// <param name="ExtraCheck">
/// A check of the caller that runs after all the standard checks have passed, on the reopened temporary file and before the target is
/// replaced (see <see cref="RewriteCheckContext"/>); <see langword="null"/> for none. It reports a finding by throwing
/// <see cref="SaveFailureException"/> (with <see cref="SaveFailureException.Details"/>); any exception it throws other than
/// <see cref="OperationCanceledException"/> ends the save with <see cref="SaveFailureReason.RedactionFailed"/>, with the target
/// untouched and the temporary file removed. It must not keep the file open or use <see cref="RewriteCheckContext.Reopened"/> after it returns.
/// </param>
/// <param name="RequiresListedSources">
/// Whether the working copy was built from an empty document (<see cref="PdfEngine.CreateEmptyAsync"/>), whose own file guards nothing,
/// and whose pages read lazily from the documents they were imported from. The rule: <b>list every document the rebuilt document reads
/// from</b> in <see cref="UnchangedSources"/>, the document that is shown and the working copy that is the real import source (a
/// different handle on the same path); each is checked handle against handle before the write, after it and just before the replace,
/// because a rewrite in place of any of them while PDFium writes would corrupt the output. With this flag at least one
/// source must be listed, otherwise the save is refused (<see cref="ArgumentException"/>) before the temporary file is created; the
/// saver cannot know which documents the working copy reads from, so completeness of the list is the caller's. The check of the
/// working copy's own file always runs. The flag also lets the progress use the length of the listed sources.
/// </param>
public sealed record RewriteRequest(
    string OriginalPath,
    string TargetPath,
    FileStamp? OriginalStamp,
    string? Password,
    IReadOnlyList<PageFingerprint> ExpectedPages,
    IReadOnlyList<PdfDocument> UnchangedSources,
    bool OverwriteChangedOriginal,
    bool ReplaceExisting = true,
    Func<RewriteCheckContext, CancellationToken, Task>? ExtraCheck = null,
    bool RequiresListedSources = false)
{
    /// <summary>Prints the members like a record does, with the password replaced (secrets never reach a log).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"OriginalPath = {OriginalPath}, TargetPath = {TargetPath}, OriginalStamp = {OriginalStamp}, Password = {(Password is null ? "null" : "(hidden)")}, ExpectedPages = {ExpectedPages?.Count ?? 0} pages, UnchangedSources = {UnchangedSources?.Count ?? 0}, OverwriteChangedOriginal = {OverwriteChangedOriginal}, ReplaceExisting = {ReplaceExisting}, ExtraCheck = {(ExtraCheck is null ? "none" : "set")}, RequiresListedSources = {RequiresListedSources}");
        return true;
    }
}

/// <summary>What <see cref="RewriteRequest.ExtraCheck"/> gets to check.</summary>
/// <param name="Reopened">The written file, opened with the engine and the password; it has passed the standard checks. Owned by the saver.</param>
/// <param name="TemporaryPath">
/// The path of the written file, closed for writing and still in the target folder (it is replaced into the target only after the
/// check returns); readable with <see cref="FileShare.ReadWrite"/>.
/// </param>
public sealed record RewriteCheckContext(PdfDocument Reopened, string TemporaryPath);
