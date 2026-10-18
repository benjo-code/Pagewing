namespace Pagewing.Core.Pdf;

/// <summary>The facts about an open document that the status bar shows.</summary>
/// <param name="FileName">File name with extension.</param>
/// <param name="Folder">Containing folder; empty when the path has none.</param>
/// <param name="PageCount">Number of pages.</param>
/// <param name="PdfVersion">PDF version from the file header, for example "1.7"; empty when unknown.</param>
/// <param name="FileSizeBytes">Size of the file; 0 when it cannot be read.</param>
/// <param name="Title">Title from the information dictionary, if any.</param>
/// <param name="Author">Author from the information dictionary, if any.</param>
/// <param name="Created">Creation date from the information dictionary, if present and valid.</param>
public sealed record DocumentInfo(
    string FileName,
    string Folder,
    int PageCount,
    string PdfVersion,
    long FileSizeBytes,
    string? Title,
    string? Author,
    DateTimeOffset? Created);

/// <summary>Collects a <see cref="DocumentInfo"/>.</summary>
public static class DocumentInfoReader
{
    /// <summary>
    /// Reads the metadata of <paramref name="document"/> and the size of the file at <paramref name="fullPath"/>.
    /// A file that cannot be read gives size 0 instead of an exception.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static async Task<DocumentInfo> ReadAsync(PdfDocument document, string fullPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        var metadata = await document.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        return new DocumentInfo(
            FileName: Path.GetFileName(fullPath),
            Folder: Path.GetDirectoryName(fullPath) ?? string.Empty,
            PageCount: document.PageCount,
            PdfVersion: document.PdfVersion,
            FileSizeBytes: ReadFileSize(fullPath),
            Title: metadata.Title,
            Author: metadata.Author,
            Created: PdfDate.TryParse(metadata.CreationDate));
    }

    private static long ReadFileSize(string fullPath)
    {
        try
        {
            return new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }
}
