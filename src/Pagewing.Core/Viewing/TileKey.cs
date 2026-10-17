using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// Identifies one rendered image of a page: the page, the scale as an integer key and the tile in the grid.
/// A preview (the coarse whole-page image) has <see cref="PreviewScaleKey"/> and tile (0, 0).
/// </summary>
/// <remarks>
/// The scale is part of the key as an integer so equal scales compare equal and every tile of a page at one
/// zoom has the same pixel grid: tiles are always rendered with <see cref="Scale"/>, never with the raw scale.
/// </remarks>
/// <param name="Page">The zero-based page index.</param>
/// <param name="ScaleKey">The scale in thousandths of a pixel per point, or <see cref="PreviewScaleKey"/>.</param>
/// <param name="Column">The tile column, 0 for whole-page images.</param>
/// <param name="Row">The tile row, 0 for whole-page images.</param>
public readonly record struct TileKey(int Page, int ScaleKey, int Column, int Row)
{
    /// <summary>The scale key of previews. Real scale keys are at least 1.</summary>
    public const int PreviewScaleKey = 0;

    /// <summary>Gets a value indicating whether this is the coarse preview of a page.</summary>
    public bool IsPreview => ScaleKey == PreviewScaleKey;

    /// <summary>Gets the scale in pixels per point that belongs to <see cref="ScaleKey"/>; 0 for previews (their scale depends on the page).</summary>
    public double Scale => ScaleKey / 1000.0;

    /// <summary>Converts a scale in pixels per point to a scale key: thousandths, rounded, at least 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not finite and positive.</exception>
    public static int ToScaleKey(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be finite and greater than 0.");
        }

        return (int)Math.Clamp(Math.Round(scale * 1000), 1, int.MaxValue);
    }
}

/// <summary>
/// One render job as planned for the scheduler. The order in a plan is the priority: earlier requests matter more.
/// </summary>
/// <param name="Key">What the result is stored under in the cache.</param>
/// <param name="Scale">The scale to render with, in pixels per point.</param>
/// <param name="Rect">The area of the page image in pixels at <paramref name="Scale"/>; the whole page image when <paramref name="WholePage"/> is set.</param>
/// <param name="WholePage">Whether the job renders the whole page rather than a tile.</param>
/// <param name="Background">Whether the job is only a buffer beyond the visible pages.</param>
public readonly record struct RenderRequest(TileKey Key, double Scale, PixelRect Rect, bool WholePage, bool Background);
