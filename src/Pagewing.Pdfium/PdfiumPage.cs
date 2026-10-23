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

    /// <summary>
    /// Draws the page, with its annotations, on a printer device context (<c>HDC</c>), always in the light colours of the
    /// page. Windows only. Must be called on the PDFium thread.
    /// </summary>
    /// <param name="deviceContext">The <c>HDC</c> of a printer with a page started.</param>
    /// <param name="x">Left edge of the display area in device units.</param>
    /// <param name="y">Top edge of the display area in device units.</param>
    /// <param name="width">Width of the display area, after <paramref name="quarterTurns"/>.</param>
    /// <param name="height">Height of the display area, after <paramref name="quarterTurns"/>.</param>
    /// <param name="quarterTurns">0, or 1 to turn the page by 90° clockwise.</param>
    /// <exception cref="PlatformNotSupportedException">The OS is not Windows.</exception>
    /// <exception cref="PdfiumRenderException">PDFium could not draw the page.</exception>
    public void RenderToDevice(nint deviceContext, int x, int y, int width, int height, int quarterTurns)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Printing to a device context needs Windows.");
        }

        ArgumentOutOfRangeException.ThrowIfZero(deviceContext);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(quarterTurns);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quarterTurns, 3);

        var native = Access();
        if (Fpdf.FPDF_RenderPage(deviceContext, native, x, y, width, height, quarterTurns, FpdfConstants.RenderAnnot | FpdfConstants.RenderPrinting) == 0)
        {
            throw new PdfiumRenderException();
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
