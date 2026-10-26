using System.Globalization;
using System.Text;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>A glyph of the text box font: its advance and its outline as content-stream path operators.</summary>
/// <param name="Advance">The advance width in em.</param>
/// <param name="Outline">
/// The outline in thousandths of an em with y up from the baseline, as <c>m</c>, <c>l</c>, <c>c</c> and <c>h</c> operators
/// (integers), ending with <c>f</c>; empty for a glyph that draws nothing (a space).
/// </param>
internal sealed record PdfiumGlyph(double Advance, string Outline);

/// <summary>The glyphs of the text box font that <see cref="PdfiumGlyphs.Read"/> took out of it, with its vertical metrics.</summary>
internal sealed class PdfiumGlyphTable(double ascent, double descent, IReadOnlyDictionary<char, PdfiumGlyph> glyphs)
{
    /// <summary>Gets how far the font reaches above the baseline, in em.</summary>
    public double Ascent { get; } = ascent;

    /// <summary>Gets how far the font reaches below the baseline, in em (positive).</summary>
    public double Descent { get; } = descent;

    /// <summary>Gets the number of glyphs.</summary>
    public int Count => glyphs.Count;

    /// <summary>Gets the glyph of a character, if the table has it.</summary>
    public bool TryGet(char character, out PdfiumGlyph glyph) => glyphs.TryGetValue(character, out glyph!);
}

/// <summary>
/// Takes the outlines and advances of a font's glyphs out of PDFium (<c>FPDFFont_GetGlyphPath</c>,
/// <c>FPDFFont_GetGlyphWidth</c>, experimental API), so that a text box can be drawn as filled paths without any font in
/// the file (variant b2 of Spike B, <c>Docs/DECISIONS.md</c>, entry "Textfeld: Erscheinungsbild und Entscheidung (M10)").
/// The font is loaded as a CID font (with a simple TrueType load PDFium finds no glyph beyond ASCII for most letters) into a
/// new document that is closed again at once and never saved. Runs on the PDFium thread.
/// </summary>
internal static unsafe class PdfiumGlyphs
{
    /// <summary>The most segments of one glyph outline that are read; a glyph with more is left out of the table.</summary>
    public const int MaxSegmentsPerGlyph = 4_096;

    /// <summary>The largest font file read, in bytes.</summary>
    public const int MaxFontBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Reads the glyphs of <paramref name="characters"/> from a TrueType font. A character whose outline PDFium cannot read,
    /// or whose outline is damaged (a curve without its three points, a coordinate that is not finite or beyond 100 em, more
    /// than <see cref="MaxSegmentsPerGlyph"/> segments), is left out. A blank glyph (no segment) gets an empty outline.
    /// </summary>
    /// <exception cref="ArgumentException">The font is empty or larger than <see cref="MaxFontBytes"/>.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the helper document or load the font.</exception>
    public static PdfiumGlyphTable Read(PdfiumThread thread, ReadOnlySpan<byte> fontData, IEnumerable<char> characters)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(characters);
        thread.VerifyAccess();
        if (fontData.IsEmpty || fontData.Length > MaxFontBytes)
        {
            throw new ArgumentException("The font is empty or too large.", nameof(fontData));
        }

        var document = Fpdf.FPDF_CreateNewDocument();
        if (document == 0)
        {
            throw new InvalidOperationException("PDFium could not create the document to load the font into.");
        }

        try
        {
            nint font;
            fixed (byte* data = fontData)
            {
                font = Fpdf.FPDFText_LoadFont(document, data, (uint)fontData.Length, FpdfConstants.FontTrueType, cid: 1);
            }

            if (font == 0)
            {
                throw new InvalidOperationException("PDFium could not load the font.");
            }

            try
            {
                float ascent = 0, descent = 0;
                if (Fpdf.FPDFFont_GetAscent(font, 1, &ascent) == 0 || Fpdf.FPDFFont_GetDescent(font, 1, &descent) == 0
                    || !float.IsFinite(ascent) || !float.IsFinite(descent) || ascent <= 0)
                {
                    throw new InvalidOperationException("PDFium could not read the metrics of the font.");
                }

                var glyphs = new Dictionary<char, PdfiumGlyph>();
                foreach (var character in characters)
                {
                    if (!glyphs.ContainsKey(character) && ReadGlyph(font, character) is { } glyph)
                    {
                        glyphs[character] = glyph;
                    }
                }

                return new PdfiumGlyphTable(ascent, Math.Abs(descent), glyphs);
            }
            finally
            {
                Fpdf.FPDFFont_Close(font);
            }
        }
        finally
        {
            Fpdf.FPDF_CloseDocument(document);
        }
    }

    private static PdfiumGlyph? ReadGlyph(nint font, char character)
    {
        float width;
        if (Fpdf.FPDFFont_GetGlyphWidth(font, character, 1, &width) == 0 || !float.IsFinite(width) || width < 0 || width > 100)
        {
            return null;
        }

        var path = Fpdf.FPDFFont_GetGlyphPath(font, character, 1);
        if (path == 0)
        {
            // PDFium has no path for a glyph without contours: a space draws nothing, anything else is missing.
            return char.IsWhiteSpace(character) ? new PdfiumGlyph(width, string.Empty) : null;
        }

        var count = Fpdf.FPDFGlyphPath_CountGlyphSegments(path);
        if (count < 0 || count > MaxSegmentsPerGlyph)
        {
            return null;
        }

        if (count == 0)
        {
            return new PdfiumGlyph(width, string.Empty);
        }

        var outline = new StringBuilder();
        var curve = 0;
        for (var i = 0; i < count; i++)
        {
            var segment = Fpdf.FPDFGlyphPath_GetGlyphPathSegment(path, i);
            float x, y;
            if (segment == 0 || Fpdf.FPDFPathSegment_GetPoint(segment, &x, &y) == 0 || !IsSane(x) || !IsSane(y))
            {
                return null;
            }

            var type = Fpdf.FPDFPathSegment_GetType(segment);
            if (curve > 0 && type != FpdfConstants.SegmentBezierTo)
            {
                // A curve needs two control points and its end.
                return null;
            }

            Append(outline, x);
            Append(outline, y);
            switch (type)
            {
                case FpdfConstants.SegmentMoveTo:
                    outline.Append("m ");
                    break;
                case FpdfConstants.SegmentLineTo:
                    outline.Append("l ");
                    break;
                case FpdfConstants.SegmentBezierTo:
                    curve = (curve + 1) % 3;
                    if (curve == 0)
                    {
                        outline.Append("c ");
                    }

                    break;
                default:
                    return null;
            }

            if (curve == 0 && Fpdf.FPDFPathSegment_GetClose(segment) != 0)
            {
                outline.Append("h ");
            }
        }

        if (curve != 0)
        {
            return null;
        }

        return new PdfiumGlyph(width, outline.Append('f').ToString());
    }

    /// <summary>A coordinate in thousandths of an em, rounded to an integer, and a space.</summary>
    private static void Append(StringBuilder builder, float value) =>
        builder.Append(((int)Math.Round(value * 1000, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture)).Append(' ');

    private static bool IsSane(float value) => float.IsFinite(value) && Math.Abs(value) <= 100;
}
