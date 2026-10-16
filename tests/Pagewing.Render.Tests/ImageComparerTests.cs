using Xunit;

namespace Pagewing.Render.Tests;

public sealed class ImageComparerTests
{
    private const int Side = 100; // 10 000 pixels: 0.002 of them are exactly 20.

    private static readonly RenderTolerance Tolerance = new(MaxChannelDelta: 16, MaxDifferingFraction: 0.002);

    private const int BlockSide = 80; // 100 blocks of 8 x 8: MaxDifferingFraction 0.01 is exactly 1 block.

    private static readonly BlockTolerance Blocks = new(BlockSize: 8, MaxBlockDelta: 40, MaxDifferingFraction: 0.01, HardBlockDelta: 89);

    private static readonly (byte R, byte G, byte B) Red = (255, 0, 0);

    [Fact]
    public void IdenticalImagesPass()
    {
        var image = Pattern(Side, Side);

        var result = ImageComparer.Compare(image, Copy(image), Tolerance);

        Assert.True(result.SizeMatches);
        Assert.Equal(0, result.DifferingPixels);
        Assert.Equal(0, result.MaxDelta);
        Assert.Equal(0, result.Fraction);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData(Side + 1, Side)]
    [InlineData(Side, Side - 1)]
    public void DifferentSizeFails(int width, int height)
    {
        var result = ImageComparer.Compare(Pattern(Side, Side), Pattern(width, height), Tolerance);

        Assert.False(result.SizeMatches);
        Assert.False(result.Passed);
    }

    [Fact]
    public void DeltaEqualToTheToleranceOnEveryPixelPasses()
    {
        var expected = Pattern(Side, Side);
        var actual = Offset(expected, Tolerance.MaxChannelDelta);

        var result = ImageComparer.Compare(expected, actual, Tolerance);

        Assert.Equal(0, result.DifferingPixels);
        Assert.Equal(Tolerance.MaxChannelDelta, result.MaxDelta);
        Assert.True(result.Passed);
    }

    [Fact]
    public void DeltaAboveTheToleranceOnEveryPixelFails()
    {
        var expected = Pattern(Side, Side);
        var actual = Offset(expected, Tolerance.MaxChannelDelta + 1);

        var result = ImageComparer.Compare(expected, actual, Tolerance);

        Assert.Equal(Side * Side, result.DifferingPixels);
        Assert.Equal(Tolerance.MaxChannelDelta + 1, result.MaxDelta);
        Assert.Equal(1.0, result.Fraction);
        Assert.False(result.Passed);
    }

    [Fact]
    public void AnyChannelAboveTheToleranceMakesThePixelDiffer()
    {
        var expected = Pattern(Side, Side);
        var actual = Copy(expected);
        actual.Pixels[(57 * 3) + 2] ^= 0xFF; // blue channel of one pixel only

        var result = ImageComparer.Compare(expected, actual, Tolerance);

        Assert.Equal(1, result.DifferingPixels);
    }

    [Theory]
    [InlineData(19, true)]
    [InlineData(20, true)] // exactly MaxDifferingFraction
    [InlineData(21, false)]
    public void DifferingFractionAtTheLimitPassesAndAboveFails(int differing, bool passes)
    {
        var expected = Pattern(Side, Side);
        var actual = Copy(expected);
        for (var i = 0; i < differing; i++)
        {
            actual.Pixels[(i * 37) * 3] ^= 0x80; // spread over the image, delta 128 in red
        }

        var result = ImageComparer.Compare(expected, actual, Tolerance);

        Assert.Equal(differing, result.DifferingPixels);
        Assert.Equal(differing / (double)(Side * Side), result.Fraction);
        Assert.Equal(128, result.MaxDelta);
        Assert.Equal(passes, result.Passed);
    }

    [Fact]
    public void DiffPngMarksDifferingPixelsRed()
    {
        var expected = Pattern(Side, Side);
        var actual = Copy(expected);
        actual.Pixels[((10 * Side) + 20) * 3] ^= 0x80; // differs
        actual.Pixels[((30 * Side) + 40) * 3] ^= 0x08; // within the tolerance

        var diff = Rgb.Decode(ImageComparer.CreateDiffPng(expected, actual, Tolerance));

        Assert.Equal((Side, Side), (diff.Width, diff.Height));
        Assert.Equal(Red, diff.Pixel(20, 10));
        var redPixels = 0;
        for (var y = 0; y < Side; y++)
        {
            for (var x = 0; x < Side; x++)
            {
                if (diff.Pixel(x, y) == Red)
                {
                    redPixels++;
                }
            }
        }

        Assert.Equal(1, redPixels);
        Assert.NotEqual(Red, diff.Pixel(40, 30));
    }

    [Fact]
    public void DiffPngOfDifferentSizesMarksTheMissingAreaRed()
    {
        var diff = Rgb.Decode(ImageComparer.CreateDiffPng(Pattern(Side, Side), Pattern(Side + 5, Side - 5), Tolerance));

        Assert.Equal((Side + 5, Side), (diff.Width, diff.Height));
        Assert.Equal(Red, diff.Pixel(Side + 2, 0)); // only in the actual image
        Assert.Equal(Red, diff.Pixel(0, Side - 2)); // only in the expected image
        Assert.NotEqual(Red, diff.Pixel(0, 0));
    }

    [Fact]
    public void BlockComparisonOfIdenticalImagesPasses()
    {
        var image = Pattern(BlockSide, BlockSide);

        var result = ImageComparer.Compare(image, Copy(image), Blocks);

        Assert.True(result.SizeMatches);
        Assert.Equal(100, result.Blocks);
        Assert.Equal(0, result.DifferingBlocks);
        Assert.Equal(0, result.MaxBlockDelta);
        Assert.True(result.Passed);
    }

    [Fact]
    public void BlockComparisonOfDifferentSizesFails()
    {
        var result = ImageComparer.Compare(Pattern(BlockSide, BlockSide), Pattern(BlockSide, BlockSide + 1), Blocks);

        Assert.False(result.SizeMatches);
        Assert.False(result.Passed);
    }

    [Fact]
    public void BlockComparisonAveragesIsolatedPixelChangesOut()
    {
        var expected = Flat(BlockSide, BlockSide, 128);
        var actual = Copy(expected);
        for (var y = 0; y < BlockSide; y += 8)
        {
            for (var x = 0; x < BlockSide; x += 8)
            {
                actual.Pixels[((y * BlockSide) + x) * 3] = 255; // one pixel per block, like a different glyph edge
            }
        }

        Assert.False(ImageComparer.Compare(expected, actual, Tolerance).Passed);
        var result = ImageComparer.Compare(expected, actual, Blocks);

        Assert.Equal(127 / 64.0, result.MaxBlockDelta);
        Assert.True(result.Passed);
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(41, false)]
    public void BlockMeanDeltaAtTheToleranceOnEveryBlockPassesAndAboveFails(int delta, bool passes)
    {
        var expected = Flat(BlockSide, BlockSide, 128);
        var actual = Copy(expected);
        for (var i = 1; i < actual.Pixels.Length; i += 3)
        {
            actual.Pixels[i] = (byte)(128 - delta); // green only
        }

        var result = ImageComparer.Compare(expected, actual, Blocks);

        Assert.Equal(delta, result.MaxBlockDelta);
        Assert.Equal(passes ? 0 : 100, result.DifferingBlocks);
        Assert.Equal(passes, result.Passed);
    }

    [Theory]
    [InlineData(1, true)] // exactly MaxDifferingFraction (1 of 100 blocks)
    [InlineData(2, false)]
    public void DifferingBlockFractionAtTheLimitPassesAndAboveFails(int blocks, bool passes)
    {
        var expected = Flat(BlockSide, BlockSide, 128);
        var actual = Copy(expected);
        for (var b = 0; b < blocks; b++)
        {
            FillBlue(actual, x0: b * 16, y0: 24, size: 8, value: 188); // delta 60
        }

        var result = ImageComparer.Compare(expected, actual, Blocks);

        Assert.Equal(blocks, result.DifferingBlocks);
        Assert.Equal(blocks / 100.0, result.Fraction);
        Assert.Equal(passes, result.Passed);
    }

    [Theory]
    [InlineData(89, true)]
    [InlineData(90, false)]
    public void ASingleBlockAboveTheHardLimitFails(int delta, bool passes)
    {
        var expected = Flat(BlockSide, BlockSide, 128);
        var actual = Copy(expected);
        FillBlue(actual, x0: 40, y0: 40, size: 8, value: (byte)(128 + delta));

        var result = ImageComparer.Compare(expected, actual, Blocks);

        Assert.Equal(1, result.DifferingBlocks); // within MaxDifferingFraction
        Assert.Equal(passes, result.Passed);
    }

    [Fact]
    public void EdgeBlocksAreAveragedOverTheirOwnPixels()
    {
        var expected = Flat(20, 20, 128); // blocks of 8, 8 and 4 pixels per side
        var actual = Copy(expected);
        FillBlue(actual, x0: 16, y0: 16, size: 4, value: 178);

        var result = ImageComparer.Compare(expected, actual, Blocks);

        Assert.Equal(9, result.Blocks);
        Assert.Equal(1, result.DifferingBlocks);
        Assert.Equal(50, result.MaxBlockDelta);
    }

    [Fact]
    public void BlockDiffPngMarksDifferingBlocksRed()
    {
        var expected = Flat(BlockSide, BlockSide, 128);
        var actual = Copy(expected);
        FillBlue(actual, x0: 16, y0: 8, size: 8, value: 200);
        actual.Pixels[((50 * BlockSide) + 50) * 3] = 255; // averaged out

        var diff = Rgb.Decode(ImageComparer.CreateDiffPng(expected, actual, Blocks));

        Assert.Equal((BlockSide, BlockSide), (diff.Width, diff.Height));
        var red = new List<(int X, int Y)>();
        for (var y = 0; y < BlockSide; y++)
        {
            for (var x = 0; x < BlockSide; x++)
            {
                if (diff.Pixel(x, y) == Red)
                {
                    red.Add((x, y));
                }
            }
        }

        Assert.Equal(64, red.Count);
        Assert.All(red, p => Assert.True(p.X is >= 16 and < 24 && p.Y is >= 8 and < 16, $"{p} is red"));
    }

    [Fact]
    public void DefaultStrictToleranceDetectsAOnePixelShiftOfShapes()
    {
        var page = Checkerboard(600, 400, square: 20);

        var result = ImageComparer.Compare(page, ShiftRight(page, 1), RenderTolerance.Default);

        Assert.False(result.Passed);
    }

    [Fact]
    public void DefaultBlockToleranceDetectsAnEightPixelShiftOfText()
    {
        var page = TextLikePage();

        var result = ImageComparer.Compare(page, ShiftRight(page, 8), BlockTolerance.Default);

        Assert.False(result.Passed);
    }

    [Fact]
    public void DefaultBlockToleranceDetectsABlankedTextLine()
    {
        var page = TextLikePage();
        var blanked = Copy(page);
        Array.Fill(blanked.Pixels, (byte)0xFF, 125 * 600 * 3, 46 * 600 * 3);

        var result = ImageComparer.Compare(page, blanked, BlockTolerance.Default);

        Assert.False(result.Passed);
    }

    [Fact]
    public void DefaultBlockToleranceDetectsAColourChangeInAFilledRectangle()
    {
        var page = TextLikePage();
        var changed = Copy(page);
        for (var y = 260; y < 300; y++)
        {
            for (var x = 140; x < 180; x++)
            {
                changed.Pixels[(((y * 600) + x) * 3) + 1] -= 60; // a 40 x 40 patch, worst block alignment (offset 4)
            }
        }

        var result = ImageComparer.Compare(page, changed, BlockTolerance.Default);

        Assert.Equal(16, result.DifferingBlocks); // the partial blocks at the edges average to 30
        Assert.False(result.Passed);
    }

    /// <summary>A colourful image with every channel in 40..215, so offsets of up to ±40 stay in range.</summary>
    private static Rgb Pattern(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                pixels[i] = (byte)(40 + (x * 7 % 176));
                pixels[i + 1] = (byte)(40 + (y * 11 % 176));
                pixels[i + 2] = (byte)(40 + ((x + y) * 5 % 176));
            }
        }

        return new Rgb(width, height, pixels);
    }

    private static Rgb Copy(Rgb image) => image with { Pixels = (byte[])image.Pixels.Clone() };

    private static Rgb Flat(int width, int height, byte value)
    {
        var pixels = new byte[width * height * 3];
        Array.Fill(pixels, value);
        return new Rgb(width, height, pixels);
    }

    private static void FillBlue(Rgb image, int x0, int y0, int size, byte value)
    {
        for (var y = y0; y < y0 + size; y++)
        {
            for (var x = x0; x < x0 + size; x++)
            {
                image.Pixels[(((y * image.Width) + x) * 3) + 2] = value;
            }
        }
    }

    private static Rgb Checkerboard(int width, int height, int square)
    {
        var image = Flat(width, height, 0xFF);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (((x / square) + (y / square)) % 2 == 0)
                {
                    image.Pixels.AsSpan(((y * width) + x) * 3, 3).Fill(0x20);
                }
            }
        }

        return image;
    }

    /// <summary>
    /// A 600 x 400 white page with a line of glyph-like black strokes (2 to 4 px wide, rows 125..170, like
    /// <c>text-basic.pdf</c> page 1) and a filled light-blue rectangle like the Square annotation of
    /// <c>annotations.pdf</c>.
    /// </summary>
    private static Rgb TextLikePage()
    {
        var page = Flat(600, 400, 0xFF);
        for (int x = 40, k = 0; x < 385; k++)
        {
            // Irregular widths and gaps, so a shift is never close to a multiple of a period.
            var width = 2 + (k * 7 % 3);
            for (var y = 125; y < 171; y++)
            {
                page.Pixels.AsSpan(((y * 600) + x) * 3, width * 3).Clear();
            }

            x += width + 3 + (k * 5 % 11);
        }

        for (var y = 226; y < 334; y++)
        {
            for (var x = 66; x < 254; x++)
            {
                var i = ((y * 600) + x) * 3;
                (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]) = (230, 230, 255);
            }
        }

        return page;
    }

    /// <summary>Moves the image <paramref name="dx"/> pixels to the right; the uncovered columns are white.</summary>
    private static Rgb ShiftRight(Rgb image, int dx)
    {
        var shifted = new byte[image.Pixels.Length];
        Array.Fill(shifted, (byte)0xFF);
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * image.Width * 3;
            Array.Copy(image.Pixels, row, shifted, row + (dx * 3), (image.Width - dx) * 3);
        }

        return image with { Pixels = shifted };
    }

    /// <summary>Adds <paramref name="delta"/> to every channel, alternating the sign per channel.</summary>
    private static Rgb Offset(Rgb image, int delta)
    {
        var pixels = (byte[])image.Pixels.Clone();
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(pixels[i] + (i % 2 == 0 ? delta : -delta));
        }

        return image with { Pixels = pixels };
    }
}
