using Pagewing.Core.Redaction;
using Pagewing.Pdfium;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

public sealed class RasterGeometryTests
{
    [Fact]
    public void A4At200DpiFits()
    {
        var size = RasterGeometry.Resolve(595.28, 841.89, 200);

        Assert.Equal(200, size.Dpi);
        Assert.Equal(1654, size.WidthPx);
        Assert.Equal(2339, size.HeightPx);
        Assert.False(size.Lowered);
    }

    [Fact]
    public void HugePageLowersTheDpiAndSaysSo()
    {
        // 3,600 pt = 50 inch a side: 300 dpi would be 225 million pixels.
        var size = RasterGeometry.Resolve(3_600, 3_600, 300);

        Assert.True(size.Lowered);
        Assert.True(size.Dpi is >= RedactionLimits.MinRasterDpi and < 300);
        Assert.True((long)size.WidthPx * size.HeightPx <= RedactionLimits.MaxRasterPixels);
    }

    [Fact]
    public void ThePixelBudgetHoldsAtEveryLoweredResolution()
    {
        // The pixel count rounds each side up, so the plain formula for the resolution must not be the last word.
        for (var side = 1_000; side <= 3_600; side += 37)
        {
            var size = RasterGeometry.Resolve(side + 0.4, side + 0.9, 300);
            Assert.True((long)size.WidthPx * size.HeightPx <= RedactionLimits.MaxRasterPixels, $"side {side}");
            Assert.True(size.Dpi >= RedactionLimits.MinRasterDpi);
        }
    }

    [Fact]
    public void PageThatNeedsLessThan72DpiIsRefused()
    {
        var error = Assert.Throws<RedactionException>(() => RasterGeometry.Resolve(14_400, 14_400, 300));

        Assert.Equal(RedactionError.LimitExceeded, error.Error);
    }

    [Fact]
    public void DpiIsClamped()
    {
        Assert.Equal(150, RasterGeometry.Resolve(595, 842, 150).Dpi);
        Assert.Equal(RedactionLimits.MaxRasterDpi, RasterGeometry.Resolve(595, 842, 1_000).Dpi);
        Assert.Equal(RedactionLimits.MinRasterDpi, RasterGeometry.Resolve(595, 842, 10).Dpi);
        Assert.False(RasterGeometry.Resolve(595, 842, 10).Lowered);
    }

    [Theory]
    [InlineData(0, 842)]
    [InlineData(595, -1)]
    [InlineData(double.NaN, 842)]
    [InlineData(595, double.PositiveInfinity)]
    public void ASizeThatIsNotPositiveAndFiniteIsRefused(double width, double height)
    {
        var error = Assert.Throws<RedactionException>(() => RasterGeometry.Resolve(width, height, 200));

        Assert.Equal(RedactionError.InvalidArea, error.Error);
    }

    [Fact]
    public void ThePdfiumSideKnowsTheSamePixelLimit()
    {
        Assert.Equal(RedactionLimits.MaxRasterPixels, PdfiumRedactionRaster.MaxPixels);
    }
}
