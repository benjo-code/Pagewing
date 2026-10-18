using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// A loaded page of a <see cref="PdfiumDocument"/>. It keeps its document open until it is disposed.
/// <see cref="Dispose"/> may be called on any thread; the page is then closed on the PDFium thread.
/// </summary>
internal sealed class PdfiumPage : IDisposable
{
    private readonly PageHandle handle;

    internal PdfiumPage(PageHandle handle, double width, double height)
    {
        this.handle = handle;
        Width = width;
        Height = height;
    }

    /// <summary>Gets the page width in points, after rotation and CropBox.</summary>
    public double Width { get; }

    /// <summary>Gets the page height in points, after rotation and CropBox.</summary>
    public double Height { get; }

    /// <summary>Gets the native page, for PDFium calls on the page's thread.</summary>
    internal PageHandle Handle => handle;

    /// <summary>Loads the text of the page. Must be called on the PDFium thread; the text page keeps this page open.</summary>
    /// <exception cref="InvalidDataException">PDFium could not load the text of the page.</exception>
    public PdfiumTextPage LoadTextPage()
    {
        handle.OwningThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);

        var textHandle = new TextPageHandle(handle.OwningThread, handle);
        try
        {
            var value = Fpdf.FPDFText_LoadPage(handle.DangerousGetHandle());
            if (value == 0)
            {
                // A BCL exception, so no PDFium type leaks through the public Core API.
                throw new InvalidDataException("The text of the page could not be loaded.");
            }

            try
            {
                textHandle.Attach(value);
            }
            catch
            {
                Fpdf.FPDFText_ClosePage(value);
                throw;
            }

            return new PdfiumTextPage(textHandle);
        }
        catch
        {
            textHandle.Dispose();
            throw;
        }
    }

    public void Dispose() => handle.Dispose();

    /// <summary>Returns the native page after checking the thread and that the page is still open.</summary>
    internal nint Access()
    {
        handle.OwningThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(handle.IsClosed, this);
        return handle.DangerousGetHandle();
    }
}
