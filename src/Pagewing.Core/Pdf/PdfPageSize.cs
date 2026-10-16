using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>Size of a page in points (1/72 inch), after rotation and CropBox.</summary>
public readonly record struct PdfPageSize(double Width, double Height)
{
    /// <summary>
    /// Gets the size in pixels of the page rendered at <paramref name="scale"/> pixels per point: each side
    /// rounded up, at least 1. This is exactly the size <see cref="PdfDocument.RenderPageAsync"/> produces,
    /// and tiles must lie within it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and positive.</exception>
    /// <exception cref="PdfRenderException">A side does not fit in an <see cref="int"/>.</exception>
    public (int Width, int Height) ToPixels(double scale)
    {
        try
        {
            return PdfiumRenderWork.PagePixelSize(Width, Height, scale);
        }
        catch (PdfiumRenderException ex)
        {
            throw new PdfRenderException(ex.Message, ex);
        }
    }
}
