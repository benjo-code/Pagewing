namespace Pagewing.Core.Signatures;

/// <summary>
/// Turns the scan or photo of a signature into an image with transparent paper: brightness becomes alpha, the colour of the ink
/// stays. Everything runs on the pixel buffer, so it needs no codec and no UI.
/// </summary>
public static class SignatureCutout
{
    /// <summary>The number of brightness levels below the threshold over which alpha rises from 0 to 255 (the soft edge).</summary>
    public const int RampLevels = 32;

    /// <summary>The margin kept around the ink by <see cref="TrimToContent"/>, in pixels.</summary>
    public const int TrimMargin = 2;

    private const int DefaultThreshold = 128;

    /// <summary>
    /// Suggests the threshold: the brightness (0 to 255) that best separates dark ink from light paper (Otsu's method; when
    /// several levels separate equally well, the middle of them). An image of one brightness gets 128.
    /// </summary>
    public static int AutoThreshold(SignatureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var histogram = new long[256];
        var bgra = image.Bgra;
        for (var i = 0; i < bgra.Length; i += 4)
        {
            histogram[Brightness(bgra, i)]++;
        }

        long total = (long)image.Width * image.Height;
        double sumAll = 0;
        for (var level = 0; level < 256; level++)
        {
            sumAll += level * (double)histogram[level];
        }

        double sumBelow = 0;
        long weightBelow = 0;
        double best = 0;
        int first = -1;
        int last = -1;
        for (var level = 0; level < 256; level++)
        {
            weightBelow += histogram[level];
            if (weightBelow == 0)
            {
                continue;
            }

            var weightAbove = total - weightBelow;
            if (weightAbove == 0)
            {
                break;
            }

            sumBelow += level * (double)histogram[level];
            var difference = (sumBelow / weightBelow) - ((sumAll - sumBelow) / weightAbove);
            var variance = weightBelow * (double)weightAbove * difference * difference;
            if (variance > best)
            {
                best = variance;
                first = level;
                last = level;
            }
            else if (variance == best && first >= 0)
            {
                last = level;
            }
        }

        return first < 0 ? DefaultThreshold : (first + last) / 2;
    }

    /// <summary>
    /// Makes paper transparent: a pixel at or above <paramref name="threshold"/> in brightness gets alpha 0, one
    /// <see cref="RampLevels"/> levels or more below it alpha 255, in between a linear ramp. The colour of every pixel stays; an
    /// alpha the source already had is multiplied in.
    /// </summary>
    public static SignatureImage Apply(SignatureImage image, int threshold)
    {
        ArgumentNullException.ThrowIfNull(image);
        var source = image.Bgra;
        var result = new byte[source.Length];
        for (var i = 0; i < source.Length; i += 4)
        {
            var alpha = Math.Clamp((threshold - Brightness(source, i)) * 255 / RampLevels, 0, 255);
            result[i] = source[i];
            result[i + 1] = source[i + 1];
            result[i + 2] = source[i + 2];
            result[i + 3] = (byte)(alpha * source[i + 3] / 255);
        }

        return new SignatureImage(image.Width, image.Height, result);
    }

    /// <summary>
    /// Crops to the pixels that show something (alpha above a negligible value) plus <see cref="TrimMargin"/> pixels on every side,
    /// never beyond the image. An image with nothing to see is returned as it is.
    /// </summary>
    public static SignatureImage TrimToContent(SignatureImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int left = image.Width;
        int right = -1;
        int top = image.Height;
        int bottom = -1;
        var bgra = image.Bgra;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (bgra[(((y * image.Width) + x) * 4) + 3] > SignatureImage.NegligibleAlpha)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        if (right < 0)
        {
            return image;
        }

        left = Math.Max(0, left - TrimMargin);
        top = Math.Max(0, top - TrimMargin);
        right = Math.Min(image.Width - 1, right + TrimMargin);
        bottom = Math.Min(image.Height - 1, bottom + TrimMargin);
        var width = right - left + 1;
        var height = bottom - top + 1;
        if (width == image.Width && height == image.Height)
        {
            return image;
        }

        var cropped = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Buffer.BlockCopy(bgra, (((top + y) * image.Width) + left) * 4, cropped, y * width * 4, width * 4);
        }

        return new SignatureImage(width, height, cropped);
    }

    /// <summary>
    /// Scales the image down so that its longer side is at most <paramref name="maxSide"/> pixels, keeping the aspect ratio (no side
    /// becomes 0), by averaging the covered source pixels weighted by their alpha (no dark fringe at the edge). An image that
    /// fits is returned as it is, never enlarged.
    /// </summary>
    public static SignatureImage FitWithin(SignatureImage image, int maxSide)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSide);
        var longest = Math.Max(image.Width, image.Height);
        if (longest <= maxSide)
        {
            return image;
        }

        var scale = (double)maxSide / longest;
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var source = image.Bgra;
        var result = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var y0 = (int)((long)y * image.Height / height);
            var y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * image.Height / height));
            for (var x = 0; x < width; x++)
            {
                var x0 = (int)((long)x * image.Width / width);
                var x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * image.Width / width));
                long alphaSum = 0;
                long blueWeighted = 0;
                long greenWeighted = 0;
                long redWeighted = 0;
                long blue = 0;
                long green = 0;
                long red = 0;
                for (var sy = y0; sy < y1; sy++)
                {
                    for (var sx = x0; sx < x1; sx++)
                    {
                        var o = ((sy * image.Width) + sx) * 4;
                        var a = source[o + 3];
                        alphaSum += a;
                        blueWeighted += source[o] * a;
                        greenWeighted += source[o + 1] * a;
                        redWeighted += source[o + 2] * a;
                        blue += source[o];
                        green += source[o + 1];
                        red += source[o + 2];
                    }
                }

                var count = (long)(y1 - y0) * (x1 - x0);
                var d = ((y * width) + x) * 4;
                if (alphaSum > 0)
                {
                    result[d] = (byte)((blueWeighted + (alphaSum / 2)) / alphaSum);
                    result[d + 1] = (byte)((greenWeighted + (alphaSum / 2)) / alphaSum);
                    result[d + 2] = (byte)((redWeighted + (alphaSum / 2)) / alphaSum);
                }
                else
                {
                    result[d] = (byte)(blue / count);
                    result[d + 1] = (byte)(green / count);
                    result[d + 2] = (byte)(red / count);
                }

                result[d + 3] = (byte)((alphaSum + (count / 2)) / count);
            }
        }

        return new SignatureImage(width, height, result);
    }

    /// <summary>The brightness (Rec. 601) of the pixel at <paramref name="offset"/>, composited on white if it has alpha.</summary>
    private static int Brightness(byte[] bgra, int offset)
    {
        var luma = ((bgra[offset + 2] * 299) + (bgra[offset + 1] * 587) + (bgra[offset] * 114) + 500) / 1000;
        var alpha = bgra[offset + 3];
        return alpha == 255 ? luma : ((luma * alpha) + (255 * (255 - alpha))) / 255;
    }
}
