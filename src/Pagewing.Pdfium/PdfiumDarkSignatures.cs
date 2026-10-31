using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// The image signatures of Pagewing on the dark page (milestone 12). PDFium's colour scheme recolours paths and text but leaves
/// the pixels of an image as they are, so a signature of dark ink would vanish on the dark ground. The dark render therefore
/// reads each own image signature once (<see cref="PdfiumSignatures.ReadOwnImage"/>: one to one, within the page's pixel budget),
/// takes its ink as the alpha-weighted mean colour of its pixels, and when that colour is too dark for 3:1
/// (<see cref="PdfiumDarkMarkups.LineColor"/>) paints the lightened colour over what PDFium drew, with the share of the image's own
/// alpha at each pixel (the average over the source pixels that pixel covers, from a summed-area table). A light ink needs
/// nothing: PDFium's drawing stays. Another program's stamp is never touched. The ink of an image is taken to be one colour (a
/// signature is); several colours come out as their mean, lightened.
/// </summary>
/// <remarks>
/// Painting over PDFium's pixels leaves a faint trace of its own dark ink at soft edges (PDFium blended the dark ink in before):
/// at most a quarter of the difference between the ink and the ground, which is small because the ink is near the ground's
/// brightness, the very reason for the painting.
/// </remarks>
internal static unsafe class PdfiumDarkSignatures
{
    /// <summary>
    /// Reads the own image signature at <paramref name="index"/>; <see langword="null"/> for any other annotation, when it is not own,
    /// when its ink needs no painting, or when <paramref name="pixelBudget"/> is used up.
    /// </summary>
    public static DarkImage? Read(
        PdfiumDocument document,
        PdfiumPage page,
        nint annotation,
        DisplayTransform transform,
        PdfiumPageColors colors,
        ref long pixelBudget)
    {
        FsRectF rect;
        if (Fpdf.FPDFAnnot_GetSubtype(annotation) != FpdfConstants.AnnotStamp || Fpdf.FPDFAnnot_GetRect(annotation, &rect) == 0
            || PdfiumSignatures.ReadOwnImage(document, page, annotation, ref pixelBudget) is not var (width, height, bgra))
        {
            return null;
        }

        long weight = 0;
        long blue = 0;
        long green = 0;
        long red = 0;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var alpha = bgra[i + 3];
            weight += alpha;
            blue += bgra[i] * alpha;
            green += bgra[i + 1] * alpha;
            red += bgra[i + 2] * alpha;
        }

        if (weight == 0)
        {
            return null;
        }

        var mean = 0xFF000000u | ((uint)((red + (weight / 2)) / weight) << 16) | ((uint)((green + (weight / 2)) / weight) << 8) | (uint)((blue + (weight / 2)) / weight);
        var ink = PdfiumDarkMarkups.LineColor(mean, colors.Background);
        if (ink == mean)
        {
            return null;
        }

        // The sum of alpha over the pixels before (x, y): the average over any box of pixels in constant time.
        var sums = new uint[(width + 1) * (height + 1)];
        for (var y = 0; y < height; y++)
        {
            uint row = 0;
            for (var x = 0; x < width; x++)
            {
                row += bgra[(((y * width) + x) * 4) + 3];
                sums[((y + 1) * (width + 1)) + x + 1] = sums[(y * (width + 1)) + x + 1] + row;
            }
        }

        // The image lies upright in the rectangle on the displayed page, whatever the page's rotation (it was made so).
        var a = transform.ToDisplay(rect.Left, rect.Bottom);
        var b = transform.ToDisplay(rect.Right, rect.Top);
        var c = transform.ToDisplay(rect.Left, rect.Top);
        var d = transform.ToDisplay(rect.Right, rect.Bottom);
        return new DarkImage(
            Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X)),
            Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y)),
            Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X)),
            Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y)),
            width,
            height,
            sums,
            ink);
    }

    /// <summary>
    /// Paints the images into a rendered BGRx bitmap (see <see cref="PdfiumDarkMarkups.Apply"/> for the mapping), each pixel inside
    /// the image's rectangle with the average alpha of the source pixels it covers. <paramref name="budget"/> counts the pixels
    /// covered; an image that would exceed it is left out.
    /// </summary>
    public static void Apply(
        IReadOnlyList<DarkImage> images,
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        double scaleX,
        double scaleY,
        double offsetX,
        double offsetY,
        ref long budget)
    {
        foreach (var image in images)
        {
            var left = (image.Left * scaleX) + offsetX;
            var right = (image.Right * scaleX) + offsetX;
            var top = (image.Top * scaleY) + offsetY;
            var bottom = (image.Bottom * scaleY) + offsetY;
            var spanX = right - left;
            var spanY = bottom - top;
            if (!(spanX > 0) || !(spanY > 0))
            {
                continue;
            }

            var x0 = Math.Max(0, (int)Math.Floor(left));
            var x1 = Math.Min(width, (int)Math.Ceiling(right));
            var y0 = Math.Max(0, (int)Math.Floor(top));
            var y1 = Math.Min(height, (int)Math.Ceiling(bottom));
            if (x1 <= x0 || y1 <= y0)
            {
                continue;
            }

            budget -= (long)(x1 - x0) * (y1 - y0);
            if (budget < 0)
            {
                return;
            }

            var inkBlue = (int)(image.Color & 0xFF);
            var inkGreen = (int)((image.Color >> 8) & 0xFF);
            var inkRed = (int)((image.Color >> 16) & 0xFF);
            var columns = image.Width + 1;
            for (var y = y0; y < y1; y++)
            {
                var v0 = Math.Clamp((int)Math.Floor((y - top) / spanY * image.Height), 0, image.Height - 1);
                var v1 = Math.Clamp((int)Math.Ceiling((y + 1 - top) / spanY * image.Height), v0 + 1, image.Height);
                for (var x = x0; x < x1; x++)
                {
                    var u0 = Math.Clamp((int)Math.Floor((x - left) / spanX * image.Width), 0, image.Width - 1);
                    var u1 = Math.Clamp((int)Math.Ceiling((x + 1 - left) / spanX * image.Width), u0 + 1, image.Width);
                    var sum = (long)image.Sums[(v1 * columns) + u1] - image.Sums[(v0 * columns) + u1] - image.Sums[(v1 * columns) + u0] + image.Sums[(v0 * columns) + u0];
                    var area = (long)(u1 - u0) * (v1 - v0);
                    var alpha = (int)((sum + (area / 2)) / area);
                    if (alpha <= 0)
                    {
                        continue;
                    }

                    var offset = (y * stride) + (x * 4);
                    pixels[offset] = Blend(pixels[offset], inkBlue, alpha);
                    pixels[offset + 1] = Blend(pixels[offset + 1], inkGreen, alpha);
                    pixels[offset + 2] = Blend(pixels[offset + 2], inkRed, alpha);
                }
            }
        }
    }

    private static byte Blend(int under, int over, int alpha) => (byte)(((under * (255 - alpha)) + (over * alpha) + 127) / 255);
}

/// <summary>
/// An image signature as the dark page paints it: its rectangle on the displayed page (points from the top left), the pixel size of
/// its image, the summed-area table of the image's alpha and the colour of its ink there (ARGB).
/// </summary>
/// <param name="Left">The left edge in points.</param>
/// <param name="Top">The top edge in points.</param>
/// <param name="Right">The right edge in points.</param>
/// <param name="Bottom">The bottom edge in points.</param>
/// <param name="Width">The width of the image in pixels.</param>
/// <param name="Height">The height of the image in pixels.</param>
/// <param name="Sums">(Width + 1) * (Height + 1) sums of alpha over the pixels before each corner.</param>
/// <param name="Color">The ink on the dark page (ARGB).</param>
internal sealed record DarkImage(double Left, double Top, double Right, double Bottom, int Width, int Height, uint[] Sums, uint Color);
