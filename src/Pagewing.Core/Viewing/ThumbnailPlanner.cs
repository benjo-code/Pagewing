using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// Plans the render jobs for the page thumbnails of the side panel: one small whole-page image per page, all in the
/// background, the pages nearest to the first requested page first.
/// </summary>
public static class ThumbnailPlanner
{
    /// <summary>The width of a thumbnail in device-independent pixels (density "Normal", design system 5.4).</summary>
    public const int Width = 136;

    /// <summary>The most thumbnails planned at once.</summary>
    public const int MaxRequests = 64;

    /// <summary>Gets the height of a thumbnail of a page of <paramref name="size"/>: <see cref="Width"/> times height over width.</summary>
    public static double HeightFor(PdfPageSize size) => HeightFor(size, Width);

    /// <summary>Gets the height of a thumbnail <paramref name="width"/> DIPs wide of a page of <paramref name="size"/>.</summary>
    public static double HeightFor(PdfPageSize size, double width) => width * size.Height / size.Width;

    /// <summary>
    /// Plans the thumbnails of <paramref name="pages"/>. The first page of <paramref name="pages"/> is the one that
    /// matters most (for example the page in view); the others follow by their distance to it (the lower page index
    /// first on a tie), at most <see cref="MaxRequests"/> of them. Repeated pages are planned once. Pages outside
    /// <paramref name="sizes"/> and pages with an unusable size are skipped.
    /// </summary>
    /// <param name="sizes">The sizes of the known pages in points.</param>
    /// <param name="pages">The zero-based indices of the pages shown in the panel, the most important first.</param>
    /// <param name="renderScaling">Device pixels per device-independent pixel of the screen.</param>
    /// <returns>The jobs, most important first, to be stored under <c>new TileKey(page, TileKey.PreviewScaleKey, 0, 0)</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="renderScaling"/> is not finite and positive.</exception>
    public static IReadOnlyList<RenderRequest> Plan(IReadOnlyList<PdfPageSize> sizes, IEnumerable<int> pages, double renderScaling) =>
        Plan(sizes, pages, renderScaling, Width);

    /// <summary>
    /// Plans the thumbnails of <paramref name="pages"/> like <see cref="Plan(IReadOnlyList{PdfPageSize}, IEnumerable{int}, double)"/>,
    /// each <paramref name="width"/> DIPs wide (the two columns of the density "Kompakt" are narrower than <see cref="Width"/>).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="renderScaling"/> or <paramref name="width"/> is not finite and positive.</exception>
    public static IReadOnlyList<RenderRequest> Plan(IReadOnlyList<PdfPageSize> sizes, IEnumerable<int> pages, double renderScaling, double width)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        ArgumentNullException.ThrowIfNull(pages);
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(renderScaling), renderScaling, "The render scaling must be finite and greater than 0.");
        }

        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "The width must be finite and greater than 0.");
        }

        var usable = new List<int>();
        var seen = new HashSet<int>();
        foreach (var page in pages)
        {
            if (page >= 0 && page < sizes.Count && IsUsable(sizes[page]) && seen.Add(page))
            {
                usable.Add(page);
            }
        }

        if (usable.Count == 0)
        {
            return [];
        }

        var first = usable[0];
        var ordered = usable
            .OrderBy(page => Math.Abs((long)page - first))
            .ThenBy(page => page);

        var requests = new List<RenderRequest>();
        foreach (var page in ordered)
        {
            if (requests.Count == MaxRequests)
            {
                break;
            }

            var size = sizes[page];
            var scale = ScaleFor(size, renderScaling, width);
            (int Width, int Height) pixels;
            try
            {
                pixels = size.ToPixels(scale);
            }
            catch (PdfRenderException)
            {
                // A degenerate page (for example extremely narrow) whose thumbnail would not fit in an int.
                continue;
            }

            requests.Add(new RenderRequest(
                new TileKey(page, TileKey.PreviewScaleKey, 0, 0),
                scale,
                new PixelRect(0, 0, pixels.Width, pixels.Height),
                WholePage: true,
                Background: true));
        }

        return requests;
    }

    /// <summary>
    /// The scale (pixels per point) at which the page is <paramref name="width"/> DIPs wide in device pixels. The product of
    /// page width and scale can come out a rounding error above the wanted width (300 × 0.68 = 204.00000000000003), and
    /// the pixel size rounds up, which would make the thumbnail a pixel wider than its place on the screen; the scale is
    /// then lowered by the smallest steps until it fits.
    /// </summary>
    private static double ScaleFor(PdfPageSize size, double renderScaling, double width)
    {
        var target = width * renderScaling;
        var wanted = Math.Ceiling(target - 1e-9);
        var scale = target / size.Width;
        for (var step = 0; step < 4 && size.Width * scale > wanted; step++)
        {
            scale = Math.BitDecrement(scale);
        }

        return scale;
    }

    private static bool IsUsable(PdfPageSize size) =>
        double.IsFinite(size.Width) && double.IsFinite(size.Height) && size.Width > 0 && size.Height > 0;
}
