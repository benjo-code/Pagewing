using System.Globalization;
using System.Runtime.ExceptionServices;
using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;

namespace Pagewing.Core.Pdf;

/// <summary>The annotations of the document: reading, and creating, changing and deleting text markup.</summary>
public sealed partial class PdfDocument
{
    /// <summary>The most annotations <see cref="GetAnnotationsAsync"/> lists for the whole document; further ones are left out silently.</summary>
    public const int MaxAnnotations = 10_000;

    /// <summary>
    /// How many annotations the last <see cref="GetAnnotationsAsync"/> of each page listed, for the document-wide cap.
    /// Only touched on the PDFium thread, so it needs no lock.
    /// </summary>
    private readonly Dictionary<int, int> listedAnnotations = [];

    /// <summary>Gets or sets the clock of <c>/M</c> and <c>/CreationDate</c> (tests replace it).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Reads the annotations of a page in the order of the page's annotation list. Popups and replies (<c>/IRT</c>)
    /// never appear; damaged entries (no <c>/Rect</c>, coordinates that are not finite or absurdly large, a markup without a complete quadrilateral) are skipped. Across the document at
    /// most <see cref="MaxAnnotations"/> are listed: each page counts with what its last read listed, so reading a page
    /// again does not count twice. <see cref="PdfAnnotation.Editable"/> is set for highlights, underlines and
    /// strike-outs that no other annotation answers. Runs with <see cref="PdfPriority.Background"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotations were read.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<IReadOnlyList<PdfAnnotation>> GetAnnotationsAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        return document.Thread.InvokeAsync<IReadOnlyList<PdfAnnotation>>(
            () =>
            {
                var others = 0;
                foreach (var (page, count) in listedAnnotations)
                {
                    others += page == pageIndex ? 0 : count;
                }

                using var loaded = document.LoadPage(pageIndex);
                var read = PdfiumAnnotations.Read(loaded, Math.Max(0, MaxAnnotations - others), cancellationToken);
                listedAnnotations[pageIndex] = read.Count;
                var result = new PdfAnnotation[read.Count];
                for (var i = 0; i < result.Length; i++)
                {
                    result[i] = ToAnnotation(pageIndex, read[i]);
                }

                return result;
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Creates a highlight, underline or strike-out over <paramref name="lineRects"/> (one rectangle per line, page points
    /// from the top left like <see cref="PageText"/>; see <see cref="MarkupGeometry.LinesFor"/>). The rectangles are
    /// converted to PDF space by PDFium (<c>FPDF_DeviceToPage</c>), one quadrilateral per line. The annotation gets a new
    /// <c>/NM</c>, <c>/T</c> when <paramref name="author"/> is not blank (trimmed), <c>/M</c> and <c>/CreationDate</c>,
    /// flag Print, the colour, <paramref name="contents"/> when not empty, and an appearance built by PDFium's own
    /// generator. Runs with <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <returns>The new annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageIndex"/> is not a page, or <paramref name="kind"/> is not a markup kind.</exception>
    /// <exception cref="ArgumentException"><paramref name="lineRects"/> is empty or has a rectangle that is empty or not finite.</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the annotation or its appearance; the page is left unchanged.</exception>
    /// <exception cref="InvalidDataException">The page is damaged and cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotation was created.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> CreateMarkupAsync(
        int pageIndex,
        AnnotationKind kind,
        IReadOnlyList<PageRect> lineRects,
        AnnotationColor color,
        string? contents,
        string? author,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(pageIndex);
        var subtype = SubtypeOf(kind);
        ArgumentNullException.ThrowIfNull(lineRects);
        if (lineRects.Count == 0)
        {
            throw new ArgumentException("A markup needs at least one rectangle.", nameof(lineRects));
        }

        foreach (var rect in lineRects)
        {
            if (!double.IsFinite(rect.Left) || !double.IsFinite(rect.Top) || !double.IsFinite(rect.Right) || !double.IsFinite(rect.Bottom)
                || rect.Width <= 0 || rect.Height <= 0)
            {
                throw new ArgumentException("Every rectangle must be finite and not empty.", nameof(lineRects));
            }
        }

        var rects = lineRects.ToArray();
        var name = NewName();
        var date = FormatPdfDate(Clock.GetUtcNow());
        var trimmedAuthor = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        return document.Thread.InvokeAsync(
            () =>
            {
                int index;
                using (var page = document.LoadPage(pageIndex))
                {
                    var transform = PdfiumPageText.TransformOf(page);
                    var quads = new PdfiumQuad[rects.Length];
                    for (var i = 0; i < rects.Length; i++)
                    {
                        quads[i] = ToPdfium(QuadOf(transform, rects[i]));
                    }

                    index = PdfiumAnnotations.CreateMarkup(page, subtype, quads, (color.R, color.G, color.B), contents, trimmedAuthor, name, date);
                }

                // From here on the annotation is on the page: whatever fails, it is removed again before the error goes on.
                try
                {
                    if (!PdfiumAnnotations.GenerateAppearance(document, pageIndex, index))
                    {
                        throw new InvalidOperationException("PDFium did not generate the appearance of the annotation.");
                    }

                    return ReadBack(pageIndex, index);
                }
                catch (Exception ex)
                {
                    TryRemove(pageIndex, index);
                    if (ex is PdfiumRenderException)
                    {
                        throw new InvalidOperationException("PDFium did not generate the appearance of the annotation.", ex);
                    }

                    throw;
                }
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Deletes a highlight, underline or strike-out together with its popup. The locator finds it by name if it has one,
    /// else by index. Runs with <see cref="PdfPriority.Interactive"/>. The deleted annotation stays in the bytes of the
    /// earlier revisions of an incrementally saved file.
    /// </summary>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">The annotation is not editable (another kind, or it has replies), or PDFium refused.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page of the locator is not a page of the document.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotation was deleted.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task DeleteAnnotationAsync(AnnotationLocator locator, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(locator.Page);
        return document.Thread.InvokeAsync(
            () =>
            {
                using var page = document.LoadPage(locator.Page);
                var index = FindEditable(page, locator, cancellationToken);
                PdfiumAnnotations.Delete(page, index, cancellationToken);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>
    /// Changes a highlight, underline or strike-out. A new comment replaces <c>/Contents</c> (an empty string empties it)
    /// and a formatted <c>/RC</c> by the same text, so old rich text never covers it; a new colour makes PDFium build the
    /// appearance anew (an old, foreign appearance is dropped). The annotation gets a <c>/NM</c> if it has none and the
    /// flag Print, <c>/M</c> becomes now, and it always leaves with an appearance: one without (also a foreign markup whose
    /// comment alone changed) gets PDFium's. Cancellation counts until the change starts. Runs with
    /// <see cref="PdfPriority.Interactive"/>.
    /// </summary>
    /// <returns>The changed annotation as <see cref="GetAnnotationsAsync"/> reads it.</returns>
    /// <exception cref="PdfAnnotationNotFoundException">No annotation matches the locator.</exception>
    /// <exception cref="InvalidOperationException">The annotation is not editable (another kind, or it has replies), or PDFium refused.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page of the locator is not a page of the document.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the annotation was changed.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<PdfAnnotation> UpdateAnnotationAsync(AnnotationLocator locator, AnnotationEdit edit, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(locator.Page);
        ArgumentNullException.ThrowIfNull(edit);
        var name = NewName();
        var date = FormatPdfDate(Clock.GetUtcNow());
        (byte R, byte G, byte B)? color = edit.Color is { } c ? (c.R, c.G, c.B) : null;
        return document.Thread.InvokeAsync(
            () =>
            {
                // Cancellation is honoured until the change starts; after that the job finishes, so the annotation is never
                // left half changed.
                int index;
                Exception? failure = null;
                using (var page = document.LoadPage(locator.Page))
                {
                    index = FindEditable(page, locator, cancellationToken);
                    try
                    {
                        PdfiumAnnotations.Update(page, index, edit.Contents, color, name, date);
                    }
                    catch (InvalidOperationException ex)
                    {
                        failure = ex;
                    }
                }

                // Every changed annotation gets an appearance: one without (a foreign markup, or the old one dropped for a
                // new colour, also when the change failed half way) is drawn once so that PDFium generates it.
                var hasAppearance = EnsureAppearance(locator.Page, index);
                if (failure is not null)
                {
                    ExceptionDispatchInfo.Throw(failure);
                }

                if (!hasAppearance)
                {
                    throw new InvalidOperationException("PDFium did not generate the appearance of the annotation.");
                }

                return ReadBack(locator.Page, index);
            },
            PdfiumPriority.Interactive,
            cancellationToken);
    }

    /// <summary>Formats a time as a PDF date in UTC, for example <c>D:20261024073005Z</c>.</summary>
    internal static string FormatPdfDate(DateTimeOffset time) =>
        "D:" + time.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "Z";

    /// <summary>
    /// The quadrilateral of one line from the four converted corners of its rectangle. The text frame is taken to be PDF
    /// user space: right for text that runs along the x axis of user space, also on a page with <c>/Rotate</c> (where it
    /// runs vertically on screen). Text that is rotated inside the content stream gets an axis-aligned quadrilateral: it
    /// still covers the line, but its corners are not in the order of the rotated line. This is the one place to change
    /// if Acrobat needs another corner order on rotated pages.
    /// </summary>
    internal static Quad MarkupQuad(PagePoint first, PagePoint second, PagePoint third, PagePoint fourth)
    {
        var minX = Math.Min(Math.Min(first.X, second.X), Math.Min(third.X, fourth.X));
        var maxX = Math.Max(Math.Max(first.X, second.X), Math.Max(third.X, fourth.X));
        var minY = Math.Min(Math.Min(first.Y, second.Y), Math.Min(third.Y, fourth.Y));
        var maxY = Math.Max(Math.Max(first.Y, second.Y), Math.Max(third.Y, fourth.Y));
        return MarkupGeometry.ToQuad(new PagePoint(minX, maxY), new PagePoint(maxX, maxY), new PagePoint(maxX, minY), new PagePoint(minX, minY));
    }

    private static Quad QuadOf(PdfiumPageText.PageTransform transform, PageRect rect)
    {
        var corners = new PagePoint[4];
        ReadOnlySpan<(double X, double Y)> display = [(rect.Left, rect.Top), (rect.Right, rect.Top), (rect.Left, rect.Bottom), (rect.Right, rect.Bottom)];
        for (var i = 0; i < corners.Length; i++)
        {
            if (!transform.TryToUser(display[i].X, display[i].Y, out var x, out var y))
            {
                throw new ArgumentException("A rectangle could not be converted to PDF space.", nameof(rect));
            }

            corners[i] = new PagePoint(x, y);
        }

        return MarkupQuad(corners[0], corners[1], corners[2], corners[3]);
    }

    private static PdfiumQuad ToPdfium(Quad quad) => new(
        new PdfiumPoint(quad.TopLeft.X, quad.TopLeft.Y),
        new PdfiumPoint(quad.TopRight.X, quad.TopRight.Y),
        new PdfiumPoint(quad.BottomLeft.X, quad.BottomLeft.Y),
        new PdfiumPoint(quad.BottomRight.X, quad.BottomRight.Y));

    private static PdfAnnotation ToAnnotation(int pageIndex, PdfiumAnnotation annotation)
    {
        var kind = KindOf(annotation.Subtype);
        var quads = new Quad[annotation.Quads.Count];
        for (var i = 0; i < quads.Length; i++)
        {
            var q = annotation.Quads[i];

            // /QuadPoints are stored in the order Acrobat reads: top left, top right, bottom left, bottom right.
            quads[i] = new Quad(new PagePoint(q.P1.X, q.P1.Y), new PagePoint(q.P2.X, q.P2.Y), new PagePoint(q.P3.X, q.P3.Y), new PagePoint(q.P4.X, q.P4.Y));
        }

        var rects = new PageRect[annotation.QuadRects.Count];
        for (var i = 0; i < rects.Length; i++)
        {
            var (left, top, right, bottom) = annotation.QuadRects[i];
            rects[i] = new PageRect(left, top, right, bottom);
        }

        var bounds = annotation.Bounds;
        return new PdfAnnotation(
            pageIndex,
            annotation.Index,
            annotation.Name,
            kind,
            annotation.SubtypeName,
            annotation.Color is { } c ? new AnnotationColor(c.R, c.G, c.B) : null,
            quads,
            rects,
            new PageRect(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom),
            annotation.Contents,
            annotation.Author,
            PdfDate.TryParse(annotation.Modified),
            kind != AnnotationKind.Other && !annotation.HasReplies);
    }

    private static AnnotationKind KindOf(int subtype) => subtype switch
    {
        FpdfConstants.AnnotHighlight => AnnotationKind.Highlight,
        FpdfConstants.AnnotUnderline => AnnotationKind.Underline,
        FpdfConstants.AnnotStrikeout => AnnotationKind.StrikeOut,
        _ => AnnotationKind.Other,
    };

    private static int SubtypeOf(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Highlight => FpdfConstants.AnnotHighlight,
        AnnotationKind.Underline => FpdfConstants.AnnotUnderline,
        AnnotationKind.StrikeOut => FpdfConstants.AnnotStrikeout,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only highlights, underlines and strike-outs can be created."),
    };

    private static string NewName() => Guid.NewGuid().ToString("N");

    /// <summary>The index of the annotation of the locator, after checking that Pagewing may change it. Runs on the PDFium thread.</summary>
    private static int FindEditable(PdfiumPage page, AnnotationLocator locator, CancellationToken cancellationToken)
    {
        var index = PdfiumAnnotations.Find(page, locator.Name, locator.Index, cancellationToken);
        if (index < 0)
        {
            throw new PdfAnnotationNotFoundException(locator.Name is null
                ? string.Create(CultureInfo.InvariantCulture, $"Page {locator.Page + 1} has no annotation {locator.Index}.")
                : string.Create(CultureInfo.InvariantCulture, $"Page {locator.Page + 1} has no annotation named \"{locator.Name}\"."));
        }

        if (!PdfiumAnnotations.IsEditable(page, index, cancellationToken))
        {
            throw new InvalidOperationException("Only highlights, underlines and strike-outs without replies can be changed.");
        }

        return index;
    }

    /// <summary>
    /// Makes sure the annotation at <paramref name="index"/> has an appearance: if it has none, the page is drawn once so
    /// that PDFium generates it. Returns whether it has one afterwards. Runs on the PDFium thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">The page could not be drawn.</exception>
    private bool EnsureAppearance(int pageIndex, int index)
    {
        using (var page = document.LoadPage(pageIndex))
        {
            if (PdfiumAnnotations.HasAppearance(page, index))
            {
                return true;
            }
        }

        try
        {
            return PdfiumAnnotations.GenerateAppearance(document, pageIndex, index);
        }
        catch (PdfiumRenderException ex)
        {
            throw new InvalidOperationException("PDFium did not generate the appearance of the annotation.", ex);
        }
    }

    /// <summary>Removes a half-created annotation again; a page that cannot be loaded any more is left as it is.</summary>
    private void TryRemove(int pageIndex, int index)
    {
        try
        {
            using var page = document.LoadPage(pageIndex);
            PdfiumAnnotations.Remove(page, index);
        }
        catch (InvalidDataException)
        {
            // The original error matters more; the page could not be loaded to clean up.
        }
    }

    /// <summary>Reads the annotation at <paramref name="index"/> after a change. Runs on the PDFium thread.</summary>
    private PdfAnnotation ReadBack(int pageIndex, int index)
    {
        using var page = document.LoadPage(pageIndex);
        var annotation = PdfiumAnnotations.ReadAt(page, index)
            ?? throw new InvalidOperationException("The changed annotation could not be read back.");
        return ToAnnotation(pageIndex, annotation);
    }

    private void ThrowIfNotAPage(int pageIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(pageIndex, pageCount);
    }
}
