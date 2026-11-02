using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>What a page is made of, counted by kind (mirror of the core type; this assembly knows nothing of the core).</summary>
/// <param name="Images">The image objects.</param>
/// <param name="Paths">The path objects.</param>
/// <param name="Texts">The text objects.</param>
/// <param name="Others">The objects of any other kind.</param>
/// <param name="Annotations">The annotations.</param>
/// <param name="PathBounds">
/// The bounds of every path object in points from the top left of the displayed page, in the order of the objects; <see langword="null"/>
/// when the page has no path object, has more than <see cref="PdfiumRedactionRaster.MaxPathBounds"/>, or the displayed position could not be worked out.
/// </param>
internal readonly record struct PdfiumPageObjectSummary(int Images, int Paths, int Texts, int Others, int Annotations, IReadOnlyList<PdfiumRedactionArea>? PathBounds = null);

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

    /// <summary>The most path objects of a page whose bounds <see cref="Summarize"/> reads; the overlay text of a document has fewer (a test pins the relation).</summary>
    public const int MaxPathBounds = 20_000;

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
    /// <param name="overlay">The overlay text to draw as path objects above the image (decided with <see cref="PlanOverlay"/> for this page size), or <see langword="null"/>.</param>
    /// <param name="overlayColor">The colour of the overlay text.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index, a size or the rotation is out of range, or the image has more than <see cref="MaxPixels"/> pixels.</exception>
    /// <exception cref="ArgumentException">The pixels are not <paramref name="widthPx"/> * <paramref name="heightPx"/> * 4 bytes.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the page or the image.</exception>
    public static void AddPage(PdfiumDocument destination, int index, double widthPt, double heightPt, int rotation, int widthPx, int heightPx, ReadOnlySpan<byte> bgra, PdfiumOverlayPlan? overlay = null, PdfiumRedactionFill overlayColor = default)
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

        if (overlay is not null && rotation != 0)
        {
            throw new ArgumentException("Overlay text is placed on a page that is not turned.", nameof(overlay));
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

            // Above the image, inside the rectangles of the marks; the displayed page is the unturned media box (origin at the lower left).
            if (overlay is { Drawn: > 0 })
            {
                foreach (var _ in PdfiumRedactionOverlay.Draw(page, overlay, overlayColor, (0, heightPt), (1, 0), (0, -1)))
                {
                }
            }

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

    /// <summary>
    /// Decides the overlay text of the rectangles of a page that becomes an image: the same function as redacting a page in place
    /// (<see cref="PdfiumRedactionOverlay.Decide"/>), with no treated image (the background is the fill colour). Rectangles are clipped to the page.
    /// </summary>
    /// <param name="spec">The texts (one for each rectangle), the layout and the budget.</param>
    /// <param name="areas">The rectangles in points from the top left of the page.</param>
    /// <param name="widthPt">The width of the page.</param>
    /// <param name="heightPt">The height of the page.</param>
    public static PdfiumOverlayPlan PlanOverlay(PdfiumOverlaySpec spec, IReadOnlyList<PdfiumRedactionArea> areas, double widthPt, double heightPt)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(areas);
        var kept = new List<(int Source, PdfiumRedactionArea Rect)>();
        for (var i = 0; i < areas.Count; i++)
        {
            var area = areas[i];
            var left = Math.Max(area.Left, 0);
            var top = Math.Max(area.Top, 0);
            var right = Math.Min(area.Right, widthPt);
            var bottom = Math.Min(area.Bottom, heightPt);
            if (right > left && bottom > top)
            {
                kept.Add((i, new PdfiumRedactionArea(left, top, right, bottom)));
            }
        }

        return PdfiumRedactionOverlay.Decide(spec, kept, static _ => false);
    }

    /// <summary>
    /// Counts the annotations of the page without looking at its objects (a page with a hostile number of objects costs nothing more
    /// than loading it).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page.</exception>
    public static int CountAnnotations(PdfiumDocument document, int pageIndex)
    {
        using var page = PdfiumRedaction.OpenPage(document, pageIndex);
        return Fpdf.FPDFPage_GetAnnotCount(page.Access());
    }

    /// <summary>Counts the page objects of the page by kind, and its annotations.</summary>
    /// <param name="document">The document.</param>
    /// <param name="pageIndex">The 0-based page.</param>
    /// <param name="boundsUnreadable">A hook for tests: asked for the index of a path object whether its bounds count as unreadable. Production code passes <see langword="null"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not load the page, or it has more than <see cref="PdfiumRedaction.MaxObjects"/> objects.</exception>
    public static PdfiumPageObjectSummary Summarize(PdfiumDocument document, int pageIndex, Func<int, bool>? boundsUnreadable = null)
    {
        using var page = PdfiumRedaction.OpenPage(document, pageIndex);

        // PDFium keeps the objects in a list: the count is cheap, reading object i takes i steps. Refuse a huge page before walking it.
        var count = page.CountObjects();
        if (count > PdfiumRedaction.MaxObjects)
        {
            throw new InvalidOperationException($"The page has {count} objects; at most {PdfiumRedaction.MaxObjects} are counted.");
        }

        int images = 0, paths = 0, texts = 0, others = 0;
        var bounds = new List<UserRect>();
        var boundsFailed = false;
        for (var i = 0; i < count; i++)
        {
            var obj = page.GetObject(i);
            switch (Fpdf.FPDFPageObj_GetType(obj))
            {
                case FpdfConstants.PageObjImage:
                    images++;
                    break;
                case FpdfConstants.PageObjPath:
                    paths++;
                    if (paths <= MaxPathBounds)
                    {
                        // A bound PDFium cannot give is not (0, 0, 0, 0), which would lie inside any area at the corner of the page: the page fails.
                        if (boundsUnreadable?.Invoke(i) == true || !page.TryGetBounds(obj, out var left, out var bottom, out var right, out var top))
                        {
                            boundsFailed = true;
                        }
                        else
                        {
                            bounds.Add(new UserRect(left, bottom, right, top));
                        }
                    }

                    break;
                case FpdfConstants.PageObjText:
                    texts++;
                    break;
                default:
                    others++;
                    break;
            }
        }

        return new PdfiumPageObjectSummary(images, paths, texts, others, Fpdf.FPDFPage_GetAnnotCount(page.Access()), paths > 0 && paths <= MaxPathBounds && !boundsFailed ? DisplayedBounds(page.Access(), bounds) : null);
    }

    /// <summary>The bounds in points from the top left of the displayed page, or <see langword="null"/> when a position cannot be worked out (the check then fails: it never reads a position it does not have).</summary>
    private static List<PdfiumRedactionArea>? DisplayedBounds(nint page, List<UserRect> bounds)
    {
        double width = Fpdf.FPDF_GetPageWidthF(page);
        double height = Fpdf.FPDF_GetPageHeightF(page);
        if (!(width > 0) || !(height > 0) || !DisplayTransform.TryCreate(page, width, height, out var transform))
        {
            return null;
        }

        var result = new List<PdfiumRedactionArea>(bounds.Count);
        foreach (var box in bounds)
        {
            var a = transform.ToDisplay(box.Left, box.Bottom);
            var b = transform.ToDisplay(box.Right, box.Top);
            var left = Math.Min(a.X, b.X);
            var right = Math.Max(a.X, b.X);
            var top = Math.Min(a.Y, b.Y);
            var bottom = Math.Max(a.Y, b.Y);
            if (!double.IsFinite(left) || !double.IsFinite(right) || !double.IsFinite(top) || !double.IsFinite(bottom))
            {
                return null;
            }

            result.Add(new PdfiumRedactionArea(left, top, right, bottom));
        }

        return result;
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
