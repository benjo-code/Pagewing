namespace Pagewing.Core.Ocr;

/// <summary>The size a page is rendered at for recognition.</summary>
/// <param name="Dpi">The resolution in pixels per inch.</param>
/// <param name="WidthPx">The width in pixels.</param>
/// <param name="HeightPx">The height in pixels.</param>
/// <param name="Lowered">Whether the pixel limit made the resolution lower than the one that was chosen (the report says so).</param>
/// <param name="TooLow">Whether the resolution is below <see cref="OcrResolution.MinDpi"/>: the page is skipped with the reason.</param>
public sealed record OcrRaster(int Dpi, int WidthPx, int HeightPx, bool Lowered, bool TooLow);

/// <summary>Chooses the resolution of a page for recognition.</summary>
public static class OcrResolution
{
    /// <summary>The lowest resolution a page is recognized at; below it the page is skipped and reported.</summary>
    public const int MinDpi = 150;

    /// <summary>The highest resolution chosen automatically.</summary>
    public const int MaxAutoDpi = 300;

    /// <summary>The resolution of a page without an image.</summary>
    public const int DefaultDpi = 300;

    /// <summary>The most pixels a page is rendered with (50 megapixels, one byte each as gray).</summary>
    public const long MaxPixels = 50_000_000;

    /// <summary>
    /// Chooses the resolution: the one asked for, or from the image of the page clamped to 150 through 300 (300 without an image), and lowers it,
    /// never silently, until the page fits <see cref="MaxPixels"/>.
    /// </summary>
    /// <param name="widthPt">The width of the displayed page in points.</param>
    /// <param name="heightPt">The height of the displayed page in points.</param>
    /// <param name="imageDpi">The effective resolution of the largest image, or <see langword="null"/>.</param>
    /// <param name="requestedDpi">The resolution asked for, or 0 for automatic.</param>
    /// <exception cref="ArgumentOutOfRangeException">A size is not a positive finite number.</exception>
    public static OcrRaster Resolve(double widthPt, double heightPt, double? imageDpi, int requestedDpi)
    {
        if (!double.IsFinite(widthPt) || widthPt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(widthPt));
        }

        if (!double.IsFinite(heightPt) || heightPt <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightPt));
        }

        int chosen;
        if (requestedDpi > 0)
        {
            chosen = requestedDpi;
        }
        else if (imageDpi is { } dpi && double.IsFinite(dpi) && dpi > 0)
        {
            chosen = (int)Math.Clamp(Math.Round(dpi), MinDpi, MaxAutoDpi);
        }
        else
        {
            chosen = DefaultDpi;
        }

        var dpiNow = chosen;
        var (width, height) = Size(widthPt, heightPt, dpiNow);
        var lowered = false;
        if (Pixels(width, height) > MaxPixels)
        {
            lowered = true;
            dpiNow = Math.Max(1, (int)Math.Floor(dpiNow * Math.Sqrt((double)MaxPixels / Pixels(width, height))));
            (width, height) = Size(widthPt, heightPt, dpiNow);
            while (Pixels(width, height) > MaxPixels && dpiNow > 1)
            {
                dpiNow--;
                (width, height) = Size(widthPt, heightPt, dpiNow);
            }
        }

        return new OcrRaster(dpiNow, width, height, lowered, dpiNow < MinDpi);
    }

    private static long Pixels(long width, long height) => width * height;

    private static (int Width, int Height) Size(double widthPt, double heightPt, int dpi)
    {
        // The renderer rounds a page up to whole pixels (scale = dpi / 72, as PdfiumRenderWork.PagePixelSize does); a size beyond int range is far above the limit and is reported as one.
        var width = Math.Min(Math.Ceiling(widthPt * (dpi / 72.0)), int.MaxValue);
        var height = Math.Min(Math.Ceiling(heightPt * (dpi / 72.0)), int.MaxValue);
        return ((int)Math.Max(1, width), (int)Math.Max(1, height));
    }
}
