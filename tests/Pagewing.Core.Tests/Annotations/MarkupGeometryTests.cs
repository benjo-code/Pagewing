using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class MarkupGeometryTests
{
    [Fact]
    public void QuadIsOrderedLikeAcrobatReadsIt()
    {
        var topLeft = new PagePoint(10, 20);
        var topRight = new PagePoint(50, 20);
        var bottomRight = new PagePoint(50, 8);
        var bottomLeft = new PagePoint(10, 8);

        var quad = MarkupGeometry.ToQuad(topLeft, topRight, bottomRight, bottomLeft);

        Assert.Equal(new Quad(topLeft, topRight, bottomLeft, bottomRight), quad);
        Assert.Equal(bottomLeft, quad.BottomLeft);
        Assert.Equal(bottomRight, quad.BottomRight);
    }

    [Fact]
    public void LinesMergeNeighboursOfOneLine()
    {
        var lines = MarkupGeometry.LinesFor(
        [
            new PageRect(10, 20, 30, 32),
            new PageRect(30, 20, 50, 32),
            new PageRect(52, 21, 70, 33),
            new PageRect(10, 40, 40, 52),
            new PageRect(100, 20, 120, 32),
        ]);

        Assert.Equal(
            [new PageRect(10, 20, 70, 33), new PageRect(10, 40, 40, 52), new PageRect(100, 20, 120, 32)],
            lines);
    }

    [Fact]
    public void LinesMergeNeighboursThatArriveOutOfOrder()
    {
        var lines = MarkupGeometry.LinesFor(
        [
            new PageRect(10, 20, 30, 32),
            new PageRect(50, 20, 70, 32),
            new PageRect(30, 20, 50, 32),
        ]);

        Assert.Equal([new PageRect(10, 20, 70, 32)], lines);
    }

    [Fact]
    public void LinesDropEmptyInvertedAndNonFiniteRects()
    {
        var lines = MarkupGeometry.LinesFor(
        [
            new PageRect(10, 20, 10, 32),
            new PageRect(10, 20, 30, 20),
            new PageRect(30, 32, 10, 20),
            new PageRect(double.NaN, 20, 30, 32),
            new PageRect(10, 20, double.PositiveInfinity, 32),
            new PageRect(10, double.NegativeInfinity, 30, 32),
            new PageRect(10, 20, 30, 32),
        ]);

        Assert.Equal([new PageRect(10, 20, 30, 32)], lines);
        Assert.Empty(MarkupGeometry.LinesFor([]));
    }
}
