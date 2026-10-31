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
public sealed record RewriteRequest(
    string OriginalPath,
    string TargetPath,
    FileStamp? OriginalStamp,
    string? Password,
    IReadOnlyList<PageFingerprint> ExpectedPages,
    IReadOnlyList<PdfDocument> UnchangedSources,
    bool OverwriteChangedOriginal)
{
    /// <summary>Prints the members like a record does, with the password replaced (secrets never reach a log).</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"OriginalPath = {OriginalPath}, TargetPath = {TargetPath}, OriginalStamp = {OriginalStamp}, Password = {(Password is null ? "null" : "(hidden)")}, ExpectedPages = {ExpectedPages?.Count ?? 0} pages, UnchangedSources = {UnchangedSources?.Count ?? 0}, OverwriteChangedOriginal = {OverwriteChangedOriginal}");
        return true;
    }
}
