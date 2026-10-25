using System.Globalization;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// The drawings of the document (freehand, line, arrow, rectangle, ellipse): creating and replacing them, and turning what
/// PDFium reads into <see cref="DrawingGeometry"/>. See <c>Docs/DECISIONS.md</c>, entry "Zeichnungen: Erscheinungsbild,
/// Pfeil als Ink, Merkmal zur Wiedererkennung, Glättung (M10)".
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// The most points of drawings one <see cref="GetAnnotationsAsync"/> of a page converts (one more ink beyond it may be
    /// read whole, at most <c>100,000</c> points); later drawings of the page are listed without their
    /// <see cref="PdfAnnotation.Drawing"/>, so a hostile page cannot fill the memory.
    /// </summary>
    public const int MaxDrawingPointsPerPage = 2_000_000;

    /// <summary>
    /// Gets or sets how a created or replaced annotation gets its appearance: <see cref="PdfiumAnnotations.GenerateAppearance"/>
    /// (tests replace it to make it fail). Called on the PDFium thread with no handle of the page open.
    /// </summary>
    internal Func<PdfiumDocument, int, int, bool> AppearanceGenerator { get; set; } = PdfiumAnnotations.GenerateAppearance;

    /// <summary>
    /// Creates a drawing: an ink for <see cref="AnnotationKind.Ink"/>, <see cref="AnnotationKind.Line"/> and
    /// <see cref="AnnotationKind.Arrow"/> (a line and an arrow carry their marker <c>/Subj</c>), a square for
    /// <see cref="AnnotationKind.Rectangle"/> and a circle for <see cref="AnnotationKind.Ellipse"/>. The points are page points
    /// from the top left of the displayed page; PDFium's conversion turns them into PDF space (rotation and CropBox) without
    /// rounding. Strokes of fewer than two points draw nothing and are left out. The annotation gets a new <c>/NM</c>,
    /// <c>/T</c> when <paramref name="author"/> is not blank (trimmed), <c>/M</c> and <c>/CreationDate</c>, flag Print, the
    /// colour with alpha 255, the width as <c>/Border</c>, <paramref name="contents"/> when not empty, and an appearance built
    /// by PDFium's own generator; never an interior colour. Runs with <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <returns>The new annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="pageIndex"/> is not a page, the kind is no drawing, the width is not above 0 and at most 1,000 points,
    /// or the drawing has more than 1,000 strokes or 100,000 points.
    /// </exception>
    /// <exception cref="ArgumentException">The drawing has nothing to draw (<see cref="DrawingGeometry.IsEmpty"/>) or a point beyond 10,000,000 points.</exception>
    /// <exception cref="PdfAnnotationLimitException">
    /// The document lists <see cref="MaxAnnotations"/> already: the pages it has listed (each with its last
    /// <see cref="GetAnnotationsAsync"/>) and this page together leave no room, so the new annotation would never be listed.
    /// Nothing is created.
    /// </exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the annotation or its appearance; the page is left unchanged.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotation was created.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> CreateDrawingAsync(
        int pageIndex,
        DrawingGeometry geometry,
        AnnotationColor color,
        string? contents,
        string? author,
        CancellationToken cancellationToken = default) =>
        CreateDrawingAsync(pageIndex, geometry, color, contents, author, name: null, cancellationToken);

    /// <summary>
    /// Like the public overload, but gives the drawing <paramref name="name"/> as its <c>/NM</c> instead of a new one (when
    /// not <see langword="null"/>), so that undo and redo find it again under the name it had.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or blank, or see the public overload.</exception>
    internal Task<PdfAnnotation> CreateDrawingAsync(
        int pageIndex,
        DrawingGeometry geometry,
        AnnotationColor color,
        string? contents,
        string? author,
        string? name,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        if (name is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
        }

        var spec = ToPdfium(geometry);
        name ??= NewName();
        var date = FormatPdfDate(Clock.GetUtcNow());
        var trimmedAuthor = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        return document.Thread.InvokeAsync(
            () =>
            {
                int index;
                using (var page = document.LoadPage(pageIndex))
                {
                    EnsureRoomForOneMore(page, pageIndex, cancellationToken);
                    index = PdfiumDrawings.Create(page, spec, (color.R, color.G, color.B), contents, trimmedAuthor, name, date);
                }

                return FinishCreated(pageIndex, index);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Replaces a drawing by <paramref name="geometry"/> (moved, resized or with another width; of the same kind) in
    /// <paramref name="color"/> (alpha 255), in one job: the new drawing is created first, with the <c>/NM</c> of the old one
    /// (a new one if it had none), its comment, its author (<c>/T</c>) and its <c>/CreationDate</c>, <c>/M</c> now, and gets
    /// its appearance; only then is the old one deleted together with its popup. If creating or the appearance fails, the
    /// new one is removed and the old one stays exactly as it was. The new annotation is the last entry of the page's
    /// <c>/Annots</c>, so its <see cref="PdfAnnotation.Index"/> changes; the name finds it. Only a drawing that is
    /// <see cref="PdfAnnotation.GeometryEditable"/> can be replaced: nothing of it may be lost on the way (no <c>/BS</c>,
    /// <c>/BE</c>, <c>/IC</c>, <c>/RD</c>, <c>/CA</c> below 1, foreign appearance, reply, or strokes beyond the limits). What
    /// does go: a formatted <c>/RC</c> (the plain comment stays), a <c>/Subj</c> of a freehand drawing and any other key of
    /// another program. Cancellation counts until the change starts. Runs with <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <returns>The new annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">
    /// The annotation is no drawing, has replies or is not <see cref="PdfAnnotation.GeometryEditable"/>, or PDFium could not
    /// create the new one or its appearance (the old one is unchanged).
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="geometry"/> is of another kind than the drawing, or see <see cref="CreateDrawingAsync"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page of the locator is not a page of the document, or see <see cref="CreateDrawingAsync"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the drawing was replaced.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> ReplaceDrawingAsync(AnnotationLocator locator, DrawingGeometry geometry, AnnotationColor color, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(locator.Page);
        var spec = ToPdfium(geometry);
        var name = NewName();
        var date = FormatPdfDate(Clock.GetUtcNow());
        return document.Thread.InvokeAsync(
            () =>
            {
                Replacement replacement;
                using (var page = document.LoadPage(locator.Page))
                {
                    var index = FindEditable(page, locator, cancellationToken);
                    if (!PdfiumAnnotations.IsDrawing(PdfiumAnnotations.SubtypeOf(page, index)))
                    {
                        throw new InvalidOperationException("Only a drawing can be replaced.");
                    }

                    replacement = PrepareReplacement(page, index, geometry, spec, (color.R, color.G, color.B), contents: null, name, date, cancellationToken);
                }

                return Replace(locator.Page, replacement);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Checks a drawing and turns it into what <see cref="PdfiumDrawings.Create"/> takes; strokes of fewer than two points are
    /// left out (they draw nothing). Every error is found here, before the PDFium thread is asked, except a point beyond
    /// PDFium's largest coordinate (that needs the page).
    /// </summary>
    private static PdfiumDrawing ToPdfium(DrawingGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var kind = geometry.Kind switch
        {
            AnnotationKind.Ink => PdfiumDrawingKind.Ink,
            AnnotationKind.Line => PdfiumDrawingKind.Line,
            AnnotationKind.Arrow => PdfiumDrawingKind.Arrow,
            AnnotationKind.Rectangle => PdfiumDrawingKind.Square,
            AnnotationKind.Ellipse => PdfiumDrawingKind.Circle,
            _ => throw new ArgumentOutOfRangeException(nameof(geometry), geometry.Kind, "Only freehand drawings, lines, arrows, rectangles and ellipses are drawings."),
        };
        if (!double.IsFinite(geometry.Width) || geometry.Width <= 0 || geometry.Width > PdfiumDrawings.MaxWidth)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), geometry.Width, string.Create(CultureInfo.InvariantCulture, $"The stroke width must be above 0 and at most {PdfiumDrawings.MaxWidth} points."));
        }

        if (geometry.IsEmpty)
        {
            throw new ArgumentException("The drawing has nothing to draw.", nameof(geometry));
        }

        var box = (geometry.Box.Left, geometry.Box.Top, geometry.Box.Right, geometry.Box.Bottom);
        if (kind is PdfiumDrawingKind.Square or PdfiumDrawingKind.Circle)
        {
            return new PdfiumDrawing(kind, [], box, geometry.Width);
        }

        var strokes = new List<IReadOnlyList<(double X, double Y)>>(geometry.Strokes.Count);
        long points = 0;
        foreach (var stroke in geometry.Strokes)
        {
            if (stroke.Count < 2)
            {
                continue;
            }

            var copy = new (double X, double Y)[stroke.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = (stroke[i].X, stroke[i].Y);
            }

            strokes.Add(copy);
            points += copy.Length;
        }

        if (strokes.Count > PdfiumDrawings.MaxInkStrokes)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), strokes.Count, string.Create(CultureInfo.InvariantCulture, $"A drawing has at most {PdfiumDrawings.MaxInkStrokes} strokes."));
        }

        if (points > PdfiumDrawings.MaxInkPoints)
        {
            throw new ArgumentOutOfRangeException(nameof(geometry), points, string.Create(CultureInfo.InvariantCulture, $"A drawing has at most {PdfiumDrawings.MaxInkPoints} points."));
        }

        return new PdfiumDrawing(kind, strokes, box, geometry.Width);
    }

    /// <summary>
    /// The shape of what PDFium read: a square is a rectangle, a circle an ellipse; an ink with the marker of a line or an
    /// arrow is one only if its strokes are exactly that shape (<see cref="ArrowGeometry.TryRecognize"/>, for an arrow also a
    /// head of the length its width makes), else freehand. Strokes of fewer than two points are left out;
    /// <see langword="null"/> when no stroke is left.
    /// </summary>
    private static DrawingGeometry? ToGeometry(PdfiumDrawing drawing)
    {
        var (left, top, right, bottom) = drawing.Box;
        switch (drawing.Kind)
        {
            case PdfiumDrawingKind.Square:
                return DrawingGeometry.Rectangle(new PageRect(left, top, right, bottom), drawing.Width);
            case PdfiumDrawingKind.Circle:
                return DrawingGeometry.Ellipse(new PageRect(left, top, right, bottom), drawing.Width);
        }

        var strokes = new List<IReadOnlyList<DisplayPoint>>(drawing.Strokes.Count);
        foreach (var stroke in drawing.Strokes)
        {
            if (stroke.Count < 2)
            {
                continue;
            }

            var points = new DisplayPoint[stroke.Count];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = new DisplayPoint(stroke[i].X, stroke[i].Y);
            }

            strokes.Add(points);
        }

        if (strokes.Count == 0)
        {
            return null;
        }

        if (drawing.Kind is PdfiumDrawingKind.Line or PdfiumDrawingKind.Arrow
            && ArrowGeometry.TryRecognize(strokes, out var start, out var end, out var head)
            && head == (drawing.Kind == PdfiumDrawingKind.Arrow)
            && (!head || HasHeadOfWidth(strokes[1][0], start, end, drawing.Width)))
        {
            return head ? DrawingGeometry.Arrow(start, end, drawing.Width) : DrawingGeometry.Line(start, end, drawing.Width);
        }

        return DrawingGeometry.Ink(strokes, drawing.Width);
    }

    /// <summary>
    /// Whether the sides of a recognised head are as long as <see cref="ArrowGeometry.HeadLength"/> of the width: only then
    /// does <see cref="DrawingGeometry.Arrow"/> make the same strokes again (relative to the size of the coordinates, as the
    /// file keeps them in single precision).
    /// </summary>
    private static bool HasHeadOfWidth(DisplayPoint side, DisplayPoint start, DisplayPoint end, double width)
    {
        var length = Math.Sqrt(((side.X - end.X) * (side.X - end.X)) + ((side.Y - end.Y) * (side.Y - end.Y)));
        var expected = ArrowGeometry.HeadLength(width);
        var scale = Math.Max(Math.Max(1, expected), Math.Max(Math.Max(Math.Abs(start.X), Math.Abs(start.Y)), Math.Max(Math.Abs(end.X), Math.Abs(end.Y))));
        return Math.Abs(length - expected) <= 1e-4 * scale;
    }

    /// <summary>
    /// Refuses a new annotation the listing would leave out: the pages listed so far (each with what its last
    /// <see cref="GetAnnotationsAsync"/> listed; pages never listed count as nothing, as for the listing) and this page
    /// together already list <see cref="MaxAnnotations"/>. The raw entry count of the page answers most cases without
    /// reading it. Runs on the PDFium thread.
    /// </summary>
    /// <exception cref="PdfAnnotationLimitException">There is no room for one more.</exception>
    private void EnsureRoomForOneMore(PdfiumPage page, int pageIndex, CancellationToken cancellationToken)
    {
        long others = 0;
        foreach (var (listed, count) in listedAnnotations)
        {
            others += listed == pageIndex ? 0 : count;
        }

        var room = MaxAnnotations - others;
        if (PdfiumAnnotations.CountEntries(page) < room)
        {
            return;
        }

        if (PdfiumAnnotations.Read(page, (int)Math.Max(0, room), cancellationToken).Count >= room)
        {
            throw new PdfAnnotationLimitException(string.Create(
                CultureInfo.InvariantCulture,
                $"The document already lists {MaxAnnotations} annotations, the most Pagewing reads; a new one would not be listed."));
        }
    }

    /// <summary>
    /// Reads what replacing the drawing at <paramref name="index"/> keeps and checks that it may be replaced. A
    /// <see langword="null"/> <paramref name="geometry"/>, <paramref name="color"/> or <paramref name="contents"/> keeps the
    /// old one. Runs on the PDFium thread; changes nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The drawing cannot be read or is not <see cref="PdfAnnotation.GeometryEditable"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="geometry"/> is of another kind than the drawing.</exception>
    private static Replacement PrepareReplacement(
        PdfiumPage page,
        int index,
        DrawingGeometry? geometry,
        PdfiumDrawing? spec,
        (byte R, byte G, byte B)? color,
        string? contents,
        string name,
        string date,
        CancellationToken cancellationToken)
    {
        var current = PdfiumAnnotations.ReadAt(page, index, cancellationToken)
            ?? throw new InvalidOperationException("The drawing cannot be read, so it is not replaced.");
        var read = new DrawingReader(page, long.MaxValue).Read(current, cancellationToken);
        if (read.Geometry is not { } old || !read.GeometryEditable || current.HasReplies)
        {
            throw new InvalidOperationException(
                "This drawing cannot be moved, resized, recoloured or rewidened without losing a part of it; it can only be deleted or commented on.");
        }

        if (geometry is not null && geometry.Kind != old.Kind)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A drawing of the kind {old.Kind} cannot become one of the kind {geometry.Kind}."), nameof(geometry));
        }

        var rgb = color ?? (current.Color is { } c ? c : ((byte)0, (byte)0, (byte)0));
        return new Replacement(
            index,
            spec ?? ToPdfium(old),
            rgb,
            contents ?? current.Contents,
            current.Author,
            current.Name ?? name,
            date,
            PdfiumAnnotations.ReadStringAt(page, index, "CreationDate\0"u8));
    }

    /// <summary>
    /// Creates the new drawing next to the old one, gives it its appearance, and only then deletes the old one (with its
    /// popup). On any failure before the old one is deleted, the new one is removed again. Runs on the PDFium thread with no
    /// handle of the page open.
    /// </summary>
    private PdfAnnotation Replace(int pageIndex, Replacement replacement)
    {
        int created;
        int entries;
        using (var page = document.LoadPage(pageIndex))
        {
            created = PdfiumDrawings.Create(
                page, replacement.Spec, replacement.Color, replacement.Contents, replacement.Author, replacement.Name, replacement.Date, replacement.CreationDate);
            entries = PdfiumAnnotations.CountEntries(page);
        }

        // From here on the new drawing is on the page next to the old one: whatever fails, the new one goes again and the
        // old one stays as it was.
        int index;
        try
        {
            if (!AppearanceGenerator(document, pageIndex, created))
            {
                throw new InvalidOperationException("PDFium did not generate the appearance of the annotation.");
            }

            using var page = document.LoadPage(pageIndex);

            // The new one is the last entry; deleting the old one (and its popups, all before it) moves it forward.
            PdfiumAnnotations.Delete(page, replacement.Index, CancellationToken.None);
            index = created - (entries - PdfiumAnnotations.CountEntries(page));
        }
        catch (Exception ex)
        {
            TryRemoveCreated(pageIndex, created, entries);
            if (ex is PdfiumRenderException)
            {
                throw new InvalidOperationException("PDFium did not generate the appearance of the annotation.", ex);
            }

            throw;
        }

        return ReadBack(pageIndex, index);
    }

    /// <summary>
    /// Removes the drawing a failed replacement created at <paramref name="created"/>, also when entries before it are gone
    /// already (the page had <paramref name="entries"/> right after it was created); a page that cannot be loaded any more is
    /// left as it is.
    /// </summary>
    private void TryRemoveCreated(int pageIndex, int created, int entries)
    {
        try
        {
            using var page = document.LoadPage(pageIndex);
            PdfiumAnnotations.Remove(page, created - (entries - PdfiumAnnotations.CountEntries(page)));
        }
        catch (InvalidDataException)
        {
            // The original error matters more; the page could not be loaded to clean up.
        }
    }

    /// <summary>The shape of a drawing as listed, and whether it may be replaced (before the replies are taken into account).</summary>
    private readonly record struct DrawingRead(DrawingGeometry? Geometry, bool GeometryEditable);

    /// <summary>What a replacement creates and which entry it deletes afterwards.</summary>
    private sealed record Replacement(
        int Index,
        PdfiumDrawing Spec,
        (byte R, byte G, byte B) Color,
        string? Contents,
        string? Author,
        string Name,
        string Date,
        string? CreationDate);

    /// <summary>
    /// Reads the drawings of one loaded page, with one conversion of the page and a budget of points across them. Runs on
    /// the PDFium thread.
    /// </summary>
    private sealed class DrawingReader(PdfiumPage page, long pointBudget)
    {
        private long budget = pointBudget;
        private bool prepared;
        private bool hasTransform;
        private DisplayTransform transform;

        /// <summary>
        /// The shape of <paramref name="annotation"/> if it is a drawing: <see langword="default"/> for another kind, a
        /// damaged drawing, or when the budget is used up. It may be replaced only with a readable, complete, non-empty
        /// shape, a width above 0 and at most 1,000 points, and no key that would be lost
        /// (<see cref="PdfiumDrawings.KeysAllowReplacing"/>); replies are the caller's part.
        /// </summary>
        public DrawingRead Read(PdfiumAnnotation annotation, CancellationToken cancellationToken)
        {
            if (!PdfiumAnnotations.IsDrawing(annotation.Subtype) || budget <= 0)
            {
                return default;
            }

            if (!prepared)
            {
                prepared = true;
                hasTransform = DisplayTransform.TryCreate(page.Access(), page.Width, page.Height, out transform);
            }

            if (!hasTransform || PdfiumDrawings.ReadGeometry(page, annotation.Index, transform, cancellationToken) is not { } drawing)
            {
                return default;
            }

            foreach (var stroke in drawing.Strokes)
            {
                budget -= stroke.Count;
            }

            var geometry = ToGeometry(drawing);
            var editable = geometry is { IsEmpty: false }
                && drawing.IsComplete
                && drawing.Width > 0
                && drawing.Width <= PdfiumDrawings.MaxWidth
                && PdfiumDrawings.KeysAllowReplacing(page, annotation.Index);
            return new DrawingRead(geometry, editable);
        }
    }
}
