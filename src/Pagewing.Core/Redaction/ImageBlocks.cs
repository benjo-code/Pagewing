using Pagewing.Core.Pdf;

namespace Pagewing.Core.Redaction;

/// <summary>
/// How the pixel rectangle under a mark is divided into blocks for pixelating and blurring: <see cref="Columns"/> times
/// <see cref="Rows"/> blocks that fill the rectangle exactly.
/// </summary>
/// <param name="Columns">The number of blocks along the width (2 up to <see cref="RedactionLimits.CoarseBlocksPerAxis"/>).</param>
/// <param name="Rows">The number of blocks along the height.</param>
/// <param name="ColumnEdges">The <see cref="Columns"/> + 1 edges of the blocks along the width, in pixels from the left edge of the rectangle: the first is 0, the last the width of the rectangle.</param>
/// <param name="RowEdges">The <see cref="Rows"/> + 1 edges of the blocks along the height, in pixels from the top edge of the rectangle.</param>
public sealed record BlockGrid(int Columns, int Rows, int[] ColumnEdges, int[] RowEdges);

/// <summary>
/// The calculation of pixelating and blurring (milestone 14.1): few coarse blocks, each with the mean of its pixels, so that nothing of
/// the original is left but those means. The redaction writes the pixels with its own, independent implementation
/// (<c>Pagewing.Pdfium.PdfiumImageBlocks</c>, which knows no Core type); this one is what the check of the written file expects and what the
/// tests hold both against. Both are held against the same vectors (<c>ImageBlockVectors</c> in the tests).
/// </summary>
/// <remarks>
/// <para>
/// <b>Blocks.</b> A rectangle of <c>w</c> x <c>h</c> pixels gets <c>min(limit, w / 8)</c> columns and <c>min(limit, h / 8)</c> rows, with
/// <c>limit</c> 6 for <see cref="RedactionImageStrength.Coarse"/> and 3 for <see cref="RedactionImageStrength.VeryCoarse"/>; with fewer than
/// two columns or two rows there is no grid (<see cref="Plan"/> returns <see langword="null"/> and the area gets the solid fill). The edge
/// <c>i</c> of <c>n</c> blocks along a length <c>L</c> is <c>i * L / n</c> (integer division), so the blocks fill the rectangle exactly and
/// differ in size by at most one pixel; every block has at least <see cref="RedactionLimits.MinBlockPixels"/> pixels along each side.
/// </para>
/// <para>
/// <b>Mean.</b> For each block and each of blue, green and red: the sum over the block, divided by the number of its pixels, rounded half
/// up (<c>(sum + count / 2) / count</c> in integers). <b>Alpha</b> carries nothing of the original: the result is always 255 (the
/// treated area is opaque, like the solid fill), whatever the alpha of the original was.
/// </para>
/// <para>
/// <b>Pixelate</b> shows the means as they are, hard at the block edges. <b>Blur</b> blends them, from the block means only (no
/// original pixel enters): the mean of a block belongs to the centre of the block; a pixel at position <c>p</c> along an axis (its centre
/// is <c>p + 1/2</c>) lies between the centres of two neighbouring blocks <c>k</c> and <c>k + 1</c> and takes the linear blend with
/// <c>t = (p + 1/2 - c_k) / (c_k+1 - c_k)</c>; before the first centre and after the last it takes that block alone. Along the two axes
/// together that is the bilinear blend of four means. In integers, with doubled coordinates (<c>c2_k = edge_k + edge_k+1</c>,
/// <c>p2 = 2p + 1</c>): <c>tx = p2 - c2_k</c>, <c>dx = c2_k+1 - c2_k</c> (and <c>ty</c>, <c>dy</c> for the rows), the value is
/// <c>(m00 (dx-tx)(dy-ty) + m01 tx (dy-ty) + m10 (dx-tx) ty + m11 tx ty + dx dy / 2) / (dx dy)</c> with <c>m</c> indexed row then column, in
/// <see cref="long"/> arithmetic. The result is exact; the check of the result still allows a difference of one per channel.
/// </para>
/// </remarks>
public static class ImageBlocks
{
    /// <summary>Divides a rectangle of <paramref name="boxWidth"/> x <paramref name="boxHeight"/> pixels into blocks.</summary>
    /// <param name="boxWidth">The width of the pixel rectangle under the mark.</param>
    /// <param name="boxHeight">The height of the pixel rectangle under the mark.</param>
    /// <param name="strength">How coarse.</param>
    /// <returns>The grid, or <see langword="null"/> when the rectangle is smaller than 2 x 2 blocks of <see cref="RedactionLimits.MinBlockPixels"/> pixels (the solid fill takes its place).</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strength"/> is not a defined value.</exception>
    public static BlockGrid? Plan(int boxWidth, int boxHeight, RedactionImageStrength strength)
    {
        var limit = strength switch
        {
            RedactionImageStrength.Coarse => RedactionLimits.CoarseBlocksPerAxis,
            RedactionImageStrength.VeryCoarse => RedactionLimits.VeryCoarseBlocksPerAxis,
            _ => throw new ArgumentOutOfRangeException(nameof(strength)),
        };
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            return null;
        }

        var columns = Math.Min(limit, boxWidth / RedactionLimits.MinBlockPixels);
        var rows = Math.Min(limit, boxHeight / RedactionLimits.MinBlockPixels);
        if (columns < 2 || rows < 2)
        {
            return null;
        }

        return new BlockGrid(columns, rows, EdgesOf(boxWidth, columns), EdgesOf(boxHeight, rows));
    }

    /// <summary>The pixels that the rectangle <paramref name="box"/> must hold after pixelating or blurring.</summary>
    /// <param name="bgra">The pixels of the whole image as they were before (blue, green, red, alpha per pixel, top row first).</param>
    /// <param name="width">The width of the image in pixels.</param>
    /// <param name="height">The height of the image in pixels.</param>
    /// <param name="box">The pixel rectangle under the mark; inside the image.</param>
    /// <param name="grid">The blocks, from <see cref="Plan"/> for the size of <paramref name="box"/>.</param>
    /// <param name="treatment"><see cref="RedactionImageTreatment.Pixelate"/> or <see cref="RedactionImageTreatment.Blur"/>.</param>
    /// <returns>The pixels of the rectangle (<c>box.Width * box.Height * 4</c> bytes, blue, green, red, alpha 255).</returns>
    /// <exception cref="ArgumentException">The image, the rectangle and the grid do not fit together.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="treatment"/> is not a block treatment.</exception>
    public static byte[] Expected(byte[] bgra, int width, int height, PixelRect box, BlockGrid grid, RedactionImageTreatment treatment)
    {
        if (treatment is not (RedactionImageTreatment.Pixelate or RedactionImageTreatment.Blur))
        {
            throw new ArgumentOutOfRangeException(nameof(treatment), "Only pixelating and blurring are calculated in blocks.");
        }

        Validate(bgra, width, height, box, grid);
        return Render(MeansOf(bgra, width, box, grid), box.Width, box.Height, grid, treatment);
    }

    /// <summary>
    /// The block means (blue, green, red per block, row by row) of the rectangle <paramref name="box"/>: all that pixelating or blurring
    /// keeps of the original. The check of a result keeps these instead of the original pixels.
    /// </summary>
    /// <param name="bgra">The pixels of the whole image (blue, green, red, alpha per pixel, top row first).</param>
    /// <param name="width">The width of the image in pixels.</param>
    /// <param name="height">The height of the image in pixels.</param>
    /// <param name="box">The pixel rectangle under the mark; inside the image.</param>
    /// <param name="grid">The blocks, from <see cref="Plan"/> for the size of <paramref name="box"/>.</param>
    /// <returns><c>Rows * Columns * 3</c> values.</returns>
    /// <exception cref="ArgumentException">The image, the rectangle and the grid do not fit together.</exception>
    public static byte[] BlockMeansOf(byte[] bgra, int width, int height, PixelRect box, BlockGrid grid)
    {
        Validate(bgra, width, height, box, grid);
        return [.. MeansOf(bgra, width, box, grid).Select(static m => (byte)m)];
    }

    /// <summary>The pixels that a rectangle must hold after pixelating or blurring, worked out from the block means alone (see <see cref="BlockMeansOf"/>); equal to <see cref="Expected"/> of the original they come from.</summary>
    /// <param name="means"><c>grid.Rows * grid.Columns * 3</c> block means (blue, green, red, row by row).</param>
    /// <param name="grid">The blocks.</param>
    /// <param name="treatment"><see cref="RedactionImageTreatment.Pixelate"/> or <see cref="RedactionImageTreatment.Blur"/>.</param>
    /// <returns>The pixels of the rectangle (width and height of the grid, blue, green, red, alpha 255).</returns>
    /// <exception cref="ArgumentException">The means do not fit the grid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="treatment"/> is not a block treatment.</exception>
    public static byte[] ExpectedFromMeans(byte[] means, BlockGrid grid, RedactionImageTreatment treatment)
    {
        ArgumentNullException.ThrowIfNull(means);
        ArgumentNullException.ThrowIfNull(grid);
        if (treatment is not (RedactionImageTreatment.Pixelate or RedactionImageTreatment.Blur))
        {
            throw new ArgumentOutOfRangeException(nameof(treatment), "Only pixelating and blurring are calculated in blocks.");
        }

        if (grid.ColumnEdges.Length != grid.Columns + 1 || grid.RowEdges.Length != grid.Rows + 1 || grid.ColumnEdges[0] != 0 || grid.RowEdges[0] != 0
            || grid.ColumnEdges[^1] <= 0 || grid.RowEdges[^1] <= 0 || means.Length != grid.Rows * grid.Columns * 3)
        {
            throw new ArgumentException("The means do not fit the grid.", nameof(means));
        }

        return Render(means.Select(static m => (int)m).ToArray(), grid.ColumnEdges[^1], grid.RowEdges[^1], grid, treatment);
    }

    private static void Validate(byte[] bgra, int width, int height, PixelRect box, BlockGrid grid)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentNullException.ThrowIfNull(grid);
        if (width <= 0 || height <= 0 || (long)width * height * 4 != bgra.Length)
        {
            throw new ArgumentException("The pixels do not have the size of the image.", nameof(bgra));
        }

        if (box.Width <= 0 || box.Height <= 0 || box.X < 0 || box.Y < 0 || (long)box.X + box.Width > width || (long)box.Y + box.Height > height)
        {
            throw new ArgumentException("The rectangle does not lie inside the image.", nameof(box));
        }

        if (grid.ColumnEdges.Length != grid.Columns + 1 || grid.RowEdges.Length != grid.Rows + 1
            || grid.ColumnEdges[0] != 0 || grid.ColumnEdges[^1] != box.Width || grid.RowEdges[0] != 0 || grid.RowEdges[^1] != box.Height)
        {
            throw new ArgumentException("The blocks do not fill the rectangle.", nameof(grid));
        }
    }

    private static byte[] Render(int[] means, int boxWidth, int boxHeight, BlockGrid grid, RedactionImageTreatment treatment)
    {
        var box = new PixelRect(0, 0, boxWidth, boxHeight);

        // Along each axis, once: the block that holds a position (pixelate) and the two blocks it lies between with the weight (blur).
        var across = new (int First, int Second, long Weight, long Divisor)[box.Width];
        for (var x = 0; x < across.Length; x++)
        {
            across[x] = treatment == RedactionImageTreatment.Pixelate ? (BlockOf(grid.ColumnEdges, x), 0, 0, 1) : Between(grid.ColumnEdges, x);
        }

        var down = new (int First, int Second, long Weight, long Divisor)[box.Height];
        for (var y = 0; y < down.Length; y++)
        {
            down[y] = treatment == RedactionImageTreatment.Pixelate ? (BlockOf(grid.RowEdges, y), 0, 0, 1) : Between(grid.RowEdges, y);
        }

        var result = new byte[box.Width * box.Height * 4];
        for (var y = 0; y < box.Height; y++)
        {
            for (var x = 0; x < box.Width; x++)
            {
                var at = ((y * box.Width) + x) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    result[at + channel] = treatment == RedactionImageTreatment.Pixelate
                        ? (byte)means[(((down[y].First * grid.Columns) + across[x].First) * 3) + channel]
                        : BlurredValue(means, grid.Columns, across[x], down[y], channel);
                }

                result[at + 3] = 255;
            }
        }

        return result;
    }

    private static int[] EdgesOf(int length, int count)
    {
        var edges = new int[count + 1];
        for (var i = 0; i <= count; i++)
        {
            edges[i] = (int)((long)i * length / count);
        }

        return edges;
    }

    /// <summary>The index of the block that holds the position (the last edge that is not beyond it).</summary>
    private static int BlockOf(int[] edges, int position)
    {
        var block = 0;
        while (edges[block + 1] <= position)
        {
            block++;
        }

        return block;
    }

    /// <summary>The mean (blue, green, red) of each block, row by row: three numbers per block.</summary>
    private static int[] MeansOf(byte[] bgra, int width, PixelRect box, BlockGrid grid)
    {
        var means = new int[grid.Rows * grid.Columns * 3];
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                var sums = new long[3];
                long count = 0;
                for (var y = grid.RowEdges[row]; y < grid.RowEdges[row + 1]; y++)
                {
                    for (var x = grid.ColumnEdges[column]; x < grid.ColumnEdges[column + 1]; x++)
                    {
                        var at = ((((long)box.Y + y) * width) + box.X + x) * 4;
                        sums[0] += bgra[at];
                        sums[1] += bgra[at + 1];
                        sums[2] += bgra[at + 2];
                        count++;
                    }
                }

                for (var channel = 0; channel < 3; channel++)
                {
                    means[(((row * grid.Columns) + column) * 3) + channel] = (int)((sums[channel] + (count / 2)) / count);
                }
            }
        }

        return means;
    }

    private static byte BlurredValue(int[] means, int columns, (int First, int Second, long Weight, long Divisor) across, (int First, int Second, long Weight, long Divisor) down, int channel)
    {
        var (column, columnNext, tx, dx) = across;
        var (row, rowNext, ty, dy) = down;
        long Mean(int r, int c) => means[(((r * columns) + c) * 3) + channel];
        var total = (Mean(row, column) * (dx - tx) * (dy - ty))
            + (Mean(row, columnNext) * tx * (dy - ty))
            + (Mean(rowNext, column) * (dx - tx) * ty)
            + (Mean(rowNext, columnNext) * tx * ty);
        var divisor = dx * dy;
        return (byte)((total + (divisor / 2)) / divisor);
    }

    /// <summary>
    /// For a pixel along one axis: the two blocks it lies between and where (see the remarks of the class). Beyond the first and the last
    /// centre both are the same block with a weight of 0.
    /// </summary>
    private static (int First, int Second, long Weight, long Divisor) Between(int[] edges, int position)
    {
        var blocks = edges.Length - 1;
        var doubled = (2L * position) + 1;
        if (doubled <= edges[0] + edges[1])
        {
            return (0, 0, 0, 1);
        }

        if (doubled >= edges[blocks - 1] + edges[blocks])
        {
            return (blocks - 1, blocks - 1, 0, 1);
        }

        var k = 0;
        while (edges[k + 1] + edges[k + 2] <= doubled)
        {
            k++;
        }

        var centre = (long)edges[k] + edges[k + 1];
        var next = (long)edges[k + 1] + edges[k + 2];
        return (k, k + 1, doubled - centre, next - centre);
    }
}
