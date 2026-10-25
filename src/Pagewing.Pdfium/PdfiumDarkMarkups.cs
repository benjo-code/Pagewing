using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// Paints the highlights, underlines and strike-outs of a page on its dark render (<see cref="PdfiumPageColors"/>), so
/// they stay visible in their own colour. PDFium's colour scheme cannot do that: with <c>FPDF_CONVERT_FILL_TO_STROKE</c>
/// the filled quadrilateral of a highlight becomes an outline in the foreground, multiplied with the dark ground
/// (<c>#1A1D23</c> on <c>#1E2127</c>, invisible), and the line of an underline or strike-out takes the foreground colour
/// like the text. After PDFium has rendered, a highlight's area takes a tint of its colour behind the text (the ground
/// becomes the tint, the light text stays, the counterpart of the multiplication on a light page), and the line of an
/// underline or strike-out is painted over every pixel PDFium's own appearance touched, opaque in its colour. Measurement
/// and rules: <c>Docs/DECISIONS.md</c>, entry "Markierungen auf der dunklen Seite und im Druck (M9)".
/// </summary>
/// <remarks>
/// The lines lie where PDFium's appearance generator (<c>CPDF_GenerateAP</c>, chromium/8100) draws them: on the bounding
/// rectangle of a quadrilateral in PDF space, 1 pt wide, an underline 1 pt above its bottom, a strike-out through its
/// middle. Another program's appearance may draw elsewhere; its line then stays in the page foreground beside the painted
/// one. The reading runs on the PDFium thread at the start of every dark render (no cache: an edit needs no invalidation),
/// at most <see cref="PdfiumAnnotations.MaxScannedPerPage"/> entries, <see cref="MaxTotalQuads"/> quadrilaterals and
/// <see cref="MaxAppearanceBytes"/>; painting covers the bitmap at most <see cref="MaxPaintedLayers"/> times. Positions are
/// converted in double precision (<see cref="DisplayTransform"/>), so the lines stay on PDFium's at any zoom. An annotation
/// hidden by optional content (<c>/OC</c>) is still painted: PDFium's API does not tell whether its group is on.
/// </remarks>
internal static unsafe class PdfiumDarkMarkups
{
    /// <summary>The width of the line of PDFium's underline and strike-out appearance, in points (<c>kLineWidth</c>).</summary>
    public const double LineWidth = 1;

    /// <summary>The contrast a line keeps against the dark ground (WCAG 2.2, non-text contrast).</summary>
    public const double LineContrast = 3;

    /// <summary>The contrast the page text keeps on the tint of a highlight (WCAG 2.2, text, as on <c>search.hitOnDark</c>).</summary>
    public const double TextContrast = 4.5;

    /// <summary>The highest share of the highlight colour in its tint, in hundredths; it is lowered until the text keeps <see cref="TextContrast"/>.</summary>
    public const int MaxFillPercent = 40;

    /// <summary>How far a too dark line colour is moved towards white per step, in 32nds.</summary>
    private const int LightenSteps = 32;

    /// <summary>The most quadrilaterals read from one page in one read, across all its markups.</summary>
    public const int MaxTotalQuads = 65_536;

    /// <summary>The most bytes of appearance strings decoded for colours in one read of a page (UTF-16LE, as PDFium reports them).</summary>
    public const long MaxAppearanceBytes = 8L * 1024 * 1024;

    /// <summary>How often the painted pixels may cover the bitmap in one paint; further markups are left out.</summary>
    public const int MaxPaintedLayers = 16;

    /// <summary>How far, in pixels, the edges of a line are moved out before the pixels it touches are taken.</summary>
    public const double EdgeSlack = 1.0 / 16;

    /// <summary>The colour PDFium's generator gives a highlight without <c>/C</c>: yellow.</summary>
    private const uint DefaultHighlight = 0xFFFFFF00;

    /// <summary>The colour PDFium's generator gives an underline or strike-out without <c>/C</c>: black.</summary>
    private const uint DefaultLine = 0xFF000000;

    /// <summary>
    /// Reads the visible highlights, underlines and strike-outs of the page (not hidden, not "no view") with the colours
    /// they get on the dark page. Must be called on the PDFium thread, before the page is rendered. Reading stops, keeping
    /// what it has, once <see cref="MaxTotalQuads"/> quadrilaterals or <see cref="MaxAppearanceBytes"/> of appearance
    /// strings are used up (a hostile page may list the same large markup thousands of times).
    /// </summary>
    public static DarkMarkupPaint Read(PdfiumPage page, PdfiumPageColors colors)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var count = Math.Min(Math.Max(0, Fpdf.FPDFPage_GetAnnotCount(native)), PdfiumAnnotations.MaxScannedPerPage);
        if (count == 0 || !DisplayTransform.TryCreate(native, page.Width, page.Height, out var transform))
        {
            return DarkMarkupPaint.Empty;
        }

        var areas = new List<DarkMarkupArea>();
        var bands = new List<DarkMarkupBand>();
        long quadBudget = MaxTotalQuads;
        long appearanceBudget = MaxAppearanceBytes;
        // Rented on the first appearance that is decoded, returned when the read ends (also by an exception).
        char[]? scratch = null;
        try
        {
            for (var i = 0; i < count && quadBudget > 0 && appearanceBudget > 0; i++)
            {
                var annotation = Fpdf.FPDFPage_GetAnnot(native, i);
                if (annotation == 0)
                {
                    continue;
                }

                try
                {
                    var subtype = Fpdf.FPDFAnnot_GetSubtype(annotation);
                    if (!PdfiumAnnotations.IsMarkup(subtype)
                        || (Fpdf.FPDFAnnot_GetFlags(annotation) & (FpdfConstants.AnnotFlagHidden | FpdfConstants.AnnotFlagNoView)) != 0)
                    {
                        continue;
                    }

                    var quadCount = Fpdf.FPDFAnnot_CountAttachmentPoints(annotation);
                    if (quadCount == 0 || quadCount > PdfiumAnnotations.MaxQuads)
                    {
                        continue;
                    }

                    var highlight = subtype == FpdfConstants.AnnotHighlight;
                    var rgb = PdfiumAnnotations.ReadColor(annotation, subtype, ref scratch, ref appearanceBudget) is { } c
                        ? 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B
                        : highlight ? DefaultHighlight : DefaultLine;
                    var color = highlight ? FillTint(rgb, colors.Background, colors.Foreground) : LineColor(rgb, colors.Background);
                    for (nuint q = 0; q < quadCount && quadBudget > 0; q++)
                    {
                        quadBudget--;
                        FsQuadPointsF points;
                        if (Fpdf.FPDFAnnot_GetAttachmentPoints(annotation, q, &points) == 0 || !PdfiumAnnotations.IsSane(points))
                        {
                            continue;
                        }

                        if (highlight)
                        {
                            areas.Add(AreaOf(transform, points, color));
                            continue;
                        }

                        // PDFium's generator: the bounding rectangle in PDF space, the line 1 pt above its bottom or through its middle.
                        var left = Math.Min(Math.Min(points.X1, points.X2), Math.Min(points.X3, points.X4));
                        var right = Math.Max(Math.Max(points.X1, points.X2), Math.Max(points.X3, points.X4));
                        var bottom = Math.Min(Math.Min(points.Y1, points.Y2), Math.Min(points.Y3, points.Y4));
                        var top = Math.Max(Math.Max(points.Y1, points.Y2), Math.Max(points.Y3, points.Y4));
                        var y = subtype == FpdfConstants.AnnotUnderline ? bottom + LineWidth : (top + bottom) / 2;
                        var a = transform.ToDisplay(left, y - (LineWidth / 2));
                        var b = transform.ToDisplay(right, y + (LineWidth / 2));
                        bands.Add(new DarkMarkupBand(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), color));
                    }
                }
                finally
                {
                    Fpdf.FPDFPage_CloseAnnot(annotation);
                }
            }
        }
        finally
        {
            PdfiumAnnotations.ReturnScratch(ref scratch);
        }

        return areas.Count == 0 && bands.Count == 0 ? DarkMarkupPaint.Empty : new DarkMarkupPaint(areas, bands);
    }

    /// <summary>
    /// The area of a highlight's quadrilateral, whatever order its corners have in the file (Acrobat writes top left, top
    /// right, bottom left, bottom right; ISO 32000 describes them counter-clockwise): the convex hull of the four corners,
    /// in order round it. Corners on one line (no area) give their bounding box.
    /// </summary>
    internal static DarkMarkupArea AreaOf(DisplayTransform transform, FsQuadPointsF points, uint tint)
    {
        Span<DarkMarkupPoint> corners =
        [
            transform.ToDisplay(points.X1, points.Y1),
            transform.ToDisplay(points.X2, points.Y2),
            transform.ToDisplay(points.X3, points.Y3),
            transform.ToDisplay(points.X4, points.Y4),
        ];
        Span<DarkMarkupPoint> hull = stackalloc DarkMarkupPoint[8];
        var size = ConvexHull(corners, hull);
        if (size >= 3)
        {
            // A triangle repeats its last corner; the repeated edge has no length and changes nothing in Inside.
            return new DarkMarkupArea(hull[0], hull[1], hull[2], hull[size == 4 ? 3 : 2], tint);
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var corner in corners)
        {
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }

        return new DarkMarkupArea(new(minX, minY), new(maxX, minY), new(maxX, maxY), new(minX, maxY), tint);
    }

    /// <summary>Andrew's monotone chain on a few points; writes the hull counter-clockwise without repeating a corner and returns its size.</summary>
    private static int ConvexHull(Span<DarkMarkupPoint> points, Span<DarkMarkupPoint> hull)
    {
        points.Sort(static (a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        var size = 0;
        for (var i = 0; i < points.Length; i++)
        {
            while (size >= 2 && Cross(hull[size - 2], hull[size - 1], points[i]) <= 0)
            {
                size--;
            }

            hull[size++] = points[i];
        }

        var lower = size + 1;
        for (var i = points.Length - 2; i >= 0; i--)
        {
            while (size >= lower && Cross(hull[size - 2], hull[size - 1], points[i]) <= 0)
            {
                size--;
            }

            hull[size++] = points[i];
        }

        // The last point is the first again.
        return Math.Max(0, size - 1);

        static double Cross(DarkMarkupPoint o, DarkMarkupPoint a, DarkMarkupPoint b) => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));
    }

    /// <summary>
    /// Paints the markups into a rendered BGRx bitmap. A point (page points from the top left) lies at
    /// <c>x * scaleX + offsetX</c>, <c>y * scaleY + offsetY</c> pixels of the bitmap, the mapping PDFium rendered with.
    /// </summary>
    public static void Apply(
        DarkMarkupPaint paint,
        PdfiumPageColors colors,
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY)
    {
        ArgumentNullException.ThrowIfNull(paint);

        // However many markups overlap, the work stays a small multiple of the bitmap.
        var budget = MaxPaintedLayers * width * (long)height;
        foreach (var area in paint.Areas)
        {
            if (!FillArea(pixels, width, height, stride, area, colors, scaleX, scaleY, offsetX, offsetY, ref budget))
            {
                break;
            }
        }

        // The lines last: a strike-out through a highlight stays a line.
        foreach (var band in paint.Bands)
        {
            if (!FillBand(pixels, width, height, stride, band, scaleX, scaleY, offsetX, offsetY, ref budget))
            {
                break;
            }
        }
    }

    /// <summary>
    /// The colour of a line on the dark ground (ARGB): the annotation's colour if it reaches <see cref="LineContrast"/>
    /// against <paramref name="background"/>, otherwise the first step towards white that does, so the hue stays.
    /// </summary>
    public static uint LineColor(uint rgb, uint background)
    {
        for (var step = 0; step <= LightenSteps; step++)
        {
            var color = Mix(rgb, 0xFFFFFFFF, step, LightenSteps);
            if (Contrast(color, background) >= LineContrast)
            {
                return color;
            }
        }

        return 0xFFFFFFFF;
    }

    /// <summary>
    /// The tint of a highlight on the dark ground (ARGB, opaque): its line colour (<see cref="LineColor"/>, so a dark
    /// colour still shows) over <paramref name="background"/> with the highest share up to <see cref="MaxFillPercent"/>
    /// on which <paramref name="foreground"/> (the page text) keeps <see cref="TextContrast"/>.
    /// </summary>
    public static uint FillTint(uint rgb, uint background, uint foreground)
    {
        var color = LineColor(rgb, background);
        for (var percent = MaxFillPercent; percent > 0; percent--)
        {
            var tint = Mix(background, color, percent, 100);
            if (Contrast(foreground, tint) >= TextContrast)
            {
                return tint;
            }
        }

        return background | 0xFF000000;
    }

    private static bool FillArea(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        DarkMarkupArea area,
        PdfiumPageColors colors,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY,
        ref long budget)
    {
        Span<(double X, double Y)> corners =
        [
            ((area.P1.X * scaleX) + offsetX, (area.P1.Y * scaleY) + offsetY),
            ((area.P2.X * scaleX) + offsetX, (area.P2.Y * scaleY) + offsetY),
            ((area.P3.X * scaleX) + offsetX, (area.P3.Y * scaleY) + offsetY),
            ((area.P4.X * scaleX) + offsetX, (area.P4.Y * scaleY) + offsetY),
        ];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in corners)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var x0 = (int)Math.Clamp(Math.Floor(minX), 0, width);
        var x1 = (int)Math.Clamp(Math.Ceiling(maxX), 0, width);
        var y0 = (int)Math.Clamp(Math.Floor(minY), 0, height);
        var y1 = (int)Math.Clamp(Math.Ceiling(maxY), 0, height);
        if (!Charge(ref budget, x0, x1, y0, y1))
        {
            return false;
        }

        var tint = Channels(area.Tint);
        var ground = Channels(colors.Background);
        var foreground = Channels(colors.Foreground);
        var groundSum = ground.B + ground.G + ground.R;
        var span = Math.Max(1, foreground.B + foreground.G + foreground.R - groundSum);
        for (var y = y0; y < y1; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (var x = x0; x < x1; x++)
            {
                // The pixel centre decides, as for any filled path.
                if (!Inside(corners, x + 0.5, y + 0.5))
                {
                    continue;
                }

                // How much of the light foreground the pixel holds (text and its smoothed edges; 0 on the ground and on
                // the dark outline PDFium leaves of the highlight): the ground under it is replaced by the tint, the rest stays.
                var pixel = row.Slice(x * 4, 4);
                var share = Math.Clamp((pixel[0] + pixel[1] + pixel[2] - groundSum) / (double)span, 0, 1);
                if (share == 0)
                {
                    pixel[0] = (byte)tint.B;
                    pixel[1] = (byte)tint.G;
                    pixel[2] = (byte)tint.R;
                    continue;
                }

                var rest = 1 - share;
                pixel[0] = Shift(pixel[0], tint.B - ground.B, rest);
                pixel[1] = Shift(pixel[1], tint.G - ground.G, rest);
                pixel[2] = Shift(pixel[2], tint.R - ground.R, rest);
            }
        }

        return true;

        static byte Shift(byte value, int delta, double rest) => (byte)Math.Clamp((int)Math.Round(value + (delta * rest)), 0, 255);
    }

    /// <summary>Takes the pixels of a clipped rectangle from the budget; <see langword="false"/> once it is used up.</summary>
    private static bool Charge(ref long budget, int x0, int x1, int y0, int y1)
    {
        var cost = Math.Max(0, x1 - x0) * (long)Math.Max(0, y1 - y0);
        if (cost > budget)
        {
            budget = 0;
            return false;
        }

        budget -= cost;
        return true;
    }

    private static (int B, int G, int R) Channels(uint argb) => ((int)(argb & 0xFF), (int)((argb >> 8) & 0xFF), (int)((argb >> 16) & 0xFF));

    private static bool FillBand(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        DarkMarkupBand band,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY,
        ref long budget)
    {
        var (left, right) = Widen((band.Left * scaleX) + offsetX, (band.Right * scaleX) + offsetX);
        var (top, bottom) = Widen((band.Top * scaleY) + offsetY, (band.Bottom * scaleY) + offsetY);

        // Every pixel the line touches, so nothing of PDFium's light line (with its smoothed edges) is left beside it. An edge
        // that falls on a pixel border may still leave a trace of PDFium's line in the pixel before it (seen at 32 px/pt):
        // EdgeSlack takes that pixel too.
        var x0 = (int)Math.Clamp(Math.Floor(left - EdgeSlack), 0, width);
        var x1 = (int)Math.Clamp(Math.Ceiling(right + EdgeSlack), 0, width);
        var y0 = (int)Math.Clamp(Math.Floor(top - EdgeSlack), 0, height);
        var y1 = (int)Math.Clamp(Math.Ceiling(bottom + EdgeSlack), 0, height);
        if (!Charge(ref budget, x0, x1, y0, y1))
        {
            return false;
        }

        ReadOnlySpan<byte> color = [(byte)band.Color, (byte)(band.Color >> 8), (byte)(band.Color >> 16), 0xFF];
        for (var y = y0; y < y1; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            for (var x = x0; x < x1; x++)
            {
                color.CopyTo(row.Slice(x * 4, 4));
            }
        }

        return true;

        // PDFium draws a line thinner than a pixel one pixel wide, around its middle.
        static (double Low, double High) Widen(double low, double high)
        {
            if (high - low >= 1)
            {
                return (low, high);
            }

            var middle = (low + high) / 2;
            return (middle - 0.5, middle + 0.5);
        }
    }

    /// <summary>Whether a point lies in the convex quadrilateral (either winding; its edges count as inside).</summary>
    private static bool Inside(ReadOnlySpan<(double X, double Y)> corners, double x, double y)
    {
        var positive = false;
        var negative = false;
        for (var i = 0; i < corners.Length; i++)
        {
            var (ax, ay) = corners[i];
            var (bx, by) = corners[(i + 1) % corners.Length];
            var cross = ((bx - ax) * (y - ay)) - ((by - ay) * (x - ax));
            positive |= cross > 0;
            negative |= cross < 0;
        }

        return !(positive && negative);
    }

    /// <summary><paramref name="from"/> moved <paramref name="part"/>/<paramref name="whole"/> of the way to <paramref name="to"/>, opaque.</summary>
    private static uint Mix(uint from, uint to, int part, int whole)
    {
        static uint Channel(uint a, uint b, int part, int whole) =>
            (uint)Math.Round(((a * (double)(whole - part)) + (b * (double)part)) / whole);

        var r = Channel((from >> 16) & 0xFF, (to >> 16) & 0xFF, part, whole);
        var g = Channel((from >> 8) & 0xFF, (to >> 8) & 0xFF, part, whole);
        var b = Channel(from & 0xFF, to & 0xFF, part, whole);
        return 0xFF000000u | (r << 16) | (g << 8) | b;
    }

    /// <summary>The contrast ratio of WCAG 2.2.</summary>
    private static double Contrast(uint a, uint b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(uint argb)
    {
        static double Linear(uint channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear((argb >> 16) & 0xFF)) + (0.7152 * Linear((argb >> 8) & 0xFF)) + (0.0722 * Linear(argb & 0xFF));
    }
}

/// <summary>A point in page points from the top left of the displayed page.</summary>
internal readonly record struct DarkMarkupPoint(double X, double Y);

/// <summary>The quadrilateral of a highlight (corners in order round it) and its tint (ARGB).</summary>
internal readonly record struct DarkMarkupArea(DarkMarkupPoint P1, DarkMarkupPoint P2, DarkMarkupPoint P3, DarkMarkupPoint P4, uint Tint);

/// <summary>The line of an underline or strike-out (page points from the top left) and its colour (ARGB).</summary>
internal readonly record struct DarkMarkupBand(double Left, double Top, double Right, double Bottom, uint Color);

/// <summary>What <see cref="PdfiumDarkMarkups.Apply"/> paints on one dark render.</summary>
internal sealed class DarkMarkupPaint(IReadOnlyList<DarkMarkupArea> areas, IReadOnlyList<DarkMarkupBand> bands)
{
    /// <summary>Gets a page without markups.</summary>
    public static DarkMarkupPaint Empty { get; } = new([], []);

    /// <summary>Gets the areas of the highlights.</summary>
    public IReadOnlyList<DarkMarkupArea> Areas { get; } = areas;

    /// <summary>Gets the lines of the underlines and strike-outs.</summary>
    public IReadOnlyList<DarkMarkupBand> Bands { get; } = bands;

    /// <summary>Gets whether there is nothing to paint.</summary>
    public bool IsEmpty => Areas.Count == 0 && Bands.Count == 0;
}

/// <summary>
/// Converts between PDF user space and points from the top left of the displayed page in double precision. The affine map
/// comes from three corners of the page through <c>FPDF_DeviceToPage</c> (which returns doubles), inverted here for
/// <see cref="ToDisplay"/>; PDFium stays the only source of rotation and CropBox, and no coordinate is rounded to a grid as
/// with <c>FPDF_PageToDevice</c>. <see cref="ToUser"/> is the map itself, so a point converted there and back stays the
/// same up to double rounding (drawings, M10).
/// </summary>
internal readonly struct DisplayTransform
{
    private const int DeviceScale = 16;

    private readonly double m11;
    private readonly double m12;
    private readonly double m21;
    private readonly double m22;
    private readonly double originX;
    private readonly double originY;

    // The map from display points to user space: user = origin + displayX * (ax, ay) + displayY * (bx, by).
    private readonly double ax;
    private readonly double ay;
    private readonly double bx;
    private readonly double by;

    private DisplayTransform(double ax, double ay, double bx, double by, double determinant, double originX, double originY)
    {
        m11 = by / determinant;
        m12 = -bx / determinant;
        m21 = -ay / determinant;
        m22 = ax / determinant;
        this.ax = ax;
        this.ay = ay;
        this.bx = bx;
        this.by = by;
        this.originX = originX;
        this.originY = originY;
    }

    /// <summary>Gets the transform that keeps every point (for tests of the geometry).</summary>
    public static DisplayTransform Identity { get; } = new(1, 0, 0, 1, 1, 0, 0);

    /// <summary>Builds the transform of a loaded page (PDFium thread); <see langword="false"/> when PDFium cannot convert.</summary>
    public static unsafe bool TryCreate(nint page, double width, double height, out DisplayTransform transform)
    {
        transform = default;
        var w = (int)Math.Round(width * DeviceScale);
        var h = (int)Math.Round(height * DeviceScale);
        double x0, y0, x1, y1, x2, y2;
        if (w <= 0 || h <= 0
            || Fpdf.FPDF_DeviceToPage(page, 0, 0, w, h, 0, 0, 0, &x0, &y0) == 0
            || Fpdf.FPDF_DeviceToPage(page, 0, 0, w, h, 0, w, 0, &x1, &y1) == 0
            || Fpdf.FPDF_DeviceToPage(page, 0, 0, w, h, 0, 0, h, &x2, &y2) == 0)
        {
            return false;
        }

        // user = origin + (displayX / width) * (corner1 - origin) + (displayY / height) * (corner2 - origin); inverted.
        var ax = (x1 - x0) / width;
        var ay = (y1 - y0) / width;
        var bx = (x2 - x0) / height;
        var by = (y2 - y0) / height;
        var determinant = (ax * by) - (bx * ay);
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
        {
            return false;
        }

        transform = new DisplayTransform(ax, ay, bx, by, determinant, x0, y0);
        return true;
    }

    /// <summary>Converts a point of PDF user space to points from the top left of the displayed page.</summary>
    public DarkMarkupPoint ToDisplay(double x, double y)
    {
        var dx = x - originX;
        var dy = y - originY;
        return new DarkMarkupPoint((m11 * dx) + (m12 * dy), (m21 * dx) + (m22 * dy));
    }

    /// <summary>Converts a point in points from the top left of the displayed page to PDF user space (the inverse of <see cref="ToDisplay"/>).</summary>
    public (double X, double Y) ToUser(double x, double y) =>
        (originX + (ax * x) + (bx * y), originY + (ay * x) + (by * y));
}
