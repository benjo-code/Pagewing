using Pagewing.Core.Pdf;

namespace Pagewing.Core.Redaction;

/// <summary>
/// Reads back from the written file what the cleaning rebuild is meant to have removed at the document level (milestone 14): the fields
/// of the information dictionary, the bookmarks, the names tree (attached files and scripts), the page labels and the other keys of
/// <see cref="PageKeyScanner.DocumentKeys"/>. Anything that is still there is a finding of kind
/// <see cref="RedactionFindingKind.CleanupItemStillPresent"/>: the report says "cleaned" only for what this read confirmed. The findings
/// name the item and never its content. The XMP and the scripts inside page-level objects (form objects, image objects, annotations)
/// are not part of it; the report says so.
/// </summary>
internal static class CleanupReadBack
{
    /// <summary>Reads the written file back (PDFium for the information dictionary and the bookmarks, the scan of the bytes for the catalog).</summary>
    /// <param name="reopened">The written file, opened again.</param>
    /// <param name="scan">The scan of the written bytes (<see cref="PageKeyScanner.Scan(Stream, int, bool, CancellationToken)"/>); when it could not check the file, nothing is concluded from it.</param>
    /// <param name="cancellationToken">Cancels between the reads.</param>
    /// <returns>The items that are still there.</returns>
    public static async Task<IReadOnlyList<RedactionFinding>> ReadAsync(PdfDocument reopened, PageKeyScan scan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reopened);
        ArgumentNullException.ThrowIfNull(scan);
        var findings = new List<RedactionFinding>();

        var metadata = await reopened.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        Field(findings, "Title", metadata.Title);
        Field(findings, "Author", metadata.Author);
        Field(findings, "Subject", metadata.Subject);
        Field(findings, "Keywords", metadata.Keywords);
        Field(findings, "Creator", metadata.Creator);
        Field(findings, "Producer", metadata.Producer);
        Field(findings, "CreationDate", metadata.CreationDate);
        Field(findings, "ModDate", metadata.ModDate);

        if ((await reopened.GetOutlineAsync(cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            findings.Add(Still("The written file still has bookmarks."));
        }

        if (await reopened.HasPageLabelsAsync(cancellationToken).ConfigureAwait(false))
        {
            findings.Add(Still("The written file still has page labels."));
        }

        // The keys of the catalog and the trailer: exact, from the tokenized bytes (PDFium has no reader for most of them).
        foreach (var key in scan.DocumentKeys)
        {
            findings.Add(Still($"The written file still has {key} at the document level ({Meaning(key)})."));
        }

        return findings;
    }

    private static void Field(List<RedactionFinding> findings, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            findings.Add(Still($"The written file still has a {name} entry in the information dictionary."));
        }
    }

    private static RedactionFinding Still(string detail) => new(RedactionFindingKind.CleanupItemStillPresent, null, detail);

    private static string Meaning(string key) => key switch
    {
        "/Info" => "the information dictionary",
        "/Metadata" => "XMP metadata of the document",
        "/Outlines" => "bookmarks",
        "/Names" => "the names tree: attached files, scripts, named destinations",
        "/PageLabels" => "page labels",
        "/OpenAction" => "an action that runs when the document opens",
        "/AA" => "actions of the document",
        "/AcroForm" => "the form",
        _ => "permissions and signature rights",
    };
}
