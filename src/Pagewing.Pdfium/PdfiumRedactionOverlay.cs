using System.Globalization;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>One line of overlay text (mirror of the Core type): the text and where it starts, in points from the top left of its area.</summary>
/// <param name="Text">The text.</param>
/// <param name="X">Where the first glyph starts, from the left edge of the area.</param>
/// <param name="Baseline">The baseline, from the top edge of the area.</param>
internal readonly record struct PdfiumOverlayLine(string Text, double X, double Baseline);

/// <summary>Where the overlay text of one area goes (mirror of the Core type).</summary>
/// <param name="Lines">The lines, in reading order; computed on access for a repeated text.</param>
/// <param name="FontSize">The font size in points.</param>
/// <param name="Fits">Whether the text fits the area.</param>
internal sealed record PdfiumOverlayPlacement(IReadOnlyList<PdfiumOverlayLine> Lines, double FontSize, bool Fits);

/// <summary>
/// What the run needs to draw overlay text (milestone 14.1): the text of each rectangle, its colour, the glyphs and the layout (both
/// reached lazily, on the PDFium thread, only when a rectangle has a text), and what is left of the budget of the document.
/// </summary>
internal sealed class PdfiumOverlaySpec
{
    /// <summary>Gets the text of each rectangle passed to the run (the same order), <see langword="null"/> for none.</summary>
    public required IReadOnlyList<string?> Texts { get; init; }

    /// <summary>Gets the colour of the glyphs.</summary>
    public required PdfiumRedactionFill Color { get; init; }

    /// <summary>Gets the glyphs of the font (read once per process; called on the PDFium thread).</summary>
    public required Func<PdfiumGlyphTable> Glyphs { get; init; }

    /// <summary>Gets the layout of a text in a rectangle (points from the top left of the displayed page), with the glyph table.</summary>
    public required Func<string, PdfiumRedactionArea, PdfiumGlyphTable, PdfiumOverlayPlacement> Layout { get; init; }

    /// <summary>Gets how many path objects the document may still get.</summary>
    public required int PathBudget { get; init; }

    /// <summary>Gets how many path segments the document may still get.</summary>
    public required long SegmentBudget { get; init; }

    /// <summary>Gets the most path objects one page may get: writing and reading back the content of a page is one job on the PDFium thread, which has to stay short.</summary>
    public required int PagePaths { get; init; }

    /// <summary>Gets the most path segments one page may get (see <see cref="PagePaths"/>).</summary>
    public required long PageSegments { get; init; }

    /// <summary>Gets whether the budget was already reached by an earlier page (every later overlay then counts as over the limit).</summary>
    public bool LimitReached { get; init; }
}

/// <summary>What happens to the overlay text of one rectangle.</summary>
internal enum PdfiumOverlayOutcome
{
    /// <summary>The text is drawn.</summary>
    Drawn,

    /// <summary>A treated image lies under the rectangle: no text (its background depends on the image).</summary>
    NotShown,

    /// <summary>The text does not fit the rectangle (or its glyphs are not all known): the area stays filled without text.</summary>
    NotFitting,

    /// <summary>The budget of the document is reached: the area stays filled without text.</summary>
    OverLimit,
}

/// <summary>The decision for one rectangle with a text.</summary>
/// <param name="Area">The index of the rectangle among those the page keeps (after clipping to the page).</param>
/// <param name="Outcome">What happens to the text.</param>
/// <param name="Rect">The rectangle in points from the top left of the displayed page.</param>
/// <param name="Text">The text.</param>
/// <param name="Placement">The placement of a drawn text, otherwise <see langword="null"/>.</param>
/// <param name="Paths">How many path objects the text takes (0 unless drawn).</param>
/// <param name="Segments">How many path segments the text takes (0 unless drawn).</param>
internal sealed record PdfiumOverlayItem(int Area, PdfiumOverlayOutcome Outcome, PdfiumRedactionArea Rect, string Text, PdfiumOverlayPlacement? Placement, int Paths, long Segments);

/// <summary>The decisions for the rectangles of one page, with their sums.</summary>
internal sealed class PdfiumOverlayPlan
{
    /// <summary>Gets the decision for every rectangle that has a text, in the order of the rectangles.</summary>
    public required IReadOnlyList<PdfiumOverlayItem> Items { get; init; }

    /// <summary>Gets the shapes of the glyphs (the plan draws with them), or <see langword="null"/> when no rectangle had a text to lay out.</summary>
    public PdfiumOverlayShapes? Shapes { get; init; }

    /// <summary>Gets how many texts are drawn.</summary>
    public int Drawn { get; init; }

    /// <summary>Gets how many texts are left out because a treated image lies under their rectangle.</summary>
    public int NotShown { get; init; }

    /// <summary>Gets how many texts do not fit (the budget reason included).</summary>
    public int NotFitting { get; init; }

    /// <summary>Gets how many of the texts that do not fit were left out because the budget of the document is reached.</summary>
    public int OverLimit { get; init; }

    /// <summary>Gets how many path objects the drawn texts take.</summary>
    public int Paths { get; init; }

    /// <summary>Gets how many path segments the drawn texts take.</summary>
    public long Segments { get; init; }

    /// <summary>Gets whether the budget of the document is reached after this page (reaching the limit of the page alone does not set it).</summary>
    public bool LimitReached { get; init; }
}

/// <summary>One glyph outline as drawing commands in em (y up from the baseline).</summary>
internal sealed class PdfiumGlyphShape
{
    /// <summary>Gets the commands: 0 move, 1 line, 2 curve, 3 close.</summary>
    public required byte[] Ops { get; init; }

    /// <summary>Gets the coordinates of the commands in em (2 for a move or line, 6 for a curve, none for a close).</summary>
    public required float[] Coordinates { get; init; }

    /// <summary>Gets the box of all points in em.</summary>
    public required (double MinX, double MinY, double MaxX, double MaxY) Bounds { get; init; }

    /// <summary>Gets how many path segments PDFium keeps for it (a curve is three).</summary>
    public required int Segments { get; init; }

    /// <summary>Gets the advance in em.</summary>
    public required double Advance { get; init; }
}

/// <summary>The outlines of the glyphs of the font as shapes, parsed once for the characters that are used.</summary>
internal sealed class PdfiumOverlayShapes(PdfiumGlyphTable table)
{
    private readonly Dictionary<char, PdfiumGlyphShape?> glyphs = [];
    private readonly Dictionary<string, TextShape?> texts = new(StringComparer.Ordinal);

    /// <summary>Gets the shape of a text: its glyphs with the position of each in em, the number of segments and the box in em.</summary>
    /// <returns>The shape, or <see langword="null"/> when a character has no known outline.</returns>
    public TextShape? Of(string text)
    {
        if (texts.TryGetValue(text, out var known))
        {
            return known;
        }

        TextShape? shape = null;
        var parts = new List<(PdfiumGlyphShape Glyph, double X)>();
        double x = 0, minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var segments = 0L;
        var ok = true;
        foreach (var character in text)
        {
            var glyph = GlyphOf(character);
            if (glyph is null)
            {
                ok = false;
                break;
            }

            if (glyph.Ops.Length > 0)
            {
                parts.Add((glyph, x));
                segments += glyph.Segments;
                minX = Math.Min(minX, x + glyph.Bounds.MinX);
                maxX = Math.Max(maxX, x + glyph.Bounds.MaxX);
                minY = Math.Min(minY, glyph.Bounds.MinY);
                maxY = Math.Max(maxY, glyph.Bounds.MaxY);
            }

            x += glyph.Advance;
        }

        if (ok)
        {
            shape = parts.Count == 0
                ? new TextShape([], 0, 0, 0, 0, 0)
                : new TextShape(parts, segments, minX, minY, maxX, maxY);
        }

        texts[text] = shape;
        return shape;
    }

    private PdfiumGlyphShape? GlyphOf(char character)
    {
        if (glyphs.TryGetValue(character, out var known))
        {
            return known;
        }

        PdfiumGlyphShape? shape = null;
        if (table.TryGet(character, out var glyph))
        {
            shape = Parse(glyph);
        }

        glyphs[character] = shape;
        return shape;
    }

    /// <summary>Reads an outline of the glyph table (<c>x y m</c>, <c>x y l</c>, <c>x1 y1 x2 y2 x3 y3 c</c>, <c>h</c>, ending with <c>f</c>, thousandths of an em).</summary>
    private static PdfiumGlyphShape? Parse(PdfiumGlyph glyph)
    {
        if (glyph.Outline.Length == 0)
        {
            return new PdfiumGlyphShape { Ops = [], Coordinates = [], Bounds = default, Segments = 0, Advance = glyph.Advance };
        }

        var ops = new List<byte>();
        var coordinates = new List<float>();
        var numbers = new List<float>(6);
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        var segments = 0;
        foreach (var token in glyph.Outline.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                numbers.Add(number / 1000f);
                if (numbers.Count > 6)
                {
                    return null;
                }

                continue;
            }

            byte op;
            int need;
            switch (token)
            {
                case "m":
                    op = 0;
                    need = 2;
                    segments++;
                    break;
                case "l":
                    op = 1;
                    need = 2;
                    segments++;
                    break;
                case "c":
                    op = 2;
                    need = 6;
                    segments += 3;
                    break;
                case "h":
                    op = 3;
                    need = 0;
                    break;
                case "f":
                    return numbers.Count == 0 && ops.Count > 0 && ops[0] == 0 ? Finish() : null;
                default:
                    return null;
            }

            if (numbers.Count != need)
            {
                return null;
            }

            ops.Add(op);
            for (var i = 0; i < numbers.Count; i += 2)
            {
                coordinates.Add(numbers[i]);
                coordinates.Add(numbers[i + 1]);
                minX = Math.Min(minX, numbers[i]);
                maxX = Math.Max(maxX, numbers[i]);
                minY = Math.Min(minY, numbers[i + 1]);
                maxY = Math.Max(maxY, numbers[i + 1]);
            }

            numbers.Clear();
        }

        // An outline always ends with the fill operator.
        return null;

        PdfiumGlyphShape Finish() => new()
        {
            Ops = [.. ops],
            Coordinates = [.. coordinates],
            Bounds = (minX, minY, maxX, maxY),
            Segments = segments,
            Advance = glyph.Advance,
        };
    }
}

/// <summary>A text as the glyphs it is made of, each at its x in em, with the box and the segment count of the whole.</summary>
internal sealed record TextShape(IReadOnlyList<(PdfiumGlyphShape Glyph, double X)> Parts, long Segments, double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// Overlay text as path objects (milestone 14.1): the glyph outlines of the text box font drawn as filled paths over the opaque area of
/// a rectangle, with no text and no font in the file. Every method must be called on the PDFium thread.
/// </summary>
/// <remarks>
/// Nothing reaches outside the rectangle: <see cref="Decide"/> refuses a text one of whose glyphs would leave it (the layout keeps a margin,
/// this is the second lock), and decides for the analysis and for applying alike. One path object holds one line (one tile of a repeated
/// text), so the number of objects stays small; the document has a budget of objects and segments (<see cref="PdfiumOverlaySpec"/>).
/// </remarks>
internal static class PdfiumRedactionOverlay
{
    /// <summary>How many lines are drawn between two points where the step may end.</summary>
    public const int LinesPerSlice = 128;

    /// <summary>The slack, in points, for the check that a glyph lies in its rectangle (a float is accurate to about 1e-4 at page sizes).</summary>
    private const double BoundsSlack = 1e-6;

    /// <summary>
    /// Decides what happens to the overlay text of every rectangle that has one: left out over a treated image, left out when it does not
    /// fit (or a glyph is unknown, or a glyph would leave the rectangle), left out when the budget is reached, otherwise drawn. The same
    /// function serves the analysis and applying, so the card says what the apply does.
    /// </summary>
    /// <param name="spec">The texts, the layout, the glyphs and the budget.</param>
    /// <param name="areas">The rectangles the page keeps: the index of the rectangle in <see cref="PdfiumOverlaySpec.Texts"/> and the rectangle in points from the top left of the displayed page.</param>
    /// <param name="isTreated">Whether a treated (pixelated or blurred) image lies under the rectangle with this index among <paramref name="areas"/>.</param>
    /// <param name="pathRoom">How many more objects the page may hold (<see cref="PdfiumRedaction.MaxObjects"/> less those it has): the page cap of paths is no more than this, so a text that would take the page past the limit is counted as over the limit now and not refused when the result is read.</param>
    public static PdfiumOverlayPlan Decide(PdfiumOverlaySpec spec, IReadOnlyList<(int Source, PdfiumRedactionArea Rect)> areas, Func<int, bool> isTreated, int pathRoom = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(isTreated);
        var items = new List<PdfiumOverlayItem>();
        PdfiumGlyphTable? table = null;
        PdfiumOverlayShapes? shapes = null;
        int drawn = 0, notShown = 0, notFitting = 0, overLimit = 0, paths = 0;
        long segments = 0;
        var limit = spec.LimitReached;
        var pageStop = false;
        var pageCap = Math.Min(spec.PagePaths, Math.Max(0, pathRoom));
        var pagePaths = Math.Min(spec.PathBudget, pageCap);
        var pageSegments = Math.Min(spec.SegmentBudget, spec.PageSegments);
        for (var i = 0; i < areas.Count; i++)
        {
            var (source, rect) = areas[i];
            if (source < 0 || source >= spec.Texts.Count || spec.Texts[source] is not { Length: > 0 } text)
            {
                continue;
            }

            if (isTreated(i))
            {
                items.Add(new PdfiumOverlayItem(i, PdfiumOverlayOutcome.NotShown, rect, text, null, 0, 0));
                notShown++;
                continue;
            }

            table ??= spec.Glyphs();
            shapes ??= new PdfiumOverlayShapes(table);
            var placement = spec.Layout(text, rect, table);
            var shape = placement.Fits ? shapes.Of(text) : null;
            if (!placement.Fits || shape is null || shape.Parts.Count == 0 || placement.Lines.Count == 0)
            {
                items.Add(new PdfiumOverlayItem(i, PdfiumOverlayOutcome.NotFitting, rect, text, null, 0, 0));
                notFitting++;
                continue;
            }

            // The budget first: the check of the glyphs below walks the lines, and the walk is bounded by the budget. From the first
            // text that does not fit into the budget no text after it is drawn (on the page, or, when the budget of the document is what
            // ran out, in the document), so that the rule the card states is a simple one.
            long linePaths = shape.Segments == 0 ? 0 : placement.Lines.Count;
            var lineSegments = linePaths * shape.Segments;
            var tooManyPaths = paths + linePaths > pagePaths;
            var tooManySegments = segments + lineSegments > pageSegments;
            if (limit || pageStop || tooManyPaths || tooManySegments)
            {
                pageStop = true;
                limit |= (tooManyPaths && spec.PathBudget <= pageCap) || (tooManySegments && spec.SegmentBudget <= spec.PageSegments);
                items.Add(new PdfiumOverlayItem(i, PdfiumOverlayOutcome.OverLimit, rect, text, null, 0, 0));
                notFitting++;
                overLimit++;
                continue;
            }

            if (!LinesStayInside(placement, shape, rect))
            {
                items.Add(new PdfiumOverlayItem(i, PdfiumOverlayOutcome.NotFitting, rect, text, null, 0, 0));
                notFitting++;
                continue;
            }

            items.Add(new PdfiumOverlayItem(i, PdfiumOverlayOutcome.Drawn, rect, text, placement, (int)linePaths, lineSegments));
            drawn++;
            paths += (int)linePaths;
            segments += lineSegments;
        }

        return new PdfiumOverlayPlan
        {
            Items = items,
            Shapes = shapes,
            Drawn = drawn,
            NotShown = notShown,
            NotFitting = notFitting,
            OverLimit = overLimit,
            Paths = paths,
            Segments = segments,
            LimitReached = limit,
        };
    }

    /// <summary>Whether every glyph of every line lies inside the rectangle (the box of all points of the outlines, curve control points included).</summary>
    private static bool LinesStayInside(PdfiumOverlayPlacement placement, TextShape shape, PdfiumRedactionArea rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var size = placement.FontSize;
        if (!(size > 0) || !double.IsFinite(size) || shape.Parts.Count == 0)
        {
            return false;
        }

        foreach (var line in placement.Lines)
        {
            if (!double.IsFinite(line.X) || !double.IsFinite(line.Baseline)
                || line.X + (shape.MinX * size) < -BoundsSlack
                || line.X + (shape.MaxX * size) > width + BoundsSlack
                || line.Baseline - (shape.MaxY * size) < -BoundsSlack
                || line.Baseline - (shape.MinY * size) > height + BoundsSlack)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Draws the texts of a plan as path objects on a page, one object for each line, over what the page already holds. Yields every
    /// <see cref="LinesPerSlice"/> lines so that the step can end. The map takes points from the top left of the displayed page to user space.
    /// </summary>
    /// <param name="page">The loaded page.</param>
    /// <param name="plan">The decisions.</param>
    /// <param name="color">The colour of the glyphs.</param>
    /// <param name="origin">The user-space point of the top left of the displayed page.</param>
    /// <param name="alongX">The step in user space of one displayed point to the right.</param>
    /// <param name="alongY">The step in user space of one displayed point down.</param>
    /// <exception cref="InvalidOperationException">PDFium could not make or add a path.</exception>
    public static IEnumerable<bool> Draw(nint page, PdfiumOverlayPlan plan, PdfiumRedactionFill color, (double X, double Y) origin, (double X, double Y) alongX, (double X, double Y) alongY)
    {
        var lines = 0;
        foreach (var item in plan.Items)
        {
            if (item.Outcome != PdfiumOverlayOutcome.Drawn || item.Placement is not { } placement || plan.Shapes?.Of(item.Text) is not { Parts.Count: > 0 } shape)
            {
                continue;
            }

            foreach (var line in placement.Lines)
            {
                var path = BuildLine(shape, placement.FontSize, item.Rect.Left + line.X, item.Rect.Top + line.Baseline, color, origin, alongX, alongY);
                if (path == 0)
                {
                    throw new InvalidOperationException("PDFium could not make the overlay text.");
                }

                if (Fpdf.FPDFPage_InsertObject(page, path) == 0)
                {
                    // FPDFPage_InsertObject frees the object itself on failure (fpdf_edit.h): destroying it here would be a double free.
                    throw new InvalidOperationException("PDFium could not add the overlay text to the page.");
                }

                if (++lines % LinesPerSlice == 0)
                {
                    yield return true;
                }
            }
        }
    }

    /// <summary>One path object for a line of text: all glyph outlines, filled with the non-zero rule, no stroke. 0 when PDFium fails (nothing is left over).</summary>
    private static nint BuildLine(TextShape shape, double size, double originX, double baselineY, PdfiumRedactionFill color, (double X, double Y) origin, (double X, double Y) alongX, (double X, double Y) alongY)
    {
        var map = new PointMap(originX, baselineY, size, origin, alongX, alongY);
        nint path = 0;
        foreach (var (glyph, x) in shape.Parts)
        {
            map.GlyphX = x;
            var ops = glyph.Ops;
            var points = glyph.Coordinates;
            var at = 0;
            foreach (var op in ops)
            {
                bool ok;
                switch (op)
                {
                    case 0:
                        {
                            var (px, py) = map.To(points[at], points[at + 1]);
                            at += 2;
                            if (path == 0)
                            {
                                path = Fpdf.FPDFPageObj_CreateNewPath(px, py);
                                ok = path != 0;
                            }
                            else
                            {
                                ok = Fpdf.FPDFPath_MoveTo(path, px, py) != 0;
                            }

                            break;
                        }

                    case 1:
                        {
                            var (px, py) = map.To(points[at], points[at + 1]);
                            at += 2;
                            ok = Fpdf.FPDFPath_LineTo(path, px, py) != 0;
                            break;
                        }

                    case 2:
                        {
                            var (x1, y1) = map.To(points[at], points[at + 1]);
                            var (x2, y2) = map.To(points[at + 2], points[at + 3]);
                            var (x3, y3) = map.To(points[at + 4], points[at + 5]);
                            at += 6;
                            ok = Fpdf.FPDFPath_BezierTo(path, x1, y1, x2, y2, x3, y3) != 0;
                            break;
                        }

                    default:
                        ok = Fpdf.FPDFPath_Close(path) != 0;
                        break;
                }

                if (!ok)
                {
                    return path == 0 ? 0 : Fail(path);
                }
            }
        }

        if (path == 0)
        {
            return 0;
        }

        if (Fpdf.FPDFPageObj_SetFillColor(path, color.Red, color.Green, color.Blue, 255) == 0 || Fpdf.FPDFPath_SetDrawMode(path, FpdfConstants.FillModeWinding, 0) == 0)
        {
            return Fail(path);
        }

        return path;
    }

    /// <summary>Maps a point of a glyph outline (em, y up from the baseline) to user space through the displayed page.</summary>
    private struct PointMap(double originX, double baselineY, double size, (double X, double Y) origin, (double X, double Y) alongX, (double X, double Y) alongY)
    {
        /// <summary>The x of the glyph in the line, in em.</summary>
        public double GlyphX;

        public readonly (float X, float Y) To(float gx, float gy)
        {
            // Displayed point: x to the right, y down; the outline has y up from the baseline.
            var dx = originX + ((GlyphX + gx) * size);
            var dy = baselineY - (gy * size);
            return ((float)(origin.X + (alongX.X * dx) + (alongY.X * dy)), (float)(origin.Y + (alongX.Y * dx) + (alongY.Y * dy)));
        }
    }

    private static nint Fail(nint path)
    {
        Fpdf.FPDFPageObj_Destroy(path);
        return 0;
    }
}
