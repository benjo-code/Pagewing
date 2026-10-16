using Xunit;

namespace Pagewing.Render.Tests;

public sealed class ImageComparerTests
{
    private const int Side = 100; // 10 000 pixels: 0.002 of them are exactly 20.

    private static readonly RenderTolerance Tolerance = new(MaxChannelDelta: 16, MaxDifferingFraction: 0.002);

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
