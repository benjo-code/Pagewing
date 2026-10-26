using Pagewing.Core.Annotations;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class StrokeSimplifierTests
{
    [Fact]
    public void CollinearPointsCollapseToTwo()
    {
        var points = Enumerable.Range(0, 20).Select(i => new DisplayPoint(i, i * 0.5)).ToArray();

        var simplified = StrokeSimplifier.Simplify(points, 0.5);

        Assert.Equal([points[0], points[^1]], simplified);
    }

    [Fact]
    public void SimplifyKeepsACornerBeyondTolerance()
    {
        DisplayPoint[] points = [new(0, 0), new(5, 0), new(10, 0), new(10, 5), new(10, 10)];

        var simplified = StrokeSimplifier.Simplify(points, 0.5);

        Assert.Equal([new DisplayPoint(0, 0), new DisplayPoint(10, 0), new DisplayPoint(10, 10)], simplified);
    }

    [Fact]
    public void SimplifyDropsABumpWithinTolerance()
    {
        DisplayPoint[] points = [new(0, 0), new(5, 0.2), new(10, 0)];

        Assert.Equal([points[0], points[2]], StrokeSimplifier.Simplify(points, 0.5));
    }

    [Fact]
    public void SinglePointStaysSingle()
    {
        DisplayPoint[] one = [new(3, 4)];

        Assert.Equal(one, StrokeSimplifier.Simplify(one, 0.5));
        Assert.Equal(one, StrokeSimplifier.Smooth(one));
        Assert.Empty(StrokeSimplifier.Simplify([], 0.5));
        Assert.Empty(StrokeSimplifier.Smooth([]));
    }

    [Fact]
    public void ASimplifiedClosedStrokeKeepsItsFarPoint()
    {
        DisplayPoint[] points = [new(0, 0), new(10, 0), new(10, 10), new(0, 0)];

        var simplified = StrokeSimplifier.Simplify(points, 0.5);

        Assert.Equal(4, simplified.Count);
        Assert.Equal(points[0], simplified[0]);
        Assert.Equal(points[^1], simplified[^1]);
    }

    [Fact]
    public void SmoothKeepsEndpointsAndDoublesThePoints()
    {
        DisplayPoint[] points = [new(0, 0), new(10, 0), new(10, 10), new(20, 10)];

        var smoothed = StrokeSimplifier.Smooth(points);

        Assert.Equal((2 * (points.Length - 1)) + 2, smoothed.Count);
        Assert.Equal(points[0], smoothed[0]);
        Assert.Equal(points[^1], smoothed[^1]);
        Assert.Equal(new DisplayPoint(2.5, 0), smoothed[1]);
        Assert.Equal(new DisplayPoint(7.5, 0), smoothed[2]);
    }

    [Fact]
    public void SmoothLeavesALineAlone()
    {
        DisplayPoint[] line = [new(0, 0), new(10, 5)];

        Assert.Equal(line, StrokeSimplifier.Smooth(line));
    }

    [Fact]
    public void ALongZigZagKeepsEveryCornerWithoutRecursion()
    {
        var zigzag = Enumerable.Range(0, 5_000).Select(i => new DisplayPoint(i, i % 2 * 10)).ToArray();

        var simplified = StrokeSimplifier.Simplify(zigzag, 0.5);

        Assert.Equal(zigzag.Length, simplified.Count);
    }
}
