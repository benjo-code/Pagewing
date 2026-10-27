using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// The highlight of the fillable form fields: <see cref="Rgb"/> as <c>0x00RRGGBB</c> and its opacity.
/// </summary>
/// <param name="Rgb">The colour, <c>0x00RRGGBB</c>.</param>
/// <param name="Alpha">The opacity, 0 to 255.</param>
internal readonly record struct PdfiumFormHighlight(uint Rgb, byte Alpha)
{
    /// <summary>Gets the colour as PDFium takes it: <c>0x00BBGGRR</c> (measured in the spike; the header says otherwise).</summary>
    public uint Bgr => ((Rgb & 0xFF) << 16) | (Rgb & 0xFF00) | ((Rgb >> 16) & 0xFF);
}

/// <summary>
/// Draws the form fields of a page onto a bitmap the page was just rendered on. <c>FPDF_RenderPageBitmap*</c> skips every
/// widget, also with <c>FPDF_ANNOT</c>; only <c>FPDF_FFLDraw</c> draws them, with the same area and flags as the render (tiles
/// included, spike: Docs/DECISIONS.md, "Formulare: Formular-Engine …", point 3). Every member runs on the PDFium thread.
/// </summary>
internal static unsafe class PdfiumFormDrawing
{
    /// <summary>
    /// Whether the page has a widget annotation. Scans at most <see cref="PdfiumAnnotations.MaxScannedPerPage"/> entries of
    /// <c>/Annots</c>; a page with more entries and no widget among them counts as having widgets, so nothing is ever left
    /// undrawn (<c>FPDF_FFLDraw</c> finds the widgets itself).
    /// </summary>
    public static bool HasWidgets(PdfiumPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var total = Fpdf.FPDFPage_GetAnnotCount(native);
        var count = Math.Clamp(total, 0, PdfiumAnnotations.MaxScannedPerPage);
        for (var index = 0; index < count; index++)
        {
            var annotation = Fpdf.FPDFPage_GetAnnot(native, index);
            if (annotation == 0)
            {
                continue;
            }

            try
            {
                if (Fpdf.FPDFAnnot_GetSubtype(annotation) == FpdfConstants.AnnotWidget)
                {
                    return true;
                }
            }
            finally
            {
                Fpdf.FPDFPage_CloseAnnot(annotation);
            }
        }

        return total > count;
    }

    /// <summary>
    /// Reads the rectangles (PDF page space) of the widgets that are shown on screen: not hidden, not <c>NoView</c>, with a
    /// usable <c>/Rect</c>; at most <see cref="PdfiumForms.MaxFieldsPerPage"/> widgets within the first
    /// <see cref="PdfiumAnnotations.MaxScannedPerPage"/> entries.
    /// </summary>
    public static FsRectF[] ReadVisibleWidgetRects(PdfiumPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var rects = new List<FsRectF>();
        PdfiumForms.ForEachWidget(page.Access(), (_, annotation) =>
        {
            if ((Fpdf.FPDFAnnot_GetFlags(annotation) & (FpdfConstants.AnnotFlagHidden | FpdfConstants.AnnotFlagNoView)) == 0
                && PdfiumForms.HasUsableRect(annotation, out var rect))
            {
                rects.Add(rect);
            }
        });

        return [.. rects];
    }

    /// <summary>
    /// Fills <paramref name="rects"/> (page space) with opaque white on a BGRX bitmap the page was rendered on with the given
    /// mapping, clipped to the bitmap. On the dark page a field without its own background (<c>/MK /BG</c>) is transparent and
    /// its black text would stand on the dark ground; filled, the field keeps the light look the document gives it.
    /// </summary>
    public static void FillWhite(
        PdfiumPage page,
        ReadOnlySpan<FsRectF> rects,
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        int startX,
        int startY,
        int sizeX,
        int sizeY)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var rows = MemoryMarshal.Cast<byte, uint>(pixels);
        var rowLength = stride / sizeof(uint);
        foreach (var rect in rects)
        {
            int x1, y1, x2, y2;
            if (Fpdf.FPDF_PageToDevice(native, startX, startY, sizeX, sizeY, 0, rect.Left, rect.Top, &x1, &y1) == 0
                || Fpdf.FPDF_PageToDevice(native, startX, startY, sizeX, sizeY, 0, rect.Right, rect.Bottom, &x2, &y2) == 0)
            {
                continue;
            }

            var left = Math.Clamp(Math.Min(x1, x2), 0, width);
            var right = Math.Clamp(Math.Max(x1, x2), 0, width);
            var top = Math.Clamp(Math.Min(y1, y2), 0, height);
            var bottom = Math.Clamp(Math.Max(y1, y2), 0, height);
            for (var y = top; y < bottom; y++)
            {
                rows.Slice((y * rowLength) + left, right - left).Fill(0xFFFFFFFF);
            }
        }
    }

    /// <summary>
    /// Sets or removes the highlight (set on every call: the environment belongs to the document, and another render may have
    /// left its own) and draws the widgets with <c>FPDF_FFLDraw</c>, rotation 0 as every render of Pagewing.
    /// </summary>
    public static void Draw(
        PdfiumFormEnvironment environment,
        nint bitmap,
        PdfiumPage page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int flags,
        PdfiumFormHighlight? highlight)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(page);
        var form = environment.Handle;
        if (highlight is { } tint)
        {
            Fpdf.FPDF_SetFormFieldHighlightColor(form, FpdfConstants.FormFieldUnknown, new CULong(tint.Bgr));
            Fpdf.FPDF_SetFormFieldHighlightAlpha(form, tint.Alpha);
        }
        else
        {
            Fpdf.FPDF_RemoveFormFieldHighlight(form);
        }

        Fpdf.FPDF_FFLDraw(form, bitmap, page.Access(), startX, startY, sizeX, sizeY, 0, flags);
    }
}
