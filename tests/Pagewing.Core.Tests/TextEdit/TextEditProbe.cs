using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;

namespace Pagewing.Core.Tests.TextEdit;

/// <summary>One top-level or nested text object of a page as the spike of milestone 16 reads it.</summary>
internal sealed record ProbeText(
    int Index,
    int Depth,
    nint Object,
    string Text,
    string Font,
    bool Embedded,
    float Size,
    FsMatrix Matrix,
    int Mode,
    (float Left, float Bottom, float Right, float Top) Bounds,
    string[] Marks);

/// <summary>The prototype of the spike of milestone 16 (<c>Docs/DECISIONS.md</c>, "Text bearbeiten: Spike (M16)"). Test code only; every method runs on the PDFium thread.</summary>
internal static unsafe class TextEditProbe
{
    public static void Log(string line)
    {
        var path = Environment.GetEnvironmentVariable("PAGEWING_SPIKE_LOG");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    /// <summary>Every text object of the page, walking into form objects (depth &gt; 0); the text is the object's own, taken from the text page.</summary>
    public static List<ProbeText> ReadTexts(nint page, nint textPage)
    {
        var result = new List<ProbeText>();
        var count = Fpdf.FPDFPage_CountObjects(page);
        for (var i = 0; i < count; i++)
        {
            Visit(Fpdf.FPDFPage_GetObject(page, i), i, 0, textPage, result);
        }

        return result;
    }

    private static void Visit(nint item, int index, int depth, nint textPage, List<ProbeText> into)
    {
        if (item == 0)
        {
            return;
        }

        var type = Fpdf.FPDFPageObj_GetType(item);
        if (type == FpdfConstants.PageObjForm)
        {
            var children = Fpdf.FPDFFormObj_CountObjects(item);
            for (var k = 0; k < children; k++)
            {
                Visit(Fpdf.FPDFFormObj_GetObject(item, new CULong((nuint)k)), index, depth + 1, textPage, into);
            }

            return;
        }

        if (type != FpdfConstants.PageObjText)
        {
            return;
        }

        float size = 0;
        _ = Fpdf.FPDFTextObj_GetFontSize(item, &size);
        FsMatrix matrix = default;
        _ = Fpdf.FPDFPageObj_GetMatrix(item, &matrix);
        float left = 0, bottom = 0, right = 0, top = 0;
        _ = Fpdf.FPDFPageObj_GetBounds(item, &left, &bottom, &right, &top);
        var font = Fpdf.FPDFTextObj_GetFont(item);
        var marks = new List<string>();
        var markCount = Fpdf.FPDFPageObj_CountMarks(item);
        for (var m = 0; m < markCount; m++)
        {
            var mark = Fpdf.FPDFPageObj_GetMark(item, new CULong((nuint)m));
            var buffer = new ushort[64];
            CULong length = default;
            fixed (ushort* p = buffer)
            {
                if (Fpdf.FPDFPageObjMark_GetName(mark, p, new CULong(128), &length) != 0)
                {
                    marks.Add(new string((char*)p));
                }
            }
        }

        into.Add(new ProbeText(index, depth, item, TextOf(item, textPage), FontName(font), Fpdf.FPDFFont_GetIsEmbedded(font) == 1, size, matrix, Fpdf.FPDFTextObj_GetTextRenderMode(item), (left, bottom, right, top), [.. marks]));
    }

    public static string TextOf(nint textObject, nint textPage)
    {
        var length = Fpdf.FPDFTextObj_GetText(textObject, textPage, null, new CULong(0));
        if (length.Value == 0)
        {
            return string.Empty;
        }

        var buffer = new ushort[(int)length.Value + 1];
        fixed (ushort* p = buffer)
        {
            _ = Fpdf.FPDFTextObj_GetText(textObject, textPage, p, new CULong((nuint)buffer.Length * 2));
            return new string((char*)p);
        }
    }

    /// <summary>The whole text of the page (lines separated by CR LF).</summary>
    public static string TextOfPage(nint textPage)
    {
        var count = Fpdf.FPDFText_CountChars(textPage);
        if (count <= 0)
        {
            return string.Empty;
        }

        var buffer = new ushort[count + 1];
        fixed (ushort* p = buffer)
        {
            var written = Fpdf.FPDFText_GetText(textPage, 0, count, p);
            return new string((char*)p, 0, Math.Max(0, written - 1));
        }
    }

    /// <summary>The keys of the first content mark of the object that has parameters.</summary>
    public static string[] MarkKeys(nint item)
    {
        var keys = new List<string>();
        var markCount = Fpdf.FPDFPageObj_CountMarks(item);
        for (var m = 0; m < markCount; m++)
        {
            var mark = Fpdf.FPDFPageObj_GetMark(item, new CULong((nuint)m));
            var parameters = Fpdf.FPDFPageObjMark_CountParams(mark);
            for (var k = 0; k < parameters; k++)
            {
                var buffer = new ushort[64];
                CULong length = default;
                fixed (ushort* p = buffer)
                {
                    if (Fpdf.FPDFPageObjMark_GetParamKey(mark, new CULong((nuint)k), p, new CULong(128), &length) != 0)
                    {
                        keys.Add(new string((char*)p));
                    }
                }
            }
        }

        return [.. keys];
    }

    /// <summary>The text a content stream shows, in order: the literal strings and the hex strings of <c>Tj</c> and <c>TJ</c> decoded as single bytes, with the kerning between them dropped.</summary>
    public static string ShownText(string content)
    {
        var shown = new StringBuilder();
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(content, @"<([0-9A-Fa-f]+)>|\(([^)]*)\)"))
        {
            if (match.Groups[1].Success)
            {
                shown.Append(Encoding.Latin1.GetString(Convert.FromHexString(match.Groups[1].Value.Length % 2 == 0 ? match.Groups[1].Value : match.Groups[1].Value + "0")));
            }
            else
            {
                shown.Append(match.Groups[2].Value);
            }
        }

        return shown.ToString();
    }

    public static string FontName(nint font)
    {
        var length = Fpdf.FPDFFont_GetBaseFontName(font, null, 0);
        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = new byte[(int)length + 1];
        fixed (byte* p = buffer)
        {
            _ = Fpdf.FPDFFont_GetBaseFontName(font, p, (nuint)buffer.Length);
        }

        return Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }

    /// <summary>Sets the text of an object from a string (UTF-16, zero terminated).</summary>
    public static bool SetText(nint textObject, string text)
    {
        var utf16 = new ushort[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            utf16[i] = text[i];
        }

        fixed (ushort* p = utf16)
        {
            return Fpdf.FPDFText_SetText(textObject, p) != 0;
        }
    }

    /// <summary>The inflated content streams of a saved file that contain text operators, in file order (the raw character codes show in them).</summary>
    public static List<string> ContentStreams(byte[] file)
    {
        var result = new List<string>();
        var text = Encoding.Latin1.GetString(file);
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"stream\r?\n"))
        {
            var start = match.Index + match.Length;
            var end = text.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0 || (match.Index >= 3 && text.AsSpan(match.Index - 3, 3).SequenceEqual("end")))
            {
                continue;
            }

            var body = file.AsSpan(start, end - start).ToArray();
            string content;
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                content = Encoding.Latin1.GetString(output.ToArray());
            }
            catch (InvalidDataException)
            {
                content = Encoding.Latin1.GetString(body);
            }

            if (content.Contains(" Tf", StringComparison.Ordinal) || content.Contains("BT", StringComparison.Ordinal))
            {
                result.Add(content);
            }
        }

        return result;
    }
}
