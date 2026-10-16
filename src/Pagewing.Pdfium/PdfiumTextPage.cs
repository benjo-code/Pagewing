using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// The text of a loaded page. It keeps its page (and so its document) open until it is disposed. Every member
/// except <see cref="Dispose"/> must be called on the PDFium thread and throws
/// <see cref="PdfiumThreadViolationException"/> otherwise. <see cref="Dispose"/> may be called on any thread;
/// the text page is then closed on the PDFium thread.
/// </summary>
internal sealed unsafe class PdfiumTextPage : IDisposable
{
    private readonly TextPageHandle handle;
    private volatile bool disposed;

    internal PdfiumTextPage(TextPageHandle handle) => this.handle = handle;

    /// <summary>Gets the number of characters (UTF-16 code units) on the page, including line breaks PDFium inserts.</summary>
    public int CharCount => Math.Max(0, Fpdf.FPDFText_CountChars(Access()));

    /// <summary>Gets the whole text of the page exactly as PDFium reports it (lines are separated by CR LF).</summary>
    public string GetText()
    {
        var textPage = Access();
        var count = Math.Max(0, Fpdf.FPDFText_CountChars(textPage));
        if (count == 0)
        {
            return string.Empty;
        }

        // PDFium writes up to count characters plus a terminator.
        var buffer = new ushort[count + 1];
        int written;
        fixed (ushort* pointer = buffer)
        {
            written = Fpdf.FPDFText_GetText(textPage, 0, count, pointer);
        }

        // The return value counts the terminator; 0 means nothing was written.
        var length = Math.Clamp(written - 1, 0, count);
        return length == 0 ? string.Empty : string.Create(length, buffer, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = (char)source[i];
            }
        });
    }

    public void Dispose()
    {
        disposed = true;
        handle.Dispose();
    }

    private nint Access()
    {
        handle.OwningThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(disposed, this);
        return handle.DangerousGetHandle();
    }
}
