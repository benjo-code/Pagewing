using Pagewing.Pdfium.Native;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>The characters of one text object as the text page reports them (UTF-16 units, origins in page space).</summary>
internal sealed record ObjectChars(uint[] Unicode, double[] X, double[] Y);

/// <summary>The prototype of the replacement of the spike of milestone 16: the text object is changed in place, the spacing is kept with <c>FPDFText_SetPositions</c>. Test code only.</summary>
internal static unsafe class SpikeEditor
{
    public static ObjectChars CharsOf(nint page, nint item)
    {
        var textPage = Fpdf.FPDFText_LoadPage(page);
        try
        {
            var count = Fpdf.FPDFText_CountChars(textPage);
            var unicode = new List<uint>();
            var xs = new List<double>();
            var ys = new List<double>();
            for (var i = 0; i < count; i++)
            {
                if (Fpdf.FPDFText_IsGenerated(textPage, i) != 0 || Fpdf.FPDFText_GetTextObject(textPage, i) != item)
                {
                    continue;
                }

                double x = 0, y = 0;
                _ = Fpdf.FPDFText_GetCharOrigin(textPage, i, &x, &y);
                unicode.Add(Fpdf.FPDFText_GetUnicode(textPage, i));
                xs.Add(x);
                ys.Add(y);
            }

            return new ObjectChars([.. unicode], [.. xs], [.. ys]);
        }
        finally
        {
            Fpdf.FPDFText_ClosePage(textPage);
        }
    }

    private static double ToText(double vx, double vy, FsMatrix m) => ((vx * m.D) - (vy * m.C)) / (((double)m.A * m.D) - ((double)m.B * m.C));

    /// <summary>The text-space offsets of the characters of a text from the first one when PDFium lays it out naturally (no kerning, no spacing).</summary>
    public static double[] Natural(nint document, nint page, nint font, float size, FsMatrix matrix, string text)
    {
        var probe = Fpdf.FPDFPageObj_CreateTextObj(document, font, size);
        _ = TextEditProbe.SetText(probe, text);
        var m = matrix;
        m.E += 20000f;
        m.F += 20000f;
        _ = Fpdf.FPDFPageObj_SetMatrix(probe, &m);
        _ = Fpdf.FPDFPage_InsertObject(page, probe);
        try
        {
            var chars = CharsOf(page, probe);
            return [.. chars.X.Select((x, i) => ToText(x - chars.X[0], chars.Y[i] - chars.Y[0], matrix))];
        }
        finally
        {
            _ = Fpdf.FPDFPage_RemoveObject(page, probe);
            Fpdf.FPDFPageObj_Destroy(probe);
        }
    }

    /// <summary>Replaces the text of the text object in place and keeps the spacing of the characters that stay (prefix and suffix).</summary>
    public static void Replace(nint document, nint page, nint item, string newText)
    {
        var font = Fpdf.FPDFTextObj_GetFont(item);
        float size = 0;
        _ = Fpdf.FPDFTextObj_GetFontSize(item, &size);
        FsMatrix matrix = default;
        _ = Fpdf.FPDFPageObj_GetMatrix(item, &matrix);
        var textMatrix = matrix;
        var old = CharsOf(page, item);
        var oldText = new string(old.Unicode.Select(static u => (char)u).ToArray());
        var offsetsOld = old.X.Select((x, i) => ToText(x - old.X[0], old.Y[i] - old.Y[0], textMatrix)).ToArray();
        var naturalOld = Natural(document, page, font, size, textMatrix, oldText);
        var naturalNew = Natural(document, page, font, size, textMatrix, newText);

        var prefix = 0;
        while (prefix < Math.Min(oldText.Length, newText.Length) && oldText[prefix] == newText[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < Math.Min(oldText.Length, newText.Length) - prefix && oldText[^(suffix + 1)] == newText[^(suffix + 1)])
        {
            suffix++;
        }

        var positions = new float[Math.Max(0, newText.Length - 1)];
        var offset = 0.0;
        for (var j = 1; j < newText.Length; j++)
        {
            var extra = 0.0;
            if (j < prefix || j - 1 >= newText.Length - suffix)
            {
                var i = j < prefix ? j - 1 : j - 1 + (oldText.Length - newText.Length);
                extra = (offsetsOld[i + 1] - offsetsOld[i]) - (naturalOld[i + 1] - naturalOld[i]);
            }

            offset += (naturalNew[j] - naturalNew[j - 1]) + extra;
            positions[j - 1] = (float)offset;
        }

        if (!TextEditProbe.SetText(item, newText))
        {
            throw new InvalidOperationException("SetText failed");
        }

        if (positions.Length > 0)
        {
            fixed (float* p = positions)
            {
                if (Fpdf.FPDFText_SetPositions(item, p, (nuint)positions.Length) == 0)
                {
                    throw new InvalidOperationException("SetPositions failed");
                }
            }
        }

        var restore = textMatrix;
        _ = Fpdf.FPDFPageObj_SetMatrix(item, &restore);
    }

    /// <summary>What the text page reads back from a scratch text object set with the text in the font (a missing glyph shows as a missing or other character).</summary>
    public static string ReadBack(nint document, nint page, nint font, float size, string text)
    {
        var probe = Fpdf.FPDFPageObj_CreateTextObj(document, font, size);
        _ = TextEditProbe.SetText(probe, text);
        var m = new FsMatrix { A = 1, B = 0, C = 0, D = 1, E = 20000f, F = 20000f };
        _ = Fpdf.FPDFPageObj_SetMatrix(probe, &m);
        _ = Fpdf.FPDFPage_InsertObject(page, probe);
        try
        {
            return new string(CharsOf(page, probe).Unicode.Select(static u => (char)u).ToArray());
        }
        finally
        {
            _ = Fpdf.FPDFPage_RemoveObject(page, probe);
            Fpdf.FPDFPageObj_Destroy(probe);
        }
    }

    /// <summary>
    /// Replaces the text of the object by <paramref name="prefix"/> (kept in the object), one character in the other font and <paramref name="suffix"/>
    /// (a new object of the old font); the spacing of prefix and suffix is the old one, the suffix moves by the width difference.
    /// </summary>
    public static void ReplaceWithOtherFont(nint document, nint page, nint item, nint otherFont, int keepPrefix, int replacedLength, string other, int keepSuffix)
    {
        var font = Fpdf.FPDFTextObj_GetFont(item);
        float size = 0;
        _ = Fpdf.FPDFTextObj_GetFontSize(item, &size);
        var segmentSize = size;
        FsMatrix matrix = default;
        _ = Fpdf.FPDFPageObj_GetMatrix(item, &matrix);
        var textMatrix = matrix;
        var old = CharsOf(page, item);
        var oldText = new string(old.Unicode.Select(static u => (char)u).ToArray());
        var offsets = old.X.Select((x, i) => ToText(x - old.X[0], old.Y[i] - old.Y[0], textMatrix)).ToArray();
        var pair = Natural(document, page, otherFont, segmentSize, textMatrix, other + other[0]);
        var otherWidth = pair[^1];
        var x0 = offsets[keepPrefix];
        var tailStart = keepPrefix + replacedLength;
        var delta = (x0 + otherWidth) - offsets[tailStart];

        float[] Positions(int from, int to, double shift) => [.. Enumerable.Range(from + 1, Math.Max(0, to - from - 1)).Select(i => (float)(offsets[i] + shift - (offsets[from] + shift)))];

        var prefix = oldText[..keepPrefix];
        _ = TextEditProbe.SetText(item, prefix);
        var prefixPositions = Positions(0, keepPrefix, 0);
        fixed (float* p = prefixPositions)
        {
            _ = Fpdf.FPDFText_SetPositions(item, p, (nuint)prefixPositions.Length);
        }

        var restore = textMatrix;
        _ = Fpdf.FPDFPageObj_SetMatrix(item, &restore);

        nint Segment(nint segmentFont, string text, double offset, float[] positions)
        {
            var created = Fpdf.FPDFPageObj_CreateTextObj(document, segmentFont, segmentSize);
            _ = TextEditProbe.SetText(created, text);
            if (positions.Length > 0)
            {
                fixed (float* q = positions)
                {
                    _ = Fpdf.FPDFText_SetPositions(created, q, (nuint)positions.Length);
                }
            }

            var mm = textMatrix;
            mm.E += (float)(offset * textMatrix.A);
            mm.F += (float)(offset * textMatrix.B);
            _ = Fpdf.FPDFPageObj_SetMatrix(created, &mm);
            uint r = 0, g = 0, b = 0, a = 0;
            _ = Fpdf.FPDFPageObj_GetFillColor(item, &r, &g, &b, &a);
            _ = Fpdf.FPDFPageObj_SetFillColor(created, r, g, b, a);
            _ = Fpdf.FPDFPage_InsertObject(page, created);
            return created;
        }

        _ = Segment(otherFont, other, x0, Positions(0, 0, 0).Length == 0 ? [.. pair[1..^1].Select(static v => (float)v)] : []);
        var suffix = oldText[tailStart..];
        var suffixPositions = Positions(tailStart, oldText.Length, 0);
        _ = Segment(font, suffix, offsets[tailStart] + delta, suffixPositions);
        _ = keepSuffix;
    }
}
