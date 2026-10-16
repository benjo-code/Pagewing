using Pagewing.Core.Imaging;
using Pagewing.Core.Pdf;
using Pagewing.TestSupport;

namespace Pagewing.Render.Tests;

/// <summary>An 8-bit RGB image, tightly packed, top row first.</summary>
internal sealed record Rgb(int Width, int Height, byte[] Pixels)
{
    /// <summary>Decodes a PNG file.</summary>
    public static Rgb Decode(byte[] png)
    {
        var (width, height, pixels) = PngDecoder.Decode(png);
        return new Rgb(width, height, pixels);
    }

    /// <summary>The colour of one pixel.</summary>
    public (byte R, byte G, byte B) Pixel(int x, int y)
    {
        var i = ((y * Width) + x) * 3;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2]);
    }

    /// <summary>Encodes the image as PNG with the production encoder.</summary>
    public byte[] EncodePng()
    {
        var bgra = new byte[Width * Height * 4];
        for (int src = 0, dst = 0; src < Pixels.Length; src += 3, dst += 4)
        {
            bgra[dst] = Pixels[src + 2];
            bgra[dst + 1] = Pixels[src + 1];
            bgra[dst + 2] = Pixels[src];
            bgra[dst + 3] = 0xFF;
        }

        return PngEncoder.Encode(new RenderedImage(Width, Height, Width * 4, bgra));
    }
}

/// <summary>
/// How far a rendering may deviate from its reference: a pixel differs when any channel deviates by more than
/// <paramref name="MaxChannelDelta"/>; the comparison fails when the sizes differ or more than
/// <paramref name="MaxDifferingFraction"/> of the pixels differ.
/// </summary>
internal sealed record RenderTolerance(int MaxChannelDelta, double MaxDifferingFraction)
{
    /// <summary>The tolerance of the render regression tests (see <c>Docs/DECISIONS.md</c>).</summary>
    public static RenderTolerance Default { get; } = new(MaxChannelDelta: 16, MaxDifferingFraction: 0.002);
}

/// <summary>The outcome of <see cref="ImageComparer.Compare"/>.</summary>
/// <param name="SizeMatches">Both images have the same width and height; the other values are only meaningful then.</param>
/// <param name="DifferingPixels">Pixels with a channel deviating by more than the tolerance.</param>
/// <param name="MaxDelta">The largest absolute deviation of any channel of any pixel.</param>
/// <param name="Fraction">The share of differing pixels (1 when the sizes differ).</param>
/// <param name="Passed">The images match within the tolerance.</param>
internal sealed record ComparisonResult(bool SizeMatches, long DifferingPixels, int MaxDelta, double Fraction, bool Passed);

/// <summary>Compares renderings with reference images.</summary>
internal static class ImageComparer
{
    private static readonly (byte R, byte G, byte B) Marker = (255, 0, 0);

    /// <summary>Compares <paramref name="actual"/> with <paramref name="expected"/>.</summary>
    public static ComparisonResult Compare(Rgb expected, Rgb actual, RenderTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return new ComparisonResult(SizeMatches: false, DifferingPixels: 0, MaxDelta: 0, Fraction: 1, Passed: false);
        }

        long differing = 0;
        var maxDelta = 0;
        var e = expected.Pixels;
        var a = actual.Pixels;
        for (var i = 0; i < e.Length; i += 3)
        {
            var delta = PixelDelta(e, a, i, i);
            maxDelta = Math.Max(maxDelta, delta);
            if (delta > tolerance.MaxChannelDelta)
            {
                differing++;
            }
        }

        var fraction = differing / ((double)expected.Width * expected.Height);
        return new ComparisonResult(
            SizeMatches: true,
            DifferingPixels: differing,
            MaxDelta: maxDelta,
            Fraction: fraction,
            Passed: fraction <= tolerance.MaxDifferingFraction);
    }

    /// <summary>
    /// Creates a PNG over the union of both sizes: differing pixels and pixels present in only one image are red,
    /// all others show the expected image faded towards white.
    /// </summary>
    public static byte[] CreateDiffPng(Rgb expected, Rgb actual, RenderTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);
        var width = Math.Max(expected.Width, actual.Width);
        var height = Math.Max(expected.Height, actual.Height);
        var diff = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = ((y * width) + x) * 3;
                var inExpected = x < expected.Width && y < expected.Height;
                var inActual = x < actual.Width && y < actual.Height;
                var ei = ((y * expected.Width) + x) * 3;
                if (inExpected && inActual
                    && PixelDelta(expected.Pixels, actual.Pixels, ei, ((y * actual.Width) + x) * 3) <= tolerance.MaxChannelDelta)
                {
                    // Fade to a quarter of the contrast, so the page stays recognisable but never looks red.
                    for (var c = 0; c < 3; c++)
                    {
                        diff[d + c] = (byte)(192 + (expected.Pixels[ei + c] / 4));
                    }
                }
                else
                {
                    (diff[d], diff[d + 1], diff[d + 2]) = Marker;
                }
            }
        }

        return new Rgb(width, height, diff).EncodePng();
    }

    private static int PixelDelta(byte[] expected, byte[] actual, int expectedIndex, int actualIndex) =>
        Math.Max(
            Math.Abs(expected[expectedIndex] - actual[actualIndex]),
            Math.Max(
                Math.Abs(expected[expectedIndex + 1] - actual[actualIndex + 1]),
                Math.Abs(expected[expectedIndex + 2] - actual[actualIndex + 2])));
}
