using Pagewing.Core.Redaction;
using Pagewing.Core.Text;
using Pagewing.Pdfium;

namespace Pagewing.Core.Pdf;

/// <summary>
/// Raster pages (milestone 14.1): a page that is one image, written into the target of a rebuild, and a count of what a page is made
/// of. Like the other page operations of a working copy these are for a document that is only written, never one that is shown.
/// Each call is one short job on the PDFium thread (<c>PdfiumPriority.Background</c>).
/// </summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Adds a page that shows the image over its whole media box and has nothing else: no text, no paths, no annotations. The page is
    /// created with the size and the <c>/Rotate</c> of <paramref name="page"/>; the pixels are checked (count and length) before
    /// anything is allocated or PDFium is asked. <see cref="PageCount"/> rises by one. For a working copy only.
    /// </summary>
    /// <param name="index">The 0-based position the new page gets: between 0 and <see cref="PageCount"/>.</param>
    /// <param name="page">The page to add.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The index, a size or the rotation of the page is out of range.</exception>
    /// <exception cref="ArgumentException">The pixels are not <c>WidthPx * HeightPx * 4</c> bytes.</exception>
    /// <exception cref="RedactionException">The image has more than <see cref="RedactionLimits.MaxRasterPixels"/> pixels (<see cref="RedactionError.LimitExceeded"/>).</exception>
    /// <exception cref="InvalidOperationException">PDFium could not create the page; nothing is left of it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task AddRasterPageAsync(int index, RasterPage page, CancellationToken cancellationToken = default) =>
        AddRasterPageAsync(index, page, overlay: null, cancellationToken);

    /// <summary>
    /// Decides the overlay text of the rectangles of a page that becomes an image, with the function that redacts a page in place decides
    /// with (no treated image: the background is the fill colour). One short job; nothing is changed. The rectangles are clipped to the page.
    /// </summary>
    /// <param name="widthPt">The width of the displayed page in points.</param>
    /// <param name="heightPt">The height of the displayed page in points.</param>
    /// <param name="areas">The rectangles in points from the top left of the page.</param>
    /// <param name="work">The texts (one for each rectangle), the style and the budget; must have a text.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    internal async Task<RasterOverlay> PlanRasterOverlayAsync(double widthPt, double heightPt, IReadOnlyList<PageRect> areas, RedactionOverlayWork work, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(areas);
        ArgumentNullException.ThrowIfNull(work);
        if (work.Texts.Count != areas.Count)
        {
            throw new ArgumentException("The overlay needs one text (or none) for each rectangle.", nameof(work));
        }

        var rectangles = areas.Select(static a => new PdfiumRedactionArea(a.Left, a.Top, a.Right, a.Bottom)).ToArray();
        var spec = work.ToSpec(document.Thread);
        var plan = await document.Thread.InvokeAsync(
            () => PdfiumRedactionRaster.PlanOverlay(spec, rectangles, widthPt, heightPt),
            PdfiumPriority.Background,
            cancellationToken).ConfigureAwait(false);
        return new RasterOverlay(plan, spec.Color);
    }

    /// <summary>As <see cref="AddRasterPageAsync(int, RasterPage, CancellationToken)"/>, and draws the overlay text above the image as path objects.</summary>
    /// <param name="index">The 0-based position the new page gets.</param>
    /// <param name="page">The page to add; not turned.</param>
    /// <param name="overlay">The decided overlay text of the page (from <see cref="PlanRasterOverlayAsync"/> for the same size), or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    internal Task AddRasterPageAsync(int index, RasterPage page, RasterOverlay? overlay, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(page);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, PageCount);

        // The limit is a rule of the redaction, so it has its own exception; it comes first so that nothing is allocated for a page beyond it.
        if (page.WidthPx > 0 && page.HeightPx > 0 && (long)page.WidthPx * page.HeightPx > RedactionLimits.MaxRasterPixels)
        {
            throw new RedactionException(RedactionError.LimitExceeded, $"A raster page has at most {RedactionLimits.MaxRasterPixels:N0} pixels.");
        }

        ArgumentNullException.ThrowIfNull(page.Bgra);
        return document.Thread.InvokeAsync(
            () =>
            {
                try
                {
                    PdfiumRedactionRaster.AddPage(document, index, page.WidthPt, page.HeightPt, page.Rotation, page.WidthPx, page.HeightPx, page.Bgra, overlay?.Plan, overlay?.Color ?? default);
                }
                finally
                {
                    RefreshPageCount();
                }
            },
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Counts the annotations of a page (links and form field widgets included) without reading its page objects, so a page with a
    /// hostile number of objects can be counted too.
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>The number of annotations.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidOperationException">The page cannot be loaded.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public Task<int> GetPageAnnotationCountAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        return document.Thread.InvokeAsync(
            () => PdfiumRedactionRaster.CountAnnotations(document, page),
            PdfiumPriority.Background,
            cancellationToken);
    }

    /// <summary>
    /// Counts the page objects of a page by kind, and its annotations. Reads the objects of the page as they are in the document (a
    /// document that was just written and opened again, or a working copy).
    /// </summary>
    /// <param name="page">The 0-based page.</param>
    /// <param name="cancellationToken">Cancels before the job starts.</param>
    /// <returns>The counts.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    /// <exception cref="InvalidOperationException">The page cannot be loaded, or has more objects than redaction handles.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled before the job started.</exception>
    /// <exception cref="ObjectDisposedException">The document is disposed.</exception>
    public async Task<PageObjectSummary> GetPageObjectSummaryAsync(int page, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ThrowIfNotAPage(page);
        var unreadable = SummarySeam?.BoundsUnreadable;
        var summary = await document.Thread.InvokeAsync(
            () => PdfiumRedactionRaster.Summarize(document, page, unreadable),
            PdfiumPriority.Background,
            cancellationToken).ConfigureAwait(false);
        var result = new PageObjectSummary(
            summary.Images,
            summary.Paths,
            summary.Texts,
            summary.Others,
            summary.Annotations,
            summary.PathBounds is null ? null : [.. summary.PathBounds.Select(static b => new PageRect(b.Left, b.Top, b.Right, b.Bottom))]);
        return SummarySeam?.Override is { } change ? change(result) : result;
    }

    /// <summary>Hooks for tests of the check of a page that became an image; <see langword="null"/> in production.</summary>
    internal PageSummarySeam? SummarySeam { get; set; }
}

/// <summary>The decided overlay text of a page that becomes an image: what is drawn, what is left out and why, and the colour.</summary>
internal sealed class RasterOverlay
{
    internal RasterOverlay(PdfiumOverlayPlan plan, PdfiumRedactionFill color)
    {
        Plan = plan;
        Color = color;
    }

    /// <summary>Gets the decisions.</summary>
    internal PdfiumOverlayPlan Plan { get; }

    /// <summary>Gets the colour of the glyphs.</summary>
    internal PdfiumRedactionFill Color { get; }

    /// <summary>Gets how many texts are drawn.</summary>
    public int Drawn => Plan.Drawn;

    /// <summary>Gets how many texts do not fit (the budget reason included).</summary>
    public int NotFitting => Plan.NotFitting;

    /// <summary>Gets how many of those are left out because the budget of the document is reached.</summary>
    public int OverLimit => Plan.OverLimit;

    /// <summary>Gets how many path objects the drawn texts take.</summary>
    public int Paths => Plan.Paths;

    /// <summary>Gets how many path segments the drawn texts take.</summary>
    public long Segments => Plan.Segments;

    /// <summary>Gets whether the budget of the document is reached after this page.</summary>
    public bool LimitReached => Plan.LimitReached;
}

/// <summary>Hooks for tests of <see cref="PdfDocument.GetPageObjectSummaryAsync"/>: a bound that PDFium cannot give, and a summary that is changed after it was read (a count that does not match, bounds that are missing). Production code sets neither.</summary>
internal sealed class PageSummarySeam
{
    /// <summary>Gets or sets the question, asked with the index of a path object on the PDFium thread, whether its bounds are unreadable.</summary>
    public Func<int, bool>? BoundsUnreadable { get; set; }

    /// <summary>Gets or sets the change made to the summary before it is returned.</summary>
    public Func<PageObjectSummary, PageObjectSummary>? Override { get; set; }
}
