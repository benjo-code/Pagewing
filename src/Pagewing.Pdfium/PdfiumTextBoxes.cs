using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// A text box in points from the top left of the displayed page (after rotation and CropBox). Pagewing's own type
/// (<c>TextBoxSpec</c> in Core) is mapped to and from this one, because <c>Pagewing.Pdfium</c> does not know Core.
/// </summary>
/// <param name="Box">The box (<c>/Rect</c>).</param>
/// <param name="Text">The text with line feeds (<c>/Contents</c>, whose CR and CR LF are read as line feeds).</param>
/// <param name="FontSize">The font size in points (<c>/DA</c>).</param>
/// <param name="Color">The colour of the text (<c>/DA</c>).</param>
internal sealed record PdfiumTextBox(
    (double Left, double Top, double Right, double Bottom) Box,
    string Text,
    double FontSize,
    (byte R, byte G, byte B) Color)
{
    /// <summary>
    /// Gets whether the text box is Pagewing's and unchanged since (read back only): its appearance starts with Pagewing's
    /// marker, whose fingerprint matches the text, size and colour and whose box matches <c>/Rect</c>, and it has no flag
    /// besides Print. Only such a text box may be moved, resized, retyped or recoloured; another program's, or one another
    /// program changed (Acrobat replaces the appearance when the text is edited there), can only be deleted.
    /// </summary>
    public bool IsOwn { get; init; }
}

/// <summary>A line of a text box: its text and where it starts, in points from the top left of the displayed page.</summary>
/// <param name="Text">The text, every character of it in the glyph table.</param>
/// <param name="X">Where the first glyph starts.</param>
/// <param name="Baseline">The baseline.</param>
internal sealed record PdfiumTextLine(string Text, double X, double Baseline);

/// <summary>
/// Creates and reads text boxes: FreeText annotations whose normal appearance Pagewing writes itself (<c>FPDFAnnot_SetAP</c>)
/// as the glyph outlines of the text box font (<see cref="PdfiumGlyphs"/>), filled in the text colour, without any font in
/// the file (variant b2, <c>Docs/DECISIONS.md</c>, entries "Textfeld: Erscheinungsbild und Entscheidung (M10)" and
/// "Textfeld: Umsetzung und Grenzen (M10)"). Every method must be called on the PDFium thread and throws
/// <see cref="PdfiumThreadViolationException"/> otherwise; every annotation handle is closed before a method returns.
/// </summary>
/// <remarks>
/// <para>
/// The dictionary carries what other programs need to show and edit the text as their own: <c>/Contents</c> (lines
/// separated by CR), <c>/DA</c> (<c>/Helv</c>, the size, the colour as <c>rg</c>), <c>/DS</c>, <c>/RC</c> (XHTML),
/// <c>/Border [0 0 0]</c>, flag Print; no <c>/C</c> (for FreeText the background) and no <c>/Popup</c>. Acrobat edits such a
/// box directly on the page and then draws it anew with Helvetica (its appearance replaces Pagewing's), on a page with
/// <c>/Rotate</c> turned with the page, because PDFium cannot write the <c>/Rotate</c> of the annotation.
/// </para>
/// <para>
/// The appearance starts with the comment <c>%PagewingTextBox 1 &lt;fingerprint&gt; &lt;l b r t&gt;</c>: the fingerprint
/// is a SHA-256 of the text, size and colour, the numbers the <c>/Rect</c> it was drawn for. Each glyph is
/// <c>q a b c d e f cm &lt;outline&gt; f Q</c> with the outline in thousandths of an em; the matrix carries the position,
/// the size and the turn and offset of the page (<see cref="DisplayTransform"/>), so the text stands upright on screen on
/// every page.
/// </para>
/// </remarks>
internal static unsafe class PdfiumTextBoxes
{
    /// <summary>The comment the appearance of a Pagewing text box starts with, before its fingerprint and box.</summary>
    public const string Marker = "%PagewingTextBox 1 ";

    /// <summary>The longest appearance written or read, in characters (UTF-16LE: twice as many bytes).</summary>
    public const int MaxAppearanceChars = 4 * 1024 * 1024;

    /// <summary>How far the box of the marker may lie from <c>/Rect</c>, in points (both are written rounded).</summary>
    private const double RectTolerance = 0.02;

    /// <summary>The font size of a FreeText without one in <c>/DA</c> (the default of a text field).</summary>
    private const double DefaultFontSize = 12;

    /// <summary>
    /// Creates a text box: <c>/Rect</c>, <c>/Border [0 0 0]</c>, flag Print, <c>/Contents</c>, <c>/DA</c>, <c>/DS</c>,
    /// <c>/RC</c>, <c>/NM</c>, <c>/M</c>, <c>/CreationDate</c> (<paramref name="creationDate"/> when given, else
    /// <paramref name="date"/>), <c>/T</c> when given, and the appearance drawn from <paramref name="lines"/>. A line whose top
    /// lies below the box is left out of the appearance (the box clips what reaches beyond it). Everything is checked before
    /// anything is added; on failure nothing is left on the page.
    /// </summary>
    /// <returns>The index of the new annotation.</returns>
    /// <exception cref="ArgumentException">A character of a line is not in <paramref name="glyphs"/>, the box is empty or beyond <see cref="PdfiumAnnotations.MaxCoordinate"/>, or a value is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The appearance would be longer than <see cref="MaxAppearanceChars"/>.</exception>
    /// <exception cref="InvalidOperationException">PDFium refused to create or fill the annotation.</exception>
    public static int Create(
        PdfiumPage page,
        PdfiumTextBox box,
        IReadOnlyList<PdfiumTextLine> lines,
        PdfiumGlyphTable glyphs,
        string? author,
        string name,
        string date,
        string? creationDate = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(box);
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(glyphs);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(date);
        if (!(box.FontSize > 0) || !double.IsFinite(box.FontSize))
        {
            throw new ArgumentException("The font size must be above 0.", nameof(box));
        }

        var native = page.Access();
        if (!DisplayTransform.TryCreate(native, page.Width, page.Height, out var transform))
        {
            throw new InvalidOperationException("PDFium cannot convert the coordinates of the page.");
        }

        var rect = BoxToUser(transform, box.Box);
        var appearance = Appearance(transform, box, rect, lines, glyphs);
        var annotation = Fpdf.FPDFPage_CreateAnnot(native, FpdfConstants.AnnotFreeText);
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

            // Writes /Border [0 0 0] (no frame in other programs) and removes an appearance; the own one comes last.
            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetBorder(annotation, 0, 0, 0));
            PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetFlags(annotation, FpdfConstants.AnnotFlagPrint));
            var size = FormatSize(box.FontSize);
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "Contents\0"u8, box.Text.Replace('\n', '\r')));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "DA\0"u8, DefaultAppearance(size, box.Color)));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "DS\0"u8, DefaultStyle(box.FontSize, box.Color)));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "RC\0"u8, RichText(box.Text, box.FontSize, box.Color)));
            if (!string.IsNullOrEmpty(author))
            {
                PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "T\0"u8, author));
            }

            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "NM\0"u8, name));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "M\0"u8, date));
            PdfiumAnnotations.Check(PdfiumAnnotations.WriteString(annotation, "CreationDate\0"u8, string.IsNullOrEmpty(creationDate) ? date : creationDate));
            fixed (char* content = appearance)
            {
                PdfiumAnnotations.Check(Fpdf.FPDFAnnot_SetAP(annotation, FpdfConstants.AnnotAppearanceNormal, (ushort*)content));
            }

            if (!PdfiumAnnotations.HasKey(annotation, "AP\0"u8))
            {
                throw new InvalidOperationException("PDFium did not keep the appearance of the text box.");
            }

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

    /// <summary>Reads the text box at <paramref name="index"/> with the page's <see cref="DisplayTransform"/>; see the other overload.</summary>
    public static PdfiumTextBox? Read(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        return DisplayTransform.TryCreate(page.Access(), page.Width, page.Height, out var transform) ? Read(page, index, transform) : null;
    }

    /// <summary>
    /// Reads the FreeText at <paramref name="index"/>: <see langword="null"/> for any other subtype and for a damaged one (no
    /// <c>/Rect</c>, or coordinates that are not finite or beyond <see cref="PdfiumAnnotations.MaxCoordinate"/>). A missing
    /// size in <c>/DA</c> reads as 12 pt, a missing colour as black, missing <c>/Contents</c> as an empty text.
    /// <see cref="PdfiumTextBox.IsOwn"/> needs the appearance, which is read up to <see cref="MaxAppearanceChars"/>.
    /// </summary>
    public static PdfiumTextBox? Read(PdfiumPage page, int index, DisplayTransform transform)
    {
        ArgumentNullException.ThrowIfNull(page);
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return null;
        }

        char[]? scratch = null;
        try
        {
            FsRectF rect;
            if (Fpdf.FPDFAnnot_GetSubtype(annotation) != FpdfConstants.AnnotFreeText || !PdfiumAnnotations.HasKey(annotation, "Rect\0"u8)
                || Fpdf.FPDFAnnot_GetRect(annotation, &rect) == 0 || !IsSane(rect))
            {
                return null;
            }

            var text = NormalizeContents(PdfiumAnnotations.ReadString(annotation, "Contents\0"u8) ?? string.Empty);
            var (size, color) = ParseDefaultAppearance(PdfiumAnnotations.ReadString(annotation, "DA\0"u8));
            var fontSize = size ?? DefaultFontSize;
            var rgb = color ?? ((byte)0, (byte)0, (byte)0);
            var own = (Fpdf.FPDFAnnot_GetFlags(annotation) & ~FpdfConstants.AnnotFlagPrint) == 0
                && size is not null
                && color is not null
                && MarkerMatches(ReadAppearance(annotation, ref scratch), rect, Fingerprint(text, FormatSize(fontSize), rgb));
            return new PdfiumTextBox(BoxToDisplay(transform, rect), text, fontSize, rgb) { IsOwn = own };
        }
        finally
        {
            if (scratch is not null)
            {
                ArrayPool<char>.Shared.Return(scratch);
            }

            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// The normal appearance of the annotation as text, up to <see cref="MaxAppearanceChars"/> (empty when missing or longer),
    /// decoded into <paramref name="scratch"/> (rented from <see cref="ArrayPool{T}.Shared"/> on first use; the caller returns it).
    /// </summary>
    internal static ReadOnlySpan<char> ReadAppearance(nint annotation, ref char[]? scratch)
    {
        var length = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, null, new CULong(0)).Value;
        if (length <= 2 || length > (nuint)MaxAppearanceChars * 2)
        {
            return [];
        }

        var chars = (int)length / 2;
        if (scratch is null || scratch.Length < chars)
        {
            if (scratch is not null)
            {
                ArrayPool<char>.Shared.Return(scratch);
            }

            scratch = ArrayPool<char>.Shared.Rent(chars);
        }

        nuint written;
        fixed (char* pointer = scratch)
        {
            written = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, (ushort*)pointer, new CULong(length)).Value;
        }

        return written == length ? scratch.AsSpan(0, chars - 1) : [];
    }

    /// <summary>
    /// Whether an appearance starts with Pagewing's marker for <paramref name="fingerprint"/> and a box within
    /// <see cref="RectTolerance"/> of <paramref name="rect"/>.
    /// </summary>
    internal static bool MarkerMatches(ReadOnlySpan<char> appearance, FsRectF rect, string fingerprint)
    {
        if (!appearance.StartsWith(Marker, StringComparison.Ordinal))
        {
            return false;
        }

        var end = appearance.IndexOfAny('\r', '\n');
        var line = (end < 0 ? appearance : appearance[..end])[Marker.Length..];
        Span<Range> parts = stackalloc Range[6];
        if (line.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries) != 5 || !line[parts[0]].SequenceEqual(fingerprint))
        {
            return false;
        }

        ReadOnlySpan<float> expected = [rect.Left, rect.Bottom, rect.Right, rect.Top];
        for (var i = 0; i < 4; i++)
        {
            if (!double.TryParse(line[parts[i + 1]], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !(Math.Abs(value - expected[i]) <= RectTolerance))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The fingerprint of a text box's marker: the first 16 bytes of the SHA-256 of text, size and colour, in hex.</summary>
    internal static string Fingerprint(string text, string size, (byte R, byte G, byte B) color)
    {
        var input = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{text}\n{size}\n{color.R} {color.G} {color.B}"));
        return Convert.ToHexStringLower(SHA256.HashData(input).AsSpan(0, 16));
    }

    /// <summary>
    /// The font size (the last operand before <c>Tf</c>) and the fill colour (<c>rg</c>, <c>g</c> or <c>k</c>, the last one
    /// set) of a <c>/DA</c>; <see langword="null"/> for what it does not set or sets out of range (size above 0 and at most
    /// 10,000 points).
    /// </summary>
    internal static (double? Size, (byte R, byte G, byte B)? Color) ParseDefaultAppearance(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return (null, null);
        }

        double? size = null;
        (byte R, byte G, byte B)? color = null;
        var operands = new List<double>(4);
        foreach (var token in text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                operands.Add(number);
                continue;
            }

            switch (token)
            {
                case "Tf" when operands.Count >= 1 && operands[^1] is > 0 and <= 10_000:
                    size = operands[^1];
                    break;
                case "rg" when operands.Count >= 3:
                    color = ToColor(operands[^3], operands[^2], operands[^1]) ?? color;
                    break;
                case "g" when operands.Count >= 1:
                    color = ToColor(operands[^1], operands[^1], operands[^1]) ?? color;
                    break;
                case "k" when operands.Count >= 4 && operands[^1] is >= 0 and <= 1:
                    var black = 1 - operands[^1];
                    color = ToColor((1 - operands[^4]) * black, (1 - operands[^3]) * black, (1 - operands[^2]) * black) ?? color;
                    break;
            }

            operands.Clear();
        }

        return (size, color);

        static (byte R, byte G, byte B)? ToColor(double r, double g, double b) =>
            r is >= 0 and <= 1 && g is >= 0 and <= 1 && b is >= 0 and <= 1
                ? (ToByte(r), ToByte(g), ToByte(b))
                : null;

        static byte ToByte(double channel) => (byte)Math.Round(channel * 255, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The glyphs of the appearance of a Pagewing text box (as <see cref="Create"/> writes it: <c>q</c>, <c>Q</c>, <c>cm</c>,
    /// <c>rg</c>, <c>m</c>, <c>l</c>, <c>c</c>, <c>h</c>, <c>f</c> and the marker comment) as flattened contours in points from
    /// the top left of the displayed page, charging each point to <paramref name="pointBudget"/>. Reading stops, keeping what
    /// it has, when the budget is used up; anything else in the stream makes it <see langword="null"/>.
    /// </summary>
    internal static List<DarkGlyph>? ParseGlyphs(ReadOnlySpan<char> content, DisplayTransform transform, ref long pointBudget)
    {
        const int MaxDepth = 32;
        var glyphs = new List<DarkGlyph>();
        var contours = new List<(double X, double Y)[]>();
        var points = new List<(double X, double Y)>();
        Span<double> operands = stackalloc double[6];
        var count = 0;
        var stack = new Stack<(double A, double B, double C, double D, double E, double F)>();
        (double A, double B, double C, double D, double E, double F) ctm = (1, 0, 0, 1, 0, 0);
        (double X, double Y) last = default;
        var i = 0;
        while (i < content.Length && pointBudget > 0)
        {
            var c = content[i];
            if (PdfiumAnnotations.IsWhite(c))
            {
                i++;
                continue;
            }

            if (c == '%')
            {
                while (i < content.Length && content[i] is not ('\r' or '\n'))
                {
                    i++;
                }

                continue;
            }

            var start = i;
            while (i < content.Length && !PdfiumAnnotations.IsWhite(content[i]) && !PdfiumAnnotations.IsDelimiter(content[i]))
            {
                i++;
            }

            if (i == start)
            {
                return null;
            }

            var token = content[start..i];
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                if (count == operands.Length || !double.IsFinite(number))
                {
                    return null;
                }

                operands[count++] = number;
                continue;
            }

            switch (token)
            {
                case "q" when count == 0 && stack.Count < MaxDepth:
                    stack.Push(ctm);
                    break;
                case "Q" when count == 0 && stack.Count > 0:
                    ctm = stack.Pop();
                    break;
                case "cm" when count == 6:
                    ctm = (
                        (operands[0] * ctm.A) + (operands[1] * ctm.C),
                        (operands[0] * ctm.B) + (operands[1] * ctm.D),
                        (operands[2] * ctm.A) + (operands[3] * ctm.C),
                        (operands[2] * ctm.B) + (operands[3] * ctm.D),
                        (operands[4] * ctm.A) + (operands[5] * ctm.C) + ctm.E,
                        (operands[4] * ctm.B) + (operands[5] * ctm.D) + ctm.F);
                    break;
                case "rg" when count == 3:
                    break;
                case "m" when count == 2:
                    Finish(points, contours);
                    last = ToDisplay(operands[0], operands[1]);
                    points.Add(last);
                    pointBudget--;
                    break;
                case "l" when count == 2 && points.Count > 0:
                    last = ToDisplay(operands[0], operands[1]);
                    points.Add(last);
                    pointBudget--;
                    break;
                case "c" when count == 6 && points.Count > 0:
                    var (p1, p2, p3) = (ToDisplay(operands[0], operands[1]), ToDisplay(operands[2], operands[3]), ToDisplay(operands[4], operands[5]));
                    var length = Distance(last, p1) + Distance(p1, p2) + Distance(p2, p3);
                    var steps = (int)Math.Clamp(Math.Ceiling(3 * Math.Sqrt(length)), 2, 64);
                    for (var s = 1; s <= steps; s++)
                    {
                        var t = s / (double)steps;
                        var u = 1 - t;
                        var (w0, w1, w2, w3) = (u * u * u, 3 * u * u * t, 3 * u * t * t, t * t * t);
                        points.Add(((w0 * last.X) + (w1 * p1.X) + (w2 * p2.X) + (w3 * p3.X), (w0 * last.Y) + (w1 * p1.Y) + (w2 * p2.Y) + (w3 * p3.Y)));
                    }

                    pointBudget -= steps;
                    last = p3;
                    break;
                case "h" when count == 0:
                    Finish(points, contours);
                    break;
                case "f" when count == 0:
                    Finish(points, contours);
                    if (contours.Count > 0)
                    {
                        glyphs.Add(new DarkGlyph([.. contours]));
                        contours.Clear();
                    }

                    break;
                default:
                    return null;
            }

            count = 0;
        }

        return glyphs;

        (double X, double Y) ToDisplay(double u, double v)
        {
            var point = transform.ToDisplay((ctm.A * u) + (ctm.C * v) + ctm.E, (ctm.B * u) + (ctm.D * v) + ctm.F);
            return (point.X, point.Y);
        }

        static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

        static void Finish(List<(double X, double Y)> points, List<(double X, double Y)[]> contours)
        {
            if (points.Count >= 3)
            {
                contours.Add([.. points]);
            }

            points.Clear();
        }
    }

    /// <summary>The size as <c>/DA</c> and the fingerprint write it: at most two decimals.</summary>
    internal static string FormatSize(double size) => size.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Reads <c>/Contents</c> with CR LF and CR as line feeds.</summary>
    internal static string NormalizeContents(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    /// <summary>The appearance stream (see the remarks of the class).</summary>
    private static string Appearance(DisplayTransform transform, PdfiumTextBox box, FsRectF rect, IReadOnlyList<PdfiumTextLine> lines, PdfiumGlyphTable glyphs)
    {
        var (r, g, b) = box.Color;
        var content = new StringBuilder();
        content.Append(Marker)
            .Append(Fingerprint(box.Text, FormatSize(box.FontSize), box.Color))
            .Append(CultureInfo.InvariantCulture, $" {F(rect.Left)} {F(rect.Bottom)} {F(rect.Right)} {F(rect.Top)}\n")
            .Append(CultureInfo.InvariantCulture, $"q {C(r)} {C(g)} {C(b)} rg\n");
        var scale = box.FontSize / 1000;
        var (ax, ay) = transform.AlongX;
        var (bx, by) = transform.AlongY;
        var (a, bb, c, d) = (ax * scale, ay * scale, -bx * scale, -by * scale);
        foreach (var line in lines)
        {
            ArgumentNullException.ThrowIfNull(line);
            if (!double.IsFinite(line.X) || !double.IsFinite(line.Baseline))
            {
                throw new ArgumentException("A line of the text box is not finite.", nameof(lines));
            }

            var visible = line.Baseline - (glyphs.Ascent * box.FontSize) < box.Box.Bottom;
            var x = line.X;
            foreach (var character in line.Text)
            {
                if (!glyphs.TryGet(character, out var glyph))
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The text box font has no glyph for U+{(int)character:X4}."), nameof(lines));
                }

                if (visible && glyph.Outline.Length > 0)
                {
                    var (e, f) = transform.ToUser(x, line.Baseline);
                    content.Append(CultureInfo.InvariantCulture, $"q {M(a)} {M(bb)} {M(c)} {M(d)} {F(e)} {F(f)} cm ").Append(glyph.Outline).Append(" Q\n");
                    if (content.Length > MaxAppearanceChars)
                    {
                        throw new ArgumentOutOfRangeException(nameof(lines), "The appearance of the text box would be too long.");
                    }
                }

                x += glyph.Advance * box.FontSize;
            }
        }

        return content.Append('Q').ToString();

        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string M(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);
        static string C(byte value) => (value / 255.0).ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string DefaultAppearance(string size, (byte R, byte G, byte B) color) =>
        string.Create(CultureInfo.InvariantCulture, $"/Helv {size} Tf {color.R / 255.0:0.####} {color.G / 255.0:0.####} {color.B / 255.0:0.####} rg");

    private static string DefaultStyle(double size, (byte R, byte G, byte B) color) =>
        string.Create(CultureInfo.InvariantCulture, $"font: Helvetica,sans-serif {size:0.0#}pt; text-align:left; color:#{color.R:X2}{color.G:X2}{color.B:X2}");

    /// <summary>The text as the XHTML of <c>/RC</c>, styled like <c>/DS</c>, one paragraph per line (as Acrobat writes it).</summary>
    private static string RichText(string text, double size, (byte R, byte G, byte B) color)
    {
        var builder = new StringBuilder(
            "<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\" xfa:APIVersion=\"Acrobat:11.0.0\" xfa:spec=\"2.0.2\" ");
        builder.Append(CultureInfo.InvariantCulture, $"style=\"font-size:{size:0.0#}pt;text-align:left;color:#{color.R:X2}{color.G:X2}{color.B:X2};font-weight:normal;font-style:normal;font-family:Helvetica,sans-serif;font-stretch:normal\">");
        foreach (var line in text.Split('\n'))
        {
            builder.Append("<p dir=\"ltr\">");
            foreach (var c in line)
            {
                builder.Append(c switch
                {
                    '&' => "&amp;",
                    '<' => "&lt;",
                    '>' => "&gt;",
                    '"' => "&quot;",
                    _ => c.ToString(),
                });
            }

            builder.Append("</p>");
        }

        return builder.Append("</body>").ToString();
    }

    private static FsRectF BoxToUser(DisplayTransform transform, (double Left, double Top, double Right, double Bottom) box)
    {
        if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top) || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom)
            || !(box.Right > box.Left) || !(box.Bottom > box.Top))
        {
            throw new ArgumentException("A text box needs a finite width and height.", nameof(box));
        }

        var (ax, ay) = transform.ToUser(box.Left, box.Top);
        var (bx, by) = transform.ToUser(box.Right, box.Bottom);
        var rect = new FsRectF
        {
            Left = (float)Math.Min(ax, bx),
            Bottom = (float)Math.Min(ay, by),
            Right = (float)Math.Max(ax, bx),
            Top = (float)Math.Max(ay, by),
        };
        if (!IsSane(rect))
        {
            throw new ArgumentException($"The text box lies beyond {PdfiumAnnotations.MaxCoordinate} points.", nameof(box));
        }

        return rect;
    }

    private static (double Left, double Top, double Right, double Bottom) BoxToDisplay(DisplayTransform transform, FsRectF rect)
    {
        var a = transform.ToDisplay(rect.Left, rect.Top);
        var b = transform.ToDisplay(rect.Right, rect.Bottom);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    private static bool IsSane(FsRectF rect) =>
        PdfiumAnnotations.IsSane(rect.Left) && PdfiumAnnotations.IsSane(rect.Top)
        && PdfiumAnnotations.IsSane(rect.Right) && PdfiumAnnotations.IsSane(rect.Bottom);
}
