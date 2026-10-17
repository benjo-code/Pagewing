using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class ThumbnailPlannerTests
{
    private static readonly PdfPageSize A4 = new(595.28, 841.89);

    [Fact]
    public void PlannerCreatesOneBackgroundWholePageRequestPerPage()
    {
        var sizes = new[] { A4, A4, A4 };

        var plan = ThumbnailPlanner.Plan(sizes, [0, 1, 2], 1.0);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, request =>
        {
            Assert.True(request.WholePage);
            Assert.True(request.Background);
            Assert.Equal(TileKey.PreviewScaleKey, request.Key.ScaleKey);
            Assert.Equal(0, request.Key.Column);
            Assert.Equal(0, request.Key.Row);
            Assert.Equal(0, request.Rect.X);
            Assert.Equal(0, request.Rect.Y);
        });
        Assert.Equal([0, 1, 2], plan.Select(request => request.Key.Page));
    }

    [Fact]
    public void ScaleGivesTheThumbnailWidthInDevicePixels()
    {
        var plan = ThumbnailPlanner.Plan([A4], [0], 1.5);

        var request = Assert.Single(plan);
        Assert.Equal(ThumbnailPlanner.Width * 1.5 / A4.Width, request.Scale, 9);
        Assert.Equal(204, request.Rect.Width);
        Assert.Equal(A4.ToPixels(request.Scale), (request.Rect.Width, request.Rect.Height));
    }

    [Theory]
    [InlineData(300.0, 1.5, 204)]
    [InlineData(300.0, 1.25, 170)]
    [InlineData(300.0, 1.75, 238)]
    [InlineData(200.0, 1.5, 204)]
    [InlineData(612.0, 1.5, 204)]
    [InlineData(595.28, 1.25, 170)]
    [InlineData(100.0, 1.1, 150)]
    public void ThumbnailIsExactlyTheWidthInDevicePixelsAtFractionalScalings(double pageWidth, double scaling, int pixels)
    {
        // Width × scale can come out a rounding error above the target (300 × 0.68 = 204.00000000000003), and the pixel
        // size rounds up: the thumbnail would be a pixel wider than its 136 DIP on the screen.
        var size = new PdfPageSize(pageWidth, 400);

        var request = Assert.Single(ThumbnailPlanner.Plan([size], [0], scaling));

        Assert.Equal(pixels, request.Rect.Width);
        Assert.Equal(size.ToPixels(request.Scale), (request.Rect.Width, request.Rect.Height));
    }

    [Fact]
    public void PlannerOrdersByDistanceToTheFirstPage()
    {
        var sizes = Enumerable.Repeat(A4, 10).ToArray();

        var plan = ThumbnailPlanner.Plan(sizes, [5, 6, 7, 4, 3, 5], 1.0);

        // Repeated pages are planned once; the pages nearest to the first one come first, the lower one on a tie.
        Assert.Equal([5, 4, 6, 3, 7], plan.Select(request => request.Key.Page));
    }

    [Fact]
    public void PlannerSkipsPagesBeyondTheKnownSizes()
    {
        var plan = ThumbnailPlanner.Plan([A4, A4], [-1, 0, 1, 2, 500], 1.0);

        Assert.Equal([0, 1], plan.Select(request => request.Key.Page));
    }

    [Fact]
    public void PlannerCapsTheRequestCount()
    {
        var sizes = Enumerable.Repeat(A4, 200).ToArray();

        var plan = ThumbnailPlanner.Plan(sizes, Enumerable.Range(0, 200), 1.0);

        Assert.Equal(ThumbnailPlanner.MaxRequests, plan.Count);
        Assert.Equal(Enumerable.Range(0, ThumbnailPlanner.MaxRequests), plan.Select(request => request.Key.Page));
    }

    [Fact]
    public void PlannerSkipsInvalidSizes()
    {
        var sizes = new[]
        {
            new PdfPageSize(0, 100),
            new PdfPageSize(100, -1),
            new PdfPageSize(double.NaN, 100),
            new PdfPageSize(100, double.PositiveInfinity),
            A4,
        };

        var plan = ThumbnailPlanner.Plan(sizes, [0, 1, 2, 3, 4], 1.0);

        Assert.Equal(4, Assert.Single(plan).Key.Page);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PlannerRejectsInvalidScaling(double scaling)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ThumbnailPlanner.Plan([A4], [0], scaling));
    }

    [Fact]
    public void HeightKeepsTheAspectRatio()
    {
        Assert.Equal(136 * 841.89 / 595.28, ThumbnailPlanner.HeightFor(A4), 9);
        Assert.Equal(136.0, ThumbnailPlanner.HeightFor(new PdfPageSize(100, 100)), 9);
        Assert.Equal(68.0, ThumbnailPlanner.HeightFor(new PdfPageSize(200, 100)), 9);
    }
}
