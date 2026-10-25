using System.Buffers;

namespace Pagewing.Pdfium;

/// <summary>
/// Paints the drawings of a page (ink, square, circle) on its dark render in their own colours. PDFium's colour scheme
/// draws every stroke in the page foreground, the colour of the text, so a drawing loses its colour. After PDFium has
/// rendered, every pixel the stroke's geometry touches gets the drawing's colour (lightened to 3:1 against the dark ground
/// like the lines of the markups, <see cref="PdfiumDarkMarkups.LineColor"/>) in the share of the light foreground PDFium
/// put there: a pixel PDFium covered fully takes the colour, a smoothed edge keeps its share, and a pixel PDFium left as the
/// ground stays the ground. Measurement and rules: <c>Docs/DECISIONS.md</c>, entry "Zeichnungen auf der dunklen Seite und
/// im Druck (M10)".
/// </summary>
/// <remarks>
/// <para>
/// The geometry is the one PDFium's appearance generator (<c>CPDF_GenerateAP</c>, chromium/8100) strokes and its AGG
/// stroker fills: an ink is each stroke of <c>/InkList</c> as a polyline, a square the rectangle <c>/Rect</c> inset by half
/// the width, a circle four Bézier arcs (control points at 0.5523 of the half axes) in that inset rectangle; constant width,
/// butt caps, miter joins up to the miter limit 10 and clipped beyond it, at least one pixel wide. Covered are the
/// rectangles of the segments and, at each join, the miter (clipped at the limit, which also covers a bevel) and the inner
/// triangle. A pixel counts when its square, widened by <see cref="PdfiumDarkMarkups.EdgeSlack"/>, meets one of these
/// convex pieces (exact per row: the piece is clipped to the row's band).
/// </para>
/// <para>
/// The light text under a stroke has the stroke's own colour in PDFium's render (both are the foreground), so a stroke over
/// text takes the colour over the text as well, as on the light page, where the opaque stroke hides it; text the geometry
/// does not touch stays as it is. Over an image the share of a smoothed edge is estimated against the dark ground and may
/// be off there. Another program's appearance may draw differently (round caps, curves); what lies outside the geometry
/// stays in the foreground.
/// </para>
/// <para>
/// Drawings are painted from the last in <c>/Annots</c> (drawn on top) to the first; a painted pixel is marked in the unused
/// fourth byte of the BGRx bitmap until the end of <see cref="Apply"/>, so the drawing under it and the overlapping pieces of
/// one drawing leave it alone. Reading runs with the markups (<see cref="PdfiumDarkMarkups.Read"/>), at most
/// <see cref="MaxTotalPoints"/> points per page; painting shares the budget of <see cref="PdfiumDarkMarkups.MaxPaintedLayers"/>.
/// </para>
/// </remarks>
internal static class PdfiumDarkDrawings
{
    /// <summary>The most points of drawings read from one page in one read; an ink that does not fit is read only as far as it does.</summary>
    public const int MaxTotalPoints = 1_000_000;

    /// <summary>The miter limit of PDFium's appearances: the generator writes no <c>M</c>, so the PDF default applies.</summary>
    public const double MiterLimit = 10;

    /// <summary>The share of a half axis at which PDFium's generator puts the control points of a quarter ellipse.</summary>
    public const double Kappa = 0.5523;

    /// <summary>How far, in pixels, a flattened arc may lie inside the curve; the stroke is widened by this much.</summary>
    private const double CurveTolerance = 0.1;

    /// <summary>The fourth byte of a pixel painted in this paint (PDFium leaves 0xFF there); reset before <see cref="Apply"/> returns.</summary>
    private const byte PaintedMark = 0xFE;

    /// <summary>The colour PDFium's generator gives a drawing without <c>/C</c>: black.</summary>
    private const uint DefaultColor = 0xFF000000;

    /// <summary>
    /// Reads the drawing at <paramref name="index"/> (whose handle the caller holds open as <paramref name="annotation"/>)
    /// with the colour it gets on the dark page, charging its points to <paramref name="pointBudget"/>; <see langword="null"/>
    /// when nothing of it can be read within the budget. PDFium thread, before the render.
    /// </summary>
    public static DarkDrawing? Read(
        PdfiumPage page,
        int index,
        nint annotation,
        int subtype,
        DisplayTransform transform,
        PdfiumPageColors colors,
        ref long pointBudget,
        ref char[]? scratch,
        ref long appearanceBudget)
    {
        var limit = (int)Math.Min(pointBudget, PdfiumDrawings.MaxInkPoints);
        if (PdfiumDrawings.ReadGeometry(page, index, transform, limit) is not { } drawing || !double.IsFinite(drawing.Width) || drawing.Width < 0)
        {
            return null;
        }

        var rgb = PdfiumAnnotations.ReadColor(annotation, subtype, ref scratch, ref appearanceBudget) is { } c
            ? 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B
            : DefaultColor;
        var color = PdfiumDarkMarkups.LineColor(rgb, colors.Background);
        var half = drawing.Width / 2;
        var (left, top, right, bottom) = drawing.Box;
        switch (drawing.Kind)
        {
            case PdfiumDrawingKind.Square:
                pointBudget -= 4;
                (double X, double Y)[] corners = [(left + half, top + half), (right - half, top + half), (right - half, bottom - half), (left + half, bottom - half)];
                return new DarkDrawing(color, drawing.Width, [new DarkDrawingPath(corners, Closed: true, Curved: false)]);
            case PdfiumDrawingKind.Circle:
                pointBudget -= 13;
                return new DarkDrawing(color, drawing.Width, [new DarkDrawingPath(Ellipse(left + half, top + half, right - half, bottom - half), Closed: true, Curved: true)]);
        }

        var paths = new DarkDrawingPath[drawing.Strokes.Count];
        for (var s = 0; s < paths.Length; s++)
        {
            pointBudget -= drawing.Strokes[s].Count;
            paths[s] = new DarkDrawingPath(drawing.Strokes[s], Closed: false, Curved: false);
        }

        return new DarkDrawing(color, drawing.Width, paths);
    }

    /// <summary>
    /// The four Bézier arcs of PDFium's ellipse in a rectangle: the start, then three points (two controls, the end) per arc.
    /// </summary>
    internal static (double X, double Y)[] Ellipse(double left, double top, double right, double bottom)
    {
        var (mx, my) = ((left + right) / 2, (top + bottom) / 2);
        var (hw, hh) = ((right - left) / 2 * Kappa, (bottom - top) / 2 * Kappa);
        return
        [
            (mx, top),
            (mx + hw, top), (right, my - hh), (right, my),
            (right, my + hh), (mx + hw, bottom), (mx, bottom),
            (mx - hw, bottom), (left, my + hh), (left, my),
            (left, my - hh), (mx - hw, top), (mx, top),
        ];
    }

    /// <summary>
    /// Paints the drawings into a rendered BGRx bitmap with the mapping PDFium rendered with (see
    /// <see cref="PdfiumDarkMarkups.Apply"/>), charging every pixel looked at to <paramref name="budget"/>; stops, keeping
    /// what is painted, once it is used up.
    /// </summary>
    /// <returns><see langword="false"/> when the budget ran out.</returns>
    public static bool Apply(
        IReadOnlyList<DarkDrawing> drawings,
        PdfiumPageColors colors,
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY,
        ref long budget)
    {
        ArgumentNullException.ThrowIfNull(drawings);
        if (drawings.Count == 0)
        {
            return true;
        }

        var painter = new Painter(pixels, width, height, stride, scaleX, scaleY, offsetX, offsetY, colors, budget);
        (double X, double Y)[]? buffer = null;
        try
        {
            // PDFium draws in /Annots order; the last one lies on top and is painted first.
            for (var d = drawings.Count - 1; d >= 0; d--)
            {
                var drawing = drawings[d];
                painter.Begin(drawing.Color);

                // At least one pixel wide, as PDFium strokes (in points, so the stroke is still widened like PDFium's).
                var half = Math.Max(drawing.Width, 1 / Math.Min(scaleX, scaleY)) / 2;
                foreach (var path in drawing.Paths)
                {
                    var points = Compact(path, scaleX, scaleY, ref buffer, out var curveSlack);
                    if (!StrokePolyline(ref painter, points, path.Closed, half + curveSlack))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        finally
        {
            budget = painter.Budget;
            painter.ClearMarks();
            if (buffer is not null)
            {
                ArrayPool<(double X, double Y)>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// The points of a path without repeated points (PDFium's stroker drops them; the generator writes the first point of a
    /// stroke twice), an ellipse flattened finely enough for the scale; <paramref name="curveSlack"/> is how much wider (in
    /// points) the stroke is taken for the flattening.
    /// </summary>
    private static ReadOnlySpan<(double X, double Y)> Compact(DarkDrawingPath path, double scaleX, double scaleY, ref (double X, double Y)[]? buffer, out double curveSlack)
    {
        var source = path.Points;
        curveSlack = 0;
        var steps = 1;
        if (path.Curved)
        {
            // A quarter of an ellipse of radius r pixels in n chords lies at most r (pi / 2n)^2 / 2 inside it: 2 sqrt(r) chords keep that below 0.08 px.
            double radius = 0;
            for (var i = 1; i < source.Count; i++)
            {
                radius = Math.Max(radius, Math.Max(Math.Abs(source[i].X - source[0].X) * scaleX, Math.Abs(source[i].Y - source[0].Y) * scaleY));
            }

            steps = (int)Math.Clamp(Math.Ceiling(2 * Math.Sqrt(radius)), 4, 1024);
            curveSlack = CurveTolerance / Math.Min(scaleX, scaleY);
        }

        var needed = path.Curved ? (((source.Count - 1) / 3) * steps) + 1 : source.Count;
        if (buffer is null || buffer.Length < needed)
        {
            if (buffer is not null)
            {
                ArrayPool<(double X, double Y)>.Shared.Return(buffer);
            }

            buffer = ArrayPool<(double X, double Y)>.Shared.Rent(needed);
        }

        var count = 0;
        void Add((double X, double Y) point, (double X, double Y)[] target)
        {
            if (count == 0 || target[count - 1] != point)
            {
                target[count++] = point;
            }
        }

        Add(source[0], buffer);
        if (path.Curved)
        {
            for (var i = 1; i + 2 < source.Count; i += 3)
            {
                var (p0, p1, p2, p3) = (source[i - 1], source[i], source[i + 1], source[i + 2]);
                for (var s = 1; s <= steps; s++)
                {
                    var t = s / (double)steps;
                    var u = 1 - t;
                    var (a, b, c, e) = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t);
                    Add(((a * p0.X) + (b * p1.X) + (c * p2.X) + (e * p3.X), (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (e * p3.Y)), buffer);
                }
            }
        }
        else
        {
            for (var i = 1; i < source.Count; i++)
            {
                Add(source[i], buffer);
            }
        }

        // A closed path that ends where it starts closes there anyway.
        if (path.Closed && count > 2 && buffer[count - 1] == buffer[0])
        {
            count--;
        }

        return buffer.AsSpan(0, count);
    }

    /// <summary>Paints the rectangles of the segments and the joins of a polyline of half-width <paramref name="half"/> (points).</summary>
    private static bool StrokePolyline(ref Painter painter, scoped ReadOnlySpan<(double X, double Y)> points, bool closed, double half)
    {
        var count = points.Length;
        if (count < 2)
        {
            // A single point with butt caps draws nothing.
            return true;
        }

        closed &= count > 2;
        var segments = closed ? count : count - 1;
        Span<(double X, double Y)> polygon = stackalloc (double X, double Y)[4];
        for (var i = 0; i < segments; i++)
        {
            var (a, b) = (points[i], points[(i + 1) % count]);
            var (dx, dy) = Direction(a, b);
            var (nx, ny) = (-dy * half, dx * half);
            polygon[0] = (a.X + nx, a.Y + ny);
            polygon[1] = (b.X + nx, b.Y + ny);
            polygon[2] = (b.X - nx, b.Y - ny);
            polygon[3] = (a.X - nx, a.Y - ny);
            if (!painter.FillConvex(polygon))
            {
                return false;
            }
        }

        for (var i = closed ? 0 : 1; i < (closed ? count : count - 1); i++)
        {
            if (!Join(ref painter, points[(i + count - 1) % count], points[i], points[(i + 1) % count], half))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The join at <paramref name="v"/> between the segment from <paramref name="previous"/> and the one to
    /// <paramref name="next"/>: on the outer side the miter (AGG's <c>calc_miter</c>: up to <see cref="MiterLimit"/> times
    /// the half-width from the corner, clipped there beyond it), on the inner side the triangle between the two segment ends.
    /// </summary>
    private static bool Join(ref Painter painter, (double X, double Y) previous, (double X, double Y) v, (double X, double Y) next, double half)
    {
        var (d1x, d1y) = Direction(previous, v);
        var (d2x, d2y) = Direction(v, next);
        var cross = (d1x * d2y) - (d1y * d2x);
        var dot = (d1x * d2x) + (d1y * d2y);
        Span<(double X, double Y)> triangle = stackalloc (double X, double Y)[3];
        if (Math.Abs(cross) < 1e-12)
        {
            if (dot > 0)
            {
                // Straight on: the two rectangles meet without a gap.
                return true;
            }

            // Straight back: the miter cannot be computed; take the clipped extension beyond the corner on both sides.
            var reach = half * MiterLimit;
            Span<(double X, double Y)> cap =
            [
                (v.X - (d1y * half), v.Y + (d1x * half)),
                (v.X - (d1y * half) + (d1x * reach), v.Y + (d1x * half) + (d1y * reach)),
                (v.X + (d1y * half) + (d1x * reach), v.Y - (d1x * half) + (d1y * reach)),
                (v.X + (d1y * half), v.Y - (d1x * half)),
            ];
            return painter.FillConvex(cap);
        }

        // The outer side lies against the turn; n1 and n2 are the normals towards it.
        var side = cross > 0 ? -1 : 1;
        var (n1x, n1y) = (-d1y * half * side, d1x * half * side);
        var (n2x, n2y) = (-d2y * half * side, d2x * half * side);
        (double X, double Y) o1 = (v.X + n1x, v.Y + n1y), o2 = (v.X + n2x, v.Y + n2y);

        // Inner side: the segment ends and the corner.
        triangle[0] = v;
        triangle[1] = (v.X - n1x, v.Y - n1y);
        triangle[2] = (v.X - n2x, v.Y - n2y);
        if (!painter.FillConvex(triangle))
        {
            return false;
        }

        // The miter point lies on the bisector of the normals, half / cos(turn / 2) from the corner.
        var cosHalf = Math.Sqrt(Math.Max(0, (1 + dot) / 2));
        var (bx, by) = (n1x + n2x, n1y + n2y);
        var length = Math.Sqrt((bx * bx) + (by * by));
        if (cosHalf < 1e-9 || length < 1e-12)
        {
            return true;
        }

        var miter = half / cosHalf;
        (double X, double Y) m = (v.X + (bx / length * miter), v.Y + (by / length * miter));
        if (miter <= half * MiterLimit)
        {
            // A kite: right angles at the segment ends, so it is convex.
            Span<(double X, double Y)> kite = [v, o1, m, o2];
            return painter.FillConvex(kite);
        }

        // Beyond the limit: cut where the bisector reaches limit x half (AGG's clipped miter; also covers a bevel).
        var bevel = half * cosHalf;
        var share = ((half * MiterLimit) - bevel) / (miter - bevel);
        (double X, double Y) c1 = (o1.X + ((m.X - o1.X) * share), o1.Y + ((m.Y - o1.Y) * share));
        (double X, double Y) c2 = (o2.X + ((m.X - o2.X) * share), o2.Y + ((m.Y - o2.Y) * share));
        Span<(double X, double Y)> clipped = [v, o1, c1, c2, o2];
        return painter.FillConvex(clipped);
    }

    private static (double X, double Y) Direction((double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        return length > 0 ? (dx / length, dy / length) : (0, 0);
    }

    /// <summary>Paints the pixels convex pieces touch, in the colour of the current drawing, and remembers where it marked them.</summary>
    private ref struct Painter
    {
        private readonly Span<byte> pixels;
        private readonly int width;
        private readonly int height;
        private readonly int stride;
        private readonly double scaleX;
        private readonly double scaleY;
        private readonly double offsetX;
        private readonly double offsetY;
        private readonly int groundSum;
        private readonly int span;
        private readonly (int B, int G, int R) foreground;
        private (int B, int G, int R) delta;
        private int markedLeft;
        private int markedTop;
        private int markedRight;
        private int markedBottom;

        public Painter(Span<byte> pixels, int width, int height, int stride, double scaleX, double scaleY, double offsetX, double offsetY, PdfiumPageColors colors, long budget)
        {
            this.pixels = pixels;
            this.width = width;
            this.height = height;
            this.stride = stride;
            this.scaleX = scaleX;
            this.scaleY = scaleY;
            this.offsetX = offsetX;
            this.offsetY = offsetY;
            var ground = Channels(colors.Background);
            foreground = Channels(colors.Foreground);
            groundSum = ground.B + ground.G + ground.R;
            span = Math.Max(1, foreground.B + foreground.G + foreground.R - groundSum);
            Budget = budget;
            markedLeft = int.MaxValue;
            markedTop = int.MaxValue;
            markedRight = int.MinValue;
            markedBottom = int.MinValue;
        }

        /// <summary>Gets what is left of the budget of pixels.</summary>
        public long Budget { get; private set; }

        /// <summary>Starts a drawing of <paramref name="color"/> (ARGB).</summary>
        public void Begin(uint color)
        {
            var c = Channels(color);
            delta = (c.B - foreground.B, c.G - foreground.G, c.R - foreground.R);
        }

        /// <summary>
        /// Paints every unmarked pixel whose square (widened by <see cref="PdfiumDarkMarkups.EdgeSlack"/>) meets the convex
        /// polygon (points of the displayed page); <see langword="false"/> once the budget is used up.
        /// </summary>
        public bool FillConvex(scoped ReadOnlySpan<(double X, double Y)> polygon)
        {
            Span<(double X, double Y)> device = stackalloc (double X, double Y)[polygon.Length];
            double minY = double.MaxValue, maxY = double.MinValue;
            for (var i = 0; i < polygon.Length; i++)
            {
                device[i] = ((polygon[i].X * scaleX) + offsetX, (polygon[i].Y * scaleY) + offsetY);
                minY = Math.Min(minY, device[i].Y);
                maxY = Math.Max(maxY, device[i].Y);
            }

            const double Slack = PdfiumDarkMarkups.EdgeSlack;
            var y0 = (int)Math.Clamp(Math.Floor(minY - Slack), 0, height);
            var y1 = (int)Math.Clamp(Math.Ceiling(maxY + Slack), 0, height);
            for (var y = y0; y < y1; y++)
            {
                // The polygon clipped to the band of the row is convex; its extent in x is reached on a clipped edge.
                var (top, bottom) = (y - Slack, y + 1 + Slack);
                double minX = double.MaxValue, maxX = double.MinValue;
                for (var i = 0; i < device.Length; i++)
                {
                    var (a, b) = (device[i], device[(i + 1) % device.Length]);
                    if (a.Y == b.Y)
                    {
                        if (a.Y >= top && a.Y <= bottom)
                        {
                            minX = Math.Min(minX, Math.Min(a.X, b.X));
                            maxX = Math.Max(maxX, Math.Max(a.X, b.X));
                        }

                        continue;
                    }

                    var t0 = (top - a.Y) / (b.Y - a.Y);
                    var t1 = (bottom - a.Y) / (b.Y - a.Y);
                    var low = Math.Max(0, Math.Min(t0, t1));
                    var high = Math.Min(1, Math.Max(t0, t1));
                    if (low > high)
                    {
                        continue;
                    }

                    var xLow = a.X + ((b.X - a.X) * low);
                    var xHigh = a.X + ((b.X - a.X) * high);
                    minX = Math.Min(minX, Math.Min(xLow, xHigh));
                    maxX = Math.Max(maxX, Math.Max(xLow, xHigh));
                }

                if (minX > maxX)
                {
                    continue;
                }

                var x0 = (int)Math.Clamp(Math.Floor(minX - Slack), 0, width);
                var x1 = (int)Math.Clamp(Math.Ceiling(maxX + Slack), 0, width);
                if (x1 <= x0)
                {
                    continue;
                }

                if (x1 - x0 > Budget)
                {
                    Budget = 0;
                    return false;
                }

                Budget -= x1 - x0;
                PaintRow(y, x0, x1);
            }

            return true;
        }

        /// <summary>Gives every pixel marked in this paint its opaque fourth byte back.</summary>
        public readonly void ClearMarks()
        {
            for (var y = Math.Max(0, markedTop); y <= Math.Min(height - 1, markedBottom); y++)
            {
                var row = pixels.Slice(y * stride, width * 4);
                for (var x = Math.Max(0, markedLeft); x <= Math.Min(width - 1, markedRight); x++)
                {
                    if (row[(x * 4) + 3] == PaintedMark)
                    {
                        row[(x * 4) + 3] = 0xFF;
                    }
                }
            }
        }

        private void PaintRow(int y, int x0, int x1)
        {
            var row = pixels.Slice(y * stride, width * 4);
            var painted = false;
            for (var x = x0; x < x1; x++)
            {
                var pixel = row.Slice(x * 4, 4);
                if (pixel[3] != 0xFF)
                {
                    // Painted already, by a drawing on top of this one or another piece of it.
                    continue;
                }

                // The share of the light foreground PDFium put here: its part of the pixel becomes the colour.
                var share = Math.Clamp((pixel[0] + pixel[1] + pixel[2] - groundSum) / (double)span, 0, 1);
                if (share <= 0)
                {
                    continue;
                }

                pixel[0] = Shift(pixel[0], delta.B, share);
                pixel[1] = Shift(pixel[1], delta.G, share);
                pixel[2] = Shift(pixel[2], delta.R, share);
                pixel[3] = PaintedMark;
                painted = true;
            }

            if (painted)
            {
                markedLeft = Math.Min(markedLeft, x0);
                markedRight = Math.Max(markedRight, x1 - 1);
                markedTop = Math.Min(markedTop, y);
                markedBottom = Math.Max(markedBottom, y);
            }

            static byte Shift(byte value, int delta, double share) => (byte)Math.Clamp((int)Math.Round(value + (delta * share)), 0, 255);
        }

        private static (int B, int G, int R) Channels(uint argb) => ((int)(argb & 0xFF), (int)((argb >> 8) & 0xFF), (int)((argb >> 16) & 0xFF));
    }
}

/// <summary>A drawing as the dark page paints it: its colour there (ARGB), its stroke width in points, and its paths.</summary>
internal sealed class DarkDrawing(uint color, double width, IReadOnlyList<DarkDrawingPath> paths)
{
    /// <summary>Gets the colour on the dark page (ARGB).</summary>
    public uint Color { get; } = color;

    /// <summary>Gets the stroke width in points.</summary>
    public double Width { get; } = width;

    /// <summary>Gets the paths that are stroked.</summary>
    public IReadOnlyList<DarkDrawingPath> Paths { get; } = paths;
}

/// <summary>
/// A path of a drawing in points from the top left of the displayed page: a polyline, closed or not, or (when
/// <paramref name="Curved"/>) a start point followed by three points per cubic Bézier arc.
/// </summary>
internal readonly record struct DarkDrawingPath(IReadOnlyList<(double X, double Y)> Points, bool Closed, bool Curved);
