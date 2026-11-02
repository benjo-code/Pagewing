namespace Pagewing.Pdfium;

/// <summary>
/// Pixelating and blurring the pixels of an image under a rectangle (milestone 14.1): the writing side of a calculation that
/// <c>Pagewing.Core.Redaction.ImageBlocks</c> does a second time for the check of the written file. This assembly knows no Core type, and the
/// two implementations share no code; both are held against the same vectors in the tests.
/// </summary>
/// <remarks>
/// The rule is the one of the spec: a rectangle of <c>w</c> x <c>h</c> pixels is divided into at most 6 (3 for "very coarse") blocks along each
/// side, none smaller than 8 pixels, the edges at <c>i * length / blocks</c>; every block gets the mean of its pixels (blue, green, red; sum plus
/// half the count, divided by the count), alpha is 255. Blurring blends these means bilinearly between the block centres, beyond the outer
/// centres the outer block alone; no original pixel enters it. The means are all taken before the first pixel is written.
/// </remarks>
internal static class PdfiumImageBlocks
{
    /// <summary>The smallest side of a block in pixels.</summary>
    internal const int MinBlockPixels = 8;

    /// <summary>The most blocks along a side, "coarse".</summary>
    internal const int CoarseBlocksPerAxis = 6;

    /// <summary>The most blocks along a side, "very coarse".</summary>
    internal const int VeryCoarseBlocksPerAxis = 3;

    /// <summary>
    /// Divides a rectangle into blocks. <see langword="false"/> when it is smaller than 2 x 2 blocks of <see cref="MinBlockPixels"/> pixels (the
    /// area gets the solid fill instead).
    /// </summary>
    public static bool TryPlan(int width, int height, bool veryCoarse, out int[] columnEdges, out int[] rowEdges)
    {
        columnEdges = [];
        rowEdges = [];
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var most = veryCoarse ? VeryCoarseBlocksPerAxis : CoarseBlocksPerAxis;
        var across = Math.Min(most, width / MinBlockPixels);
        var down = Math.Min(most, height / MinBlockPixels);
        if (across < 2 || down < 2)
        {
            return false;
        }

        columnEdges = Divide(width, across);
        rowEdges = Divide(height, down);
        return true;
    }

    /// <summary>
    /// Replaces the pixels of <paramref name="box"/> in <paramref name="pixels"/> (blue, green, red, alpha; top row first; rows of
    /// <paramref name="imageWidth"/> pixels) by their blocks; every other pixel stays as it is.
    /// </summary>
    /// <param name="pixels">The pixels of the whole image, changed in place.</param>
    /// <param name="imageWidth">The width of the image.</param>
    /// <param name="box">The rectangle, inside the image.</param>
    /// <param name="columnEdges">The edges of the blocks across, from <see cref="TryPlan"/> for the size of the box.</param>
    /// <param name="rowEdges">The edges of the blocks down.</param>
    /// <param name="blur"><see langword="true"/> to blend the means, <see langword="false"/> to show the blocks hard.</param>
    public static void Apply(byte[] pixels, int imageWidth, PixelBox box, int[] columnEdges, int[] rowEdges, bool blur)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(columnEdges);
        ArgumentNullException.ThrowIfNull(rowEdges);
        var across = columnEdges.Length - 1;
        var down = rowEdges.Length - 1;
        if (imageWidth <= 0 || box.Width <= 0 || box.Height <= 0 || box.X0 < 0 || box.Y0 < 0 || box.X1 > imageWidth
            || ((long)box.Y1 * imageWidth * 4) > pixels.Length || across < 1 || down < 1
            || columnEdges[0] != 0 || columnEdges[across] != box.Width || rowEdges[0] != 0 || rowEdges[down] != box.Height)
        {
            throw new ArgumentException("The rectangle and the blocks do not fit the image.");
        }

        // One mean per block and channel, taken from the original pixels before anything is written.
        var mean = new int[down, across, 3];
        for (var r = 0; r < down; r++)
        {
            for (var c = 0; c < across; c++)
            {
                long blue = 0;
                long green = 0;
                long red = 0;
                for (var y = box.Y0 + rowEdges[r]; y < box.Y0 + rowEdges[r + 1]; y++)
                {
                    var at = (((long)y * imageWidth) + box.X0 + columnEdges[c]) * 4;
                    for (var x = columnEdges[c]; x < columnEdges[c + 1]; x++, at += 4)
                    {
                        blue += pixels[at];
                        green += pixels[at + 1];
                        red += pixels[at + 2];
                    }
                }

                long count = (long)(columnEdges[c + 1] - columnEdges[c]) * (rowEdges[r + 1] - rowEdges[r]);
                mean[r, c, 0] = (int)((blue + (count / 2)) / count);
                mean[r, c, 1] = (int)((green + (count / 2)) / count);
                mean[r, c, 2] = (int)((red + (count / 2)) / count);
            }
        }

        if (!blur)
        {
            for (var r = 0; r < down; r++)
            {
                for (var c = 0; c < across; c++)
                {
                    for (var y = box.Y0 + rowEdges[r]; y < box.Y0 + rowEdges[r + 1]; y++)
                    {
                        var at = (((long)y * imageWidth) + box.X0 + columnEdges[c]) * 4;
                        for (var x = columnEdges[c]; x < columnEdges[c + 1]; x++, at += 4)
                        {
                            pixels[at] = (byte)mean[r, c, 0];
                            pixels[at + 1] = (byte)mean[r, c, 1];
                            pixels[at + 2] = (byte)mean[r, c, 2];
                            pixels[at + 3] = 255;
                        }
                    }
                }
            }

            return;
        }

        // For every column and row of the box: the two blocks it lies between and the weights (of the first and of the second) over a common divisor.
        var xs = Blend(columnEdges, box.Width);
        var ys = Blend(rowEdges, box.Height);
        for (var y = 0; y < box.Height; y++)
        {
            var (r0, r1, wy0, wy1) = ys[y];
            var at = (((long)(box.Y0 + y) * imageWidth) + box.X0) * 4;
            for (var x = 0; x < box.Width; x++, at += 4)
            {
                var (c0, c1, wx0, wx1) = xs[x];
                var divisor = (wx0 + wx1) * (wy0 + wy1);
                for (var channel = 0; channel < 3; channel++)
                {
                    var sum = (((long)mean[r0, c0, channel] * wx0) + ((long)mean[r0, c1, channel] * wx1)) * wy0;
                    sum += (((long)mean[r1, c0, channel] * wx0) + ((long)mean[r1, c1, channel] * wx1)) * wy1;
                    pixels[at + channel] = (byte)((sum + (divisor / 2)) / divisor);
                }

                pixels[at + 3] = 255;
            }
        }
    }

    private static int[] Divide(int length, int parts)
    {
        var edges = new int[parts + 1];
        for (var i = 1; i <= parts; i++)
        {
            edges[i] = (int)((long)length * i / parts);
        }

        return edges;
    }

    /// <summary>
    /// For each position along an axis: the block before, the block after, and the weights of the two (their sum is the common divisor of the
    /// position). The centre of a block is the middle of its edges; a position is the middle of its pixel. Before the first and after the last centre the
    /// first respectively the last block takes all the weight.
    /// </summary>
    private static (int Before, int After, long WeightBefore, long WeightAfter)[] Blend(int[] edges, int length)
    {
        var blocks = edges.Length - 1;
        var centres = new long[blocks];
        for (var b = 0; b < blocks; b++)
        {
            centres[b] = (long)edges[b] + edges[b + 1];
        }

        var result = new (int, int, long, long)[length];
        var current = 0;
        for (var p = 0; p < length; p++)
        {
            var at = (2L * p) + 1;
            if (at <= centres[0])
            {
                result[p] = (0, 0, 1, 0);
            }
            else if (at >= centres[blocks - 1])
            {
                result[p] = (blocks - 1, blocks - 1, 1, 0);
            }
            else
            {
                while (centres[current + 1] <= at)
                {
                    current++;
                }

                var span = centres[current + 1] - centres[current];
                var past = at - centres[current];
                result[p] = (current, current + 1, span - past, past);
            }
        }

        return result;
    }
}
