namespace Pagewing.Core.Redaction;

/// <summary>The resolution and pixel size a page is rasterized at.</summary>
/// <param name="Dpi">The resolution in dots per inch that is used.</param>
/// <param name="WidthPx">The width in pixels.</param>
/// <param name="HeightPx">The height in pixels.</param>
/// <param name="Lowered">Whether <paramref name="Dpi"/> is lower than the one asked for because the page would have too many pixels.</param>
public readonly record struct RasterSize(int Dpi, int WidthPx, int HeightPx, bool Lowered);

/// <summary>The size of a raster page (milestone 14.1): the resolution asked for, lowered when the page would exceed <see cref="RedactionLimits.MaxRasterPixels"/>.</summary>
public static class RasterGeometry
{
    /// <summary>
    /// Works out the resolution and the pixel size of a page. The resolution asked for is held to
    /// <see cref="RedactionLimits.MinRasterDpi"/> to <see cref="RedactionLimits.MaxRasterDpi"/>, then lowered as far as the page needs
    /// to stay within <see cref="RedactionLimits.MaxRasterPixels"/> (each side is rounded up, as the renderer does).
    /// </summary>
    /// <param name="widthPt">The width of the displayed page in points.</param>
    /// <param name="heightPt">The height of the displayed page in points.</param>
    /// <param name="requestedDpi">The resolution asked for.</param>
    /// <returns>The resolution and size; <see cref="RasterSize.Lowered"/> says whether the resolution is below the one asked for (after clamping).</returns>
    /// <exception cref="RedactionException">The size is not positive and finite (<see cref="RedactionError.InvalidArea"/>), or the page has too many pixels even at <see cref="RedactionLimits.MinRasterDpi"/> (<see cref="RedactionError.LimitExceeded"/>).</exception>
    public static RasterSize Resolve(double widthPt, double heightPt, int requestedDpi)
    {
        if (!double.IsFinite(widthPt) || !double.IsFinite(heightPt) || !(widthPt > 0) || !(heightPt > 0))
        {
            throw new RedactionException(RedactionError.InvalidArea, "A page needs a positive, finite size.");
        }

        var wanted = Math.Clamp(requestedDpi, RedactionLimits.MinRasterDpi, RedactionLimits.MaxRasterDpi);
        var widthIn = widthPt / 72.0;
        var heightIn = heightPt / 72.0;
        var fits = Math.Sqrt(RedactionLimits.MaxRasterPixels / (widthIn * heightIn));
        var dpi = (int)Math.Min(wanted, Math.Floor(fits));

        // The pixels of each side are rounded up, so the plain formula can be a hair too generous: step down until the page fits.
        while (dpi >= RedactionLimits.MinRasterDpi)
        {
            var width = Pixels(widthPt, dpi);
            var height = Pixels(heightPt, dpi);
            if ((long)width * height <= RedactionLimits.MaxRasterPixels)
            {
                return new RasterSize(dpi, width, height, dpi < wanted);
            }

            dpi--;
        }

        throw new RedactionException(RedactionError.LimitExceeded, $"The page is too large to rasterize: even at {RedactionLimits.MinRasterDpi} dpi it would have more than {RedactionLimits.MaxRasterPixels:N0} pixels.");
    }

    private static int Pixels(double points, int dpi)
    {
        var pixels = Math.Ceiling(points * dpi / 72.0);
        return pixels >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)pixels);
    }
}
