using Pagewing.Core.Annotations;
using Pagewing.Core.Signatures;
using Xunit;

namespace Pagewing.Core.Tests.Signatures;

public sealed class SignaturePlacementTests
{
    [Theory]
    [InlineData(600, 800, 160)]
    [InlineData(480, 800, 160)]
    [InlineData(300, 200, 100)]
    [InlineData(100, 300, 100d / 3)]
    public void WidthIsOneHundredSixtyOrAThirdOfThePage(double pageWidth, double pageHeight, double expected)
    {
        var box = SignaturePlacement.Fit(2.5, new DisplayPoint(pageWidth / 2, pageHeight / 2), pageWidth, pageHeight);

        Assert.Equal(expected, box.Width, 6);
    }

    [Theory]
    [InlineData(2.5)]
    [InlineData(1)]
    [InlineData(0.4)]
    [InlineData(12)]
    public void KeepsAspectRatio(double aspect)
    {
        var box = SignaturePlacement.Fit(aspect, new DisplayPoint(200, 300), 600, 800);

        Assert.Equal(aspect, box.Width / box.Height, 6);
        Assert.Equal(200, (box.Left + box.Right) / 2, 6);
        Assert.Equal(300, (box.Top + box.Bottom) / 2, 6);
    }

    [Theory]
    [InlineData(40, 30, 2.5)]
    [InlineData(40, 30, 0.2)]
    [InlineData(5, 5, 1)]
    [InlineData(600, 20, 2.5)]
    public void StaysOnTheTinyPage(double pageWidth, double pageHeight, double aspect)
    {
        var box = SignaturePlacement.Fit(aspect, new DisplayPoint(pageWidth / 2, pageHeight / 2), pageWidth, pageHeight);

        Assert.True(box.Left >= -1e-9 && box.Top >= -1e-9 && box.Right <= pageWidth + 1e-9 && box.Bottom <= pageHeight + 1e-9, box.ToString());
        Assert.Equal(aspect, box.Width / box.Height, 6);
        Assert.True(box.Width > 0 && box.Height > 0);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(600, 800)]
    [InlineData(600, 0)]
    [InlineData(-50, 900)]
    [InlineData(300, 10_000)]
    public void StaysOnThePageAtTheEdge(double x, double y)
    {
        var box = SignaturePlacement.Fit(2.5, new DisplayPoint(x, y), 600, 800);

        Assert.True(box.Left >= 0 && box.Top >= 0 && box.Right <= 600 && box.Bottom <= 800, box.ToString());
        Assert.Equal(160, box.Width, 6);
        Assert.Equal(64, box.Height, 6);
    }

    [Fact]
    public void RefusesAnAspectThatIsNotAPositiveNumber()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SignaturePlacement.Fit(0, new DisplayPoint(1, 1), 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SignaturePlacement.Fit(double.NaN, new DisplayPoint(1, 1), 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => SignaturePlacement.Fit(2, new DisplayPoint(1, 1), 0, 100));
    }

    [Fact]
    public void AnUnusableCentreFallsBackToThePageCentre()
    {
        var box = SignaturePlacement.Fit(2.5, new DisplayPoint(double.NaN, double.PositiveInfinity), 600, 800);

        Assert.Equal(300, (box.Left + box.Right) / 2, 6);
        Assert.Equal(400, (box.Top + box.Bottom) / 2, 6);
    }
}
