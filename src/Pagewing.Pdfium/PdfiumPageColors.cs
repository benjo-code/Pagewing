namespace Pagewing.Pdfium;

/// <summary>
/// The colours of a recoloured page, as ARGB (<c>0xAARRGGBB</c>, PDFium's <c>FPDF_DWORD</c> colour format): the
/// bitmap is filled with <paramref name="Background"/>, filled paths take that colour too (and are drawn as outlines),
/// and text and path strokes are drawn in <paramref name="Foreground"/>. Images keep their colours.
/// </summary>
internal readonly record struct PdfiumPageColors(uint Background, uint Foreground);
