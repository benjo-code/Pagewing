using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>What a page is made of, counted by kind (mirror of the core type; this assembly knows nothing of the core).</summary>
internal readonly record struct PdfiumPageObjectSummary(int Images, int Paths, int Texts, int Others, int Annotations);

/// <summary>
/// Raster pages for redaction (milestone 14.1): a page that is one image and nothing else, and a count of what a page is made of.
/// Every method must be called on the PDFium thread and throws <see cref="PdfiumThreadViolationException"/> otherwise.
/// </summary>
/// <remarks>
/// The page is made in a document that is only written (the target of a rebuild), never shown: it is created with the size of the
/// media box, gets one image object whose matrix is the page, and its content is generated at once. The pixels are wrapped in a
/// <c>BGRx</c> bitmap (no alpha, so PDFium writes a plain image with no <c>/SMask</c>) that lives only for the call of
/// <c>FPDFImageObj_SetBitmap</c>, which copies it; the caller's buffer is pinned for that call and not copied again.
/// </remarks>
internal static unsafe class PdfiumRedactionRaster
{
    /// <summary>The most pixels (width times height) of one raster page; the same number as the core's limit (a test pins it).</summary>
    public const int MaxPixels = 32_000_000;

    /// <summary>
    /// Inserts a page of <paramref name="widthPt"/> x <paramref name="heightPt"/> points at <paramref name="index"/> that shows the
    /// pixels over its whole media box. On failure the page is removed again.
    /// </summary>
    /// <param name="destination">The document that receives the page.</param>
    /// <param name="index">The 0-based position, between 0 and the page count.</param>
    /// <param name="widthPt">The width of the media box in points.</param>
    /// <param name="heightPt">The height of the media box in points.</param>
    /// <param name="rotation">The <c>/Rotate</c>: 0, 90, 180 or 270.</param>
    /// <param name="widthPx">The width of the image in pixels.</param>
    /// <param name="heightPx">The height of the image in pixels.</param>
    /// <param name="bgra">The pixels, top row first, four bytes each; the fourth byte is ignored.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index, a size or the rotation is out of range, or the image has more than <see cref="MaxPixels"/> pixels.</exception>
    /// <exception cref="ArgumentException">The pixels are not <paramref name="widthPx"/> * <paramref name="heightPx"/> * 4 bytes.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the page or the image.</exception>
    public static void AddPage(PdfiumDocument destination, int index, double widthPt, double heightPt, int rotation, int widthPx, int heightPx, ReadOnlySpan<byte> bgra)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var native = destination.Access();

        // All checks come before PDFium is asked for anything, and the pixel count before the length of the buffer is trusted.
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, Fpdf.FPDF_GetPageCount(native));
        if (!double.IsFinite(widthPt) || !(widthPt > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(widthPt), widthPt, "The width must be a positive number.");
        }

        if (!double.IsFinite(heightPt) || !(heightPt > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(heightPt), heightPt, "The height must be a positive number.");
        }

        if (rotation is not (0 or 90 or 180 or 270))
        {
            throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "The rotation must be 0, 90, 180 or 270.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPx);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPx);
        if ((long)widthPx * heightPx > MaxPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPx), widthPx, $"A raster page has at most {MaxPixels} pixels.");
        }

        if (bgra.Length != widthPx * heightPx * 4)
        {
            throw new ArgumentException("The pixels must be width * height * 4 bytes.", nameof(bgra));
        }

        var page = Fpdf.FPDFPage_New(native, index, widthPt, heightPt);
        if (page == 0)
        {
            throw new InvalidOperationException("PDFium could not create the page.");
        }

        var done = false;
        try
        {
            AddImage(native, page, widthPt, heightPt, widthPx, heightPx, bgra);
            if (Fpdf.FPDFPage_GenerateContent(page) == 0)
            {
                throw new InvalidOperationException("PDFium could not write the content of the page.");
            }

            if (rotation != 0)
            {
                Fpdf.FPDFPage_SetRotation(page, rotation / 90);
            }

            done = true;
        }
        finally
        {
            Fpdf.FPDF_ClosePage(page);
            if (!done)
            {
                Fpdf.FPDFPage_Delete(native, index);
            }
        }
    }

    /// <summary>Counts the page objects of the page by kind, and its annotations.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page, or it has more than <see cref="PdfiumRedaction.MaxObjects"/> objects.</exception>
    public static PdfiumPageObjectSummary Summarize(PdfiumDocument document, int pageIndex)
    {
        using var page = PdfiumRedaction.OpenPage(document, pageIndex);

        // PDFium keeps the objects in a list: the count is cheap, reading object i takes i steps. Refuse a huge page before walking it.
        var count = page.CountObjects();
        if (count > PdfiumRedaction.MaxObjects)
        {
            throw new InvalidOperationException($"The page has {count} objects; at most {PdfiumRedaction.MaxObjects} are counted.");
        }

        int images = 0, paths = 0, texts = 0, others = 0;
        for (var i = 0; i < count; i++)
        {
            switch (Fpdf.FPDFPageObj_GetType(page.GetObject(i)))
            {
                case FpdfConstants.PageObjImage:
                    images++;
                    break;
                case FpdfConstants.PageObjPath:
                    paths++;
                    break;
                case FpdfConstants.PageObjText:
                    texts++;
                    break;
                default:
                    others++;
                    break;
            }
        }

        return new PdfiumPageObjectSummary(images, paths, texts, others, Fpdf.FPDFPage_GetAnnotCount(page.Access()));
    }

    private static void AddImage(nint nativeDocument, nint page, double widthPt, double heightPt, int widthPx, int heightPx, ReadOnlySpan<byte> bgra)
    {
        var image = Fpdf.FPDFPageObj_NewImageObj(nativeDocument);
        if (image == 0)
        {
            throw new InvalidOperationException("PDFium could not create the image.");
        }

        var inserted = false;
        try
        {
            fixed (byte* pixels = bgra)
            {
                // BGRx: the fourth byte is not alpha, so no soft mask is made. SetBitmap copies the pixels; the wrapper is gone right after.
                var bitmap = Fpdf.FPDFBitmap_CreateEx(widthPx, heightPx, FpdfConstants.BitmapBgrx, pixels, widthPx * 4);
                if (bitmap == 0)
                {
                    throw new InvalidOperationException("PDFium could not wrap the pixels.");
                }

                try
                {
                    if (Fpdf.FPDFImageObj_SetBitmap(null, 0, image, bitmap) == 0)
                    {
                        throw new InvalidOperationException("PDFium could not take the pixels.");
                    }
                }
                finally
                {
                    Fpdf.FPDFBitmap_Destroy(bitmap);
                }
            }

            var matrix = new FsMatrix { A = (float)widthPt, D = (float)heightPt };
            if (Fpdf.FPDFPageObj_SetMatrix(image, &matrix) == 0)
            {
                throw new InvalidOperationException("PDFium could not place the image.");
            }

            // The page takes ownership on success, and PDFium frees the object itself on failure: it is not destroyed again either way.
            inserted = true;
            if (Fpdf.FPDFPage_InsertObject(page, image) == 0)
            {
                throw new InvalidOperationException("PDFium could not add the image to the page.");
            }
        }
        finally
        {
            if (!inserted)
            {
                Fpdf.FPDFPageObj_Destroy(image);
            }
        }
    }
}
