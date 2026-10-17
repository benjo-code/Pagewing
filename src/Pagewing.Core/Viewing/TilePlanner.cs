using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// Plans the render jobs for the sharp image of one page: one job for a page that is small enough, otherwise
/// the tiles of a 512 pixel grid that touch the visible area.
/// </summary>
public static class TilePlanner
{
    /// <summary>The edge length of a tile in pixels.</summary>
    public const int TileSize = 512;

    /// <summary>The length of the longest side of a preview in pixels.</summary>
    public const int PreviewMaxSide = 320;

    /// <summary>Pages up to this many pixels are rendered as a single whole-page job (A4 up to about 200 %).</summary>
    public const long WholePageMaxPixels = 4_000_000;

    /// <summary>Gets the scale at which the longest side of <paramref name="size"/> is <see cref="PreviewMaxSide"/> pixels.</summary>
    public static double PreviewScale(PdfPageSize size) => PreviewMaxSide / Math.Max(size.Width, size.Height);

    /// <summary>
    /// Plans the sharp image of <paramref name="page"/> at <paramref name="scale"/>: only the tiles that touch
    /// <paramref name="visible"/>, ordered by the distance of their center to the center of <paramref name="visible"/>.
    /// </summary>
    /// <param name="page">The page index.</param>
    /// <param name="size">The page size in points.</param>
    /// <param name="scale">The wanted scale in pixels per point; it is rounded to a scale key, see <see cref="TileKey"/>.</param>
    /// <param name="visible">The visible area in pixels of the page at <paramref name="scale"/>; it may reach beyond the page.</param>
    /// <param name="background">Whether the jobs are only a buffer.</param>
    /// <returns>The jobs, most important first; empty when nothing of the page is visible.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and positive.</exception>
    public static IReadOnlyList<RenderRequest> PlanSharp(int page, PdfPageSize size, double scale, PixelRect visible, bool background)
    {
        var scaleKey = TileKey.ToScaleKey(scale);
        var renderScale = scaleKey / 1000.0;
        var (pageWidth, pageHeight) = size.ToPixels(renderScale);

        // The visible area limited to the page, in long so that a huge visible rectangle cannot overflow.
        var left = Math.Max(0L, visible.X);
        var top = Math.Max(0L, visible.Y);
        var right = Math.Min(pageWidth, (long)visible.X + visible.Width);
        var bottom = Math.Min(pageHeight, (long)visible.Y + visible.Height);
        if (right <= left || bottom <= top)
        {
            return [];
        }

        if ((long)pageWidth * pageHeight <= WholePageMaxPixels)
        {
            return [new RenderRequest(new TileKey(page, scaleKey, 0, 0), renderScale, new PixelRect(0, 0, pageWidth, pageHeight), true, background)];
        }

        var firstColumn = (int)(left / TileSize);
        var lastColumn = (int)((right - 1) / TileSize);
        var firstRow = (int)(top / TileSize);
        var lastRow = (int)((bottom - 1) / TileSize);
        var centerX = visible.X + (visible.Width / 2.0);
        var centerY = visible.Y + (visible.Height / 2.0);

        var tiles = new List<(RenderRequest Request, double Distance)>((lastColumn - firstColumn + 1) * (lastRow - firstRow + 1));
        for (var row = firstRow; row <= lastRow; row++)
        {
            for (var column = firstColumn; column <= lastColumn; column++)
            {
                var x = column * TileSize;
                var y = row * TileSize;
                var rect = new PixelRect(x, y, Math.Min(TileSize, pageWidth - x), Math.Min(TileSize, pageHeight - y));
                var dx = x + (rect.Width / 2.0) - centerX;
                var dy = y + (rect.Height / 2.0) - centerY;
                tiles.Add((new RenderRequest(new TileKey(page, scaleKey, column, row), renderScale, rect, false, background), (dx * dx) + (dy * dy)));
            }
        }

        // The loops produce row-major order, so the stable sort breaks ties top to bottom, left to right.
        return tiles.OrderBy(tile => tile.Distance).Select(tile => tile.Request).ToArray();
    }
}
