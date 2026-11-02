using System.Globalization;
using Pagewing.Pdfium;

namespace Pagewing.Core.Annotations;

/// <summary>
/// The font text boxes and the overlay text of a redaction are drawn with: Geist Regular, embedded once in this assembly
/// (<c>Assets/Fonts/Geist-Regular.ttf</c>; the app links the same file for its own text), so the application and the command line
/// both have it without any help. Nothing is read until the first text box or overlay text is laid out; then its glyphs are read
/// once per process on the PDFium thread and kept (<see cref="PdfiumGlyphs"/>).
/// </summary>
public static class TextBoxFont
{
    /// <summary>The name of the embedded font file.</summary>
    private const string ResourceName = "Pagewing.Core.Assets.Fonts.Geist-Regular.ttf";

    private static readonly object Gate = new();
    private static PdfiumGlyphTable? table;

    /// <summary>Gets whether the font is embedded, which it always is in a whole build; the command line needs nothing handed over.</summary>
    public static bool IsAvailable => typeof(TextBoxFont).Assembly.GetManifestResourceInfo(ResourceName) is not null;

    /// <summary>Reads the bytes of Geist Regular (a TrueType file) from the assembly.</summary>
    /// <returns>The bytes; a new array on every call.</returns>
    /// <exception cref="InvalidOperationException">The font is not embedded (a broken build).</exception>
    internal static byte[] ReadData()
    {
        using var stream = typeof(TextBoxFont).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The font of the text boxes is not embedded in Pagewing.Core.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// The glyphs of every character of <see cref="TextBoxCharacters"/>, read on the first call. Runs on the PDFium thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The font is not embedded, or lacks a glyph of the supported set.</exception>
    internal static PdfiumGlyphTable Table(PdfiumThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        thread.VerifyAccess();
        lock (Gate)
        {
            if (table is not null)
            {
                return table;
            }
        }

        var read = PdfiumGlyphs.Read(thread, ReadData(), TextBoxCharacters.All);
        var missing = TextBoxCharacters.All.Where(c => !read.TryGet(c, out _)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture, $"The font of the text boxes has no glyph for {string.Join(", ", missing.Take(8).Select(c => $"U+{(int)c:X4}"))}."));
        }

        lock (Gate)
        {
            return table ??= read;
        }
    }

    /// <summary>The widths of <paramref name="glyphs"/> for <see cref="TextBoxLayout"/>.</summary>
    internal static IGlyphMetrics MetricsOf(PdfiumGlyphTable glyphs) => new TableMetrics(glyphs);

    private sealed class TableMetrics(PdfiumGlyphTable glyphs) : IGlyphMetrics
    {
        public double Ascent => glyphs.Ascent;

        public double Descent => glyphs.Descent;

        public double AdvanceOf(char character) => glyphs.TryGet(character, out var glyph) ? glyph.Advance : 0;
    }
}
