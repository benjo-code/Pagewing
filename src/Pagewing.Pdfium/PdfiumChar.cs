namespace Pagewing.Pdfium;

/// <summary>
/// A character of a page as PDFium reports it, at PDFium's character index. Geometry is in page points with the
/// origin at the top left of the displayed page (after rotation and CropBox), so <see cref="Top"/> is less than
/// <see cref="Bottom"/>.
/// </summary>
/// <param name="CodePoint">
/// The Unicode scalar value; U+FFFD if PDFium has none. PDFium replaces a line-end hyphen it recognizes with a
/// marker (U+0002 from <c>FPDFText_GetUnicode</c>, U+FFFE in <c>FPDFText_GetText</c>) and inserts no line break
/// after it; such a character is reported as U+002D with <paramref name="Hyphen"/> set.
/// </param>
/// <param name="Left">Left edge in points; 0 when geometry was not read or PDFium has no box.</param>
/// <param name="Top">Top edge in points from the top of the page; 0 when geometry was not read.</param>
/// <param name="Right">Right edge in points; 0 when geometry was not read.</param>
/// <param name="Bottom">Bottom edge in points from the top of the page; 0 when geometry was not read.</param>
/// <param name="Generated">
/// Inserted by PDFium (spaces and line breaks, a line break is a CR LF pair), not drawn by the page. The box of a
/// line break has no area and sits at the end of the line before it.
/// </param>
/// <param name="Hyphen">
/// PDFium recognized the character as a hyphen at the end of a line. The next character is the first one of the
/// next line; no generated line break lies between them.
/// </param>
/// <param name="FontSize">The font size in points (PDFium reports 1 for generated characters); 0 when style was not read.</param>
/// <param name="Bold">The font is bold (weight, ForceBold flag or font name); false when style was not read.</param>
/// <param name="Italic">The font is italic (Italic flag or font name); false when style was not read.</param>
internal readonly record struct PdfiumChar(
    int CodePoint,
    double Left,
    double Top,
    double Right,
    double Bottom,
    bool Generated,
    bool Hyphen,
    float FontSize,
    bool Bold,
    bool Italic);
