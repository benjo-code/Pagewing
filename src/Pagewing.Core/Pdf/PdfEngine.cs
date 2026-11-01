using System.Globalization;
using Pagewing.Core.Saving;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// Opens PDF documents. All PDFium work runs on the process-wide PDFium thread, which starts with the first
/// document; the caller never blocks on PDFium.
/// </summary>
public sealed class PdfEngine
{
    /// <summary>Resolved on the first open, so creating an engine does not start PDFium (startup path).</summary>
    private readonly Func<PdfiumThread> getThread = static () => PdfiumThread.Shared;

    /// <summary>
    /// Opens a PDF file. The file is read on demand and stays open, shared for reading, writing and deleting,
    /// until the document is disposed.
    /// </summary>
    /// <param name="path">Path of the file; relative paths are resolved against the current directory now.</param>
    /// <param name="password">User or owner password of an encrypted file, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the open if it has not started yet.</param>
    /// <exception cref="PdfOpenException">The file could not be opened; <see cref="PdfOpenException.Error"/> says why.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<PdfDocument> OpenAsync(string path, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(path);
        var thread = getThread();
        try
        {
            var (document, pageCount, fileVersion, stamp, formType) = await thread.InvokeAsync(
                () =>
                {
                    // On the PDFium thread like all file access of an open, never on the caller's (UI) thread: a stat can
                    // block for seconds on a network drive or a disk that spins up.
                    var stamp = FileStamp.Of(fullPath);
                    var opened = PdfiumDocument.Open(thread, fullPath, password);
                    try
                    {
                        return (opened, opened.PageCount, opened.FileVersion, stamp, opened.FormType);
                    }
                    catch
                    {
                        opened.Dispose();
                        throw;
                    }
                },
                PdfiumPriority.Interactive,
                cancellationToken).ConfigureAwait(false);
            return new PdfDocument(document, pageCount, FormatVersion(fileVersion), formType) { PathStampAtOpen = stamp };
        }
        catch (PdfiumOpenException ex)
        {
            throw new PdfOpenException(ToOpenError(ex.Error, password), ex);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PdfOpenException(PdfOpenError.FileNotFound, ex);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new PdfOpenException(PdfOpenError.CannotRead, ex);
        }
    }

    /// <summary>
    /// The smallest PDF file that has a catalog and an empty page tree and nothing else: no information dictionary, no pages. Pages
    /// imported into a document opened from it bring none of the document-level data of their source with them, which is what a
    /// cleaning rebuild relies on (Docs/DECISIONS.md, "Schwärzen: Spike und QPDF-Entscheidung (M14)", question 4).
    /// </summary>
    private const string EmptyDocumentText =
        "%PDF-1.7\n"
        + "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
        + "2 0 obj\n<< /Type /Pages /Count 0 /Kids [] >>\nendobj\n"
        + "xref\n0 3\n0000000000 65535 f \n0000000009 00000 n \n0000000058 00000 n \n"
        + "trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n110\n%%EOF\n";

    /// <summary>
    /// Creates a document without pages from a fixed minimal file in the temporary folder (opened like any file, because that is
    /// how the spike checked the cleaning: <c>FPDF_CreateNewDocument</c> was not used). The file is deleted again as soon as the
    /// document is open (it stays readable through PDFium's handle); if it cannot be deleted it stays, a few hundred bytes
    /// under a random name. Meant to receive pages with <see cref="PdfDocument.ImportPagesAsync(PdfDocument, IReadOnlyList{int}, int, CancellationToken)"/>
    /// and to be written with <see cref="PdfDocument.SaveRewriteAsync"/>; it must not be shown or rendered.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open if it has not started yet.</param>
    /// <returns>The document with <see cref="PdfDocument.PageCount"/> 0.</returns>
    /// <exception cref="PdfOpenException">The file could not be written or opened.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async Task<PdfDocument> CreateEmptyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(Path.GetTempPath(), $"pagewing-empty-{Guid.NewGuid():N}.pdf");
        try
        {
            // CreateNew: never an existing file or a link planted under the name; the content is a constant.
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await file.WriteAsync(System.Text.Encoding.ASCII.GetBytes(EmptyDocumentText), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new PdfOpenException(PdfOpenError.CannotRead, ex);
        }

        try
        {
            return await OpenAsync(path, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Stays in the temporary folder; nothing depends on it.
            }
        }
    }

    private static PdfOpenError ToOpenError(PdfiumError error, string? password) => error switch
    {
        PdfiumError.Password => string.IsNullOrEmpty(password) ? PdfOpenError.PasswordRequired : PdfOpenError.PasswordIncorrect,
        PdfiumError.Format => PdfOpenError.NotAPdf,
        PdfiumError.Security => PdfOpenError.UnsupportedEncryption,
        PdfiumError.File => PdfOpenError.CannotRead,
        _ => PdfOpenError.Unknown,
    };

    /// <summary>Formats PDFium's file version (17) as text ("1.7"); empty when unknown.</summary>
    private static string FormatVersion(int? fileVersion) =>
        fileVersion is { } version
            ? string.Create(CultureInfo.InvariantCulture, $"{version / 10}.{version % 10}")
            : string.Empty;
}
