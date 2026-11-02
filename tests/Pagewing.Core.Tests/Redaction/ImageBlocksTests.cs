using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Pdfium;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// The block calculation of pixelating and blurring (milestone 14.1, task 12): the plan of the blocks, the pixelated and the blurred
/// result, and that the second implementation in <c>Pagewing.Pdfium</c> (the one that writes the pixels; it knows no Core type) agrees with
/// <see cref="ImageBlocks"/> (the one the check of the result expects) on every vector.
/// </summary>
public sealed class ImageBlocksTests
{
    private static RedactionImageStrength StrengthOf(bool veryCoarse) => veryCoarse ? RedactionImageStrength.VeryCoarse : RedactionImageStrength.Coarse;

    private static PixelRect Whole(int side) => new(0, 0, side, side);

    // ---- The plan of the blocks ----

    [Fact]
    public void BlockGridNeverExceedsTheAxisLimit()
    {
        for (var side = 0; side <= 5_000; side += (side < 300 ? 1 : 37))
        {
            var coarse = ImageBlocks.Plan(side, side, RedactionImageStrength.Coarse);
            var veryCoarse = ImageBlocks.Plan(side, side + 11, RedactionImageStrength.VeryCoarse);
            if (coarse is not null)
            {
                Assert.InRange(coarse.Columns, 2, RedactionLimits.CoarseBlocksPerAxis);
                Assert.InRange(coarse.Rows, 2, RedactionLimits.CoarseBlocksPerAxis);
            }

            if (veryCoarse is not null)
            {
                Assert.InRange(veryCoarse.Columns, 2, RedactionLimits.VeryCoarseBlocksPerAxis);
                Assert.InRange(veryCoarse.Rows, 2, RedactionLimits.VeryCoarseBlocksPerAxis);
            }
        }

        Assert.Equal(6, ImageBlocks.Plan(1_000, 1_000, RedactionImageStrength.Coarse)!.Columns);
        Assert.Equal(3, ImageBlocks.Plan(1_000, 1_000, RedactionImageStrength.VeryCoarse)!.Rows);
    }

    [Fact]
    public void BlocksAreAtLeastEightPixels()
    {
        foreach (var strength in new[] { RedactionImageStrength.Coarse, RedactionImageStrength.VeryCoarse })
        {
            for (var width = 1; width <= 160; width++)
            {
                for (var height = 1; height <= 160; height += 7)
                {
                    var grid = ImageBlocks.Plan(width, height, strength);
                    if (grid is null)
                    {
                        continue;
                    }

                    AssertFillsExactly(grid.ColumnEdges, grid.Columns, width);
                    AssertFillsExactly(grid.RowEdges, grid.Rows, height);
                }
            }
        }

        static void AssertFillsExactly(int[] edges, int count, int length)
        {
            Assert.Equal(count + 1, edges.Length);
            Assert.Equal(0, edges[0]);
            Assert.Equal(length, edges[^1]);
            var smallest = int.MaxValue;
            var largest = 0;
            for (var i = 0; i < count; i++)
            {
                var size = edges[i + 1] - edges[i];
                smallest = Math.Min(smallest, size);
                largest = Math.Max(largest, size);
            }

            Assert.True(smallest >= RedactionLimits.MinBlockPixels, $"A block of {smallest} pixels in {length}.");
            Assert.True(largest - smallest <= 1, $"Blocks of {smallest} to {largest} pixels in {length}.");
        }
    }

    [Fact]
    public void TooSmallBoxFallsBack()
    {
        foreach (var (width, height) in ImageBlockVectors.TooSmall)
        {
            Assert.Null(ImageBlocks.Plan(width, height, RedactionImageStrength.Coarse));
            Assert.Null(ImageBlocks.Plan(width, height, RedactionImageStrength.VeryCoarse));
        }

        Assert.Null(ImageBlocks.Plan(-5, 40, RedactionImageStrength.Coarse));
        Assert.NotNull(ImageBlocks.Plan(16, 16, RedactionImageStrength.Coarse));
    }

    [Fact]
    public void ThePlanGivesTheHandWorkedGrids()
    {
        foreach (var vector in ImageBlockVectors.Grids)
        {
            var grid = ImageBlocks.Plan(vector.Width, vector.Height, StrengthOf(vector.VeryCoarse));
            Assert.NotNull(grid);
            Assert.Equal(vector.Columns, grid.Columns);
            Assert.Equal(vector.Rows, grid.Rows);
            Assert.Equal(vector.ColumnEdges, grid.ColumnEdges);
            Assert.Equal(vector.RowEdges, grid.RowEdges);
        }
    }

    // ---- The result ----

    [Fact]
    public void PixelateIsConstantPerBlock()
    {
        var width = 50;
        var height = 40;
        var image = ImageBlockVectors.RandomImage(width, height, seed: 7);
        var box = new PixelRect(3, 2, 40, 32);
        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
        var result = ImageBlocks.Expected(image, width, height, box, grid, RedactionImageTreatment.Pixelate);
        Assert.Equal(box.Width * box.Height * 4, result.Length);
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                // The mean of the block, worked out the plain way: the sum of the original pixels, divided by their number and rounded half up.
                var sums = new long[3];
                var count = 0;
                for (var y = grid.RowEdges[row]; y < grid.RowEdges[row + 1]; y++)
                {
                    for (var x = grid.ColumnEdges[column]; x < grid.ColumnEdges[column + 1]; x++)
                    {
                        var at = (((box.Y + y) * width) + box.X + x) * 4;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            sums[channel] += image[at + channel];
                        }

                        count++;
                    }
                }

                for (var y = grid.RowEdges[row]; y < grid.RowEdges[row + 1]; y++)
                {
                    for (var x = grid.ColumnEdges[column]; x < grid.ColumnEdges[column + 1]; x++)
                    {
                        var at = ((y * box.Width) + x) * 4;
                        for (var channel = 0; channel < 3; channel++)
                        {
                            Assert.Equal((byte)((sums[channel] + (count / 2)) / count), result[at + channel]);
                        }

                        Assert.Equal(255, result[at + 3]);
                    }
                }
            }
        }
    }

    [Fact]
    public void PixelateOfTheHandVectorGivesTheBlockMeans()
    {
        var grid = ImageBlocks.Plan(ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, RedactionImageStrength.Coarse)!;
        var result = ImageBlocks.Expected(ImageBlockVectors.BlurImage(), ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, Whole(ImageBlockVectors.BlurSide), grid, RedactionImageTreatment.Pixelate);
        for (var y = 0; y < ImageBlockVectors.BlurSide; y++)
        {
            for (var x = 0; x < ImageBlockVectors.BlurSide; x++)
            {
                var at = ((y * ImageBlockVectors.BlurSide) + x) * 4;
                var mean = (byte)ImageBlockVectors.BlurMeans[y / 8][x / 8];
                Assert.Equal([mean, mean, mean, (byte)255], result[at..(at + 4)]);
            }
        }
    }

    [Fact]
    public void BlurGivesTheHandWorkedValues()
    {
        var grid = ImageBlocks.Plan(ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, RedactionImageStrength.Coarse)!;
        var result = ImageBlocks.Expected(ImageBlockVectors.BlurImage(), ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, Whole(ImageBlockVectors.BlurSide), grid, RedactionImageTreatment.Blur);
        foreach (var sample in ImageBlockVectors.BlurSamples)
        {
            var at = ((sample.Y * ImageBlockVectors.BlurSide) + sample.X) * 4;
            Assert.True(
                result[at] == sample.Value && result[at + 1] == sample.Value && result[at + 2] == sample.Value && result[at + 3] == 255,
                $"Pixel ({sample.X}, {sample.Y}) is {result[at]},{result[at + 1]},{result[at + 2]},{result[at + 3]}, expected {sample.Value}.");
        }
    }

    [Fact]
    public void BlurGivesTheHandWorkedValuesOnAnUnequalGrid()
    {
        // 3 columns of 8, 9 and 9 pixels and 2 rows of 8 and 9 pixels: the centres of the blocks are not evenly spaced.
        var grid = ImageBlocks.Plan(ImageBlockVectors.UnequalWidth, ImageBlockVectors.UnequalHeight, RedactionImageStrength.Coarse)!;
        Assert.Equal([0, 8, 17, 26], grid.ColumnEdges);
        Assert.Equal([0, 8, 17], grid.RowEdges);
        var box = new PixelRect(0, 0, ImageBlockVectors.UnequalWidth, ImageBlockVectors.UnequalHeight);
        var result = ImageBlocks.Expected(ImageBlockVectors.UnequalImage(), ImageBlockVectors.UnequalWidth, ImageBlockVectors.UnequalHeight, box, grid, RedactionImageTreatment.Blur);
        foreach (var sample in ImageBlockVectors.UnequalSamples)
        {
            var at = ((sample.Y * ImageBlockVectors.UnequalWidth) + sample.X) * 4;
            Assert.True(
                result[at] == sample.Value && result[at + 1] == sample.Value && result[at + 2] == sample.Value && result[at + 3] == 255,
                $"Pixel ({sample.X}, {sample.Y}) is {result[at]},{result[at + 1]},{result[at + 2]},{result[at + 3]}, expected {sample.Value}.");
        }
    }

    [Fact]
    public void BlurDependsOnlyOnBlockMeans()
    {
        var one = ImageBlockVectors.BlurImage();
        var other = ImageBlockVectors.BlurImageOtherPixels();
        Assert.NotEqual(one, other);
        var grid = ImageBlocks.Plan(ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, RedactionImageStrength.Coarse)!;
        foreach (var treatment in new[] { RedactionImageTreatment.Blur, RedactionImageTreatment.Pixelate })
        {
            var first = ImageBlocks.Expected(one, ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, Whole(ImageBlockVectors.BlurSide), grid, treatment);
            var second = ImageBlocks.Expected(other, ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, Whole(ImageBlockVectors.BlurSide), grid, treatment);
            Assert.Equal(first, second);
        }

        // A pixel that moves the mean of its block (by 1 of 64 pixels) does change the result.
        var moved = (byte[])one.Clone();
        moved[(((3 * ImageBlockVectors.BlurSide) + 3) * 4) + 1] = (byte)(moved[(((3 * ImageBlockVectors.BlurSide) + 3) * 4) + 1] + 64);
        Assert.NotEqual(
            ImageBlocks.Expected(one, ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, Whole(ImageBlockVectors.BlurSide), grid, RedactionImageTreatment.Blur),
            ImageBlocks.Expected(moved, ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, Whole(ImageBlockVectors.BlurSide), grid, RedactionImageTreatment.Blur));
    }

    [Fact]
    public void AlphaCarriesNoOriginalDetail()
    {
        var noisy = ImageBlockVectors.RandomImage(40, 40, seed: 3);
        var opaque = (byte[])noisy.Clone();
        for (var i = 3; i < opaque.Length; i += 4)
        {
            opaque[i] = 255;
        }

        var grid = ImageBlocks.Plan(40, 40, RedactionImageStrength.Coarse)!;
        foreach (var treatment in new[] { RedactionImageTreatment.Blur, RedactionImageTreatment.Pixelate })
        {
            var result = ImageBlocks.Expected(noisy, 40, 40, Whole(40), grid, treatment);
            for (var i = 3; i < result.Length; i += 4)
            {
                Assert.Equal(255, result[i]);
            }

            // The colour does not look at the alpha either: an image with the same colours and another alpha gives the same bytes.
            Assert.Equal(ImageBlocks.Expected(opaque, 40, 40, Whole(40), grid, treatment), result);
        }
    }

    [Fact]
    public void ExpectedRefusesWhatDoesNotFit()
    {
        var image = new byte[20 * 20 * 4];
        var grid = ImageBlocks.Plan(16, 16, RedactionImageStrength.Coarse)!;
        Assert.Throws<ArgumentException>(() => ImageBlocks.Expected(new byte[10], 20, 20, new PixelRect(0, 0, 16, 16), grid, RedactionImageTreatment.Pixelate));
        Assert.Throws<ArgumentException>(() => ImageBlocks.Expected(image, 20, 20, new PixelRect(8, 8, 16, 16), grid, RedactionImageTreatment.Pixelate));
        Assert.Throws<ArgumentException>(() => ImageBlocks.Expected(image, 20, 20, new PixelRect(0, 0, 18, 16), grid, RedactionImageTreatment.Pixelate));
        Assert.Throws<ArgumentOutOfRangeException>(() => ImageBlocks.Expected(image, 20, 20, new PixelRect(0, 0, 16, 16), grid, RedactionImageTreatment.Solid));
    }

    // ---- The second implementation agrees ----

    [Fact]
    public void ThePdfiumConstantsAreTheLimitsOfCore()
    {
        Assert.Equal(RedactionLimits.MinBlockPixels, PdfiumImageBlocks.MinBlockPixels);
        Assert.Equal(RedactionLimits.CoarseBlocksPerAxis, PdfiumImageBlocks.CoarseBlocksPerAxis);
        Assert.Equal(RedactionLimits.VeryCoarseBlocksPerAxis, PdfiumImageBlocks.VeryCoarseBlocksPerAxis);
    }

    [Fact]
    public void ThePdfiumPlanAgreesOnEveryGridVector()
    {
        foreach (var vector in ImageBlockVectors.Grids)
        {
            Assert.True(PdfiumImageBlocks.TryPlan(vector.Width, vector.Height, vector.VeryCoarse, out var columns, out var rows));
            Assert.Equal(vector.ColumnEdges, columns);
            Assert.Equal(vector.RowEdges, rows);
        }

        foreach (var (width, height) in ImageBlockVectors.TooSmall)
        {
            Assert.False(PdfiumImageBlocks.TryPlan(width, height, veryCoarse: false, out _, out _));
            Assert.False(PdfiumImageBlocks.TryPlan(width, height, veryCoarse: true, out _, out _));
        }
    }

    [Fact]
    public void ThePdfiumPlanAgreesWithCoreOnEverySize()
    {
        foreach (var veryCoarse in new[] { false, true })
        {
            for (var width = 0; width <= 140; width++)
            {
                for (var height = 0; height <= 140; height += 3)
                {
                    var core = ImageBlocks.Plan(width, height, StrengthOf(veryCoarse));
                    var found = PdfiumImageBlocks.TryPlan(width, height, veryCoarse, out var columns, out var rows);
                    Assert.Equal(core is not null, found);
                    if (core is not null)
                    {
                        Assert.Equal(core.ColumnEdges, columns);
                        Assert.Equal(core.RowEdges, rows);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePdfiumApplyAgreesOnTheHandVectors(bool blur)
    {
        Assert.True(PdfiumImageBlocks.TryPlan(ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide, veryCoarse: false, out var columns, out var rows));
        var pixels = ImageBlockVectors.BlurImage();
        PdfiumImageBlocks.Apply(pixels, ImageBlockVectors.BlurSide, new PixelBox(0, 0, ImageBlockVectors.BlurSide, ImageBlockVectors.BlurSide), columns, rows, blur);
        if (blur)
        {
            foreach (var sample in ImageBlockVectors.BlurSamples)
            {
                var at = ((sample.Y * ImageBlockVectors.BlurSide) + sample.X) * 4;
                Assert.True(
                    pixels[at] == sample.Value && pixels[at + 1] == sample.Value && pixels[at + 2] == sample.Value && pixels[at + 3] == 255,
                    $"Pixel ({sample.X}, {sample.Y}) is {pixels[at]},{pixels[at + 1]},{pixels[at + 2]},{pixels[at + 3]}, expected {sample.Value}.");
            }
        }
        else
        {
            for (var y = 0; y < ImageBlockVectors.BlurSide; y++)
            {
                for (var x = 0; x < ImageBlockVectors.BlurSide; x++)
                {
                    var at = ((y * ImageBlockVectors.BlurSide) + x) * 4;
                    var mean = (byte)ImageBlockVectors.BlurMeans[y / 8][x / 8];
                    Assert.Equal([mean, mean, mean, (byte)255], pixels[at..(at + 4)]);
                }
            }
        }
    }

    [Fact]
    public void ThePdfiumApplyAgreesOnTheUnequalGrid()
    {
        Assert.True(PdfiumImageBlocks.TryPlan(ImageBlockVectors.UnequalWidth, ImageBlockVectors.UnequalHeight, veryCoarse: false, out var columns, out var rows));
        var pixels = ImageBlockVectors.UnequalImage();
        PdfiumImageBlocks.Apply(pixels, ImageBlockVectors.UnequalWidth, new PixelBox(0, 0, ImageBlockVectors.UnequalWidth, ImageBlockVectors.UnequalHeight), columns, rows, blur: true);
        foreach (var sample in ImageBlockVectors.UnequalSamples)
        {
            var at = ((sample.Y * ImageBlockVectors.UnequalWidth) + sample.X) * 4;
            Assert.True(
                pixels[at] == sample.Value && pixels[at + 1] == sample.Value && pixels[at + 2] == sample.Value && pixels[at + 3] == 255,
                $"Pixel ({sample.X}, {sample.Y}) is {pixels[at]},{pixels[at + 1]},{pixels[at + 2]},{pixels[at + 3]}, expected {sample.Value}.");
        }
    }

    [Fact]
    public void ThePdfiumApplyAgreesWithCoreOnGeneratedImagesAndLeavesTheRestAlone()
    {
        const int Width = 130;
        const int Height = 90;
        var seed = 100;
        foreach (var (x, y, w, h) in ImageBlockVectors.CrossBoxes)
        {
            foreach (var veryCoarse in new[] { false, true })
            {
                foreach (var treatment in new[] { RedactionImageTreatment.Pixelate, RedactionImageTreatment.Blur })
                {
                    var original = ImageBlockVectors.RandomImage(Width, Height, seed++);
                    var grid = ImageBlocks.Plan(w, h, StrengthOf(veryCoarse))!;
                    Assert.NotNull(grid);
                    var expected = ImageBlocks.Expected(original, Width, Height, new PixelRect(x, y, w, h), grid, treatment);

                    var written = (byte[])original.Clone();
                    Assert.True(PdfiumImageBlocks.TryPlan(w, h, veryCoarse, out var columns, out var rows));
                    PdfiumImageBlocks.Apply(written, Width, new PixelBox(x, y, x + w, y + h), columns, rows, treatment == RedactionImageTreatment.Blur);
                    for (var row = 0; row < Height; row++)
                    {
                        for (var column = 0; column < Width; column++)
                        {
                            var at = ((row * Width) + column) * 4;
                            var inside = column >= x && column < x + w && row >= y && row < y + h;
                            if (inside)
                            {
                                var inBox = (((row - y) * w) + (column - x)) * 4;
                                Assert.True(
                                    written[at] == expected[inBox] && written[at + 1] == expected[inBox + 1] && written[at + 2] == expected[inBox + 2] && written[at + 3] == expected[inBox + 3],
                                    $"Box ({x},{y},{w},{h}) {treatment} very coarse {veryCoarse}: pixel ({column},{row}) differs between the implementations.");
                            }
                            else
                            {
                                Assert.True(
                                    written[at] == original[at] && written[at + 1] == original[at + 1] && written[at + 2] == original[at + 2] && written[at + 3] == original[at + 3],
                                    $"Pixel ({column},{row}) outside the box changed.");
                            }
                        }
                    }
                }
            }
        }
    }
}
