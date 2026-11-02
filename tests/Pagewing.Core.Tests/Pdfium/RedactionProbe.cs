using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>A rectangle in page space (PDF user space, y up), as PDFium reports boxes and bounds.</summary>
internal readonly record struct ProbeRect(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Whether the two rectangles share an area (touching edges do not count).</summary>
    public bool Overlaps(ProbeRect other) => Left < other.Right && other.Left < Right && Bottom < other.Top && other.Bottom < Top;

    public ProbeRect Inflate(double margin) => new(Left - margin, Bottom - margin, Right + margin, Top + margin);

    public ProbeRect Union(ProbeRect other) => new(Math.Min(Left, other.Left), Math.Min(Bottom, other.Bottom), Math.Max(Right, other.Right), Math.Max(Top, other.Top));

    public ProbeRect Shift(double dx, double dy) => new(Left + dx, Bottom + dy, Right + dx, Top + dy);
}

/// <summary>One character of a text page with its text object, box and origin.</summary>
internal sealed record ProbeChar(int Index, uint Unicode, bool Generated, ProbeRect Box, double OriginX, double OriginY, nint TextObject);

internal enum TextOutcome
{
    /// <summary>The survivors were recreated as new text objects.</summary>
    Split,

    /// <summary>Every character was under the rectangles; the object is gone.</summary>
    RemovedAll,

    /// <summary>Some characters survive but the object was removed as a whole, with the reason.</summary>
    RemovedWhole,
}

/// <summary>What the trial decomposition did with one text object.</summary>
internal sealed record TextDecision(string Text, TextOutcome Outcome, string Reason, int Removed, int Kept, string Font, bool Embedded, IReadOnlyList<ProbeChar> Survivors)
{
    /// <summary>Set by <see cref="RedactionProbe.Verify"/>: the survivors read back from the regenerated page equal the originals (Unicode, origin, box).</summary>
    public bool? Verified { get; set; }
}

/// <summary>
/// The prototype of the spike of milestone 14 (<c>Docs/DECISIONS.md</c>, "Schwärzen: Spike und QPDF-Entscheidung (M14)"): reading
/// the text objects of a page, splitting them around rectangles, and the render and byte checks around it. Test code only.
/// </summary>
internal static unsafe class RedactionProbe
{
    public const string Term = "Zephyrstraße 17";

    public static void Log(string line)
    {
        var path = Environment.GetEnvironmentVariable("PAGEWING_SPIKE_LOG");
        if (!string.IsNullOrEmpty(path))
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }

    public static ProbeChar[] ReadChars(nint textPage)
    {
        var count = Math.Max(0, Fpdf.FPDFText_CountChars(textPage));
        var chars = new ProbeChar[count];
        for (var i = 0; i < count; i++)
        {
            double left = 0;
            double right = 0;
            double bottom = 0;
            double top = 0;
            _ = Fpdf.FPDFText_GetCharBox(textPage, i, &left, &right, &bottom, &top);
            double ox = 0;
            double oy = 0;
            _ = Fpdf.FPDFText_GetCharOrigin(textPage, i, &ox, &oy);
            chars[i] = new ProbeChar(i, Fpdf.FPDFText_GetUnicode(textPage, i), Fpdf.FPDFText_IsGenerated(textPage, i) != 0, new ProbeRect(left, bottom, right, top), ox, oy, Fpdf.FPDFText_GetTextObject(textPage, i));
        }

        return chars;
    }

    /// <summary>The text of the characters that are not generated and not white space, in text page order.</summary>
    public static string Letters(IEnumerable<ProbeChar> chars) =>
        string.Concat(chars.Where(c => !c.Generated && c.Unicode > 32).Select(c => c.Unicode <= 0xFFFF ? ((char)c.Unicode).ToString() : char.ConvertFromUtf32((int)c.Unicode)));

    /// <summary>The text objects of a page with the box of the characters that spell <paramref name="term"/> in them, found by their Unicode.</summary>
    public static ProbeRect? FindTerm(IEnumerable<ProbeChar> chars, string term)
    {
        var list = chars.Where(c => !c.Generated && c.Unicode != 0).ToList();
        var text = string.Concat(list.Select(c => (char)c.Unicode));
        var at = text.IndexOf(term, StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        return list.Skip(at).Take(term.Length).Select(c => c.Box).Aggregate((a, b) => a.Union(b));
    }

    public static string FontName(nint font)
    {
        if (font == 0)
        {
            return "?";
        }

        var buffer = new byte[256];
        nuint length;
        fixed (byte* pointer = buffer)
        {
            length = Fpdf.FPDFFont_GetBaseFontName(font, pointer, (nuint)buffer.Length);
        }

        return length <= 1 || length > (nuint)buffer.Length ? "?" : Encoding.UTF8.GetString(buffer, 0, (int)length - 1);
    }

    private sealed class Plan
    {
        public required nint Object { get; init; }

        public required string Text { get; init; }

        public required List<ProbeChar> Chars { get; init; }

        public required bool[] Hit { get; init; }

        public nint Font { get; set; }

        public float Size { get; set; }

        public FsMatrix Matrix { get; set; }

        public int Mode { get; set; }

        public uint[] Fill { get; set; } = [0, 0, 0, 255];

        public uint[] Stroke { get; set; } = [0, 0, 0, 255];

        public string FontName { get; set; } = "?";

        public bool Embedded { get; set; }

        public string? Fallback { get; set; }

        public List<List<ProbeChar>> Runs { get; } = [];
    }

    /// <summary>
    /// The trial decomposition: every text object with a character that overlaps one of <paramref name="rects"/> is replaced by new
    /// text objects for the runs of surviving characters (same font object, size, mode, colours; position and spacing from the
    /// character origins), or removed whole when that does not work. The page is changed; the caller generates its content.
    /// </summary>
    public static List<TextDecision> DecomposeText(PdfiumDocument document, PdfiumRedaction.RawPage page, IReadOnlyList<ProbeRect> rects, Func<ProbeChar, bool>? extraHit = null)
    {
        var plans = new List<Plan>();
        var textPage = page.LoadTextPage();
        try
        {
            var chars = ReadChars(textPage);
            var order = new List<nint>();
            var byObject = new Dictionary<nint, List<ProbeChar>>();
            foreach (var c in chars)
            {
                if (c.Generated || c.TextObject == 0)
                {
                    continue;
                }

                if (!byObject.TryGetValue(c.TextObject, out var list))
                {
                    byObject[c.TextObject] = list = [];
                    order.Add(c.TextObject);
                }

                list.Add(c);
            }

            foreach (var obj in order)
            {
                var list = byObject[obj];
                var hit = list.Select(c => rects.Any(r => r.Overlaps(c.Box)) || (extraHit?.Invoke(c) ?? false)).ToArray();
                if (!hit.Any(h => h))
                {
                    continue;
                }

                var plan = new Plan
                {
                    Object = obj,
                    Text = string.Concat(list.Select(c => c.Unicode is 0 or > 0xFFFF ? "?" : ((char)c.Unicode).ToString())),
                    Chars = list,
                    Hit = hit,
                    Font = Fpdf.FPDFTextObj_GetFont(obj),
                    Mode = Fpdf.FPDFTextObj_GetTextRenderMode(obj),
                };
                float size = 0;
                _ = Fpdf.FPDFTextObj_GetFontSize(obj, &size);
                plan.Size = size;
                FsMatrix matrix = default;
                _ = Fpdf.FPDFPageObj_GetMatrix(obj, &matrix);
                plan.Matrix = matrix;
                uint r = 0;
                uint g = 0;
                uint b = 0;
                uint a = 0;
                _ = Fpdf.FPDFPageObj_GetFillColor(obj, &r, &g, &b, &a);
                plan.Fill = [r, g, b, a];
                _ = Fpdf.FPDFPageObj_GetStrokeColor(obj, &r, &g, &b, &a);
                plan.Stroke = [r, g, b, a];
                plan.FontName = FontName(plan.Font);
                plan.Embedded = plan.Font != 0 && Fpdf.FPDFFont_GetIsEmbedded(plan.Font) != 0;

                List<ProbeChar>? run = null;
                for (var i = 0; i < list.Count; i++)
                {
                    if (hit[i])
                    {
                        run = null;
                        continue;
                    }

                    if (run is null)
                    {
                        run = [];
                        plan.Runs.Add(run);
                    }

                    run.Add(list[i]);
                }

                var kept = list.Where((_, i) => !hit[i]).ToList();
                if (kept.Count > 0)
                {
                    if (kept.Any(c => c.Unicode is 0 or 0xFFFD || c.Unicode is >= 0xD800 and <= 0xDFFF))
                    {
                        plan.Fallback = "no reliable Unicode for a surviving character";
                    }
                    else if (plan.Font == 0 || plan.Size <= 0)
                    {
                        plan.Fallback = "no font or size";
                    }
                    else if (Math.Abs((matrix.A * matrix.D) - (matrix.B * matrix.C)) < 1e-9)
                    {
                        plan.Fallback = "degenerate text matrix";
                    }
                }

                plans.Add(plan);
            }
        }
        finally
        {
            page.CloseTextPage(textPage);
        }

        var decisions = new List<TextDecision>();
        var nativeDocument = page.NativeDocument;
        foreach (var plan in plans)
        {
            var removed = plan.Hit.Count(h => h);
            var kept = plan.Hit.Length - removed;
            var index = IndexOf(page, plan.Object);
            if (index < 0)
            {
                decisions.Add(new TextDecision(plan.Text, TextOutcome.RemovedWhole, "not an object of the page (inside a form XObject): flatten the form first", removed, kept, plan.FontName, plan.Embedded, []));
                continue;
            }

            if (kept == 0)
            {
                Replace(page, plan.Object, index, []);
                decisions.Add(new TextDecision(plan.Text, TextOutcome.RemovedAll, string.Empty, removed, 0, plan.FontName, plan.Embedded, []));
                continue;
            }

            var created = new List<nint>();
            var reason = plan.Fallback;
            if (reason is null)
            {
                reason = TryCreate(nativeDocument, plan, created);
                if (reason is not null)
                {
                    foreach (var obj in created)
                    {
                        page.Destroy(obj);
                    }

                    created.Clear();
                }
            }

            if (reason is not null)
            {
                Replace(page, plan.Object, index, []);
                decisions.Add(new TextDecision(plan.Text, TextOutcome.RemovedWhole, reason, removed, kept, plan.FontName, plan.Embedded, []));
            }
            else
            {
                Replace(page, plan.Object, index, created);
                decisions.Add(new TextDecision(plan.Text, TextOutcome.Split, string.Empty, removed, kept, plan.FontName, plan.Embedded, plan.Chars.Where((_, i) => !plan.Hit[i]).ToList()));
            }
        }

        return decisions;
    }

    private static string? TryCreate(nint document, Plan plan, List<nint> created)
    {
        var m = plan.Matrix;
        var det = (m.A * m.D) - (m.B * m.C);
        foreach (var run in plan.Runs)
        {
            var text = string.Concat(run.Select(c => char.ConvertFromUtf32((int)c.Unicode)));
            var obj = Fpdf.FPDFPageObj_CreateTextObj(document, plan.Font, plan.Size);
            if (obj == 0)
            {
                return "FPDFPageObj_CreateTextObj failed";
            }

            created.Add(obj);
            var utf16 = new ushort[text.Length + 1];
            for (var i = 0; i < text.Length; i++)
            {
                utf16[i] = text[i];
            }

            fixed (ushort* pointer = utf16)
            {
                if (Fpdf.FPDFText_SetText(obj, pointer) == 0)
                {
                    return "FPDFText_SetText failed";
                }
            }

            if (run.Count > 1)
            {
                var positions = new float[run.Count - 1];
                for (var k = 1; k < run.Count; k++)
                {
                    var vx = run[k].OriginX - run[0].OriginX;
                    var vy = run[k].OriginY - run[0].OriginY;
                    positions[k - 1] = (float)(((vx * m.D) - (vy * m.C)) / det);
                }

                fixed (float* pointer = positions)
                {
                    if (Fpdf.FPDFText_SetPositions(obj, pointer, (nuint)positions.Length) == 0)
                    {
                        return "FPDFText_SetPositions failed";
                    }
                }
            }

            var matrix = new FsMatrix { A = m.A, B = m.B, C = m.C, D = m.D, E = (float)run[0].OriginX, F = (float)run[0].OriginY };
            if (Fpdf.FPDFPageObj_SetMatrix(obj, &matrix) == 0)
            {
                return "FPDFPageObj_SetMatrix failed";
            }

            if (plan.Mode > 0 && Fpdf.FPDFTextObj_SetTextRenderMode(obj, plan.Mode) == 0)
            {
                return "FPDFTextObj_SetTextRenderMode failed";
            }

            if (plan.Mode == FpdfConstants.TextRenderModeInvisible && Fpdf.FPDFTextObj_GetTextRenderMode(obj) != plan.Mode)
            {
                return "the render mode did not stay";
            }

            _ = Fpdf.FPDFPageObj_SetFillColor(obj, plan.Fill[0], plan.Fill[1], plan.Fill[2], plan.Fill[3]);
            _ = Fpdf.FPDFPageObj_SetStrokeColor(obj, plan.Stroke[0], plan.Stroke[1], plan.Stroke[2], plan.Stroke[3]);
        }

        return null;
    }

    private static int IndexOf(PdfiumRedaction.RawPage page, nint obj)
    {
        var count = page.CountObjects();
        for (var i = 0; i < count; i++)
        {
            if (page.GetObject(i) == obj)
            {
                return i;
            }
        }

        return -1;
    }

    private static void Replace(PdfiumRedaction.RawPage page, nint old, int index, List<nint> created)
    {
        if (!page.Remove(old))
        {
            throw new InvalidOperationException("FPDFPage_RemoveObject failed.");
        }

        page.Destroy(old);
        for (var k = 0; k < created.Count; k++)
        {
            if (!page.InsertAt(created[k], index + k))
            {
                throw new InvalidOperationException("FPDFPage_InsertObjectAtIndex failed.");
            }
        }
    }

    /// <summary>
    /// The read-back of the trial: after <c>FPDFPage_GenerateContent</c> the page is loaded again and every survivor of every split
    /// object (white space is not looked at: PDFium leaves a leading space out of the text page) must be there with the same Unicode, origin (0.02 pt) and box edges (0.05 pt). Sets <see cref="TextDecision.Verified"/>.
    /// </summary>
    public static void Verify(PdfiumDocument document, int pageIndex, IReadOnlyList<TextDecision> decisions)
    {
        using var page = PdfiumRedaction.OpenPage(document, pageIndex);
        var textPage = page.LoadTextPage();
        try
        {
            var actual = ReadChars(textPage).Where(c => !c.Generated && c.TextObject != 0).ToList();
            foreach (var decision in decisions.Where(d => d.Outcome == TextOutcome.Split))
            {
                foreach (var sv in decision.Survivors.Where(c => c.Unicode > 0x20).Take(40))
                {
                    var near = actual.Where(a => a.Unicode == sv.Unicode).OrderBy(a => Math.Abs(a.OriginX - sv.OriginX) + Math.Abs(a.OriginY - sv.OriginY)).FirstOrDefault();
                    if (near is null || Math.Abs(near.OriginX - sv.OriginX) > 0.02 || Math.Abs(near.Box.Left - sv.Box.Left) > 0.05 || Math.Abs(near.Box.Right - sv.Box.Right) > 0.05 || Math.Abs(near.Box.Top - sv.Box.Top) > 0.05 || Math.Abs(near.Box.Bottom - sv.Box.Bottom) > 0.05 || Math.Abs(near.OriginY - sv.OriginY) > 0.02)
                    {
                        Log($"Q2 mismatch {decision.Font} U+{sv.Unicode:X} want o=({sv.OriginX:0.###},{sv.OriginY:0.###}) box=({sv.Box.Left:0.###},{sv.Box.Bottom:0.###},{sv.Box.Right:0.###},{sv.Box.Top:0.###}) got {(near is null ? "none" : $"o=({near.OriginX:0.###},{near.OriginY:0.###}) box=({near.Box.Left:0.###},{near.Box.Bottom:0.###},{near.Box.Right:0.###},{near.Box.Top:0.###})")}");
                        break;
                    }
                }

                decision.Verified = decision.Survivors.Where(c => c.Unicode > 0x20).All(s => actual.Any(a =>
                    a.Unicode == s.Unicode
                    && Math.Abs(a.OriginX - s.OriginX) < 0.02 && Math.Abs(a.OriginY - s.OriginY) < 0.02
                    && Math.Abs(a.Box.Left - s.Box.Left) < 0.05 && Math.Abs(a.Box.Right - s.Box.Right) < 0.05
                    && Math.Abs(a.Box.Bottom - s.Box.Bottom) < 0.05 && Math.Abs(a.Box.Top - s.Box.Top) < 0.05));
            }
        }
        finally
        {
            page.CloseTextPage(textPage);
        }
    }

    /// <summary>What PDFium says about an image object: filters, size, colour space and bits per pixel.</summary>
    public sealed record ImageFacts(string[] Filters, uint Width, uint Height, int ColorSpace, uint BitsPerPixel, int RawLength, byte[] Raw, int DecodedLength);

    public static ImageFacts ReadImageFacts(PdfiumRedaction.RawPage page, nint image)
    {
        _ = page.Access();
        var filters = new List<string>();
        var count = Fpdf.FPDFImageObj_GetImageFilterCount(image);
        for (var i = 0; i < count; i++)
        {
            var buffer = new byte[64];
            fixed (byte* pointer = buffer)
            {
                var length = (int)Fpdf.FPDFImageObj_GetImageFilter(image, i, pointer, new CULong(64)).Value;
                filters.Add(Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length - 1)));
            }
        }

        FpdfImageObjMetadata metadata = default;
        _ = Fpdf.FPDFImageObj_GetImageMetadata(image, page.Access(), &metadata);
        var rawLength = (int)Fpdf.FPDFImageObj_GetImageDataRaw(image, null, new CULong(0)).Value;
        var raw = new byte[rawLength];
        fixed (byte* pointer = raw)
        {
            _ = Fpdf.FPDFImageObj_GetImageDataRaw(image, pointer, new CULong((nuint)rawLength));
        }

        var decodedLength = (int)Fpdf.FPDFImageObj_GetImageDataDecoded(image, null, new CULong(0)).Value;
        return new ImageFacts([.. filters], metadata.Width, metadata.Height, metadata.ColorSpace, metadata.BitsPerPixel, rawLength, raw, decodedLength);
    }

    /// <summary>
    /// Reads the pixels of an image object the way the signature code does: the matrix is set to the pixel size, the object is
    /// rendered (mask and alpha applied) and the matrix is put back. Returns BGRA rows (top first) and the bitmap format.
    /// </summary>
    public static (int Width, int Height, int Format, byte[] Bgra)? ReadImagePixels(PdfiumRedaction.RawPage page, nint image, uint pixelWidth, uint pixelHeight)
    {
        if (!page.TryGetMatrix(image, out var original))
        {
            return null;
        }

        var unit = new FsMatrix { A = pixelWidth, B = 0, C = 0, D = pixelHeight, E = 0, F = 0 };
        if (!page.SetMatrix(image, unit))
        {
            return null;
        }

        try
        {
            var bitmap = Fpdf.FPDFImageObj_GetRenderedBitmap(page.NativeDocument, page.Access(), image);
            if (bitmap == 0)
            {
                return null;
            }

            try
            {
                var width = Fpdf.FPDFBitmap_GetWidth(bitmap);
                var height = Fpdf.FPDFBitmap_GetHeight(bitmap);
                var stride = Fpdf.FPDFBitmap_GetStride(bitmap);
                var format = Fpdf.FPDFBitmap_GetFormat(bitmap);
                var result = new byte[width * height * 4];
                var source = (byte*)Fpdf.FPDFBitmap_GetBuffer(bitmap);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            result[(((y * width) + x) * 4) + c] = format == FpdfConstants.BitmapBgra ? source[(y * stride) + (x * 4) + c] : c == 3 ? (byte)255 : source[(y * stride) + (x * 4) + c];
                        }
                    }
                }

                return (width, height, format, result);
            }
            finally
            {
                Fpdf.FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            _ = page.SetMatrix(image, original);
        }
    }

    /// <summary>
    /// Sets the pixels of an image object from BGRA rows (a copy goes into a new image stream; <c>FPDFImageObj_SetBitmap</c>
    /// re-encodes with Flate). The matrix of the object is not touched.
    /// </summary>
    public static bool WriteImagePixels(PdfiumRedaction.RawPage page, nint image, int width, int height, byte[] bgra)
    {
        _ = page.Access();
        fixed (byte* pixels = bgra)
        {
            var bitmap = Fpdf.FPDFBitmap_CreateEx(width, height, FpdfConstants.BitmapBgra, pixels, width * 4);
            if (bitmap == 0)
            {
                return false;
            }

            try
            {
                return Fpdf.FPDFImageObj_SetBitmap(null, 0, image, bitmap) != 0;
            }
            finally
            {
                Fpdf.FPDFBitmap_Destroy(bitmap);
            }
        }
    }

    /// <summary>What PDFium says about a path object: draw mode, colours and the points of its segments.</summary>
    public sealed record PathFacts(int Fill, bool Stroke, uint[] FillColor, uint[] StrokeColor, FsMatrix Matrix, int SegmentCount, (int Type, float X, float Y, bool Close)[] Segments);

    public static PathFacts ReadPath(PdfiumRedaction.RawPage page, nint path)
    {
        _ = page.Access();
        int fill = 0;
        int stroke = 0;
        _ = Fpdf.FPDFPath_GetDrawMode(path, &fill, &stroke);
        uint r = 0;
        uint g = 0;
        uint b = 0;
        uint a = 0;
        _ = Fpdf.FPDFPageObj_GetFillColor(path, &r, &g, &b, &a);
        var fillColor = new[] { r, g, b, a };
        _ = Fpdf.FPDFPageObj_GetStrokeColor(path, &r, &g, &b, &a);
        var strokeColor = new[] { r, g, b, a };
        _ = page.TryGetMatrix(path, out var matrix);
        var count = Fpdf.FPDFPath_CountSegments(path);
        var segments = new List<(int, float, float, bool)>();
        for (var i = 0; i < count; i++)
        {
            var segment = Fpdf.FPDFPath_GetPathSegment(path, i);
            float x = 0;
            float y = 0;
            _ = Fpdf.FPDFPathSegment_GetPoint(segment, &x, &y);
            segments.Add((Fpdf.FPDFPathSegment_GetType(segment), x, y, Fpdf.FPDFPathSegment_GetClose(segment) != 0));
        }

        return new PathFacts(fill, stroke != 0, fillColor, strokeColor, matrix, count, [.. segments]);
    }

    /// <summary>The filled rectangles that are left of <paramref name="bounds"/> when <paramref name="hole"/> is cut out (at most four), as new path objects.</summary>
    public static List<nint> CropRectangle(PdfiumRedaction.RawPage page, ProbeRect bounds, ProbeRect hole, int fillMode, uint[] color)
    {
        _ = page.Access();
        var h = new ProbeRect(Math.Max(hole.Left, bounds.Left), Math.Max(hole.Bottom, bounds.Bottom), Math.Min(hole.Right, bounds.Right), Math.Min(hole.Top, bounds.Top));
        var parts = new List<ProbeRect>();
        if (h.Bottom > bounds.Bottom)
        {
            parts.Add(new ProbeRect(bounds.Left, bounds.Bottom, bounds.Right, h.Bottom));
        }

        if (h.Top < bounds.Top)
        {
            parts.Add(new ProbeRect(bounds.Left, h.Top, bounds.Right, bounds.Top));
        }

        if (h.Left > bounds.Left)
        {
            parts.Add(new ProbeRect(bounds.Left, h.Bottom, h.Left, h.Top));
        }

        if (h.Right < bounds.Right)
        {
            parts.Add(new ProbeRect(h.Right, h.Bottom, bounds.Right, h.Top));
        }

        var created = new List<nint>();
        foreach (var part in parts)
        {
            var rect = Fpdf.FPDFPageObj_CreateNewRect((float)part.Left, (float)part.Bottom, (float)(part.Right - part.Left), (float)(part.Top - part.Bottom));
            _ = Fpdf.FPDFPageObj_SetFillColor(rect, color[0], color[1], color[2], color[3]);
            _ = Fpdf.FPDFPath_SetDrawMode(rect, fillMode, 0);
            created.Add(rect);
        }

        return created;
    }

    /// <summary>Replaces an object of the page by others at the same place in the drawing order.</summary>
    public static void ReplaceObject(PdfiumRedaction.RawPage page, nint old, IReadOnlyList<nint> created)
    {
        var index = IndexOf(page, old);
        Replace(page, old, index, [.. created]);
    }

    public static bool SetCharcodes(nint textObject, uint[] codes)
    {
        fixed (uint* pointer = codes)
        {
            return Fpdf.FPDFText_SetCharcodes(textObject, pointer, (nuint)codes.Length) != 0;
        }
    }

    public static byte[] Save(PdfiumDocument document)
    {
        using var stream = new MemoryStream();
        PdfiumPageEdit.SaveRewrite(document, stream);
        return stream.ToArray();
    }

    /// <summary>The characters of a page of a saved file, read back through PDFium (not generated, no white space).</summary>
    public static string PageLetters(PdfiumDocument document, int pageIndex)
    {
        using var page = PdfiumRedaction.OpenPage(document, pageIndex);
        var textPage = page.LoadTextPage();
        try
        {
            return Letters(ReadChars(textPage));
        }
        finally
        {
            page.CloseTextPage(textPage);
        }
    }

    /// <summary>
    /// Compares two renders of the same page outside <paramref name="rects"/> (grown by <paramref name="marginPoints"/>): the number of
    /// pixels that differ by more than 16 in a channel, the pixels compared, and the largest difference.
    /// </summary>
    public static (int Different, int Compared, int MaxDelta) CompareOutside(RenderedImage a, RenderedImage b, double scale, IReadOnlyList<ProbeRect> rects, double marginPoints, ProbeRect? within = null)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return (int.MaxValue, 0, 255);
        }

        var pageHeight = a.Height / scale;
        var grown = rects.Select(r => r.Inflate(marginPoints)).ToList();
        var different = 0;
        var compared = 0;
        var max = 0;
        var pa = a.Pixels.Span;
        var pb = b.Pixels.Span;
        for (var y = 0; y < a.Height; y++)
        {
            var pageY = pageHeight - ((y + 0.5) / scale);
            for (var x = 0; x < a.Width; x++)
            {
                var pageX = (x + 0.5) / scale;
                if (within is { } w && !(pageX >= w.Left && pageX <= w.Right && pageY >= w.Bottom && pageY <= w.Top))
                {
                    continue;
                }

                if (grown.Any(r => pageX >= r.Left && pageX <= r.Right && pageY >= r.Bottom && pageY <= r.Top))
                {
                    continue;
                }

                compared++;
                var delta = 0;
                for (var c = 0; c < 3; c++)
                {
                    delta = Math.Max(delta, Math.Abs(pa[(y * a.Stride) + (x * 4) + c] - pb[(y * b.Stride) + (x * 4) + c]));
                }

                max = Math.Max(max, delta);
                if (delta > 16)
                {
                    different++;
                }
            }
        }

        return (different, compared, max);
    }

    /// <summary>The box (page space) around all pixels that differ by more than 16, or null.</summary>
    public static ProbeRect? DiffBox(RenderedImage a, RenderedImage b, double scale)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return null;
        }

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var pa = a.Pixels.Span;
        var pb = b.Pixels.Span;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    if (Math.Abs(pa[(y * a.Stride) + (x * 4) + c] - pb[(y * b.Stride) + (x * 4) + c]) > 16)
                    {
                        minX = Math.Min(minX, x);
                        maxX = Math.Max(maxX, x);
                        minY = Math.Min(minY, y);
                        maxY = Math.Max(maxY, y);
                        break;
                    }
                }
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        var height = a.Height / scale;
        return new ProbeRect(minX / scale, height - ((maxY + 1) / scale), (maxX + 1) / scale, height - (minY / scale));
    }

    /// <summary>Counts <paramref name="term"/> in the file and in the file with its Flate streams decoded.</summary>
    public static (int Plain, int Decoded) RawCount(byte[] file, string term) =>
        (Redaction.RawPdf.Count(file, term), Redaction.RawPdf.Count(Redaction.RawPdf.InflateAllStreams(file), term));

    /// <summary>
    /// Where the term is in the file: for every hit (any byte form, Flate streams decoded) the head of the object that holds it.
    /// </summary>
    public static List<string> Where(byte[] file, string term)
    {
        var inflated = Redaction.RawPdf.InflateAllStreams(file);
        var text = Encoding.Latin1.GetString(inflated);
        var forms = new List<byte[]>();
        if (term.All(c => c <= 0xFF))
        {
            forms.Add(Encoding.Latin1.GetBytes(term));
        }

        forms.Add(Encoding.UTF8.GetBytes(term));
        forms.Add(Encoding.BigEndianUnicode.GetBytes(term));
        var result = new List<string>();
        var seen = new HashSet<int>();
        foreach (var form in forms.DistinctBy(f => Convert.ToHexString(f)))
        {
            var pattern = Encoding.Latin1.GetString(form);
            for (var at = text.IndexOf(pattern, StringComparison.Ordinal); at >= 0; at = text.IndexOf(pattern, at + pattern.Length, StringComparison.Ordinal))
            {
                if (!seen.Add(at))
                {
                    continue;
                }

                var start = Math.Max(0, text.LastIndexOf(" obj", at, StringComparison.Ordinal));
                var head = text.Substring(start, Math.Min(240, text.Length - start)).Replace((char)10, ' ').Replace((char)13, ' ');
                result.Add(head);
            }
        }

        return result;
    }

    /// <summary>The catalog and page keys of interest that occur in the file text (Flate streams decoded).</summary>
    public static string[] KeysIn(byte[] file)
    {
        var text = Encoding.Latin1.GetString(Redaction.RawPdf.InflateAllStreams(file));
        string[] keys = ["/Outlines", "/Names", "/EmbeddedFile", "/Filespec", "/Metadata", "/Author", "/Subject", "/Keywords", "/Title", "/JavaScript", "/OpenAction", "/PageLabels", "/Thumb", "/PieceInfo", "/AA", "/StructTreeRoot", "/StructParents", "/OCProperties", "/OC", "/AcroForm", "/Annots", "/Popup", "/IRT", "/Pattern", "/Type3", "/SMask"];
        return keys.Where(k => text.Contains(k, StringComparison.Ordinal)).ToArray();
    }

    public static string Describe(TextDecision d) =>
        string.Create(CultureInfo.InvariantCulture, $"[{d.Outcome}{(d.Verified is null ? string.Empty : d.Verified.Value ? " verified" : " NOT-VERIFIED")}] removed={d.Removed} kept={d.Kept} font={d.Font} embedded={d.Embedded}{(d.Reason.Length > 0 ? " reason=" + d.Reason : string.Empty)} text='{d.Text}'");
}

/// <summary>The render helper of the spike (the probe class is unsafe and cannot await).</summary>
internal static class RedactionRender
{
    /// <summary>Renders page 1 of the file at <paramref name="scale"/> through the normal engine.</summary>
    public static async Task<RenderedImage> RenderAsync(PdfEngine engine, string path, double scale, int pageIndex = 0)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
        return await document.RenderPageAsync(pageIndex, scale, cancellationToken: TestContext.Current.CancellationToken);
    }

}
