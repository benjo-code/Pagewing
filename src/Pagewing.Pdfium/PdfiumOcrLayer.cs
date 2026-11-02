using System.Runtime.InteropServices;
using System.Text;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>A recognized word in points from the top left of the displayed page.</summary>
internal readonly record struct PdfiumOcrWord(string Text, double Left, double Top, double Right, double Bottom, float Confidence);

/// <summary>What a page is made of, as far as the decision to recognize it needs (see <c>PageOcrInfo</c> in Core).</summary>
internal readonly record struct PdfiumOcrInfo(int TextCharacters, bool HasOwnLayer, double? ImageDpi, double ImageCoverage);

/// <summary>
/// The invisible text layer of an OCR scan and what the decision to write it needs (milestone 15, <c>Docs/DECISIONS.md</c>, entry "OCR: Textebene,
/// Spike"). A word is one text object in render mode 3 (invisible), set in Geist Regular loaded as a CID font so that PDFium writes the Unicode
/// map, fitted to the word box by the text matrix and placed with <see cref="DisplayTransform"/> (no rotation or crop box maths of our own), inside
/// the marked content <c>/PagewingOcr</c> that makes it ours, at the end of the page content. Everything runs on the PDFium thread.
/// </summary>
internal static unsafe class PdfiumOcrLayer
{
    /// <summary>The marked content tag of every text object of the layer.</summary>
    public const string Mark = "PagewingOcr";

    /// <summary>The most page objects looked at when a page is read (a page with more is not made of images and text in any sense that matters).</summary>
    public const int MaxObjectsScanned = 200_000;

    /// <summary>The most characters of text counted on a page; more counts as "has text".</summary>
    public const int MaxTextCharacters = 2_000_000;

    /// <summary>The most words one page takes.</summary>
    public const int MaxWordsPerPage = 100_000;

    /// <summary>The baseline lies this share of the box height above its bottom (the writer and the reader agree on it).</summary>
    public const double BaselineShare = 0.2;

    /// <summary>Counts the text of a page (without whitespace and without our own layer), tells whether our layer is there, and reads the largest image.</summary>
    public static PdfiumOcrInfo ReadInfo(PdfiumDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var page = document.LoadPage(pageIndex);
        using var text = page.LoadTextPage();
        var handle = page.Access();
        var textPage = text.Access();

        var total = text.CharCount > MaxTextCharacters ? int.MaxValue : CountVisible(text.GetText());
        var own = 0L;
        var hasOwn = false;
        var bestArea = 0.0;
        double? dpi = null;
        var count = Math.Min(Fpdf.FPDFPage_CountObjects(handle), MaxObjectsScanned);
        for (var i = 0; i < count; i++)
        {
            var item = Fpdf.FPDFPage_GetObject(handle, i);
            if (item == 0)
            {
                continue;
            }

            var type = Fpdf.FPDFPageObj_GetType(item);
            if (type == FpdfConstants.PageObjText)
            {
                if (IsOwn(item))
                {
                    hasOwn = true;
                    own += CountVisible(ObjectText(item, textPage));
                }
            }
            else if (type == FpdfConstants.PageObjImage)
            {
                float left, bottom, right, top;
                FpdfImageObjMetadata metadata;
                if (Fpdf.FPDFPageObj_GetBounds(item, &left, &bottom, &right, &top) == 0
                    || Fpdf.FPDFImageObj_GetImageMetadata(item, handle, &metadata) == 0)
                {
                    continue;
                }

                var area = Math.Max(0.0, (right - left) * (double)(top - bottom));
                if (area > bestArea)
                {
                    bestArea = area;
                    dpi = double.IsFinite(metadata.HorizontalDpi) && metadata.HorizontalDpi > 0 ? metadata.HorizontalDpi : null;
                }
            }
        }

        var pageArea = page.Width * page.Height;
        var coverage = pageArea > 0 ? Math.Min(1.0, bestArea / pageArea) : 0;
        var characters = total == int.MaxValue ? int.MaxValue : (int)Math.Max(0, Math.Min(int.MaxValue, total - own));
        return new PdfiumOcrInfo(characters, hasOwn, dpi, coverage);
    }

    /// <summary>
    /// Writes the words of a page as invisible text. A word with a character <paramref name="isWritable"/> refuses (one the font lacks) is left
    /// out and counted. The font is loaded once for the document. If anything fails after the first object was inserted, the objects already
    /// inserted are taken out again, so the page keeps no half layer.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="pageIndex">The 0-based page.</param>
    /// <param name="words">The words, each in points from the top left of the displayed page.</param>
    /// <param name="loadFont">Gives the TrueType file of the font; called only when the document has no font for the layer yet.</param>
    /// <param name="isWritable">Whether a word's text can be written with the font.</param>
    /// <returns>How many words were written and how many were left out.</returns>
    /// <exception cref="ArgumentException">A box is not finite or has no area, a text is empty, or there are more than <see cref="MaxWordsPerPage"/> words.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused to load the font, to create or place an object or to write the content.</exception>
    public static (int Written, int Skipped) Add(PdfiumDocument document, int pageIndex, IReadOnlyList<PdfiumOcrWord> words, Func<byte[]> loadFont, Func<string, bool> isWritable)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(isWritable);
        ArgumentNullException.ThrowIfNull(loadFont);
        if (words.Count > MaxWordsPerPage)
        {
            throw new ArgumentException($"A page takes at most {MaxWordsPerPage:N0} words.", nameof(words));
        }

        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word.Text) || !double.IsFinite(word.Left) || !double.IsFinite(word.Top) || !double.IsFinite(word.Right) || !double.IsFinite(word.Bottom)
                || word.Right <= word.Left || word.Bottom <= word.Top)
            {
                throw new ArgumentException("Every word needs a text and a box with an area.", nameof(words));
            }
        }

        if (words.Count == 0)
        {
            return (0, 0);
        }

        var nativeDocument = document.Access();
        using var page = document.LoadPage(pageIndex);
        var handle = page.Access();
        if (!DisplayTransform.TryCreate(handle, page.Width, page.Height, out var transform))
        {
            throw new InvalidOperationException("PDFium could not convert the coordinates of the page.");
        }

        var font = FontOf(document, loadFont);
        var inserted = new List<nint>();
        var written = 0;
        var skipped = 0;
        try
        {
            foreach (var word in words)
            {
                if (!isWritable(word.Text))
                {
                    skipped++;
                    continue;
                }

                var item = Place(nativeDocument, handle, font, transform, word);
                if (item == 0)
                {
                    skipped++;
                    continue;
                }

                inserted.Add(item);
                written++;
            }

            if (inserted.Count > 0 && Fpdf.FPDFPage_GenerateContent(handle) == 0)
            {
                throw new InvalidOperationException("PDFium could not write the content of the page.");
            }
        }
        catch
        {
            foreach (var item in inserted)
            {
                if (Fpdf.FPDFPage_RemoveObject(handle, item) != 0)
                {
                    Fpdf.FPDFPageObj_Destroy(item);
                }
            }

            throw;
        }

        return (written, skipped);
    }

    /// <summary>Takes our text objects out of the page and writes its content. Other invisible text is not touched.</summary>
    /// <returns>How many objects were removed.</returns>
    public static int Remove(PdfiumDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var page = document.LoadPage(pageIndex);
        var handle = page.Access();
        var removed = 0;
        for (var i = Math.Min(Fpdf.FPDFPage_CountObjects(handle), MaxObjectsScanned) - 1; i >= 0; i--)
        {
            var item = Fpdf.FPDFPage_GetObject(handle, i);
            if (item == 0 || Fpdf.FPDFPageObj_GetType(item) != FpdfConstants.PageObjText || !IsOwn(item))
            {
                continue;
            }

            if (Fpdf.FPDFPage_RemoveObject(handle, item) == 0)
            {
                throw new InvalidOperationException("PDFium could not take a text object out of the page.");
            }

            Fpdf.FPDFPageObj_Destroy(item);
            removed++;
        }

        if (removed > 0 && Fpdf.FPDFPage_GenerateContent(handle) == 0)
        {
            throw new InvalidOperationException("PDFium could not write the content of the page.");
        }

        return removed;
    }

    /// <summary>
    /// Reads our layer back: the text of each object and its box in points from the top left of the displayed page, in the order of the page.
    /// The box comes from the text matrix and the font size the writer set (the baseline and the height) and the bounds of the object (left and
    /// right), so a layer that is written, read and written again does not drift. The confidence is not stored in the file and reads as 0.
    /// </summary>
    public static IReadOnlyList<PdfiumOcrWord> Read(PdfiumDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var page = document.LoadPage(pageIndex);
        using var text = page.LoadTextPage();
        var handle = page.Access();
        var textPage = text.Access();
        if (!DisplayTransform.TryCreate(handle, page.Width, page.Height, out var transform))
        {
            throw new InvalidOperationException("PDFium could not convert the coordinates of the page.");
        }

        var words = new List<PdfiumOcrWord>();
        var count = Math.Min(Fpdf.FPDFPage_CountObjects(handle), MaxObjectsScanned);
        for (var i = 0; i < count; i++)
        {
            var item = Fpdf.FPDFPage_GetObject(handle, i);
            if (item == 0 || Fpdf.FPDFPageObj_GetType(item) != FpdfConstants.PageObjText || !IsOwn(item))
            {
                continue;
            }

            float left, bottom, right, top, size;
            FsMatrix matrix;
            if (Fpdf.FPDFPageObj_GetBounds(item, &left, &bottom, &right, &top) == 0
                || Fpdf.FPDFPageObj_GetMatrix(item, &matrix) == 0
                || Fpdf.FPDFTextObj_GetFontSize(item, &size) == 0
                || !float.IsFinite(size) || size <= 0)
            {
                continue;
            }

            var corners = new[] { transform.ToDisplay(left, bottom), transform.ToDisplay(right, bottom), transform.ToDisplay(right, top), transform.ToDisplay(left, top) };
            var baseline = transform.ToDisplay(matrix.E, matrix.F).Y;
            var boxBottom = baseline + (BaselineShare * size);
            words.Add(new PdfiumOcrWord(ObjectText(item, textPage).Trim(), corners.Min(static c => c.X), boxBottom - size, corners.Max(static c => c.X), boxBottom, 0));
        }

        return words;
    }

    private static nint FontOf(PdfiumDocument document, Func<byte[]> loadFont)
    {
        var owner = document.Handle;
        if (owner.OcrFont != 0)
        {
            return owner.OcrFont;
        }

        var fontData = loadFont();
        if (fontData.Length == 0)
        {
            throw new InvalidOperationException("The font of the layer is empty.");
        }

        nint font;
        fixed (byte* data = fontData)
        {
            // A CID font: PDFium writes the Unicode map (ToUnicode) and embeds the font file once.
            font = Fpdf.FPDFText_LoadFont(document.Access(), data, (uint)fontData.Length, FpdfConstants.FontTrueType, cid: 1);
        }

        if (font == 0)
        {
            throw new InvalidOperationException("PDFium could not load the font of the layer.");
        }

        owner.OcrFont = font;
        return font;
    }

    // Creates the text object of a word and inserts it; 0 when the font cannot show the text (nothing is left behind then).
    private static nint Place(nint document, nint page, nint font, DisplayTransform transform, PdfiumOcrWord word)
    {
        var height = word.Bottom - word.Top;
        var item = Fpdf.FPDFPageObj_CreateTextObj(document, font, (float)height);
        if (item == 0)
        {
            throw new InvalidOperationException("PDFium could not create a text object.");
        }

        var utf16 = new ushort[word.Text.Length + 1];
        for (var i = 0; i < word.Text.Length; i++)
        {
            utf16[i] = word.Text[i];
        }

        float left, bottom, right, top;
        fixed (ushort* characters = utf16)
        {
            if (Fpdf.FPDFText_SetText(item, characters) == 0
                || Fpdf.FPDFTextObj_SetTextRenderMode(item, FpdfConstants.TextRenderModeInvisible) == 0
                || Fpdf.FPDFPageObj_GetBounds(item, &left, &bottom, &right, &top) == 0)
            {
                Fpdf.FPDFPageObj_Destroy(item);
                throw new InvalidOperationException("PDFium could not set the text of a text object.");
            }
        }

        if (!float.IsFinite(left) || !float.IsFinite(right) || right - left <= 0)
        {
            // A text without any ink (the font has no outline for it): nothing to select.
            Fpdf.FPDFPageObj_Destroy(item);
            return 0;
        }

        // Stretch the word to the box and stand it on the baseline; the matrix is the page's own direction (DisplayTransform), turned the right way up.
        var scale = (word.Right - word.Left) / (right - left);
        var origin = transform.ToUser(word.Left - (left * scale), word.Bottom - (height * BaselineShare));
        var along = transform.AlongX;
        var down = transform.AlongY;
        var matrix = new FsMatrix
        {
            A = (float)(along.X * scale),
            B = (float)(along.Y * scale),
            C = (float)-down.X,
            D = (float)-down.Y,
            E = (float)origin.X,
            F = (float)origin.Y,
        };
        var tag = Encoding.ASCII.GetBytes(Mark + "\0");
        fixed (byte* name = tag)
        {
            if (Fpdf.FPDFPageObj_SetMatrix(item, &matrix) == 0 || Fpdf.FPDFPageObj_AddMark(item, name) == 0)
            {
                Fpdf.FPDFPageObj_Destroy(item);
                throw new InvalidOperationException("PDFium could not place a text object.");
            }
        }

        // FPDFPage_InsertObject frees the object itself when it fails (fpdf_edit.h): no Destroy after a failed insert.
        if (Fpdf.FPDFPage_InsertObject(page, item) == 0)
        {
            throw new InvalidOperationException("PDFium could not insert a text object.");
        }

        return item;
    }

    private static bool IsOwn(nint item)
    {
        // Our layer is invisible text: visible text that happens to carry the mark name is somebody else's and is never read as, or removed with, the layer.
        if (Fpdf.FPDFTextObj_GetTextRenderMode(item) != FpdfConstants.TextRenderModeInvisible)
        {
            return false;
        }

        var marks = Fpdf.FPDFPageObj_CountMarks(item);
        for (var m = 0; m < marks; m++)
        {
            var mark = Fpdf.FPDFPageObj_GetMark(item, new CULong((uint)m));
            CULong length;
            if (mark == 0 || Fpdf.FPDFPageObjMark_GetName(mark, null, new CULong(0), &length) == 0 || length.Value is < 2 or > 64)
            {
                continue;
            }

            var buffer = new ushort[(int)length.Value / 2];
            fixed (ushort* name = buffer)
            {
                if (Fpdf.FPDFPageObjMark_GetName(mark, name, length, &length) != 0
                    && new string((char*)name, 0, (int)length.Value / 2).TrimEnd('\0') == Mark)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ObjectText(nint item, nint textPage)
    {
        var bytes = Fpdf.FPDFTextObj_GetText(item, textPage, null, new CULong(0)).Value;
        if (bytes < 2 || bytes > 4 * 1024 * 1024)
        {
            return string.Empty;
        }

        var buffer = new ushort[(int)bytes / 2];
        fixed (ushort* characters = buffer)
        {
            var written = Fpdf.FPDFTextObj_GetText(item, textPage, characters, new CULong((uint)bytes)).Value;
            return new string((char*)characters, 0, (int)Math.Min(written, bytes) / 2).TrimEnd('\0');
        }
    }

    private static int CountVisible(string text)
    {
        var count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c))
            {
                count++;
            }
        }

        return count;
    }
}
