using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>Why PDFium text cannot be edited (mirrored by <c>TextEditLockReason</c> in Core).</summary>
internal enum PdfiumTextLock
{
    None,
    Invisible,
    Type3,
    Vertical,
    RenderMode,
    Form,
    MarkedContent,
    TooManyRuns,
    RunTooLong,
    Ligature,
}

/// <summary>A box in points from the top left of the displayed page.</summary>
internal readonly record struct PdfiumTextArea(double Left, double Top, double Right, double Bottom)
{
    public static PdfiumTextArea Empty { get; } = new(double.NaN, double.NaN, double.NaN, double.NaN);

    public bool IsEmpty => double.IsNaN(Left);

    public PdfiumTextArea Union(PdfiumTextArea other) =>
        IsEmpty ? other : other.IsEmpty ? this : new PdfiumTextArea(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
}

/// <summary>A run of a page: one text object, or after an edit the consecutive objects that replaced one.</summary>
internal sealed record PdfiumTextRun(
    int Index,
    int Count,
    string Text,
    PdfiumTextArea Box,
    string FontName,
    bool Embedded,
    bool Standard14,
    float Size,
    byte Red,
    byte Green,
    byte Blue,
    string Fingerprint,
    PdfiumTextLock Lock);

/// <summary>The runs of a page and its size.</summary>
internal sealed record PdfiumPageRuns(IReadOnlyList<PdfiumTextRun> Runs, double Width, double Height);

/// <summary>A piece of new text and the font it is set in (the font of the run, or Geist).</summary>
internal sealed record PdfiumSegment(string Text, bool InGeist);

/// <summary>What a replacement or a swap did.</summary>
/// <param name="Before">The run that was replaced, as it was.</param>
/// <param name="After">The run that stands now, or <see langword="null"/> when the run was removed.</param>
/// <param name="BoxBefore">The box of the old run.</param>
/// <param name="BoxAfter">The box of the new run (empty when removed).</param>
/// <param name="Stash">The token under which the objects that were taken out wait for an undo.</param>
internal sealed record PdfiumTextEdited(PdfiumTextRun Before, PdfiumTextRun? After, PdfiumTextArea BoxBefore, PdfiumTextArea BoxAfter, long Stash);

/// <summary>Why PDFium refused to edit a run.</summary>
internal enum PdfiumTextEditFailure
{
    /// <summary>The run on the page is not the one that was asked for.</summary>
    Stale,

    /// <summary>The run is locked (<see cref="PdfiumTextEditException.Lock"/>).</summary>
    Locked,

    /// <summary>Rewriting the page would change something else (found on a copy of the page); the document is untouched.</summary>
    NotRewritable,

    /// <summary>Something failed after the first change to the document; the page may have changed.</summary>
    Failed,
}

internal sealed class PdfiumTextEditException(PdfiumTextEditFailure failure, PdfiumTextLock lockReason, string message) : Exception(message)
{
    public PdfiumTextEditFailure Failure { get; } = failure;

    public PdfiumTextLock Lock { get; } = lockReason;
}

/// <summary>
/// Reading and replacing a run of text (milestone 16, <c>Docs/DECISIONS.md</c>, "Text bearbeiten: Spike (M16)"). A run is one text object of PDFium;
/// it is replaced by new text objects of the same matrix, colour, mode and marks, whose character positions keep the spacing of the characters that
/// stay; the old objects wait in the document handle for an undo. PDFium writes the whole page content again, so every replacement first
/// runs on a copy of the page and is refused when anything else on the page changes. Everything runs on the PDFium thread.
/// </summary>
internal static unsafe class PdfiumTextEdit
{
    /// <summary>The most levels of Form XObjects looked into.</summary>
    private const int MaxFormDepth = 16;

    /// <summary>Where probe objects stand: far outside the page, so that the text page does not drop their characters as duplicates of the real ones.</summary>
    private const float ProbeOffset = 20000f;

    /// <summary>The longest side, in pixels, of the renders of the check on the copy.</summary>
    private const double MaxCheckPixels = 4_000_000;

    private static readonly string[] Standard14Prefixes = ["courier", "helvetica", "times-", "symbol", "zapfdingbats"];

    // ---------------------------------------------------------------------------------------------------------------- reading

    private sealed class RawText
    {
        public nint Object { get; init; }

        public int Index { get; init; }

        public int Depth { get; init; }

        public nint Font { get; init; }

        public string FontName { get; init; } = string.Empty;

        public bool Embedded { get; init; }

        public TextFacts Facts { get; init; } = new();

        public List<int> Chars { get; } = [];

        public string Text { get; set; } = string.Empty;

        public PdfiumTextLock Lock { get; set; }

        public string Fingerprint { get; set; } = string.Empty;
    }

    private sealed class PageRead
    {
        public List<RawText> Texts { get; } = [];

        public CharTable Table { get; set; } = new(0);

        public DisplayTransform Transform { get; set; }

        public double Width { get; init; }

        public double Height { get; init; }
    }

    /// <summary>Reads the runs of a page.</summary>
    public static PdfiumPageRuns ReadRuns(PdfiumDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var page = document.LoadPage(pageIndex);
        var read = ReadPage(page.Access(), page.Width, page.Height);
        return new PdfiumPageRuns([.. read.Texts.Select(t => ToRun(read, t, t.Index, 1))], page.Width, page.Height);
    }

    private static PageRead ReadPage(nint page, double width, double height)
    {
        var read = new PageRead { Width = width, Height = height };
        if (!DisplayTransform.TryCreate(page, width, height, out var transform))
        {
            throw new InvalidOperationException("PDFium could not convert the coordinates of the page.");
        }

        read.Transform = transform;
        var textPage = Fpdf.FPDFText_LoadPage(page);
        if (textPage == 0)
        {
            throw new InvalidDataException("The text of the page could not be loaded.");
        }

        try
        {
            var count = Math.Max(0, Fpdf.FPDFText_CountChars(textPage));
            var table = new CharTable(count);
            PdfiumRedactionNative.ReadChars(textPage, table, 0, count);
            read.Table = table;
            var byObject = new Dictionary<nint, List<int>>();
            for (var i = 0; i < count; i++)
            {
                if (!table.Generated[i] && table.TextObject[i] != 0)
                {
                    if (!byObject.TryGetValue(table.TextObject[i], out var list))
                    {
                        byObject[table.TextObject[i]] = list = [];
                    }

                    list.Add(i);
                }
            }

            var total = Math.Min(Fpdf.FPDFPage_CountObjects(page), PdfiumTextEditLimits.ObjectsScanned);
            for (var i = 0; i < total; i++)
            {
                Visit(Fpdf.FPDFPage_GetObject(page, i), i, 0, byObject, read);
            }
        }
        finally
        {
            Fpdf.FPDFText_ClosePage(textPage);
        }

        var type3 = read.Texts.Any(static t => t.FontName.Length == 0);
        var tooMany = read.Texts.Count > PdfiumTextEditLimits.RunsPerPage || Fpdf.FPDFPage_CountObjects(page) > PdfiumTextEditLimits.ObjectsScanned;
        foreach (var t in read.Texts)
        {
            if (tooMany)
            {
                t.Lock = PdfiumTextLock.TooManyRuns;
            }
            else if (type3 && t.Lock == PdfiumTextLock.None)
            {
                t.Lock = PdfiumTextLock.Type3;
            }

            t.Fingerprint = FingerprintOf(t);
        }

        return read;
    }

    private static void Visit(nint item, int topIndex, int depth, Dictionary<nint, List<int>> byObject, PageRead read)
    {
        if (item == 0)
        {
            return;
        }

        var type = Fpdf.FPDFPageObj_GetType(item);
        if (type == FpdfConstants.PageObjForm)
        {
            if (depth >= MaxFormDepth)
            {
                return;
            }

            var children = Math.Min(Fpdf.FPDFFormObj_CountObjects(item), PdfiumTextEditLimits.ObjectsScanned);
            for (var k = 0; k < children; k++)
            {
                Visit(Fpdf.FPDFFormObj_GetObject(item, new System.Runtime.InteropServices.CULong((nuint)k)), topIndex, depth + 1, byObject, read);
            }

            return;
        }

        if (type != FpdfConstants.PageObjText || !byObject.TryGetValue(item, out var chars))
        {
            return;
        }

        var facts = PdfiumRedactionNative.ReadTextFacts(item);
        var name = FontNameOf(facts.Font);
        var raw = new RawText
        {
            Object = item,
            Index = topIndex,
            Depth = depth,
            Font = facts.Font,
            FontName = name,
            Embedded = Fpdf.FPDFFont_GetIsEmbedded(facts.Font) == 1,
            Facts = facts,
        };
        raw.Chars.AddRange(chars);
        var builder = new StringBuilder(chars.Count);
        foreach (var c in chars)
        {
            builder.Append((char)read.Table.Unicode[c]);
        }

        raw.Text = builder.ToString();
        raw.Lock = LockOf(raw, read);
        read.Texts.Add(raw);
    }

    private static PdfiumTextLock LockOf(RawText t, PageRead read)
    {
        if (t.FontName.Length == 0)
        {
            return PdfiumTextLock.Type3;
        }

        if (t.Facts.Mode == FpdfConstants.TextRenderModeInvisible)
        {
            return PdfiumTextLock.Invisible;
        }

        if (t.Depth > 0)
        {
            return PdfiumTextLock.Form;
        }

        if (HasMarkedContentLock(t.Object))
        {
            return PdfiumTextLock.MarkedContent;
        }

        if (t.Facts.Mode is < 0 or > 2)
        {
            return PdfiumTextLock.RenderMode;
        }

        if (t.Text.Length > PdfiumTextEditLimits.CharsPerRun)
        {
            return PdfiumTextLock.RunTooLong;
        }

        if (IsVertical(t, read))
        {
            return PdfiumTextLock.Vertical;
        }

        return HasLigature(t, read) ? PdfiumTextLock.Ligature : PdfiumTextLock.None;
    }

    // A glyph that stands for several characters ("fi") reads as characters at one origin; writing the line again would stack them.
    private static bool HasLigature(RawText t, PageRead read)
    {
        if (t.Chars.Count < 2 || !t.Facts.MatrixRead)
        {
            return false;
        }

        var table = read.Table;
        for (var k = 1; k < t.Chars.Count; k++)
        {
            var current = t.Chars[k];
            var previous = t.Chars[k - 1];
            if (char.IsLowSurrogate((char)table.Unicode[current]))
            {
                continue;
            }

            var vx = table.OriginX[current] - table.OriginX[previous];
            var vy = table.OriginY[current] - table.OriginY[previous];
            if (Math.Abs(TextX(vx, vy, t.Facts.Matrix)) < 0.001 * Math.Max(1.0, t.Facts.Size) && Math.Abs(TextY(vx, vy, t.Facts.Matrix)) < 0.001 * Math.Max(1.0, t.Facts.Size))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVertical(RawText t, PageRead read)
    {
        if (t.Chars.Count < 2 || !t.Facts.MatrixRead)
        {
            return false;
        }

        var table = read.Table;
        double dx = 0;
        double dy = 0;
        var m = t.Facts.Matrix;
        for (var k = 1; k < t.Chars.Count; k++)
        {
            var vx = table.OriginX[t.Chars[k]] - table.OriginX[t.Chars[k - 1]];
            var vy = table.OriginY[t.Chars[k]] - table.OriginY[t.Chars[k - 1]];
            dx += TextX(vx, vy, m);
            dy += TextY(vx, vy, m);
        }

        return Math.Abs(dy) > Math.Abs(dx) && Math.Abs(dy) > 1e-3;
    }

    private static bool HasMarkedContentLock(nint item)
    {
        var markCount = Fpdf.FPDFPageObj_CountMarks(item);
        for (var m = 0; m < markCount; m++)
        {
            var mark = Fpdf.FPDFPageObj_GetMark(item, new System.Runtime.InteropServices.CULong((nuint)m));
            if (mark == 0)
            {
                continue;
            }

            if (MarkName(mark) == "OC")
            {
                return true;
            }

            var parameters = Fpdf.FPDFPageObjMark_CountParams(mark);
            for (var k = 0; k < parameters; k++)
            {
                if (MarkKey(mark, k) is "ActualText" or "Alt" or "E")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string MarkName(nint mark)
    {
        var buffer = new ushort[64];
        System.Runtime.InteropServices.CULong length = default;
        fixed (ushort* p = buffer)
        {
            return Fpdf.FPDFPageObjMark_GetName(mark, p, new System.Runtime.InteropServices.CULong(128), &length) != 0 ? new string((char*)p) : string.Empty;
        }
    }

    private static string MarkKey(nint mark, int index)
    {
        var buffer = new ushort[64];
        System.Runtime.InteropServices.CULong length = default;
        fixed (ushort* p = buffer)
        {
            return Fpdf.FPDFPageObjMark_GetParamKey(mark, new System.Runtime.InteropServices.CULong((nuint)index), p, new System.Runtime.InteropServices.CULong(128), &length) != 0 ? new string((char*)p) : string.Empty;
        }
    }

    private static string FontNameOf(nint font)
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

    private static bool IsStandard14(string name)
    {
        var plain = name.Length > 7 && name[6] == '+' ? name[7..] : name;
        return Standard14Prefixes.Any(prefix => plain.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static double Determinant(FsMatrix m) => ((double)m.A * m.D) - ((double)m.B * m.C);

    private static double TextX(double vx, double vy, FsMatrix m) => ((vx * m.D) - (vy * m.C)) / Determinant(m);

    private static double TextY(double vx, double vy, FsMatrix m) => ((vy * m.A) - (vx * m.B)) / Determinant(m);

    private static string FingerprintOf(RawText t)
    {
        var m = t.Facts.Matrix;
        var data = string.Create(
            CultureInfo.InvariantCulture,
            $"{t.Text}|{t.FontName}|{t.Facts.Size:R}|{m.A:R},{m.B:R},{m.C:R},{m.D:R},{m.E:R},{m.F:R}|{t.Facts.Fill[0]},{t.Facts.Fill[1]},{t.Facts.Fill[2]},{t.Facts.Fill[3]}|{t.Facts.Mode}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data)))[..32];
    }

    private static string CombinedFingerprint(IEnumerable<RawText> texts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(';', texts.Select(static t => t.Fingerprint)))))[..32];

    private static PdfiumTextArea BoxOf(PageRead read, IEnumerable<RawText> texts)
    {
        var box = PdfiumTextArea.Empty;
        foreach (var t in texts)
        {
            foreach (var c in t.Chars)
            {
                if (!read.Table.BoxUsable(c))
                {
                    continue;
                }

                var b = read.Table.Box(c);
                var corners = new[] { read.Transform.ToDisplay(b.Left, b.Bottom), read.Transform.ToDisplay(b.Right, b.Bottom), read.Transform.ToDisplay(b.Right, b.Top), read.Transform.ToDisplay(b.Left, b.Top) };
                box = box.Union(new PdfiumTextArea(corners.Min(static p => p.X), corners.Min(static p => p.Y), corners.Max(static p => p.X), corners.Max(static p => p.Y)));
            }
        }

        return box;
    }

    private static PdfiumTextRun ToRun(PageRead read, RawText first, int index, int count) => ToRun(read, [first], index, count);

    private static PdfiumTextRun ToRun(PageRead read, IReadOnlyList<RawText> texts, int index, int count)
    {
        var first = texts[0];
        return new PdfiumTextRun(
            index,
            count,
            string.Concat(texts.Select(static t => t.Text)),
            BoxOf(read, texts),
            first.FontName,
            first.Embedded,
            IsStandard14(first.FontName),
            first.Facts.Size,
            (byte)Math.Clamp(first.Facts.Fill[0], 0u, 255u),
            (byte)Math.Clamp(first.Facts.Fill[1], 0u, 255u),
            (byte)Math.Clamp(first.Facts.Fill[2], 0u, 255u),
            texts.Count == 1 ? first.Fingerprint : CombinedFingerprint(texts),
            texts.Select(static t => t.Lock).FirstOrDefault(static l => l != PdfiumTextLock.None));
    }

    private static List<RawText> Range(PageRead read, int index, int count, string fingerprint)
    {
        var texts = read.Texts.Where(t => t.Depth == 0 && t.Index >= index && t.Index < index + count).ToList();
        if (texts.Count != count || texts[0].Index != index || (count > 1 && CombinedFingerprint(texts) != fingerprint) || (count == 1 && texts[0].Fingerprint != fingerprint))
        {
            throw new PdfiumTextEditException(PdfiumTextEditFailure.Stale, PdfiumTextLock.None, "The run on the page is not the one asked for.");
        }

        var locked = texts.Select(static t => t.Lock).FirstOrDefault(static l => l != PdfiumTextLock.None);
        if (locked != PdfiumTextLock.None)
        {
            throw new PdfiumTextEditException(PdfiumTextEditFailure.Locked, locked, "The run is locked.");
        }

        return texts;
    }

    // ---------------------------------------------------------------------------------------------------------------- coverage

    /// <summary>
    /// Tells for each code point whether the font of the run can draw it (see the decision (b) of the spike): a character the font does not
    /// draw on this page already never counts for an embedded font, and for every font the character must read back as itself.
    /// </summary>
    public static bool[] FontCoverage(PdfiumDocument document, int pageIndex, int index, string fingerprint, IReadOnlyList<int> codePoints)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(codePoints);
        using var page = document.LoadPage(pageIndex);
        var handle = page.Access();
        var read = ReadPage(handle, page.Width, page.Height);
        var run = Range(read, index, 1, fingerprint)[0];
        var present = new HashSet<int>();
        foreach (var t in read.Texts.Where(t => t.Font == run.Font))
        {
            foreach (var rune in t.Text.EnumerateRunes())
            {
                present.Add(rune.Value);
            }
        }

        var result = new bool[codePoints.Count];
        var nativeDocument = document.Access();
        for (var i = 0; i < codePoints.Count; i++)
        {
            var code = codePoints[i];
            if (!Rune.IsValid(code) || (run.Embedded && !present.Contains(code)))
            {
                continue;
            }

            var text = char.ConvertFromUtf32(code);
            result[i] = ReadBack(nativeDocument, handle, run.Font, run.Facts.Size, text) == text;
        }

        return result;
    }

    private static string ReadBack(nint document, nint page, nint font, float size, string text)
    {
        var probe = NewProbe(document, page, font, size, text);
        try
        {
            return new string(CharsOf(page, probe).Unicode.Select(static u => (char)u).ToArray());
        }
        finally
        {
            RemoveProbe(page, probe);
        }
    }

    private static nint NewProbe(nint document, nint page, nint font, float size, string text)
    {
        var probe = Fpdf.FPDFPageObj_CreateTextObj(document, font, size);
        if (probe == 0)
        {
            throw new InvalidOperationException("PDFium could not create a text object.");
        }

        if (!SetText(probe, text))
        {
            Fpdf.FPDFPageObj_Destroy(probe);
            throw new InvalidOperationException("PDFium could not set the text of a text object.");
        }

        var matrix = new FsMatrix { A = 1, B = 0, C = 0, D = 1, E = ProbeOffset, F = ProbeOffset };
        _ = Fpdf.FPDFPageObj_SetMatrix(probe, &matrix);
        if (Fpdf.FPDFPage_InsertObject(page, probe) == 0)
        {
            Fpdf.FPDFPageObj_Destroy(probe);
            throw new InvalidOperationException("PDFium could not insert a text object.");
        }

        return probe;
    }

    private static void RemoveProbe(nint page, nint probe)
    {
        if (Fpdf.FPDFPage_RemoveObject(page, probe) != 0)
        {
            Fpdf.FPDFPageObj_Destroy(probe);
        }
    }

    private static bool SetText(nint textObject, string text)
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

    private sealed record ObjectChars(uint[] Unicode, double[] X, double[] Y);

    private static ObjectChars CharsOf(nint page, nint item)
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

                double x = 0;
                double y = 0;
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

    // ---------------------------------------------------------------------------------------------------------------- replacing

    /// <summary>
    /// Replaces the run (one text object at <paramref name="index"/>) by the segments, or removes it when there are none. The page is rewritten
    /// on a copy first; the real page is touched only when that copy shows nothing else changing.
    /// </summary>
    /// <exception cref="PdfiumTextEditException">The run is stale or locked, or the page cannot be rewritten safely.</exception>
    public static PdfiumTextEdited Replace(PdfiumDocument document, int pageIndex, int index, string fingerprint, IReadOnlyList<PdfiumSegment> segments, Func<byte[]> loadGeist)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(loadGeist);
        using var page = document.LoadPage(pageIndex);
        var handle = page.Access();
        var before = ReadPage(handle, page.Width, page.Height);
        var old = Range(before, index, 1, fingerprint);
        var boxBefore = BoxOf(before, old);
        var runBefore = ToRun(before, old, index, 1);
        CheckOnCopy(document, pageIndex, page.Width, page.Height, index, fingerprint, segments, loadGeist, boxBefore);

        List<nint> removed;
        int inserted;
        try
        {
            (removed, inserted) = Apply(document.Access(), handle, before, old[0], segments, () => GeistOf(document, loadGeist));
            if (Fpdf.FPDFPage_GenerateContent(handle) == 0)
            {
                throw new InvalidOperationException("PDFium could not write the content of the page.");
            }
        }
        catch (Exception exception) when (exception is not PdfiumTextEditException)
        {
            throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, exception.Message);
        }

        var stash = document.Handle.Stash(removed);
        return Finish(document, pageIndex, index, inserted, runBefore, boxBefore, stash, expected: string.Concat(segments.Select(static s => s.Text)), others: before.Texts.Where(t => t != old[0]).Select(static t => t.Text).ToList(), page);
    }

    /// <summary>
    /// Puts the objects kept under <paramref name="stash"/> back in place of the run that stands now (<paramref name="count"/> objects at
    /// <paramref name="index"/>); the run taken out waits under the returned token (redo is the same call with it). Undo of a removal has <paramref name="count"/> 0.
    /// </summary>
    public static PdfiumTextEdited Swap(PdfiumDocument document, int pageIndex, int index, int count, string fingerprint, long stash)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var page = document.LoadPage(pageIndex);
        var handle = page.Access();
        var before = ReadPage(handle, page.Width, page.Height);
        var current = count == 0 ? [] : Range(before, index, count, fingerprint);
        var objects = document.Handle.TakeStash(stash) ?? throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, "The objects of the earlier text are gone.");
        var boxBefore = BoxOf(before, current);
        var runBefore = current.Count == 0 ? null : ToRun(before, current, index, count);
        var others = before.Texts.Where(t => !current.Contains(t)).Select(static t => t.Text).ToList();
        try
        {
            for (var k = 0; k < objects.Count; k++)
            {
                if (Fpdf.FPDFPage_InsertObjectAtIndex(handle, objects[k], (nuint)(index + k)) == 0)
                {
                    throw new InvalidOperationException("PDFium could not insert a text object.");
                }
            }

            var taken = new List<nint>();
            foreach (var t in current)
            {
                if (Fpdf.FPDFPage_RemoveObject(handle, t.Object) == 0)
                {
                    throw new InvalidOperationException("PDFium could not take a text object out of the page.");
                }

                taken.Add(t.Object);
            }

            if (Fpdf.FPDFPage_GenerateContent(handle) == 0)
            {
                throw new InvalidOperationException("PDFium could not write the content of the page.");
            }

            var token = document.Handle.Stash(taken);
            return Finish(document, pageIndex, index, objects.Count, runBefore, boxBefore, token, expected: null, others, page);
        }
        catch (Exception exception) when (exception is not PdfiumTextEditException)
        {
            throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, exception.Message);
        }
    }

    // Reloads the page (the content written by PDFium is parsed again), checks it, and describes the new run.
    private static PdfiumTextEdited Finish(PdfiumDocument document, int pageIndex, int index, int inserted, PdfiumTextRun? runBefore, PdfiumTextArea boxBefore, long stash, string? expected, List<string> others, PdfiumPage written)
    {
        written.Dispose();
        using var page = document.LoadPage(pageIndex);
        var after = ReadPage(page.Access(), page.Width, page.Height);
        var fresh = after.Texts.Where(t => t.Depth == 0 && t.Index >= index && t.Index < index + inserted).ToList();
        var rest = after.Texts.Where(t => !fresh.Contains(t)).Select(static t => t.Text).ToList();
        var text = string.Concat(fresh.Select(static t => t.Text));
        if (fresh.Count != inserted || !rest.SequenceEqual(others, StringComparer.Ordinal) || (expected is not null && text != expected))
        {
            throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, "The page read back differently from what was written.");
        }

        return new PdfiumTextEdited(
            runBefore ?? new PdfiumTextRun(index, 0, string.Empty, PdfiumTextArea.Empty, string.Empty, false, false, 0, 0, 0, 0, string.Empty, PdfiumTextLock.None),
            inserted == 0 ? null : ToRun(after, fresh, index, inserted),
            boxBefore,
            inserted == 0 ? PdfiumTextArea.Empty : BoxOf(after, fresh),
            stash);
    }

    private static nint GeistOf(PdfiumDocument document, Func<byte[]> loadGeist)
    {
        var owner = document.Handle;
        if (owner.OcrFont != 0)
        {
            return owner.OcrFont;
        }

        var data = loadGeist();
        nint font;
        fixed (byte* bytes = data)
        {
            font = Fpdf.FPDFText_LoadFont(document.Access(), bytes, (uint)data.Length, FpdfConstants.FontTrueType, cid: 1);
        }

        if (font == 0)
        {
            throw new InvalidOperationException("PDFium could not load the replacement font.");
        }

        owner.OcrFont = font;
        return font;
    }

    /// <summary>Rewrites the page on a copy in a scratch document and refuses when anything outside the run would change.</summary>
    private static void CheckOnCopy(PdfiumDocument document, int pageIndex, double width, double height, int index, string fingerprint, IReadOnlyList<PdfiumSegment> segments, Func<byte[]> loadGeist, PdfiumTextArea boxBefore)
    {
        var scratch = Fpdf.FPDF_CreateNewDocument();
        if (scratch == 0)
        {
            throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, "PDFium could not create the scratch document.");
        }

        nint geist = 0;
        List<nint> takenOut = [];
        try
        {
            if (!PdfiumRedactionNative.ImportPage(scratch, document.Access(), pageIndex))
            {
                throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, "PDFium could not copy the page.");
            }

            var page = Fpdf.FPDF_LoadPage(scratch, 0);
            if (page == 0)
            {
                throw new PdfiumTextEditException(PdfiumTextEditFailure.Failed, PdfiumTextLock.None, "PDFium could not load the copy of the page.");
            }

            PageRead before;
            byte[] imageBefore;
            var pixels = CheckPixels(width, height);
            List<string> others;
            try
            {
                before = ReadPage(page, width, height);
                var old = Range(before, index, 1, fingerprint);
                others = before.Texts.Where(t => t != old[0]).Select(static t => t.Text).ToList();
                imageBefore = Render(page, pixels.Width, pixels.Height);
                (takenOut, _) = Apply(
                    scratch,
                    page,
                    before,
                    old[0],
                    segments,
                    () =>
                    {
                        if (geist == 0)
                        {
                            var data = loadGeist();
                            fixed (byte* bytes = data)
                            {
                                geist = Fpdf.FPDFText_LoadFont(scratch, bytes, (uint)data.Length, FpdfConstants.FontTrueType, cid: 1);
                            }
                        }

                        return geist != 0 ? geist : throw new InvalidOperationException("PDFium could not load the replacement font.");
                    });
                if (Fpdf.FPDFPage_GenerateContent(page) == 0)
                {
                    throw new InvalidOperationException("PDFium could not write the content of the copy.");
                }
            }
            catch (Exception exception) when (exception is not PdfiumTextEditException)
            {
                throw new PdfiumTextEditException(PdfiumTextEditFailure.NotRewritable, PdfiumTextLock.None, exception.Message);
            }
            finally
            {
                Fpdf.FPDF_ClosePage(page);
            }

            var reloaded = Fpdf.FPDF_LoadPage(scratch, 0);
            if (reloaded == 0)
            {
                throw new PdfiumTextEditException(PdfiumTextEditFailure.NotRewritable, PdfiumTextLock.None, "The copy cannot be loaded again.");
            }

            try
            {
                var after = ReadPage(reloaded, width, height);
                var inserted = segments.Count == 0 ? 0 : after.Texts.Count - others.Count;
                var rest = after.Texts.Where(t => t.Index < index || t.Index >= index + Math.Max(inserted, 0)).Select(static t => t.Text).ToList();
                if (!rest.SequenceEqual(others, StringComparer.Ordinal))
                {
                    throw new PdfiumTextEditException(PdfiumTextEditFailure.NotRewritable, PdfiumTextLock.None, "Rewriting the page changes other text.");
                }

                // What stands in the place of the run reads back as the text that was asked for (a glyph the font cannot draw would read back as another character).
                var written = string.Concat(after.Texts.Where(t => t.Depth == 0 && t.Index >= index && t.Index < index + Math.Max(inserted, 0)).Select(static t => t.Text));
                if (!string.Equals(written, string.Concat(segments.Select(static s => s.Text)), StringComparison.Ordinal))
                {
                    throw new PdfiumTextEditException(PdfiumTextEditFailure.NotRewritable, PdfiumTextLock.None, "The new text does not read back as written.");
                }

                var union = boxBefore.Union(BoxOf(after, after.Texts.Where(t => t.Index >= index && t.Index < index + inserted)));
                if (!SameOutside(imageBefore, Render(reloaded, pixels.Width, pixels.Height), pixels, union))
                {
                    throw new PdfiumTextEditException(PdfiumTextEditFailure.NotRewritable, PdfiumTextLock.None, "Rewriting the page changes how it looks.");
                }
            }
            finally
            {
                Fpdf.FPDF_ClosePage(reloaded);
            }
        }
        finally
        {
            // The object taken out of the copy goes with it (it holds the font program it was read with).
            foreach (var item in takenOut)
            {
                Fpdf.FPDFPageObj_Destroy(item);
            }

            if (geist != 0)
            {
                Fpdf.FPDFFont_Close(geist);
            }

            Fpdf.FPDF_CloseDocument(scratch);
        }
    }

    private readonly record struct CheckSize(int Width, int Height, double Scale);

    private static CheckSize CheckPixels(double width, double height)
    {
        var scale = Math.Min(1.0, Math.Sqrt(MaxCheckPixels / Math.Max(1.0, width * height)));
        return new CheckSize(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), scale);
    }

    private static byte[] Render(nint page, int width, int height)
    {
        var buffer = new byte[width * height * 4];
        Array.Fill(buffer, (byte)0xFF);
        fixed (byte* pixels = buffer)
        {
            var bitmap = Fpdf.FPDFBitmap_CreateEx(width, height, FpdfConstants.BitmapBgra, pixels, width * 4);
            if (bitmap == 0)
            {
                throw new InvalidOperationException("PDFium could not create the bitmap of the check.");
            }

            try
            {
                Fpdf.FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, 0);
            }
            finally
            {
                Fpdf.FPDFBitmap_Destroy(bitmap);
            }
        }

        return buffer;
    }

    private static bool SameOutside(byte[] a, byte[] b, CheckSize size, PdfiumTextArea changed)
    {
        const int Margin = 3;
        var left = changed.IsEmpty ? 0 : (int)Math.Floor((changed.Left * size.Scale) - Margin);
        var top = changed.IsEmpty ? 0 : (int)Math.Floor((changed.Top * size.Scale) - Margin);
        var right = changed.IsEmpty ? -1 : (int)Math.Ceiling((changed.Right * size.Scale) + Margin);
        var bottom = changed.IsEmpty ? -1 : (int)Math.Ceiling((changed.Bottom * size.Scale) + Margin);
        long different = 0;
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                if (x >= left && x <= right && y >= top && y <= bottom)
                {
                    continue;
                }

                var i = ((y * size.Width) + x) * 4;
                if (Math.Abs(a[i] - b[i]) > 24 || Math.Abs(a[i + 1] - b[i + 1]) > 24 || Math.Abs(a[i + 2] - b[i + 2]) > 24)
                {
                    different++;
                }
            }
        }

        return different <= Math.Max(8, (long)size.Width * size.Height / 5000);
    }

    // Replaces the text object by new objects at its place; returns the objects taken out and the number put in. The page is not yet written.
    private static (List<nint> Removed, int Inserted) Apply(nint document, nint page, PageRead read, RawText old, IReadOnlyList<PdfiumSegment> segments, Func<nint> geistFont)
    {
        var facts = old.Facts;
        if (!facts.MatrixRead || Math.Abs(Determinant(facts.Matrix)) < 1e-9)
        {
            throw new InvalidOperationException("The matrix of the text object cannot be inverted.");
        }

        var matrix = facts.Matrix;
        var size = facts.Size;
        var oldRunes = RunesOf(old, read);
        var newRunes = new List<(int Rune, bool Geist)>();
        foreach (var segment in segments)
        {
            foreach (var rune in segment.Text.EnumerateRunes())
            {
                newRunes.Add((rune.Value, segment.InGeist));
            }
        }

        var created = new List<nint>();
        if (newRunes.Count > 0)
        {
            var offsets = Layout(document, page, old, oldRunes, newRunes, size, geistFont);
            var geist = newRunes.Any(static r => r.Geist) ? geistFont() : 0;
            var at = 0;
            while (at < newRunes.Count)
            {
                var to = at;
                while (to < newRunes.Count && newRunes[to].Geist == newRunes[at].Geist)
                {
                    to++;
                }

                var text = string.Concat(newRunes.Skip(at).Take(to - at).Select(static r => char.ConvertFromUtf32(r.Rune)));
                created.Add(NewObject(document, newRunes[at].Geist ? geist : old.Font, size, matrix, text, [.. offsets.Skip(at).Take(to - at).Select(o => (float)(o - offsets[at]))], offsets[at], old));
                at = to;
            }
        }

        var removed = new List<nint>();
        try
        {
            for (var k = 0; k < created.Count; k++)
            {
                if (Fpdf.FPDFPage_InsertObjectAtIndex(page, created[k], (nuint)(old.Index + k)) == 0)
                {
                    throw new InvalidOperationException("PDFium could not insert a text object.");
                }
            }

            if (Fpdf.FPDFPage_RemoveObject(page, old.Object) == 0)
            {
                throw new InvalidOperationException("PDFium could not take the text object out of the page.");
            }

            removed.Add(old.Object);
        }
        catch
        {
            // The objects not yet inserted are destroyed; those inserted stay (the caller discards the copy or reports the failure).
            foreach (var item in created)
            {
                if (Fpdf.FPDFPage_RemoveObject(page, item) != 0)
                {
                    Fpdf.FPDFPageObj_Destroy(item);
                }
            }

            throw;
        }

        return (removed, created.Count);
    }

    private static List<(int Rune, double X)> RunesOf(RawText t, PageRead read)
    {
        var table = read.Table;
        var m = t.Facts.Matrix;
        var first = t.Chars[0];
        var result = new List<(int, double)>();
        for (var k = 0; k < t.Chars.Count; k++)
        {
            var start = t.Chars[k];
            var c = (char)table.Unicode[start];
            int rune = c;
            if (char.IsHighSurrogate(c) && k + 1 < t.Chars.Count && char.IsLowSurrogate((char)table.Unicode[t.Chars[k + 1]]))
            {
                rune = char.ConvertToUtf32(c, (char)table.Unicode[t.Chars[k + 1]]);
                k++;
            }

            result.Add((rune, TextX(table.OriginX[start] - table.OriginX[first], table.OriginY[start] - table.OriginY[first], m)));
        }

        return result;
    }

    // The offset of every new character from the start of the run, in text space: the characters before and after the change keep the spacing they
    // had, the new ones follow each other as the font lays them out (plus the usual extra spacing of the run), the rest moves by the difference.
    private static double[] Layout(nint document, nint page, RawText old, List<(int Rune, double X)> oldRunes, List<(int Rune, bool Geist)> newRunes, float size, Func<nint> geistFont)
    {
        var prefix = 0;
        while (prefix < Math.Min(oldRunes.Count, newRunes.Count) && oldRunes[prefix].Rune == newRunes[prefix].Rune && !newRunes[prefix].Geist)
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < Math.Min(oldRunes.Count, newRunes.Count) - prefix && oldRunes[^(suffix + 1)].Rune == newRunes[^(suffix + 1)].Rune && !newRunes[^(suffix + 1)].Geist)
        {
            suffix++;
        }

        var natural = new double[newRunes.Count];

        // Advances of the middle characters in their own fonts (a run of the same font is laid out by a probe with its first character appended).
        var advances = new double[newRunes.Count];
        var from = prefix;
        while (from < newRunes.Count - suffix)
        {
            var to = from;
            while (to < newRunes.Count - suffix && newRunes[to].Geist == newRunes[from].Geist)
            {
                to++;
            }

            var group = newRunes.Skip(from).Take(to - from).Select(static r => r.Rune).ToList();
            var font = newRunes[from].Geist ? geistFont() : old.Font;
            var offsets = ProbeOffsets(document, page, font, size, string.Concat(group.Select(char.ConvertFromUtf32)) + char.ConvertFromUtf32(group[0]));
            for (var k = 0; k < group.Count; k++)
            {
                advances[from + k] = offsets[k + 1] - offsets[k];
            }

            from = to;
        }

        // The extra spacing of the run (character spacing, tracking): the median of what the unchanged neighbours have over the natural layout.
        var extra = 0.0;
        if (oldRunes.Count > 1)
        {
            var oldNatural = ProbeOffsets(document, page, old.Font, size, string.Concat(oldRunes.Select(static r => char.ConvertFromUtf32(r.Rune))));
            var gaps = new List<double>();
            for (var i = 0; i + 1 < oldRunes.Count; i++)
            {
                gaps.Add((oldRunes[i + 1].X - oldRunes[i].X) - (oldNatural[i + 1] - oldNatural[i]));
            }

            gaps.Sort();
            extra = gaps[gaps.Count / 2];
        }

        for (var j = 0; j < prefix; j++)
        {
            natural[j] = oldRunes[j].X;
        }

        double x0;
        if (prefix == 0)
        {
            x0 = 0;
        }
        else if (prefix < oldRunes.Count)
        {
            x0 = oldRunes[prefix].X;
        }
        else
        {
            var endAdvance = ProbeOffsets(document, page, old.Font, size, char.ConvertFromUtf32(oldRunes[prefix - 1].Rune) + char.ConvertFromUtf32(oldRunes[prefix - 1].Rune));
            x0 = oldRunes[prefix - 1].X + (endAdvance[1] - endAdvance[0]) + extra;
        }

        var position = x0;
        for (var j = prefix; j < newRunes.Count - suffix; j++)
        {
            natural[j] = position;
            position += advances[j] + extra;
        }

        var shift = suffix == 0 ? 0 : position - oldRunes[^suffix].X;
        for (var k = 0; k < suffix; k++)
        {
            natural[newRunes.Count - suffix + k] = oldRunes[oldRunes.Count - suffix + k].X + shift;
        }

        return natural;
    }

    private static double[] ProbeOffsets(nint document, nint page, nint font, float size, string text)
    {
        var probe = NewProbe(document, page, font, size, text);
        try
        {
            var chars = CharsOf(page, probe);
            if (chars.X.Length != text.Length)
            {
                throw new InvalidOperationException("PDFium laid out a probe differently from its text.");
            }

            return [.. chars.X.Select(x => x - chars.X[0])];
        }
        finally
        {
            RemoveProbe(page, probe);
        }
    }

    private static nint NewObject(nint document, nint font, float size, FsMatrix matrix, string text, float[] positions, double offset, RawText old)
    {
        var created = Fpdf.FPDFPageObj_CreateTextObj(document, font, size);
        if (created == 0)
        {
            throw new InvalidOperationException("PDFium could not create a text object.");
        }

        try
        {
            if (!SetText(created, text))
            {
                throw new InvalidOperationException("PDFium could not set the text of a text object.");
            }

            var spacing = positions.Skip(1).ToArray();
            if (spacing.Length > 0)
            {
                fixed (float* p = spacing)
                {
                    if (Fpdf.FPDFText_SetPositions(created, p, (nuint)spacing.Length) == 0)
                    {
                        throw new InvalidOperationException("PDFium could not set the positions of a text object.");
                    }
                }
            }

            var placed = matrix;
            placed.E += (float)(offset * matrix.A);
            placed.F += (float)(offset * matrix.B);
            if (Fpdf.FPDFPageObj_SetMatrix(created, &placed) == 0)
            {
                throw new InvalidOperationException("PDFium could not place a text object.");
            }

            var facts = old.Facts;
            if (facts.Mode > 0 && Fpdf.FPDFTextObj_SetTextRenderMode(created, facts.Mode) == 0)
            {
                throw new InvalidOperationException("PDFium could not set the render mode.");
            }

            _ = Fpdf.FPDFPageObj_SetFillColor(created, facts.Fill[0], facts.Fill[1], facts.Fill[2], facts.Fill[3]);
            _ = Fpdf.FPDFPageObj_SetStrokeColor(created, facts.Stroke[0], facts.Stroke[1], facts.Stroke[2], facts.Stroke[3]);
            CopyMarks(document, old.Object, created);
            return created;
        }
        catch
        {
            Fpdf.FPDFPageObj_Destroy(created);
            throw;
        }
    }

    // The marks (tagged content, for example the number of the marked content of the structure tree) of the old object; only integer parameters are copied.
    private static void CopyMarks(nint document, nint source, nint target)
    {
        var markCount = Fpdf.FPDFPageObj_CountMarks(source);
        for (var m = 0; m < markCount; m++)
        {
            var mark = Fpdf.FPDFPageObj_GetMark(source, new System.Runtime.InteropServices.CULong((nuint)m));
            if (mark == 0)
            {
                continue;
            }

            var name = Encoding.UTF8.GetBytes(MarkName(mark) + "\0");
            nint copy;
            fixed (byte* namePointer = name)
            {
                copy = Fpdf.FPDFPageObj_AddMark(target, namePointer);
            }

            if (copy == 0)
            {
                throw new InvalidOperationException("PDFium could not copy a mark.");
            }

            var parameters = Fpdf.FPDFPageObjMark_CountParams(mark);
            for (var k = 0; k < parameters; k++)
            {
                var key = Encoding.UTF8.GetBytes(MarkKey(mark, k) + "\0");
                fixed (byte* keyPointer = key)
                {
                    int value;
                    if (Fpdf.FPDFPageObjMark_GetParamValueType(mark, keyPointer) == 2 && Fpdf.FPDFPageObjMark_GetParamIntValue(mark, keyPointer, &value) != 0)
                    {
                        _ = Fpdf.FPDFPageObjMark_SetIntParam(document, target, copy, keyPointer, value);
                    }
                }
            }
        }
    }
}

/// <summary>The limits of <see cref="PdfiumTextEdit"/> (the same numbers as <c>TextEditLimits</c> in Core).</summary>
internal static class PdfiumTextEditLimits
{
    public const int ObjectsScanned = 200_000;

    public const int RunsPerPage = 5000;

    public const int CharsPerRun = 2000;
}
