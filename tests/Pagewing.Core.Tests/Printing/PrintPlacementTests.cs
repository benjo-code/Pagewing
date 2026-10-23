using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Xunit;

namespace Pagewing.Core.Tests.Printing;

public sealed class PrintPlacementTests
{
    private static readonly PdfPageSize A4Portrait = new(595, 842);
    private static readonly PdfPageSize A4Landscape = new(842, 595);
    private static readonly PaperArea PortraitPaper = new(575, 822);

    [Fact]
    public void PortraitOnPortraitFits()
    {
        var placement = PrintPlacement.Plan(A4Portrait, PortraitPaper, PrintScaling.FitToPaper);

        Assert.Equal(0, placement.QuarterTurns);
        Assert.Equal(575d / 595d * 595d, placement.Width, 6);
        Assert.False(placement.Clipped);
    }

    [Fact]
    public void LandscapeOnPortraitRotates()
    {
        var placement = PrintPlacement.Plan(A4Landscape, PortraitPaper, PrintScaling.FitToPaper);

        Assert.Equal(1, placement.QuarterTurns);
        Assert.True(placement.Height > placement.Width);
    }

    [Fact]
    public void PortraitOnLandscapePaperRotates()
    {
        var placement = PrintPlacement.Plan(A4Portrait, new PaperArea(822, 575), PrintScaling.FitToPaper);

        Assert.Equal(1, placement.QuarterTurns);
        Assert.True(placement.Width > placement.Height);
    }

    [Fact]
    public void FitScalesUpASmallPage()
    {
        var placement = PrintPlacement.Plan(new PdfPageSize(100, 200), new PaperArea(400, 400), PrintScaling.FitToPaper);

        Assert.Equal(200, placement.Width, 6);
        Assert.Equal(400, placement.Height, 6);
    }

    [Fact]
    public void FitScalesDownALargePage()
    {
        var placement = PrintPlacement.Plan(new PdfPageSize(2000, 1000), new PaperArea(500, 500), PrintScaling.FitToPaper);

        Assert.Equal(500, placement.Width, 6);
        Assert.Equal(250, placement.Height, 6);
        Assert.False(placement.Clipped);
    }

    [Fact]
    public void FitCentersInThePrintableArea()
    {
        var placement = PrintPlacement.Plan(new PdfPageSize(100, 200), new PaperArea(400, 300), PrintScaling.FitToPaper, autoRotate: false);

        Assert.Equal(150, placement.Width, 6);
        Assert.Equal(300, placement.Height, 6);
        Assert.Equal(125, placement.X, 6);
        Assert.Equal(0, placement.Y, 6);
    }

    [Fact]
    public void ActualSizeCentersWithoutScaling()
    {
        var placement = PrintPlacement.Plan(new PdfPageSize(100, 200), new PaperArea(400, 400), PrintScaling.ActualSize);

        Assert.Equal(100, placement.Width, 6);
        Assert.Equal(200, placement.Height, 6);
        Assert.Equal(150, placement.X, 6);
        Assert.Equal(100, placement.Y, 6);
        Assert.False(placement.Clipped);
    }

    [Fact]
    public void ActualSizeClipsASmallPaperSymmetrically()
    {
        var placement = PrintPlacement.Plan(new PdfPageSize(300, 400), new PaperArea(200, 300), PrintScaling.ActualSize);

        Assert.True(placement.Clipped);
        Assert.Equal(-50, placement.X, 6);
        Assert.Equal(-50, placement.Y, 6);
        Assert.Equal(300, placement.Width, 6);
    }

    [Fact]
    public void SquareAreaDoesNotRotate()
    {
        Assert.Equal(0, PrintPlacement.Plan(A4Landscape, new PaperArea(500, 500), PrintScaling.FitToPaper).QuarterTurns);
        Assert.Equal(0, PrintPlacement.Plan(new PdfPageSize(300, 300), PortraitPaper, PrintScaling.FitToPaper).QuarterTurns);
    }

    [Fact]
    public void AutoRotateCanBeSwitchedOff()
    {
        var placement = PrintPlacement.Plan(A4Landscape, PortraitPaper, PrintScaling.FitToPaper, autoRotate: false);

        Assert.Equal(0, placement.QuarterTurns);
        Assert.True(placement.Width > placement.Height);
    }

    [Fact]
    public void ToDeviceConvertsPointsToPixels()
    {
        var placement = new PrintPlacement(36, 72, 144, 72, 1, false);

        var device = placement.ToDevice(600, 300);

        Assert.Equal(new DevicePlacement(300, 300, 1200, 300, 1), device);
    }

    [Fact]
    public void ToDeviceNeverGivesAnEmptyArea()
    {
        var device = new PrintPlacement(0, 0, 0.01, 0.01, 0, false).ToDevice(72, 72);

        Assert.Equal(1, device.Width);
        Assert.Equal(1, device.Height);
    }

    [Theory]
    [InlineData(0, 300)]
    [InlineData(300, -1)]
    [InlineData(double.NaN, 300)]
    public void ToDeviceRejectsABadResolution(double dpiX, double dpiY)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PrintPlacement(0, 0, 10, 10, 0, false).ToDevice(dpiX, dpiY));
    }

    [Theory]
    [InlineData(0, 100, 100, 100)]
    [InlineData(100, 0, 100, 100)]
    [InlineData(100, 100, 0, 100)]
    [InlineData(100, 100, 100, 0)]
    [InlineData(double.NaN, 100, 100, 100)]
    [InlineData(100, 100, double.PositiveInfinity, 100)]
    public void ZeroSizedPageOrPaperThrows(double pageWidth, double pageHeight, double paperWidth, double paperHeight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PrintPlacement.Plan(new PdfPageSize(pageWidth, pageHeight), new PaperArea(paperWidth, paperHeight), PrintScaling.FitToPaper));
    }
}
