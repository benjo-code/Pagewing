using System.Globalization;
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
/// How a rendering is compared with its reference. Both modes use the same reference images (rendered on Windows);
/// see <c>Docs/DECISIONS.md</c>, "Render-Regression: zwei Vergleichsmodi".
/// </summary>
internal enum CompareMode
{
    /// <summary>
    /// Pixel by pixel with <see cref="RenderTolerance.Default"/>. For pages without text, which render nearly
    /// identically on Windows and Linux.
    /// </summary>
    Strict,

    /// <summary>
    /// Means of <see cref="BlockTolerance.BlockSize"/> × <see cref="BlockTolerance.BlockSize"/> blocks with
    /// <see cref="BlockTolerance.Default"/>. For pages with text: PDFium substitutes different system fonts for the
    /// non-embedded standard fonts on Windows and Linux, so glyph edges and anti-aliasing differ while the layout does
    /// not. A low-pass comparison ignores the edges but still sees missing, moved or recoloured content.
    /// </summary>
    Tolerant,
}

/// <summary>
/// Tolerance of <see cref="CompareMode.Strict"/>: a pixel differs when any channel deviates by more than
/// <paramref name="MaxChannelDelta"/>; the comparison fails when the sizes differ or more than
/// <paramref name="MaxDifferingFraction"/> of the pixels differ.
/// </summary>
internal sealed record RenderTolerance(int MaxChannelDelta, double MaxDifferingFraction)
{
    /// <summary>The strict tolerance of the render regression tests (see <c>Docs/DECISIONS.md</c>).</summary>
    public static RenderTolerance Default { get; } = new(MaxChannelDelta: 16, MaxDifferingFraction: 0.002);
}

/// <summary>
/// Tolerance of <see cref="CompareMode.Tolerant"/>: both images are reduced to the per-channel means of
/// <paramref name="BlockSize"/> × <paramref name="BlockSize"/> blocks (smaller at the right and bottom edge). A block
/// differs when any channel mean deviates by more than <paramref name="MaxBlockDelta"/>; the comparison fails when the
/// sizes differ, more than <paramref name="MaxDifferingFraction"/> of the blocks differ, or any block deviates by more
/// than <paramref name="HardBlockDelta"/>.
/// </summary>
internal sealed record BlockTolerance(int BlockSize, double MaxBlockDelta, double MaxDifferingFraction, double HardBlockDelta)
{
    /// <summary>The block tolerance of the render regression tests (see <c>Docs/DECISIONS.md</c>).</summary>
    public static BlockTolerance Default { get; } = new(BlockSize: 8, MaxBlockDelta: 40, MaxDifferingFraction: 0.0027, HardBlockDelta: 89);
}

/// <summary>The outcome of <see cref="ImageComparer.Compare(Rgb, Rgb, RenderTolerance)"/>.</summary>
/// <param name="SizeMatches">Both images have the same width and height; the other values are only meaningful then.</param>
/// <param name="DifferingPixels">Pixels with a channel deviating by more than the tolerance.</param>
/// <param name="MaxDelta">The largest absolute deviation of any channel of any pixel.</param>
/// <param name="Fraction">The share of differing pixels (1 when the sizes differ).</param>
/// <param name="Passed">The images match within the tolerance.</param>
internal sealed record ComparisonResult(bool SizeMatches, long DifferingPixels, int MaxDelta, double Fraction, bool Passed);

/// <summary>The outcome of <see cref="ImageComparer.Compare(Rgb, Rgb, BlockTolerance)"/>.</summary>
/// <param name="SizeMatches">Both images have the same width and height; the other values are only meaningful then.</param>
/// <param name="Blocks">The number of blocks.</param>
/// <param name="DifferingBlocks">Blocks with a channel mean deviating by more than the block tolerance.</param>
/// <param name="MaxBlockDelta">The largest absolute deviation of any channel mean of any block.</param>
/// <param name="Fraction">The share of differing blocks (1 when the sizes differ).</param>
/// <param name="Passed">The images match within the tolerance.</param>
internal sealed record BlockComparisonResult(bool SizeMatches, long Blocks, long DifferingBlocks, double MaxBlockDelta, double Fraction, bool Passed);

/// <summary>The outcome of a comparison in either <see cref="CompareMode"/>.</summary>
/// <param name="Passed">The images match within the tolerance of the mode.</param>
/// <param name="Summary">One line with the measured values and the limits, for failure messages.</param>
internal sealed record ComparisonReport(bool Passed, string Summary);

/// <summary>Compares renderings with reference images (see <see cref="CompareMode"/>).</summary>
internal static class ImageComparer
{
    private static readonly (byte R, byte G, byte B) Marker = (255, 0, 0);

    /// <summary>Compares in the given mode with its default tolerance.</summary>
    public static ComparisonReport Compare(Rgb expected, Rgb actual, CompareMode mode)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return new ComparisonReport(false, string.Create(
                CultureInfo.InvariantCulture,
                $"size {actual.Width} x {actual.Height} differs from the reference {expected.Width} x {expected.Height}."));
        }

        if (mode == CompareMode.Strict)
        {
            var t = RenderTolerance.Default;
            var r = Compare(expected, actual, t);
            return new ComparisonReport(r.Passed, string.Create(
                CultureInfo.InvariantCulture,
                $"strict: {r.DifferingPixels} of {(long)expected.Width * expected.Height} pixels differ by more than {t.MaxChannelDelta} (fraction {r.Fraction:0.######}, allowed {t.MaxDifferingFraction}), max channel delta {r.MaxDelta}."));
        }

        var b = BlockTolerance.Default;
        var br = Compare(expected, actual, b);
        return new ComparisonReport(br.Passed, string.Create(
            CultureInfo.InvariantCulture,
            $"tolerant: {br.DifferingBlocks} of {br.Blocks} blocks ({b.BlockSize} x {b.BlockSize}) differ by more than {b.MaxBlockDelta} (fraction {br.Fraction:0.######}, allowed {b.MaxDifferingFraction}), max block delta {br.MaxBlockDelta:0.##} (hard limit {b.HardBlockDelta})."));
    }

    /// <summary>Creates the diff image of the given mode with its default tolerance.</summary>
    public static byte[] CreateDiffPng(Rgb expected, Rgb actual, CompareMode mode) =>
        mode == CompareMode.Strict
            ? CreateDiffPng(expected, actual, RenderTolerance.Default)
            : CreateDiffPng(expected, actual, BlockTolerance.Default);

    /// <summary>Compares <paramref name="actual"/> with <paramref name="expected"/> pixel by pixel.</summary>
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

    /// <summary>Compares the block means of <paramref name="actual"/> with those of <paramref name="expected"/>.</summary>
    public static BlockComparisonResult Compare(Rgb expected, Rgb actual, BlockTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);
        if (expected.Width != actual.Width || expected.Height != actual.Height)
        {
            return new BlockComparisonResult(SizeMatches: false, Blocks: 0, DifferingBlocks: 0, MaxBlockDelta: 0, Fraction: 1, Passed: false);
        }

        var deltas = BlockDeltas(expected, actual, tolerance.BlockSize);
        var differing = deltas.LongCount(delta => delta > tolerance.MaxBlockDelta);
        var max = deltas.Max();
        var fraction = differing / (double)deltas.Length;
        return new BlockComparisonResult(
            SizeMatches: true,
            Blocks: deltas.Length,
            DifferingBlocks: differing,
            MaxBlockDelta: max,
            Fraction: fraction,
            Passed: fraction <= tolerance.MaxDifferingFraction && max <= tolerance.HardBlockDelta);
    }

    /// <summary>
    /// Creates a PNG over the union of both sizes: differing pixels and pixels present in only one image are red,
    /// all others show the expected image faded towards white.
    /// </summary>
    public static byte[] CreateDiffPng(Rgb expected, Rgb actual, RenderTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(tolerance);
        return CreateDiff(expected, actual, (x, y) =>
            PixelDelta(expected.Pixels, actual.Pixels, ((y * expected.Width) + x) * 3, ((y * actual.Width) + x) * 3) > tolerance.MaxChannelDelta);
    }

    /// <summary>
    /// Creates a PNG over the union of both sizes: pixels of differing blocks and pixels present in only one image
    /// are red, all others show the expected image faded towards white.
    /// </summary>
    public static byte[] CreateDiffPng(Rgb expected, Rgb actual, BlockTolerance tolerance)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(tolerance);
        var size = tolerance.BlockSize;
        var sameSize = expected.Width == actual.Width && expected.Height == actual.Height;
        var deltas = sameSize ? BlockDeltas(expected, actual, size) : [];
        var blocksPerRow = (expected.Width + size - 1) / size;
        return CreateDiff(expected, actual, (x, y) =>
            !sameSize || deltas[((y / size) * blocksPerRow) + (x / size)] > tolerance.MaxBlockDelta);
    }

    private static byte[] CreateDiff(Rgb expected, Rgb actual, Func<int, int, bool> differs)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var width = Math.Max(expected.Width, actual.Width);
        var height = Math.Max(expected.Height, actual.Height);
        var diff = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var d = ((y * width) + x) * 3;
                var inBoth = x < expected.Width && y < expected.Height && x < actual.Width && y < actual.Height;
                if (inBoth && !differs(x, y))
                {
                    // Fade to a quarter of the contrast, so the page stays recognisable but never looks red.
                    var ei = ((y * expected.Width) + x) * 3;
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

    /// <summary>The largest per-channel deviation of the block means, row by row; both images have the same size.</summary>
    private static double[] BlockDeltas(Rgb expected, Rgb actual, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        var blocksPerRow = (expected.Width + size - 1) / size;
        var blockRows = (expected.Height + size - 1) / size;
        var sums = new long[blocksPerRow * blockRows * 3];
        var counts = new int[blocksPerRow * blockRows];
        for (var y = 0; y < expected.Height; y++)
        {
            var rowBlock = (y / size) * blocksPerRow;
            for (var x = 0; x < expected.Width; x++)
            {
                var block = rowBlock + (x / size);
                var i = ((y * expected.Width) + x) * 3;
                for (var c = 0; c < 3; c++)
                {
                    sums[(block * 3) + c] += expected.Pixels[i + c] - actual.Pixels[i + c];
                }

                counts[block]++;
            }
        }

        var deltas = new double[counts.Length];
        for (var block = 0; block < counts.Length; block++)
        {
            for (var c = 0; c < 3; c++)
            {
                deltas[block] = Math.Max(deltas[block], Math.Abs(sums[(block * 3) + c]) / (double)counts[block]);
            }
        }

        return deltas;
    }

    private static int PixelDelta(byte[] expected, byte[] actual, int expectedIndex, int actualIndex) =>
        Math.Max(
            Math.Abs(expected[expectedIndex] - actual[actualIndex]),
            Math.Max(
                Math.Abs(expected[expectedIndex + 1] - actual[actualIndex + 1]),
                Math.Abs(expected[expectedIndex + 2] - actual[actualIndex + 2])));
}
