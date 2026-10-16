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

    public void Dispose() => handle.Dispose();
}
