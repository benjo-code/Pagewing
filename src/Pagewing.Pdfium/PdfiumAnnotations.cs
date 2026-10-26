using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>A point in PDF user space (origin bottom left, y up).</summary>
internal readonly record struct PdfiumPoint(double X, double Y);

/// <summary>The four corners of one quadrilateral of <c>/QuadPoints</c>, in the order of the array (x1 y1 … x4 y4).</summary>
internal readonly record struct PdfiumQuad(PdfiumPoint P1, PdfiumPoint P2, PdfiumPoint P3, PdfiumPoint P4);

/// <summary>An annotation of a page as PDFium reports it. Rectangles are points from the top left of the displayed page.</summary>
/// <param name="Index">The index in the page's <c>/Annots</c>.</param>
/// <param name="Subtype">The <c>FPDF_ANNOTATION_SUBTYPE</c>.</param>
/// <param name="SubtypeName">The <c>/Subtype</c> name, for example "Highlight".</param>
/// <param name="Name">The <c>/NM</c>, or <see langword="null"/>.</param>
/// <param name="Color">The colour, or <see langword="null"/> without <c>/C</c> or when it cannot be read.</param>
/// <param name="Quads">The quadrilaterals in user space; empty for annotations other than highlight, underline and strike-out.</param>
/// <param name="QuadRects">The same quadrilaterals as boxes on the displayed page.</param>
/// <param name="Bounds">The <c>/Rect</c> as a box on the displayed page.</param>
/// <param name="Contents">The <c>/Contents</c>, or <see langword="null"/> when missing or empty.</param>
/// <param name="Author">The <c>/T</c>, or <see langword="null"/>.</param>
/// <param name="Modified">The raw <c>/M</c> date string, or <see langword="null"/>.</param>
/// <param name="HasReplies">Whether another annotation of the page points at this one with <c>/IRT</c>.</param>
internal sealed record PdfiumAnnotation(
    int Index,
    int Subtype,
    string SubtypeName,
    string? Name,
    (byte R, byte G, byte B)? Color,
    IReadOnlyList<PdfiumQuad> Quads,
    IReadOnlyList<(double Left, double Top, double Right, double Bottom)> QuadRects,
    (double Left, double Top, double Right, double Bottom) Bounds,
    string? Contents,
    string? Author,
    string? Modified,
    bool HasReplies);

/// <summary>
/// Reads, creates, changes and deletes the annotations of a loaded page. Every method must be called on the PDFium
/// thread and throws <see cref="PdfiumThreadViolationException"/> otherwise; every annotation handle is closed before
/// a method returns.
/// </summary>
/// <remarks>
/// Appearances are never written by Pagewing: a markup is created without <c>/AP</c>, and <see cref="GenerateAppearance"/>
/// renders the page once with <c>FPDF_ANNOT</c>, which makes PDFium's own generator build it (highlight with blend mode
/// Multiply, underline and strike-out as a line) and store it as its own Form XObject. A colour change drops the old
/// appearance first. See <c>Docs/DECISIONS.md</c>, entry "Erscheinungsbild der Annotationen: PDFium-Generator (Variante D) …".
/// </remarks>
internal static unsafe class PdfiumAnnotations
{
    /// <summary>
    /// The most entries of a page's <c>/Annots</c> that are looked at; further ones are ignored (the document lists at most
    /// 10,000 anyway).
    /// </summary>
    public const int MaxScannedPerPage = 20_000;

    /// <summary>
    /// The work allowed for resolving links (<c>/IRT</c>, <c>/Popup</c>, <c>/Parent</c>) of one page in one operation, in
    /// entries compared: <c>FPDFPage_GetAnnotIndex</c> walks <c>/Annots</c> and resolves each reference, so every link
    /// costs as much as the page has entries. On a page with many links the budget runs out; then every markup of the page
    /// counts as answered (read-only), and popups beyond the budget stay when a markup is deleted.
    /// </summary>
    public const long MaxLinkWork = 2_000_000;

    /// <summary>How many entries pass between two checks of the cancellation token.</summary>
    internal const int CancellationInterval = 64;

    /// <summary>What <see cref="LinkedIndex"/> returns when the link budget is used up.</summary>
    private const int LinkUnresolved = -2;

    /// <summary>The most quadrilaterals of one markup; a markup with more is treated as damaged and skipped.</summary>
    public const int MaxQuads = 4_096;

    /// <summary>The largest coordinate accepted (points); larger values, infinity and NaN make the annotation damaged.</summary>
    public const double MaxCoordinate = 10_000_000;

    /// <summary>The longest string value read, in bytes of UTF-16LE; a longer one reads as missing.</summary>
    private const int MaxStringBytes = 8 * 1024 * 1024;

    /// <summary>The longest appearance stream whose colour (or a drawing's line width) is looked for, in bytes of UTF-16LE.</summary>
    internal const int MaxAppearanceBytes = 1024 * 1024;

    /// <summary>The characters of the buffer an appearance stream is decoded into (<see cref="MaxAppearanceBytes"/>).</summary>
    private const int ScratchLength = MaxAppearanceBytes / 2;

    /// <summary>The part of an appearance stream that is searched for its colour (or a drawing's line width), in characters.</summary>
    internal const int AppearanceScanLength = 64 * 1024;

    /// <summary>The scale of the render that makes PDFium generate missing appearances (the bitmap is thrown away).</summary>
    private const double GenerateScale = 0.25;

    /// <summary>Whether a subtype is one of the text markups Pagewing creates and changes.</summary>
    public static bool IsMarkup(int subtype) =>
        subtype is FpdfConstants.AnnotHighlight or FpdfConstants.AnnotUnderline or FpdfConstants.AnnotStrikeout;

    /// <summary>Whether a subtype is one of the drawings Pagewing creates (ink, square, circle; see <see cref="PdfiumDrawings"/>).</summary>
    public static bool IsDrawing(int subtype) =>
        subtype is FpdfConstants.AnnotInk or FpdfConstants.AnnotSquare or FpdfConstants.AnnotCircle;

    /// <summary>Whether a subtype is a text box (FreeText; see <see cref="PdfiumTextBoxes"/>).</summary>
    public static bool IsTextBox(int subtype) => subtype == FpdfConstants.AnnotFreeText;

    /// <summary>
    /// Reads the annotations of a page in <c>/Annots</c> order, at most <paramref name="limit"/>. Popups and replies
    /// (entries with <c>/IRT</c>) are left out; damaged entries (no <c>/Rect</c>, coordinates that are not finite or
    /// beyond <see cref="MaxCoordinate"/>, a highlight, underline or strike-out without a complete quadrilateral or
    /// with more than <see cref="MaxQuads"/>) are skipped.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while reading.</exception>
    public static List<PdfiumAnnotation> Read(PdfiumPage page, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var result = new List<PdfiumAnnotation>();
        if (limit <= 0)
        {
            return result;
        }

        var scan = Scan(native, cancellationToken);
        var transform = PdfiumPageText.TransformOf(page);
        for (var index = 0; index < scan.Subtypes.Length && result.Count < limit; index++)
        {
            if (index % CancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (scan.IsReply[index] || scan.Subtypes[index] == FpdfConstants.AnnotPopup)
            {
                continue;
            }

            if (ReadOne(native, transform, index, scan.Subtypes[index], scan.HasReplies(index)) is { } annotation)
            {
                result.Add(annotation);
            }
        }

        return result;
    }

    /// <summary>Reads the annotation at <paramref name="index"/>, or <see langword="null"/> if it is missing, a popup, a reply or damaged.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while reading.</exception>
    public static PdfiumAnnotation? ReadAt(PdfiumPage page, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var scan = Scan(native, cancellationToken);
        if (index < 0 || index >= scan.Subtypes.Length || scan.IsReply[index] || scan.Subtypes[index] == FpdfConstants.AnnotPopup)
        {
            return null;
        }

        return ReadOne(native, PdfiumPageText.TransformOf(page), index, scan.Subtypes[index], scan.HasReplies(index));
    }

    /// <summary>
    /// Finds an annotation: by <c>/NM</c> when <paramref name="name"/> is given (then only by name), else by
    /// <paramref name="index"/>. Returns its index, or -1 if there is none.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while searching.</exception>
    public static int Find(PdfiumPage page, string? name, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var count = Math.Clamp(Fpdf.FPDFPage_GetAnnotCount(native), 0, MaxScannedPerPage);
        if (name is null)
        {
            return index >= 0 && index < count ? index : -1;
        }

        for (var i = 0; i < count; i++)
        {
            if (i % CancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var annotation = Fpdf.FPDFPage_GetAnnot(native, i);
            if (annotation == 0)
            {
                continue;
            }

            try
            {
                if (string.Equals(ReadString(annotation, "NM\0"u8), name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            finally
            {
                Fpdf.FPDFPage_CloseAnnot(annotation);
            }
        }

        return -1;
    }

    /// <summary>
    /// Whether the entry at <paramref name="index"/> is a reply (<c>/IRT</c>) or may be answered by one (an <c>/IRT</c>
    /// points at it, or the link budget ran out); <see langword="true"/> for an index outside the page.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while checking.</exception>
    internal static bool IsReplyOrAnswered(PdfiumPage page, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var scan = Scan(page.Access(), cancellationToken);
        return index < 0 || index >= scan.Subtypes.Length || scan.IsReply[index] || scan.HasReplies(index);
    }

    /// <summary>
    /// Whether Pagewing may change or delete the annotation: a highlight, underline or strike-out, a drawing (ink,
    /// square, circle) or a text box (FreeText), that is no reply and that no other annotation answers. Whether a drawing may also be replaced is
    /// <see cref="PdfiumDrawings.IsGeometryEditable"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled while checking.</exception>
    public static bool IsEditable(PdfiumPage page, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var scan = Scan(native, cancellationToken);
        return index >= 0 && index < scan.Subtypes.Length && (IsMarkup(scan.Subtypes[index]) || IsDrawing(scan.Subtypes[index]) || IsTextBox(scan.Subtypes[index]))
            && !scan.IsReply[index] && !scan.HasReplies(index);
    }

    /// <summary>The number of entries of the page's <c>/Annots</c> as PDFium counts them (popups, replies and links included).</summary>
    public static int CountEntries(PdfiumPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return Math.Max(0, Fpdf.FPDFPage_GetAnnotCount(page.Access()));
    }

    /// <summary>The <c>FPDF_ANNOTATION_SUBTYPE</c> of the entry at <paramref name="index"/>, or -1 if there is none.</summary>
    public static int SubtypeOf(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        return SubtypeAt(page.Access(), index);
    }

    /// <summary>
    /// Reads a string value of the entry at <paramref name="index"/> (for example <c>"CreationDate\0"u8</c>);
    /// <see langword="null"/> when the entry or the value is missing (see <see cref="ReadString"/>).
    /// </summary>
    public static string? ReadStringAt(PdfiumPage page, int index, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(page);
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return null;
        }

        try
        {
            return ReadString(annotation, key);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// Creates a highlight, underline or strike-out without an appearance (see <see cref="GenerateAppearance"/>):
    /// <c>/Rect</c> (the hull of the quadrilaterals), <c>/QuadPoints</c>, <c>/C</c>, <c>/F</c> Print, <c>/NM</c>,
    /// <c>/M</c>, <c>/CreationDate</c>, and <c>/Contents</c> and <c>/T</c> when given. On failure nothing is left on the page.
    /// </summary>
    /// <returns>The index of the new annotation.</returns>
    /// <exception cref="InvalidOperationException">PDFium refused to create or fill the annotation.</exception>
    public static int CreateMarkup(
        PdfiumPage page,
        int subtype,
        IReadOnlyList<PdfiumQuad> quads,
        (byte R, byte G, byte B) color,
        string? contents,
        string? author,
        string name,
        string date)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(quads);
        ArgumentOutOfRangeException.ThrowIfZero(quads.Count);
        if (!IsMarkup(subtype))
        {
            throw new ArgumentOutOfRangeException(nameof(subtype), subtype, "Only highlights, underlines and strike-outs are created.");
        }

        var native = page.Access();
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

            var rect = Hull(quads);
            Check(Fpdf.FPDFAnnot_SetRect(annotation, &rect));
            foreach (var quad in quads)
            {
                var points = new FsQuadPointsF
                {
                    X1 = (float)quad.P1.X,
                    Y1 = (float)quad.P1.Y,
                    X2 = (float)quad.P2.X,
                    Y2 = (float)quad.P2.Y,
                    X3 = (float)quad.P3.X,
                    Y3 = (float)quad.P3.Y,
                    X4 = (float)quad.P4.X,
                    Y4 = (float)quad.P4.Y,
                };
                Check(Fpdf.FPDFAnnot_AppendAttachmentPoints(annotation, &points));
            }

            Check(Fpdf.FPDFAnnot_SetColor(annotation, 0, color.R, color.G, color.B, 255));
            Check(Fpdf.FPDFAnnot_SetFlags(annotation, FpdfConstants.AnnotFlagPrint));
            if (!string.IsNullOrEmpty(contents))
            {
                Check(WriteString(annotation, "Contents\0"u8, contents));
            }

            if (!string.IsNullOrEmpty(author))
            {
                Check(WriteString(annotation, "T\0"u8, author));
            }

            Check(WriteString(annotation, "NM\0"u8, name));
            Check(WriteString(annotation, "M\0"u8, date));
            Check(WriteString(annotation, "CreationDate\0"u8, date));
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

    /// <summary>
    /// Changes an annotation: a new <paramref name="contents"/> (an existing <c>/RC</c> is replaced by the same text as
    /// plain XHTML, so old rich text cannot cover it; PDFium cannot remove a key); a new <paramref name="color"/> drops
    /// the appearance so that <see cref="GenerateAppearance"/> builds it anew; <paramref name="name"/> is written when
    /// the annotation has no <c>/NM</c>; <c>/M</c> becomes <paramref name="date"/>; the flag Print is set. The caller
    /// then makes sure the annotation has an appearance (<see cref="HasAppearance"/>, <see cref="GenerateAppearance"/>),
    /// also when this method throws after the old appearance was dropped.
    /// </summary>
    /// <exception cref="InvalidOperationException">PDFium refused a change.</exception>
    public static void Update(PdfiumPage page, int index, string? contents, (byte R, byte G, byte B)? color, string name, string date)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        var annotation = Fpdf.FPDFPage_GetAnnot(native, index);
        if (annotation == 0)
        {
            throw new InvalidOperationException("PDFium could not load the annotation.");
        }

        try
        {
            if (contents is not null)
            {
                Check(WriteString(annotation, "Contents\0"u8, contents));
                if (HasKey(annotation, "RC\0"u8))
                {
                    Check(WriteString(annotation, "RC\0"u8, RichTextOf(contents)));
                }
            }

            if (color is { } rgb)
            {
                // PDFium's generator widens the /Rect of an ink by half its width each time it builds the appearance, so the
                // /Rect is set anew from the strokes before the appearance is dropped (the width may come from it).
                var inkRect = Fpdf.FPDFAnnot_GetSubtype(annotation) == FpdfConstants.AnnotInk ? PdfiumDrawings.InkRectOf(annotation) : null;

                // FPDFAnnot_SetColor refuses an annotation with an appearance; null clears the appearance of every mode.
                Check(Fpdf.FPDFAnnot_SetAP(annotation, FpdfConstants.AnnotAppearanceNormal, null));
                Check(Fpdf.FPDFAnnot_SetColor(annotation, 0, rgb.R, rgb.G, rgb.B, 255));
                if (inkRect is { } rect)
                {
                    Check(Fpdf.FPDFAnnot_SetRect(annotation, &rect));
                }
            }

            if (ReadString(annotation, "NM\0"u8) is null)
            {
                Check(WriteString(annotation, "NM\0"u8, name));
            }

            Check(WriteString(annotation, "M\0"u8, date));
            Check(Fpdf.FPDFAnnot_SetFlags(annotation, Fpdf.FPDFAnnot_GetFlags(annotation) | FpdfConstants.AnnotFlagPrint));
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// Removes an annotation and its popups: the one its <c>/Popup</c> names (only if that entry is a popup) and every
    /// popup whose <c>/Parent</c> is the annotation, as far as <see cref="MaxLinkWork"/> reaches (a popup beyond it stays).
    /// </summary>
    /// <exception cref="InvalidOperationException">PDFium could not remove an entry.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before anything was removed.</exception>
    public static void Delete(PdfiumPage page, int index, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(page);
        var native = page.Access();
        // A link search walks the whole /Annots array, so it costs the raw entry count, not the clamped one.
        var entries = Math.Max(0, Fpdf.FPDFPage_GetAnnotCount(native));
        var count = Math.Min(entries, MaxScannedPerPage);
        var budget = MaxLinkWork;
        var remove = new SortedSet<int> { index };
        var annotation = Fpdf.FPDFPage_GetAnnot(native, index);
        if (annotation == 0)
        {
            throw new InvalidOperationException("PDFium could not load the annotation.");
        }

        try
        {
            var popup = LinkedIndex(native, annotation, "Popup\0"u8, entries, ref budget);
            if (popup >= 0 && popup != index && SubtypeAt(native, popup) == FpdfConstants.AnnotPopup)
            {
                remove.Add(popup);
            }
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }

        for (var i = 0; i < count && budget >= entries; i++)
        {
            if (i % CancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var candidate = Fpdf.FPDFPage_GetAnnot(native, i);
            if (candidate == 0)
            {
                continue;
            }

            try
            {
                if (Fpdf.FPDFAnnot_GetSubtype(candidate) == FpdfConstants.AnnotPopup && LinkedIndex(native, candidate, "Parent\0"u8, entries, ref budget) == index)
                {
                    remove.Add(i);
                }
            }
            finally
            {
                Fpdf.FPDFPage_CloseAnnot(candidate);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // From the back, so the indices still to remove stay valid.
        foreach (var entry in remove.Reverse())
        {
            if (Fpdf.FPDFPage_RemoveAnnot(native, entry) == 0)
            {
                throw new InvalidOperationException("PDFium could not remove the annotation.");
            }
        }
    }

    /// <summary>
    /// Makes PDFium generate the missing appearances of a page: one render with <c>FPDF_ANNOT</c> at a small scale builds
    /// <c>CPDF_AnnotList</c>, which writes an appearance into every annotation that has none. Call it with no other
    /// handle of the page open. Returns whether the annotation at <paramref name="index"/> has an appearance afterwards.
    /// </summary>
    /// <exception cref="PdfiumRenderException">The render failed.</exception>
    public static bool GenerateAppearance(PdfiumDocument document, int pageIndex, int index)
    {
        ArgumentNullException.ThrowIfNull(document);
        using (var work = new PdfiumRenderWork(document, pageIndex, GenerateScale, null, annotations: true, TimeSpan.MaxValue))
        {
            while (!work.Step())
            {
            }

            _ = work.Complete();
        }

        using var page = document.LoadPage(pageIndex);
        return HasAppearance(page, index);
    }

    /// <summary>Whether the annotation at <paramref name="index"/> has an <c>/AP</c>.</summary>
    public static bool HasAppearance(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        var annotation = Fpdf.FPDFPage_GetAnnot(page.Access(), index);
        if (annotation == 0)
        {
            return false;
        }

        try
        {
            return HasKey(annotation, "AP\0"u8);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>Removes the entry at <paramref name="index"/> of the page's <c>/Annots</c>.</summary>
    public static void Remove(PdfiumPage page, int index)
    {
        ArgumentNullException.ThrowIfNull(page);
        _ = Fpdf.FPDFPage_RemoveAnnot(page.Access(), index);
    }

    /// <summary>
    /// The colour an appearance stream paints with: the first fill colour (<c>rg</c>, <c>g</c>, <c>k</c>) when
    /// <paramref name="preferFill"/> is set, else the first stroke colour (<c>RG</c>, <c>G</c>, <c>K</c>), falling back to
    /// the other kind. PDFium reports no <c>/C</c> for an annotation with an appearance, so this is how such a colour is
    /// read. Strings, names, arrays, dictionaries and comments are skipped; the scan stops at an inline image. Operands
    /// outside 0 to 1 are ignored.
    /// </summary>
    internal static (byte R, byte G, byte B)? ColorFromAppearance(ReadOnlySpan<char> content, bool preferFill)
    {
        Span<double> operands = stackalloc double[4];
        var operandCount = 0;
        (byte R, byte G, byte B)? fill = null;
        (byte R, byte G, byte B)? stroke = null;
        var i = 0;
        while (i < content.Length)
        {
            var c = content[i];
            if (IsWhite(c))
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
                i = SkipString(content, i);
                operandCount = 0;
            }
            else if (c == '<' && i + 1 < content.Length && content[i + 1] == '<')
            {
                i += 2;
                operandCount = 0;
            }
            else if (c == '<')
            {
                var end = content[i..].IndexOf('>');
                i = end < 0 ? content.Length : i + end + 1;
                operandCount = 0;
            }
            else if (c is '>' or '[' or ']' or '{' or '}' or ')')
            {
                i++;
                operandCount = 0;
            }
            else if (c == '/')
            {
                i++;
                while (i < content.Length && !IsWhite(content[i]) && !IsDelimiter(content[i]))
                {
                    i++;
                }

                operandCount = 0;
            }
            else
            {
                var start = i;
                while (i < content.Length && !IsWhite(content[i]) && !IsDelimiter(content[i]))
                {
                    i++;
                }

                var token = content[start..i];
                if (c is (>= '0' and <= '9') or '+' or '-' or '.')
                {
                    if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        if (operandCount == operands.Length)
                        {
                            // Keep the last operands, as PDFium does (Span.CopyTo handles the overlap).
                            operands[1..].CopyTo(operands);
                            operandCount--;
                        }

                        operands[operandCount++] = number;
                    }
                    else
                    {
                        operandCount = 0;
                    }

                    continue;
                }

                if (token.SequenceEqual("BI"))
                {
                    break;
                }

                var colour = ToColor(token, operands[..operandCount], out var isFill);
                if (colour is not null)
                {
                    if (isFill)
                    {
                        fill ??= colour;
                    }
                    else
                    {
                        stroke ??= colour;
                    }

                    if ((preferFill ? fill : stroke) is { } preferred)
                    {
                        return preferred;
                    }
                }

                operandCount = 0;
            }
        }

        return preferFill ? fill ?? stroke : stroke ?? fill;
    }

    /// <summary>The plain text as the XHTML body of a <c>/RC</c> entry: one paragraph per line, with escaped characters.</summary>
    /// <remarks>
    /// Characters XML 1.0 does not allow are dropped (C0 controls other than tab, line feed and carriage return,
    /// U+FFFE, U+FFFF and lone surrogates); a vertical tab or form feed counts as a line break.
    /// </remarks>
    internal static string RichTextOf(string text)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\">");
        var lines = text.Replace('\v', '\n').Replace('\f', '\n').ReplaceLineEndings("\n").Split('\n');
        foreach (var line in lines)
        {
            builder.Append("<p>");
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
                {
                    builder.Append(c).Append(line[++i]);
                    continue;
                }

                if (char.IsSurrogate(c) || c is '\uFFFE' or '\uFFFF' || (c < ' ' && c != '\t'))
                {
                    continue;
                }

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

    private static int SubtypeAt(nint page, int index)
    {
        var annotation = Fpdf.FPDFPage_GetAnnot(page, index);
        if (annotation == 0)
        {
            return -1;
        }

        try
        {
            return Fpdf.FPDFAnnot_GetSubtype(annotation);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// One pass over <c>/Annots</c>: subtypes, which entries are replies, and which entries they answer. Resolving a
    /// reply's target costs as much as the page has entries, so only <see cref="MaxLinkWork"/> is spent; when it runs out,
    /// every entry counts as answered.
    /// </summary>
    private static PageScan Scan(nint page, CancellationToken cancellationToken)
    {
        // A link search walks the whole /Annots array, so it costs the raw entry count, not the clamped one.
        var entries = Math.Max(0, Fpdf.FPDFPage_GetAnnotCount(page));
        var count = Math.Min(entries, MaxScannedPerPage);
        var subtypes = new int[count];
        var isReply = new bool[count];
        var repliedTo = new HashSet<int>();
        var budget = MaxLinkWork;
        var exhausted = false;
        for (var i = 0; i < count; i++)
        {
            if (i % CancellationInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var annotation = Fpdf.FPDFPage_GetAnnot(page, i);
            if (annotation == 0)
            {
                // Not a dictionary: treated like a popup, never listed.
                subtypes[i] = FpdfConstants.AnnotPopup;
                continue;
            }

            try
            {
                subtypes[i] = Fpdf.FPDFAnnot_GetSubtype(annotation);
                if (HasKey(annotation, "IRT\0"u8))
                {
                    isReply[i] = true;
                    if (!exhausted)
                    {
                        var target = LinkedIndex(page, annotation, "IRT\0"u8, entries, ref budget);
                        exhausted = target == LinkUnresolved;
                        if (target >= 0 && target != i)
                        {
                            repliedTo.Add(target);
                        }
                    }
                }
            }
            finally
            {
                Fpdf.FPDFPage_CloseAnnot(annotation);
            }
        }

        return new PageScan(subtypes, isReply, repliedTo, exhausted);
    }

    private static PdfiumAnnotation? ReadOne(nint page, PdfiumPageText.PageTransform transform, int index, int subtype, bool hasReplies)
    {
        var annotation = Fpdf.FPDFPage_GetAnnot(page, index);
        if (annotation == 0)
        {
            return null;
        }

        try
        {
            FsRectF rect;
            if (!HasKey(annotation, "Rect\0"u8) || Fpdf.FPDFAnnot_GetRect(annotation, &rect) == 0
                || !IsSane(rect.Left) || !IsSane(rect.Top) || !IsSane(rect.Right) || !IsSane(rect.Bottom))
            {
                return null;
            }

            var quads = new List<PdfiumQuad>();
            var quadRects = new List<(double, double, double, double)>();
            if (IsMarkup(subtype))
            {
                var quadCount = Fpdf.FPDFAnnot_CountAttachmentPoints(annotation);
                if (quadCount == 0 || quadCount > MaxQuads)
                {
                    return null;
                }

                for (nuint q = 0; q < quadCount; q++)
                {
                    FsQuadPointsF points;
                    if (Fpdf.FPDFAnnot_GetAttachmentPoints(annotation, q, &points) == 0 || !IsSane(points))
                    {
                        return null;
                    }

                    var quad = new PdfiumQuad(new(points.X1, points.Y1), new(points.X2, points.Y2), new(points.X3, points.Y3), new(points.X4, points.Y4));
                    quads.Add(quad);
                    quadRects.Add(Box(transform, quad));
                }
            }

            return new PdfiumAnnotation(
                index,
                subtype,
                ReadString(annotation, "Subtype\0"u8) ?? "Unknown",
                ReadString(annotation, "NM\0"u8),
                ReadColor(annotation, subtype),
                quads,
                quadRects,
                transform.ToPage(rect.Left, rect.Top, rect.Right, rect.Bottom),
                ReadString(annotation, "Contents\0"u8),
                ReadString(annotation, "T\0"u8),
                ReadString(annotation, "M\0"u8),
                hasReplies);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(annotation);
        }
    }

    /// <summary>
    /// <c>/C</c> through <c>FPDFAnnot_GetColor</c>, which fails for an annotation with an appearance; then the colour
    /// its appearance paints with. <see langword="null"/> without <c>/C</c>.
    /// </summary>
    private static (byte R, byte G, byte B)? ReadColor(nint annotation, int subtype)
    {
        if (!HasKey(annotation, "C\0"u8))
        {
            return null;
        }

        uint r, g, b, a;
        if (Fpdf.FPDFAnnot_GetColor(annotation, 0, &r, &g, &b, &a) != 0)
        {
            return ((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255));
        }

        var length = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, null, new CULong(0)).Value;
        if (length <= 2 || length > MaxAppearanceBytes)
        {
            return null;
        }

        var buffer = new char[(int)length / 2];
        nuint written;
        fixed (char* pointer = buffer)
        {
            written = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, (ushort*)pointer, new CULong(length)).Value;
        }

        if (written != length)
        {
            return null;
        }

        var text = buffer.AsSpan(0, buffer.Length - 1);
        return ColorFromAppearance(text[..Math.Min(text.Length, AppearanceScanLength)], subtype == FpdfConstants.AnnotHighlight);
    }

    /// <summary>
    /// Like <see cref="ReadColor"/>, but decodes the appearance at most once (into <paramref name="scratch"/>, rented from
    /// <see cref="ArrayPool{T}.Shared"/> on first use; the caller keeps it for the next annotation and returns it with
    /// <see cref="ReturnScratch"/>) and charges the bytes PDFium reports to <paramref name="appearanceBudget"/>. Without
    /// budget left the appearance is not looked at (<see langword="null"/>). A new 1 MiB array per read (one per dark tile
    /// or preview) cost a large-object allocation each time.
    /// </summary>
    internal static (byte R, byte G, byte B)? ReadColor(nint annotation, int subtype, ref char[]? scratch, ref long appearanceBudget)
    {
        if (!HasKey(annotation, "C\0"u8))
        {
            return null;
        }

        uint r, g, b, a;
        if (Fpdf.FPDFAnnot_GetColor(annotation, 0, &r, &g, &b, &a) != 0)
        {
            return ((byte)Math.Min(r, 255), (byte)Math.Min(g, 255), (byte)Math.Min(b, 255));
        }

        if (appearanceBudget <= 0)
        {
            return null;
        }

        // One call: PDFium copies the string only when it fits, and reports its length either way.
        // The pool may hand out a longer array; only ScratchLength of it is offered, so the limit stays the same.
        scratch ??= ArrayPool<char>.Shared.Rent(ScratchLength);
        nuint length;
        fixed (char* pointer = scratch)
        {
            length = Fpdf.FPDFAnnot_GetAP(annotation, FpdfConstants.AnnotAppearanceNormal, (ushort*)pointer, new CULong((nuint)(ScratchLength * 2))).Value;
        }

        appearanceBudget -= (long)Math.Min((ulong)length, long.MaxValue);
        if (length <= 2 || length > (nuint)(ScratchLength * 2))
        {
            return null;
        }

        var text = scratch.AsSpan(0, ((int)length / 2) - 1);
        return ColorFromAppearance(text[..Math.Min(text.Length, AppearanceScanLength)], subtype == FpdfConstants.AnnotHighlight);
    }

    /// <summary>Gives a buffer rented by <see cref="ReadColor(nint, int, ref char[], ref long)"/> back to the pool (nothing without one).</summary>
    internal static void ReturnScratch(ref char[]? scratch)
    {
        if (scratch is not null)
        {
            ArrayPool<char>.Shared.Return(scratch);
            scratch = null;
        }
    }

    /// <summary>Reads a string or name value (UTF-16LE); <see langword="null"/> when missing, empty, of another type or longer than <see cref="MaxStringBytes"/>.</summary>
    internal static string? ReadString(nint annotation, ReadOnlySpan<byte> key)
    {
        fixed (byte* keyPointer = key)
        {
            // The length is in bytes and includes the 2-byte terminator.
            var length = Fpdf.FPDFAnnot_GetStringValue(annotation, keyPointer, null, new CULong(0)).Value;
            if (length <= 2 || length > MaxStringBytes)
            {
                return null;
            }

            var buffer = new char[(int)length / 2];
            nuint written;
            fixed (char* pointer = buffer)
            {
                written = Fpdf.FPDFAnnot_GetStringValue(annotation, keyPointer, (ushort*)pointer, new CULong(length)).Value;
            }

            if (written != length)
            {
                return null;
            }

            return buffer.Length > 1 ? new string(buffer, 0, buffer.Length - 1) : null;
        }
    }

    internal static int WriteString(nint annotation, ReadOnlySpan<byte> key, string value)
    {
        fixed (byte* keyPointer = key)
        fixed (char* valuePointer = value)
        {
            // A .NET string is NUL-terminated, as FPDF_WIDESTRING must be.
            return Fpdf.FPDFAnnot_SetStringValue(annotation, keyPointer, (ushort*)valuePointer);
        }
    }

    internal static bool HasKey(nint annotation, ReadOnlySpan<byte> key)
    {
        fixed (byte* keyPointer = key)
        {
            return Fpdf.FPDFAnnot_HasKey(annotation, keyPointer) != 0;
        }
    }

    /// <summary>
    /// The index on the page of the annotation that <paramref name="key"/> refers to, or -1 if there is none, or
    /// <see cref="LinkUnresolved"/> if <paramref name="budget"/> cannot pay the <paramref name="count"/> entries the
    /// search may compare (the raw <c>FPDFPage_GetAnnotCount</c>, because the search walks the whole <c>/Annots</c>).
    /// </summary>
    private static int LinkedIndex(nint page, nint annotation, ReadOnlySpan<byte> key, int count, ref long budget)
    {
        if (!HasKey(annotation, key))
        {
            return -1;
        }

        if (budget < count)
        {
            return LinkUnresolved;
        }

        budget -= Math.Max(count, 1);
        nint linked;
        fixed (byte* keyPointer = key)
        {
            linked = Fpdf.FPDFAnnot_GetLinkedAnnot(annotation, keyPointer);
        }

        if (linked == 0)
        {
            return -1;
        }

        try
        {
            return Fpdf.FPDFPage_GetAnnotIndex(page, linked);
        }
        finally
        {
            Fpdf.FPDFPage_CloseAnnot(linked);
        }
    }

    private static (double Left, double Top, double Right, double Bottom) Box(PdfiumPageText.PageTransform transform, PdfiumQuad quad)
    {
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var point in (ReadOnlySpan<PdfiumPoint>)[quad.P1, quad.P2, quad.P3, quad.P4])
        {
            if (!transform.TryToPage(point.X, point.Y, out var x, out var y))
            {
                return default;
            }

            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x);
            bottom = Math.Max(bottom, y);
        }

        return (left, top, right, bottom);
    }

    private static FsRectF Hull(IReadOnlyList<PdfiumQuad> quads)
    {
        double left = double.MaxValue, bottom = double.MaxValue, right = double.MinValue, top = double.MinValue;
        foreach (var quad in quads)
        {
            foreach (var point in (ReadOnlySpan<PdfiumPoint>)[quad.P1, quad.P2, quad.P3, quad.P4])
            {
                left = Math.Min(left, point.X);
                bottom = Math.Min(bottom, point.Y);
                right = Math.Max(right, point.X);
                top = Math.Max(top, point.Y);
            }
        }

        return new FsRectF { Left = (float)left, Top = (float)top, Right = (float)right, Bottom = (float)bottom };
    }

    internal static bool IsSane(float value) => float.IsFinite(value) && Math.Abs(value) <= MaxCoordinate;

    internal static bool IsSane(FsQuadPointsF p) =>
        IsSane(p.X1) && IsSane(p.Y1) && IsSane(p.X2) && IsSane(p.Y2) && IsSane(p.X3) && IsSane(p.Y3) && IsSane(p.X4) && IsSane(p.Y4);

    internal static void Check(int result)
    {
        if (result == 0)
        {
            throw new InvalidOperationException("PDFium refused to change the annotation.");
        }
    }

    private static (byte R, byte G, byte B)? ToColor(ReadOnlySpan<char> op, ReadOnlySpan<double> operands, out bool isFill)
    {
        isFill = op.Length > 0 && char.IsLower(op[0]);
        Span<double> rgb = stackalloc double[3];
        switch (op)
        {
            case "rg" or "RG" when operands.Length >= 3:
                operands[^3..].CopyTo(rgb);
                break;
            case "g" or "G" when operands.Length >= 1:
                rgb.Fill(operands[^1]);
                break;
            case "k" or "K" when operands.Length >= 4:
                var cmyk = operands[^4..];
                if (!InRange(cmyk[3]))
                {
                    return null;
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    if (!InRange(cmyk[channel]))
                    {
                        return null;
                    }

                    rgb[channel] = (1 - cmyk[channel]) * (1 - cmyk[3]);
                }

                break;
            default:
                return null;
        }

        if (!InRange(rgb[0]) || !InRange(rgb[1]) || !InRange(rgb[2]))
        {
            return null;
        }

        return (ToByte(rgb[0]), ToByte(rgb[1]), ToByte(rgb[2]));
    }

    private static bool InRange(double value) => value is >= 0 and <= 1;

    private static byte ToByte(double channel) => (byte)Math.Round(channel * 255, MidpointRounding.AwayFromZero);

    internal static bool IsWhite(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f' or '\0';

    internal static bool IsDelimiter(char c) => c is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%';

    /// <summary>Skips a literal string with nested parentheses and backslash escapes; returns the index after it.</summary>
    internal static int SkipString(ReadOnlySpan<char> content, int start)
    {
        var depth = 0;
        for (var i = start; i < content.Length; i++)
        {
            switch (content[i])
            {
                case '\\':
                    i++;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    if (--depth == 0)
                    {
                        return i + 1;
                    }

                    break;
            }
        }

        return content.Length;
    }

    /// <summary>The result of <see cref="Scan"/>.</summary>
    /// <param name="Subtypes">The subtype of each entry (a popup for an entry that is no dictionary).</param>
    /// <param name="IsReply">Whether each entry has <c>/IRT</c>.</param>
    /// <param name="RepliedTo">The entries that a resolved <c>/IRT</c> points at.</param>
    /// <param name="Unresolved">Whether some <c>/IRT</c> was not resolved because the link budget ran out.</param>
    private sealed record PageScan(int[] Subtypes, bool[] IsReply, HashSet<int> RepliedTo, bool Unresolved)
    {
        /// <summary>Whether the entry may have replies: it has a resolved one, or not every reply was resolved.</summary>
        public bool HasReplies(int index) => Unresolved || RepliedTo.Contains(index);
    }
}
