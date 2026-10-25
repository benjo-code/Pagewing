using System.Globalization;
using System.Runtime.InteropServices;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>The kinds of drawings <see cref="PdfiumDrawings"/> reads and creates.</summary>
internal enum PdfiumDrawingKind
{
    /// <summary>An ink annotation without a marker of Pagewing: freehand.</summary>
    Ink,

    /// <summary>An ink annotation with <c>/Subj (Line)</c>. Read back, this only says what <c>/Subj</c> says; the caller checks the shape.</summary>
    Line,

    /// <summary>An ink annotation with <c>/Subj (Arrow)</c>. Read back, this only says what <c>/Subj</c> says; the caller checks the shape.</summary>
    Arrow,

    /// <summary>A square annotation (rectangle outline).</summary>
    Square,

    /// <summary>A circle annotation (ellipse outline).</summary>
    Circle,
}

/// <summary>
/// A drawing in points from the top left of the displayed page (after rotation and CropBox), converted in double precision
/// (<see cref="DisplayTransform"/>). Pagewing's own type (<c>DrawingGeometry</c> in Core) is mapped to and from this one,
/// because <c>Pagewing.Pdfium</c> does not know Core.
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Strokes">The strokes of an ink, line or arrow; empty for a square and a circle.</param>
/// <param name="Box">
/// For a square and a circle the <c>/Rect</c>, the outer edge (the stroke lies inside); for the others the smallest box
/// around the points, without the stroke width. <see cref="PdfiumDrawings.Create"/> ignores it for ink, line and arrow.
/// </param>
/// <param name="Width">The stroke width in points.</param>
internal sealed record PdfiumDrawing(
    PdfiumDrawingKind Kind,
    IReadOnlyList<IReadOnlyList<(double X, double Y)>> Strokes,
    (double Left, double Top, double Right, double Bottom) Box,
    double Width)
{
    /// <summary>
    /// Gets whether every stroke of the file was read: <see langword="false"/> when strokes beyond
    /// <see cref="PdfiumDrawings.MaxInkStrokes"/> or <see cref="PdfiumDrawings.MaxInkPoints"/>, or damaged strokes, were
    /// skipped. Such a drawing must not be replaced, the skipped strokes would be lost.
    /// </summary>
    public bool IsComplete { get; init; } = true;
}

/// <summary>
/// Reads and creates the drawings of a loaded page: ink (freehand, line, arrow), square and circle. Every method must be
/// called on the PDFium thread and throws <see cref="PdfiumThreadViolationException"/> otherwise; every annotation handle is
/// closed before a method returns.
/// </summary>
/// <remarks>
/// <para>
/// The appearance is PDFium's (Variante D, as for the markups): a drawing is created without <c>/AP</c>, with the width as
/// <c>/Border [0 0 w]</c> and the colour as <c>/C</c> with alpha 255, and <see cref="PdfiumAnnotations.GenerateAppearance"/>
/// makes the generator build it. The generator widens the <c>/Rect</c> of an ink by half the width each time it builds an
/// appearance, so <c>/Rect</c> is set by Pagewing before every generation: for an ink the hull of the points plus the room the
/// miter joins need beyond half the width (for an arrow's head w / (2 sin 25°) - w / 2), for a square and a circle the
/// wanted outer edge. A line and an arrow carry <c>/Subj</c> <see cref="LineSubject"/> or <see cref="ArrowSubject"/>.
/// See <c>Docs/DECISIONS.md</c>, entry "Zeichnungen: Erscheinungsbild, Pfeil als Ink, Merkmal zur Wiedererkennung, Glättung (M10)".
/// </para>
/// <para>
/// Points are converted with <see cref="DisplayTransform"/> in both directions, not on the 1/16-pt grid of
/// <see cref="PdfiumPageText.PageTransform"/>: rounding to the grid would turn the head of an arrow by up to 0.005 rad, and
/// the shape check on reading would no longer recognise it.
/// </para>
/// </remarks>
internal static unsafe class PdfiumDrawings
{
    /// <summary>The most strokes of one ink that are read or created; further strokes are ignored when reading.</summary>
    public const int MaxInkStrokes = 1_000;

    /// <summary>The most points of all strokes of one ink that are read or created; a stroke that does not fit is skipped when reading.</summary>
    public const int MaxInkPoints = 100_000;

    /// <summary>The widest stroke created, in points.</summary>
    public const double MaxWidth = 1_000;

    /// <summary>The <c>/Subj</c> of an arrow of Pagewing (fixed English, never translated).</summary>
    public const string ArrowSubject = "Arrow";

    /// <summary>The <c>/Subj</c> of a line of Pagewing (fixed English, never translated).</summary>
    public const string LineSubject = "Line";

    /// <summary>The stroke width when neither <c>/Border</c> nor the appearance tells it (the default of <c>/BS /W</c> and <c>/Border</c>).</summary>
    public const double DefaultWidth = 1;

    /// <summary>The miter limit PDFium's generator leaves at the PDF default (it writes no <c>M</c>); beyond it a join is beveled.</summary>
    private const double MiterLimit = 10;

    /// <summary>
    /// Creates an ink (for <see cref="PdfiumDrawingKind.Ink"/>, <see cref="PdfiumDrawingKind.Line"/> and
    /// <see cref="PdfiumDrawingKind.Arrow"/>), a square or a circle without an appearance: <c>/Rect</c> (see the remarks of
    /// the class), the strokes (<c>/InkList</c>), <c>/C</c> with alpha 255, <c>/Border [0 0 w]</c>, <c>/F</c> Print,
    /// <c>/Subj</c> for a line and an arrow, <c>/NM</c>, <c>/M</c>, <c>/CreationDate</c>, and <c>/Contents</c> and <c>/T</c>
    /// when given. Never an interior colour. Every point is checked and converted before anything is added; on failure
    /// nothing is left on the page.
    /// </summary>
    /// <returns>The index of the new annotation.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// More than <see cref="MaxInkStrokes"/> strokes or <see cref="MaxInkPoints"/> points, or a width that is not above 0
    /// and at most <see cref="MaxWidth"/>.
    /// </exception>
    /// <exception cref="ArgumentException">No stroke, a stroke of fewer than two points (it would draw nothing and is not read back), a point that is not finite or lies beyond
    /// <see cref="PdfiumAnnotations.MaxCoordinate"/>, or a box without width or height.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused to create or fill the annotation.</exception>
    public static int Create(
        PdfiumPage page,
        PdfiumDrawing drawing,
        (byte R, byte G, byte B) color,
        string? contents,
        string? author,
        string name,
        string date)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(drawing);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(date);
        var width = drawing.Width;
        if (!double.IsFinite(width) || width <= 0 || width > MaxWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(drawing), width, $"The stroke width must be above 0 and at most {MaxWidth} points.");
        }

        var native = page.Access();
        if (!DisplayTransform.TryCreate(native, page.Width, page.Height, out var transform))
        {
            throw new InvalidOperationException("PDFium cannot convert the coordinates of the page.");
        }

        var isInk = drawing.Kind is PdfiumDrawingKind.Ink or PdfiumDrawingKind.Line or PdfiumDrawingKind.Arrow;
        FsPointF[][] strokes = [];
        FsRectF rect;
        if (isInk)
        {
            strokes = ToUser(transform, drawing.Strokes);
            rect = InkRect(strokes, width);
        }
        else
        {
            rect = BoxToUser(transform, drawing.Box);
        }

        var subtype = drawing.Kind switch
        {
            PdfiumDrawingKind.Square => FpdfConstants.AnnotSquare,
            PdfiumDrawingKind.Circle => FpdfConstants.AnnotCircle,
            _ => FpdfConstants.AnnotInk,
        };
        var annotation = Fpdf.FPDFPage_CreateAnnot(native, subtype);
        if (annotation == 0)
        {
            throw new InvalidOperationException("PDFium could not create the annotation.");
        }

        var index = -1;
        try
        {
            index = Fpdf.FPDFPage_GetAnnotIndex(native, annotation);
            if (index < 0)
            {
                // Not in /Annots, so there is nothing to remove either.
                throw new InvalidOperationException("PDFium did not add the annotation to the page.");
            }

            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetRect(annotation, &rect));
            foreach (var stroke in strokes)
            {
                fixed (FsPointF* points = stroke)
                {
                    if (Fpdf.FPDFAnnot_AddInkStroke(annotation, points, (nuint)stroke.Length) < 0)
                    {
                        throw new InvalidOperationException("PDFium refused a stroke of the drawing.");
                    }
                }
            }

            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetColor(annotation, 0, color.R, color.G, color.B, 255));

            // Removes an appearance (there is none yet) and writes /Border [0 0 w], which the generator reads.
            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetBorder(annotation, 0, 0, (float)width));
            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetFlags(annotation, FpdfConstants.AnnotFlagPrint));
            if (drawing.Kind is PdfiumDrawingKind.Arrow or PdfiumDrawingKind.Line)
            {
                PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "Subj\0"u8, drawing.Kind == PdfiumDrawingKind.Arrow ? ArrowSubject : LineSubject));
            }

            if (!string.IsNullOrEmpty(contents))
            {
                PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "Contents\0"u8, contents));
            }

            if (!string.IsNullOrEmpty(author))
            {
                PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "T\0"u8, author));
            }

            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "NM\0"u8, name));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "M\0"u8, date));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "CreationDate\0"u8, date));
            return index;
        }
        catch
        {
            if (index >= 0)
            {
                _ = Fpdf.FPDFPage_RemoveAnnot(native, index);
            }

            throw;
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>Reads the drawing at <paramref name="index"/>, converting with the page's <see cref="DisplayTransform"/>.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while reading.</exception>
    public static PdfiumDrawing? ReadGeometry(PdfiumPage page, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        return DisplayTransform.TryCreate(page.Access(), page.Width, page.Height, out var transform)
            ? ReadGeometry(page, index, transform, cancellationToken)
            : null;
    }

    /// <summary>
    /// Reads the drawing at <paramref name="index"/>: <see langword="null"/> for any other subtype and for a damaged one (no
    /// <c>/Rect</c> or coordinates that are not finite or beyond <see cref="PdfiumAnnotations.MaxCoordinate"/>, an ink
    /// without a readable stroke of two points or more; shorter strokes draw nothing and are passed over). Of an ink at most <see cref="MaxInkStrokes"/> strokes and <see cref="MaxInkPoints"/>
    /// points are read; a stroke that does not fit into what is left, cannot be read or has a damaged coordinate is skipped
    /// (without reading it, if it is too long) and the result is not <see cref="PdfiumDrawing.IsComplete"/>. An ink is a
    /// <see cref="PdfiumDrawingKind.Line"/> or <see cref="PdfiumDrawingKind.Arrow"/> when its <c>/Subj</c> says so; the caller
    /// checks the shape. The width is <c>/Border</c>, else the first line width of the appearance, else <see cref="DefaultWidth"/>
    /// (PDFium cannot read <c>/BS</c>).
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while reading.</exception>
    public static PdfiumDrawing? ReadGeometry(PdfiumPage page, int index, DisplayTransform transform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return null;
        }

        try
        {
            var subtype = Fpdf.FPDFAnnot_GetSubtype(annotation);
            FsRectF rect;
            if (!PdfiumAnnotations.IsDrawing(subtype) || !PdfiumAnnotations.HasKey(annotation, "Rect\0"u8)
                || Fpdf.FPDFAnnot_GetRect(annotation, &rect) == 0 || !IsSane(rect))
            {
                return null;
            }

            var width = ReadWidth(annotation);
            if (subtype != FpdfConstants.AnnotInk)
            {
                var kind = subtype == FpdfConstants.AnnotSquare ? PdfiumDrawingKind.Square : PdfiumDrawingKind.Circle;
                return new PdfiumDrawing(kind, [], BoxToDisplay(transform, rect), width);
            }

            var strokes = ReadStrokes(annotation, out var complete, cancellationToken);
            if (strokes.Count == 0)
            {
                return null;
            }

            var display = new List<IReadOnlyList<(double X, double Y)>>(strokes.Count);
            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var stroke in strokes)
            {
                var points = new (double X, double Y)[stroke.Length];
                for (var i = 0; i < stroke.Length; i++)
                {
                    var point = transform.ToDisplay(stroke[i].X, stroke[i].Y);
                    points[i] = (point.X, point.Y);
                    left = Math.Min(left, point.X);
                    top = Math.Min(top, point.Y);
                    right = Math.Max(right, point.X);
                    bottom = Math.Max(bottom, point.Y);
                }

                display.Add(points);
            }

            var marked = PdfiumAnnotations.ReadString(annotation, "Subj\0"u8) switch
            {
                ArrowSubject => PdfiumDrawingKind.Arrow,
                LineSubject => PdfiumDrawingKind.Line,
                _ => PdfiumDrawingKind.Ink,
            };
            return new PdfiumDrawing(marked, display, (left, top, right, bottom), width) { IsComplete = complete };
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// Whether Pagewing may move, resize, recolour or rewiden the drawing, which it does by deleting it and creating it anew
    /// with its own conventions. Only when nothing would be lost on the way:
    /// <list type="bullet">
    /// <item>an ink, square or circle that is no reply and that no reply (<c>/IRT</c>) points at;</item>
    /// <item>
    /// no <c>/BS</c>: PDFium cannot read inside it, so its style and dashes are unknown and would be replaced by a plain
    /// <c>/Border</c>; a foreign drawing with <c>/BS</c>, even a solid one, can only be deleted or commented on;
    /// </item>
    /// <item>no <c>/BE</c> (a cloudy border);</item>
    /// <item>
    /// no <c>/CA</c> below 1 (read with <c>FPDFAnnot_GetNumberValue</c>; a <c>/CA</c> that is no number locks too): the new
    /// annotation is opaque, because <c>FPDFAnnot_SetColor</c> writes <c>/CA</c> 1 with alpha 255 (so Pagewing's own
    /// drawings always carry <c>/CA 1</c>);
    /// </item>
    /// <item>no <c>/IC</c> (interior colour): PDFium's generator paints it as a fill, and Pagewing never writes one;</item>
    /// <item>
    /// no <c>/RD</c> (the difference between <c>/Rect</c> and the drawn box of a square or circle): the box is read as the
    /// whole <c>/Rect</c>, so a replaced shape would grow to its edges;
    /// </item>
    /// <item>
    /// no appearance, or one PDFium generated (<c>/PDFIUM_HasGeneratedAP</c>): another program's appearance would be
    /// replaced by PDFium's;
    /// </item>
    /// <item>every stroke readable within <see cref="MaxInkStrokes"/> and <see cref="MaxInkPoints"/> (<see cref="PdfiumDrawing.IsComplete"/>).</item>
    /// </list>
    /// A known gap: dashes in a fourth entry of <c>/Border</c> cannot be read either (<c>FPDFAnnot_GetBorder</c> reads three)
    /// and are lost on replacing. Locked like <c>/BS</c> and <c>/BE</c>: <c>/CA</c> below 1, <c>/IC</c> and <c>/RD</c>.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while checking.</exception>
    public static bool IsGeometryEditable(PdfiumPage page, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (PdfiumAnnotations.IsReplyOrAnswered(page, index, cancellationToken))
        {
            return false;
        }

        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return false;
        }

        try
        {
            if (!PdfiumAnnotations.IsDrawing(Fpdf.FPDFAnnot_GetSubtype(annotation))
                || PdfiumAnnotations.HasKey(annotation, "BS\0"u8)
                || PdfiumAnnotations.HasKey(annotation, "BE\0"u8)
                || PdfiumAnnotations.HasKey(annotation, "IC\0"u8)
                || PdfiumAnnotations.HasKey(annotation, "RD\0"u8)
                || !IsOpaque(annotation)
                || (PdfiumAnnotations.HasKey(annotation, "AP\0"u8) && !PdfiumAnnotations.HasKey(annotation, "PDFIUM_HasGeneratedAP\0"u8)))
            {
                return false;
            }
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }

        return ReadGeometry(page, index, cancellationToken) is { IsComplete: true };
    }

    /// <summary>
    /// The first line width an appearance stream sets (<c>w</c> with a finite operand of at least 0), or
    /// <see langword="null"/>. Strings, names, arrays, dictionaries and comments are skipped; the scan stops at an inline
    /// image. A <c>cm</c> before it is not taken into account. Read from the text of <c>FPDFAnnot_GetAP</c>, which only
    /// reads; <c>FPDFAnnot_GetObjectCount</c> would set the <c>/Matrix</c> of a foreign appearance to the identity, and that
    /// would go into the saved file (M9, entry "Erscheinungsbild der Annotationen …").
    /// </summary>
    internal static double? WidthFromAppearance(ReadOnlySpan<char> content)
    {
        double? operand = null;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (PdfiumAnnotations.IsWhite(c))
            {
                i++;
            }
            else if (c == '%')
            {
                while (i < content.Length && content[i] is not ('\r' or '\n'))
                {
                    i++;
                }
            }
            else if (c == '(')
            {
                i = PdfiumAnnotations.SkipString(content, i);
                operand = null;
            }
            else if (c == '<' && i + 1 < content.Length && content[i + 1] == '<')
            {
                i += 2;
                operand = null;
            }
            else if (c == '<')
            {
                var end = content[i..].IndexOf('>');
                i = end < 0 ? content.Length : i + end + 1;
                operand = null;
            }
            else if (c is '>' or '[' or ']' or '{' or '}' or ')')
            {
                i++;
                operand = null;
            }
            else if (c == '/')
            {
                i++;
                while (i < content.Length && !PdfiumAnnotations.IsWhite(content[i]) && !PdfiumAnnotations.IsDelimiter(content[i]))
                {
                    i++;
                }

                operand = null;
            }
            else
            {
                var start = i;
                while (i < content.Length && !PdfiumAnnotations.IsWhite(content[i]) && !PdfiumAnnotations.IsDelimiter(content[i]))
                {
                    i++;
                }

                var token = content[start..i];
                if (c is (>= '0' and <= '9') or '+' or '-' or '.')
                {
                    operand = double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
                    continue;
                }

                if (token.SequenceEqual("BI"))
                {
                    break;
                }

                if (token.SequenceEqual("w") && operand is { } value && double.IsFinite(value) && value >= 0)
                {
                    return value;
                }

                operand = null;
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>/Rect</c> an ink needs before PDFium's generator builds its appearance (see the remarks of the class), from its
    /// <c>/InkList</c> and width; <see langword="null"/> when not every stroke can be read.
    /// </summary>
    internal static FsRectF? InkRectOf(nint annotation)
    {
        var strokes = ReadStrokes(annotation, out var complete, CancellationToken.None);
        return complete && strokes.Count > 0 ? InkRect(strokes, ReadWidth(annotation)) : null;
    }

    /// <summary>The strokes of an ink in user space, within the limits (see <see cref="ReadGeometry(PdfiumPage, int, DisplayTransform, CancellationToken)"/>).</summary>
    private static List<FsPointF[]> ReadStrokes(nint annotation, out bool complete, CancellationToken cancellationToken)
    {
        var strokes = new List<FsPointF[]>();
        var count = Fpdf.FPDFAnnot_GetInkListCount(annotation).Value;
        complete = count <= MaxInkStrokes;
        var budget = MaxInkPoints;
        for (nuint path = 0; path < Math.Min(count, MaxInkStrokes); path++)
        {
            if (path % PdfiumAnnotations.CancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var length = Fpdf.FPDFAnnot_GetInkListPath(annotation, new CULong(path), null, new CULong(0)).Value;
            if (length < 2)
            {
                // An empty stroke or a single point draws nothing (butt caps); dropping it loses nothing. PDFium also ends
                // an array at a token it cannot read (NaN), so such a stroke may be what is left of a damaged one.
                continue;
            }

            if (length > (nuint)budget)
            {
                complete = false;
                continue;
            }

            var points = new FsPointF[(int)length];
            nuint written;
            fixed (FsPointF* buffer = points)
            {
                written = Fpdf.FPDFAnnot_GetInkListPath(annotation, new CULong(path), buffer, new CULong(length)).Value;
            }

            if (written != length || !Array.TrueForAll(points, p => PdfiumAnnotations.IsSane(p.X) && PdfiumAnnotations.IsSane(p.Y)))
            {
                complete = false;
                continue;
            }

            budget -= (int)length;
            strokes.Add(points);
        }

        return strokes;
    }

    /// <summary><c>/Border</c>, else the first line width of the normal appearance, else <see cref="DefaultWidth"/>.</summary>
    private static double ReadWidth(nint annotation)
    {
        float horizontal, vertical, border;
        if (Fpdf.FPDFAnnot_GetBorder(annotation, &horizontal, &vertical, &border) != 0
            && PdfiumAnnotations.IsSane(border) && border >= 0)
        {
            return border;
        }

        var length = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, null, new CULong(0)).Value;
        if (length <= 2 || length > PdfiumAnnotations.MaxAppearanceBytes)
        {
            return DefaultWidth;
        }

        var text = new char[(int)length / 2];
        nuint written;
        fixed (char* pointer = text)
        {
            written = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, (ushort*)pointer, new CULong(length)).Value;
        }

        if (written != length)
        {
            return DefaultWidth;
        }

        var content = text.AsSpan(0, text.Length - 1);
        var width = WidthFromAppearance(content[..Math.Min(content.Length, PdfiumAnnotations.AppearanceScanLength)]);
        return width is { } value && value <= PdfiumAnnotations.MaxCoordinate ? value : DefaultWidth;
    }

    /// <summary>Checks the strokes and converts them to user space.</summary>
    private static FsPointF[][] ToUser(DisplayTransform transform, IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes)
    {
        ArgumentNullException.ThrowIfNull(strokes, nameof(PdfiumDrawing.Strokes));
        if (strokes.Count == 0)
        {
            throw new ArgumentException("A drawing needs at least one stroke.", nameof(strokes));
        }

        if (strokes.Count > MaxInkStrokes)
        {
            throw new ArgumentOutOfRangeException(nameof(strokes), strokes.Count, $"A drawing has at most {MaxInkStrokes} strokes.");
        }

        long total = 0;
        foreach (var stroke in strokes)
        {
            total += stroke?.Count ?? 0;
        }

        if (total > MaxInkPoints)
        {
            throw new ArgumentOutOfRangeException(nameof(strokes), total, $"A drawing has at most {MaxInkPoints} points.");
        }

        var result = new FsPointF[strokes.Count][];
        for (var s = 0; s < strokes.Count; s++)
        {
            var stroke = strokes[s];
            if (stroke is null || stroke.Count < 2)
            {
                throw new ArgumentException("A stroke of a drawing needs at least two points.", nameof(strokes));
            }

            var points = new FsPointF[stroke.Count];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = ToUserPoint(transform, stroke[i].X, stroke[i].Y, nameof(strokes));
            }

            result[s] = points;
        }

        return result;
    }

    private static FsRectF BoxToUser(DisplayTransform transform, (double Left, double Top, double Right, double Bottom) box)
    {
        if (!(box.Right > box.Left) || !(box.Bottom > box.Top))
        {
            throw new ArgumentException("A rectangle or ellipse needs a width and a height.", nameof(box));
        }

        var a = ToUserPoint(transform, box.Left, box.Top, nameof(box));
        var b = ToUserPoint(transform, box.Right, box.Bottom, nameof(box));
        return new FsRectF { Left = Math.Min(a.X, b.X), Bottom = Math.Min(a.Y, b.Y), Right = Math.Max(a.X, b.X), Top = Math.Max(a.Y, b.Y) };
    }

    private static FsPointF ToUserPoint(DisplayTransform transform, double x, double y, string parameter)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
        {
            throw new ArgumentException("A point of the drawing is not finite.", parameter);
        }

        var (userX, userY) = transform.ToUser(x, y);
        var point = new FsPointF { X = (float)userX, Y = (float)userY };
        if (!PdfiumAnnotations.IsSane(point.X) || !PdfiumAnnotations.IsSane(point.Y))
        {
            throw new ArgumentException($"A point of the drawing lies beyond {PdfiumAnnotations.MaxCoordinate} points.", parameter);
        }

        return point;
    }

    private static (double Left, double Top, double Right, double Bottom) BoxToDisplay(DisplayTransform transform, FsRectF rect)
    {
        var a = transform.ToDisplay(rect.Left, rect.Top);
        var b = transform.ToDisplay(rect.Right, rect.Bottom);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>
    /// The <c>/Rect</c> of an ink before PDFium's generator adds half the width to it: the hull of the points plus the
    /// farthest a miter join reaches beyond half the width (for the 50-degree tip of an arrow w / (2 sin 25°) - w / 2).
    /// Joins sharper than the miter limit are beveled and reach no further than half the width; caps are butt caps.
    /// </summary>
    private static FsRectF InkRect(IReadOnlyList<FsPointF[]> strokes, double width)
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        var excess = 0.0;
        foreach (var stroke in strokes)
        {
            foreach (var point in stroke)
            {
                left = Math.Min(left, point.X);
                bottom = Math.Min(bottom, point.Y);
                right = Math.Max(right, point.X);
                top = Math.Max(top, point.Y);
            }

            excess = Math.Max(excess, MiterExcess(stroke, width));
        }

        return new FsRectF
        {
            Left = (float)(left - excess),
            Bottom = (float)(bottom - excess),
            Right = (float)(right + excess),
            Top = (float)(top + excess),
        };
    }

    /// <summary>How far the miter joins of a stroke reach beyond half the width; repeated points are passed over.</summary>
    private static double MiterExcess(FsPointF[] stroke, double width)
    {
        var excess = 0.0;
        var previous = -1;
        var corner = -1;
        for (var i = 0; i < stroke.Length; i++)
        {
            if (corner >= 0 && stroke[i].X == stroke[corner].X && stroke[i].Y == stroke[corner].Y)
            {
                continue;
            }

            if (previous >= 0)
            {
                // The angle at the corner between the way back and the way on.
                double ax = stroke[previous].X - stroke[corner].X, ay = stroke[previous].Y - stroke[corner].Y;
                double bx = stroke[i].X - stroke[corner].X, by = stroke[i].Y - stroke[corner].Y;
                var cos = ((ax * bx) + (ay * by)) / (Math.Sqrt((ax * ax) + (ay * ay)) * Math.Sqrt((bx * bx) + (by * by)));
                var sinHalf = Math.Sqrt(Math.Max(0, (1 - cos) / 2));
                if (sinHalf > 0 && 1 / sinHalf <= MiterLimit)
                {
                    excess = Math.Max(excess, ((1 / sinHalf) - 1) * width / 2);
                }
            }

            previous = corner;
            corner = i;
        }

        return excess;
    }

    /// <summary>Whether the annotation has no <c>/CA</c> or a number of at least 1 there.</summary>
    private static bool IsOpaque(nint annotation)
    {
        if (!PdfiumAnnotations.HasKey(annotation, "CA\0"u8))
        {
            return true;
        }

        float opacity;
        fixed (byte* key = "CA\0"u8)
        {
            return Fpdf.FPDFAnnot_GetNumberValue(annotation, key, &opacity) != 0 && opacity >= 1;
        }
    }

    private static bool IsSane(FsRectF rect) =>
        PdfiumAnnotations.IsSane(rect.Left) && PdfiumAnnotations.IsSane(rect.Top)
        && PdfiumAnnotations.IsSane(rect.Right) && PdfiumAnnotations.IsSane(rect.Bottom);
}
