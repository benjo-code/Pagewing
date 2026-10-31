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
