using System.Globalization;
using Pagewing.Pdfium;

namespace Pagewing.Core.Annotations;

/// <summary>
/// The font text boxes are drawn with: Geist Regular, which the application ships (<c>Pagewing.App/Assets/Fonts</c>) and
/// hands over with <see cref="Use"/>, so the file exists once. Nothing is loaded until the first text box is created or
/// changed; then its glyphs are read once per process on the PDFium thread and kept (<see cref="PdfiumGlyphs"/>).
/// </summary>
public static class TextBoxFont
{
    private static readonly object Gate = new();
    private static Func<byte[]>? source;
    private static PdfiumGlyphTable? table;

    /// <summary>Gets whether a source of the font is set (<see cref="Use"/>); without one no text box can be created or changed.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                return source is not null;
            }
        }
    }

    /// <summary>
    /// Sets where the font comes from, a cheap delegate that is called once, the first time a text box is created or changed.
    /// The first source set is kept; later calls change nothing (every caller hands over the same file).
    /// </summary>
    /// <param name="loadFontData">Returns the bytes of Geist Regular (a TrueType file).</param>
    /// <exception cref="ArgumentNullException"><paramref name="loadFontData"/> is <see langword="null"/>.</exception>
    public static void Use(Func<byte[]> loadFontData)
    {
        ArgumentNullException.ThrowIfNull(loadFontData);
        lock (Gate)
        {
            source ??= loadFontData;
        }
    }

    /// <summary>
    /// The glyphs of every character of <see cref="TextBoxCharacters"/>, read on the first call. Runs on the PDFium thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">No source is set, or the font lacks a glyph of the supported set.</exception>
    internal static PdfiumGlyphTable Table(PdfiumThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        thread.VerifyAccess();
        Func<byte[]> load;
        lock (Gate)
        {
            if (table is not null)
            {
                return table;
            }

            load = source ?? throw new InvalidOperationException("The font of the text boxes is not set (TextBoxFont.Use).");
        }

        var read = PdfiumGlyphs.Read(thread, load(), TextBoxCharacters.All);
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
