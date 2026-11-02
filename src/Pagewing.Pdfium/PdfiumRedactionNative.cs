using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>A rectangle in PDF user space (y up, before rotation): what the character boxes, object bounds and annotation rectangles of PDFium use.</summary>
internal readonly record struct UserRect(double Left, double Bottom, double Right, double Top)
{
    /// <summary>The slack, in points, for "touches": rectangles that meet at an edge up to double rounding still touch.</summary>
    public const double TouchSlack = 1e-6;

    /// <summary>Whether the rectangles share an area or an edge or a corner (an edge is enough).</summary>
    public bool Touches(UserRect other) =>
        Left <= other.Right + TouchSlack && other.Left <= Right + TouchSlack && Bottom <= other.Top + TouchSlack && other.Bottom <= Top + TouchSlack;
}

/// <summary>The characters of a text page as PDFium reports them, in parallel arrays (a page may have hundreds of thousands).</summary>
internal sealed class CharTable(int count)
{
    public int Count { get; } = count;

    public uint[] Unicode { get; } = new uint[count];

    public bool[] Generated { get; } = new bool[count];

    public double[] Left { get; } = new double[count];

    public double[] Right { get; } = new double[count];

    public double[] Bottom { get; } = new double[count];

    public double[] Top { get; } = new double[count];

    public double[] OriginX { get; } = new double[count];

    public double[] OriginY { get; } = new double[count];

    /// <summary>Whether PDFium answered the request for the box of the character.</summary>
    public bool[] BoxRead { get; } = new bool[count];

    public nint[] TextObject { get; } = new nint[count];

    /// <summary>The box of a character.</summary>
    public UserRect Box(int index) => new(Left[index], Bottom[index], Right[index], Top[index]);

    /// <summary>Whether PDFium gave a box for the character and all four edges are finite numbers; only then can it be compared with a rectangle.</summary>
    public bool BoxUsable(int index) =>
        BoxRead[index] && double.IsFinite(Left[index]) && double.IsFinite(Right[index]) && double.IsFinite(Bottom[index]) && double.IsFinite(Top[index]);
}

/// <summary>What a text object looks like: everything a new text object needs to look the same.</summary>
internal sealed class TextFacts
{
    public nint Font { get; init; }

    public float Size { get; init; }

    public FsMatrix Matrix { get; init; }

    public bool MatrixRead { get; init; }

    public int Mode { get; init; }

    public uint[] Fill { get; init; } = [0, 0, 0, 255];

    public bool FillRead { get; init; }

    public uint[] Stroke { get; init; } = [0, 0, 0, 255];

    public bool StrokeRead { get; init; }
}

/// <summary>
/// The native steps of <see cref="PdfiumRedactionRun"/> that need pointers (the run itself is an iterator and keeps them out).
/// Every method must be called on the PDFium thread.
/// </summary>
internal static unsafe class PdfiumRedactionNative
{
    /// <summary>Copies one page of <paramref name="source"/> to the start of <paramref name="destination"/> (a scratch document that is never saved).</summary>
    public static bool ImportPage(nint destination, nint source, int pageIndex)
    {
        var index = pageIndex;
        return Fpdf.FPDF_ImportPagesByIndex(destination, source, &index, new CULong(1), 0) != 0;
    }

    /// <summary>The text object, font, matrix, mode and colours of a text object.</summary>
    public static TextFacts ReadTextFacts(nint textObject)
    {
        float size = 0;
        _ = Fpdf.FPDFTextObj_GetFontSize(textObject, &size);
        FsMatrix matrix = default;
        var matrixRead = Fpdf.FPDFPageObj_GetMatrix(textObject, &matrix) != 0;
        uint r = 0;
        uint g = 0;
        uint b = 0;
        uint a = 0;
        var fillRead = Fpdf.FPDFPageObj_GetFillColor(textObject, &r, &g, &b, &a) != 0;
        var fill = new[] { r, g, b, a };
        r = g = b = a = 0;
        var strokeRead = Fpdf.FPDFPageObj_GetStrokeColor(textObject, &r, &g, &b, &a) != 0;
        var stroke = new[] { r, g, b, a };
        return new TextFacts
        {
            Font = Fpdf.FPDFTextObj_GetFont(textObject),
            Size = size,
            Matrix = matrix,
            MatrixRead = matrixRead,
            Mode = Fpdf.FPDFTextObj_GetTextRenderMode(textObject),
            Fill = fill,
            FillRead = fillRead,
            Stroke = stroke,
            StrokeRead = strokeRead,
        };
    }

    /// <summary>Reads the characters <paramref name="from"/> up to (not including) <paramref name="to"/> into the table.</summary>
    public static void ReadChars(nint textPage, CharTable table, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            // A hyphen at the end of a line comes as the marker U+0002; it is a hyphen glyph, written and read back as one.
            table.Unicode[i] = Fpdf.FPDFText_IsHyphen(textPage, i) == 1 ? 0x2D : Fpdf.FPDFText_GetUnicode(textPage, i);
            var generated = Fpdf.FPDFText_IsGenerated(textPage, i) != 0;
            table.Generated[i] = generated;
            double left = 0;
            double right = 0;
            double bottom = 0;
            double top = 0;
            table.BoxRead[i] = Fpdf.FPDFText_GetCharBox(textPage, i, &left, &right, &bottom, &top) != 0;
            table.Left[i] = left;
            table.Right[i] = right;
            table.Bottom[i] = bottom;
            table.Top[i] = top;
            double x = 0;
            double y = 0;
            _ = Fpdf.FPDFText_GetCharOrigin(textPage, i, &x, &y);
            table.OriginX[i] = x;
            table.OriginY[i] = y;
            table.TextObject[i] = generated ? 0 : Fpdf.FPDFText_GetTextObject(textPage, i);
        }
    }

    /// <summary>The bounds of a page object in user space.</summary>
    public static bool TryGetBounds(nint pageObject, out UserRect bounds)
    {
        float left = 0;
        float bottom = 0;
        float right = 0;
        float top = 0;
        var ok = Fpdf.FPDFPageObj_GetBounds(pageObject, &left, &bottom, &right, &top) != 0;
        bounds = new UserRect(left, bottom, right, top);
        return ok && float.IsFinite(left) && float.IsFinite(bottom) && float.IsFinite(right) && float.IsFinite(top);
    }

    /// <summary>
    /// Creates a new text object for every run of surviving characters (the same font object, size, mode and colours; the position
    /// and spacing come from the character origins, which carries <c>TJ</c> adjustments, <c>Tc</c>, <c>Tw</c> and <c>Tz</c>). The
    /// objects are added to <paramref name="created"/> as they are made so that the caller frees them if this fails.
    /// </summary>
    /// <returns><see langword="null"/> when all runs were created, else why not.</returns>
    public static string? CreateRuns(nint nativeDocument, TextFacts facts, CharTable table, IReadOnlyList<int[]> runs, List<nint> created)
    {
        var m = facts.Matrix;
        var det = ((double)m.A * m.D) - ((double)m.B * m.C);
        foreach (var run in runs)
        {
            var obj = Fpdf.FPDFPageObj_CreateTextObj(nativeDocument, facts.Font, facts.Size);
            if (obj == 0)
            {
                return "FPDFPageObj_CreateTextObj failed";
            }

            created.Add(obj);
            var text = new ushort[run.Length + 1];
            for (var i = 0; i < run.Length; i++)
            {
                text[i] = (ushort)table.Unicode[run[i]];
            }

            fixed (ushort* pointer = text)
            {
                if (Fpdf.FPDFText_SetText(obj, pointer) == 0)
                {
                    return "FPDFText_SetText failed";
                }
            }

            if (run.Length > 1)
            {
                var positions = new float[run.Length - 1];
                for (var k = 1; k < run.Length; k++)
                {
                    var vx = table.OriginX[run[k]] - table.OriginX[run[0]];
                    var vy = table.OriginY[run[k]] - table.OriginY[run[0]];
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

            var matrix = new FsMatrix { A = m.A, B = m.B, C = m.C, D = m.D, E = (float)table.OriginX[run[0]], F = (float)table.OriginY[run[0]] };
            if (Fpdf.FPDFPageObj_SetMatrix(obj, &matrix) == 0)
            {
                return "FPDFPageObj_SetMatrix failed";
            }

            if (facts.Mode > 0 && Fpdf.FPDFTextObj_SetTextRenderMode(obj, facts.Mode) == 0)
            {
                return "FPDFTextObj_SetTextRenderMode failed";
            }

            if (Fpdf.FPDFTextObj_GetTextRenderMode(obj) != facts.Mode)
            {
                return "the text render mode did not stay";
            }

            _ = Fpdf.FPDFPageObj_SetFillColor(obj, facts.Fill[0], facts.Fill[1], facts.Fill[2], facts.Fill[3]);
            _ = Fpdf.FPDFPageObj_SetStrokeColor(obj, facts.Stroke[0], facts.Stroke[1], facts.Stroke[2], facts.Stroke[3]);
        }

        return null;
    }

    /// <summary>A new opaque filled rectangle (user space) without a stroke.</summary>
    public static nint CreateFillRectangle(UserRect rect, byte red, byte green, byte blue)
    {
        var path = Fpdf.FPDFPageObj_CreateNewRect((float)rect.Left, (float)rect.Bottom, (float)(rect.Right - rect.Left), (float)(rect.Top - rect.Bottom));
        if (path == 0)
        {
            return 0;
        }

        if (Fpdf.FPDFPageObj_SetFillColor(path, red, green, blue, 255) == 0 || Fpdf.FPDFPath_SetDrawMode(path, FpdfConstants.FillModeWinding, 0) == 0)
        {
            Fpdf.FPDFPageObj_Destroy(path);
            return 0;
        }

        return path;
    }

    /// <summary>The rectangle of an annotation in user space, or <see langword="false"/> without a usable <c>/Rect</c>.</summary>
    public static bool TryReadAnnotation(nint page, int index, out UserRect rect, out int subtype, out int irt, out int popup, out int parent, ref long linkBudget, int entries)
    {
        rect = default;
        subtype = -1;
        irt = -1;
        popup = -1;
        parent = -1;
        var annotation = Fpdf.FPDFPage_GetAnnot(page, index);
        if (annotation == 0)
        {
            return false;
        }

        try
        {
            subtype = Fpdf.FPDFAnnot_GetSubtype(annotation);
            irt = PdfiumAnnotations.LinkedIndex(page, annotation, "IRT\0"u8, entries, ref linkBudget);
            popup = PdfiumAnnotations.LinkedIndex(page, annotation, "Popup\0"u8, entries, ref linkBudget);
            parent = subtype == FpdfConstants.AnnotPopup ? PdfiumAnnotations.LinkedIndex(page, annotation, "Parent\0"u8, entries, ref linkBudget) : -1;
            FsRectF value;
            if (!PdfiumAnnotations.HasKey(annotation, "Rect\0"u8) || Fpdf.FPDFAnnot_GetRect(annotation, &value) == 0
                || !PdfiumAnnotations.IsSane(value.Left) || !PdfiumAnnotations.IsSane(value.Top) || !PdfiumAnnotations.IsSane(value.Right) || !PdfiumAnnotations.IsSane(value.Bottom))
            {
                return false;
            }

            rect = new UserRect(Math.Min(value.Left, value.Right), Math.Min(value.Bottom, value.Top), Math.Max(value.Left, value.Right), Math.Max(value.Bottom, value.Top));
            return true;
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }
}

/// <summary>
/// Answers whether a box touches any of up to <see cref="PdfiumRedaction.MaxAreas"/> rectangles without comparing it with all of
/// them: the rectangles are sorted into a grid over their bounds (a rectangle that covers many cells stays in a list every
/// query scans). Without it a page with 40,000 characters and 10,000 rectangles costs 400 million comparisons per pass.
/// </summary>
internal sealed class AreaIndex
{
    private const int Grid = 32;
    private const int MaxCellsPerArea = 64;
    private const int LinearBelow = 8;

    private readonly UserRect[] areas;
    private readonly List<int>[]? cells;
    private readonly List<int> big = [];
    private readonly double minX;
    private readonly double minY;
    private readonly double maxX;
    private readonly double maxY;
    private readonly double cellWidth;
    private readonly double cellHeight;

    public AreaIndex(List<UserRect> areas)
    {
        this.areas = [.. areas];
        Count = this.areas.Length;
        if (Count < LinearBelow)
        {
            return;
        }

        minX = areas.Min(static a => a.Left);
        minY = areas.Min(static a => a.Bottom);
        maxX = areas.Max(static a => a.Right);
        maxY = areas.Max(static a => a.Top);
        cellWidth = Math.Max((maxX - minX) / Grid, 1e-9);
        cellHeight = Math.Max((maxY - minY) / Grid, 1e-9);
        cells = new List<int>[Grid * Grid];
        for (var i = 0; i < this.areas.Length; i++)
        {
            var (x0, x1) = Range(this.areas[i].Left, this.areas[i].Right, minX, cellWidth);
            var (y0, y1) = Range(this.areas[i].Bottom, this.areas[i].Top, minY, cellHeight);
            if (((x1 - x0) + 1) * ((y1 - y0) + 1) > MaxCellsPerArea)
            {
                big.Add(i);
                continue;
            }

            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    (cells[(y * Grid) + x] ??= []).Add(i);
                }
            }
        }
    }

    /// <summary>The number of rectangles.</summary>
    public int Count { get; }

    /// <summary>Whether the box touches a rectangle (an edge or a corner is enough).</summary>
    public bool Hit(UserRect box)
    {
        if (cells is null)
        {
            foreach (var area in areas)
            {
                if (box.Touches(area))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var i in big)
        {
            if (box.Touches(areas[i]))
            {
                return true;
            }
        }

        if (box.Right < minX - UserRect.TouchSlack || box.Left > maxX + UserRect.TouchSlack || box.Top < minY - UserRect.TouchSlack || box.Bottom > maxY + UserRect.TouchSlack)
        {
            return false;
        }

        var (x0, x1) = Range(box.Left - UserRect.TouchSlack, box.Right + UserRect.TouchSlack, minX, cellWidth);
        var (y0, y1) = Range(box.Bottom - UserRect.TouchSlack, box.Top + UserRect.TouchSlack, minY, cellHeight);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var cell = cells[(y * Grid) + x];
                if (cell is null)
                {
                    continue;
                }

                foreach (var i in cell)
                {
                    if (box.Touches(areas[i]))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static (int From, int To) Range(double low, double high, double origin, double size)
    {
        var from = (int)Math.Clamp(Math.Floor((low - origin) / size), 0, Grid - 1);
        var to = (int)Math.Clamp(Math.Floor((high - origin) / size), 0, Grid - 1);
        return (from, Math.Max(from, to));
    }
}
