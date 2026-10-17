using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class TilePlannerTests
{
    private static readonly PdfPageSize A4 = new(595.28, 841.89);

    [Fact]
    public void ScaleKeyRoundTrips()
    {
        var key = TileKey.ToScaleKey(1.3333);

        Assert.Equal(1333, key);
        Assert.Equal(1.333, new TileKey(0, key, 0, 0).Scale, 9);
        Assert.False(new TileKey(0, key, 0, 0).IsPreview);
        Assert.True(new TileKey(0, TileKey.PreviewScaleKey, 0, 0).IsPreview);
        Assert.Equal(1, TileKey.ToScaleKey(0.0001));
    }

    [Fact]
    public void PreviewScaleFitsTheLongestSide()
    {
        Assert.Equal(320 / 841.89, TilePlanner.PreviewScale(A4), 9);
        Assert.Equal(1.6, TilePlanner.PreviewScale(new PdfPageSize(200, 100)), 9);
    }

    [Fact]
    public void SmallPagesAreOneWholePageTile()
    {
        var (width, height) = A4.ToPixels(1.5);

        var plan = TilePlanner.PlanSharp(3, A4, 1.5, new PixelRect(0, 0, 400, 400), background: false);

        var request = Assert.Single(plan);
        Assert.True(request.WholePage);
        Assert.False(request.Background);
        Assert.Equal(new TileKey(3, 1500, 0, 0), request.Key);
        Assert.Equal(1.5, request.Scale, 9);
        Assert.Equal(new PixelRect(0, 0, width, height), request.Rect);
    }

    [Fact]
    public void LargePagesAreSplitIntoA512Grid()
    {
        // A4 at 4.0 is 2382 x 3368 px (the sides are rounded up): 5 columns x 7 rows.
        var (width, height) = A4.ToPixels(4.0);
        Assert.Equal((2382, 3368), (width, height));

        var plan = TilePlanner.PlanSharp(0, A4, 4.0, new PixelRect(0, 0, width, height), background: true);

        Assert.Equal(35, plan.Count);
        Assert.All(plan, request =>
        {
            Assert.False(request.WholePage);
            Assert.True(request.Background);
            Assert.Equal(4.0, request.Scale, 9);
        });
        Assert.Equal(4, plan.Max(request => request.Key.Column));
        Assert.Equal(6, plan.Max(request => request.Key.Row));
        Assert.Equal(new PixelRect(0, 0, 512, 512), plan.Single(request => request.Key is { Column: 0, Row: 0 }).Rect);
        Assert.Equal(new PixelRect(2048, 3072, 334, 296), plan.Single(request => request.Key is { Column: 4, Row: 6 }).Rect);
        Assert.Equal(width * (long)height, plan.Sum(request => (long)request.Rect.Width * request.Rect.Height));
    }

    [Fact]
    public void OnlyTilesIntersectingTheVisibleRectArePlanned()
    {
        var single = TilePlanner.PlanSharp(0, A4, 4.0, new PixelRect(10, 10, 500, 500), background: false);
        var two = TilePlanner.PlanSharp(0, A4, 4.0, new PixelRect(500, 0, 100, 100), background: false);
        var outside = TilePlanner.PlanSharp(0, A4, 4.0, new PixelRect(5000, 0, 100, 100), background: false);

        Assert.Equal(new TileKey(0, 4000, 0, 0), Assert.Single(single).Key);
        Assert.Equal([0, 1], two.Select(request => request.Key.Column).Order());
        Assert.All(two, request => Assert.Equal(0, request.Key.Row));
        Assert.Empty(outside);
    }

    [Fact]
    public void TilesAreSortedByDistanceToTheVisibleCenter()
    {
        var visible = new PixelRect(1000, 1500, 200, 200);

        var plan = TilePlanner.PlanSharp(0, A4, 4.0, visible, background: false);

        Assert.Equal(4, plan.Count);
        Assert.Equal(new TileKey(0, 4000, 2, 3), plan[0].Key);
        var centerX = visible.X + (visible.Width / 2.0);
        var centerY = visible.Y + (visible.Height / 2.0);
        var distances = plan.Select(request =>
        {
            var dx = request.Rect.X + (request.Rect.Width / 2.0) - centerX;
            var dy = request.Rect.Y + (request.Rect.Height / 2.0) - centerY;
            return (dx * dx) + (dy * dy);
        }).ToArray();
        Assert.Equal(distances.Order(), distances);
    }

    [Fact]
    public void HugePagePlansOnlyVisibleTiles()
    {
        var huge = new PdfPageSize(14400, 14400);

        var plan = TilePlanner.PlanSharp(0, huge, 10.67, new PixelRect(0, 0, 1920, 1080), background: false);

        Assert.InRange(plan.Count, 1, 12);
        Assert.All(plan, request =>
        {
            Assert.False(request.WholePage);
            Assert.InRange(request.Rect.Width, 1, 512);
            Assert.InRange(request.Rect.Height, 1, 512);
        });

        var middle = TilePlanner.PlanSharp(0, huge, 10.67, new PixelRect(70000, 70000, 1920, 1080), background: false);

        Assert.InRange(middle.Count, 1, 20);
    }
}
