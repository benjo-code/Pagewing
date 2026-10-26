using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class ArrowGeometryTests
{
    private static readonly DisplayPoint Start = new(100, 100);
    private static readonly DisplayPoint End = new(200, 100);

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 8)]
    [InlineData(4, 16)]
    public void ArrowIsAShaftAndOneHeadPolyline(double width, double headLength)
    {
        var strokes = ArrowGeometry.Strokes(Start, End, width, head: true);

        Assert.Equal(2, strokes.Count);
        Assert.Equal([Start, End], strokes[0]);
        Assert.Equal(3, strokes[1].Count);
        Assert.Equal(End, strokes[1][1]);

        var angle = 25 * Math.PI / 180;
        foreach (var tip in new[] { strokes[1][0], strokes[1][2] })
        {
            // The head points back along the shaft (towards the start) at 25 degrees to it.
            var dx = tip.X - End.X;
            var dy = tip.Y - End.Y;
            Assert.Equal(headLength, Math.Sqrt((dx * dx) + (dy * dy)), 9);
            Assert.Equal(-headLength * Math.Cos(angle), dx, 9);
            Assert.Equal(headLength * Math.Sin(angle), Math.Abs(dy), 9);
        }

        Assert.Equal(strokes[1][0].Y - End.Y, End.Y - strokes[1][2].Y, 9);
    }

    [Fact]
    public void ALineIsOneStrokeOfTwoPoints()
    {
        var strokes = ArrowGeometry.Strokes(Start, End, 2, head: false);

        Assert.Single(strokes);
        Assert.Equal([Start, End], strokes[0]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryRecognizeFindsItsOwnArrowAndLine(bool head)
    {
        var start = new DisplayPoint(31.25, 480.5);
        var end = new DisplayPoint(212.75, 77.125);

        var strokes = ArrowGeometry.Strokes(start, end, 2, head);

        Assert.True(ArrowGeometry.TryRecognize(strokes, out var foundStart, out var foundEnd, out var foundHead));
        Assert.Equal(start, foundStart);
        Assert.Equal(end, foundEnd);
        Assert.Equal(head, foundHead);
    }

    [Fact]
    public void TryRecognizeSurvivesSinglePrecisionCoordinates()
    {
        var strokes = ArrowGeometry.Strokes(new DisplayPoint(512.3, 700.7), new DisplayPoint(33.1, 41.9), 4, head: true);
        var rounded = strokes
            .Select(s => (IReadOnlyList<DisplayPoint>)s.Select(p => new DisplayPoint((float)p.X, (float)p.Y)).ToArray())
            .ToArray();

        Assert.True(ArrowGeometry.TryRecognize(rounded, out _, out _, out var head));
        Assert.True(head);
    }

    [Fact]
    public void RejectsFreehandWithThreeStrokes()
    {
        IReadOnlyList<DisplayPoint>[] three =
        [
            [Start, End],
            [new(190, 95), End, new(190, 105)],
            [new(1, 1), new(2, 2)],
        ];

        Assert.False(ArrowGeometry.TryRecognize(three, out _, out _, out _));
    }

    [Fact]
    public void RejectsShapesThatAreNotLineOrArrow()
    {
        var arrow = ArrowGeometry.Strokes(Start, End, 2, head: true);

        // Head whose middle point is not the shaft end.
        IReadOnlyList<DisplayPoint>[] moved = [arrow[0], [arrow[1][0], new DisplayPoint(End.X + 1, End.Y), arrow[1][2]]];
        Assert.False(ArrowGeometry.TryRecognize(moved, out _, out _, out _));

        // Head at the wrong angle.
        IReadOnlyList<DisplayPoint>[] wrongAngle = [arrow[0], [new DisplayPoint(190, 80), End, new DisplayPoint(190, 120)]];
        Assert.False(ArrowGeometry.TryRecognize(wrongAngle, out _, out _, out _));

        // Three points in one stroke, a single point, no strokes, an empty stroke, a degenerate shaft, not finite.
        Assert.False(ArrowGeometry.TryRecognize([[Start, new DisplayPoint(150, 100), End]], out _, out _, out _));
        Assert.False(ArrowGeometry.TryRecognize([[Start]], out _, out _, out _));
        Assert.False(ArrowGeometry.TryRecognize([], out _, out _, out _));
        Assert.False(ArrowGeometry.TryRecognize([[]], out _, out _, out _));
        Assert.False(ArrowGeometry.TryRecognize([[Start, Start]], out _, out _, out _));
        Assert.False(ArrowGeometry.TryRecognize([[Start, new DisplayPoint(double.NaN, 1)]], out _, out _, out _));
    }

    [Fact]
    public void ADegenerateArrowIsJustItsShaft()
    {
        var strokes = ArrowGeometry.Strokes(Start, Start, 2, head: true);

        Assert.Single(strokes);
    }

    [Fact]
    public void SnapToAngleRoundsToMultiplesOf45()
    {
        var anchor = new DisplayPoint(50, 50);

        // Nearly horizontal and nearly vertical: onto the axis, keeping the length.
        var horizontal = ArrowGeometry.SnapToAngle(anchor, new DisplayPoint(60, 53), 45);
        Assert.Equal(50 + Math.Sqrt(109), horizontal.X, 9);
        Assert.Equal(50, horizontal.Y);

        var vertical = ArrowGeometry.SnapToAngle(anchor, new DisplayPoint(53, 40), 45);
        Assert.Equal(50, vertical.X);
        Assert.Equal(50 - Math.Sqrt(109), vertical.Y, 9);

        Assert.Equal(new DisplayPoint(50, 50), ArrowGeometry.SnapToAngle(anchor, anchor, 45));

        // Nearly diagonal: onto the diagonal, keeping the length.
        var diagonal = ArrowGeometry.SnapToAngle(anchor, new DisplayPoint(70, 66), 45);
        Assert.Equal(diagonal.X - 50, diagonal.Y - 50, 9);
        Assert.Equal(Math.Sqrt((20 * 20) + (16 * 16)), Math.Sqrt(Math.Pow(diagonal.X - 50, 2) + Math.Pow(diagonal.Y - 50, 2)), 9);
        Assert.True(diagonal.X > 50);

        // A step of zero or less leaves the point alone.
        Assert.Equal(new DisplayPoint(60, 53), ArrowGeometry.SnapToAngle(anchor, new DisplayPoint(60, 53), 0));
    }

    [Fact]
    public void SquareBoxKeepsTheLongerSide()
    {
        var anchor = new DisplayPoint(100, 100);

        Assert.Equal(new PageRect(100, 100, 140, 140), ArrowGeometry.SquareBox(anchor, new DisplayPoint(140, 120)));
        Assert.Equal(new PageRect(100, 100, 130, 130), ArrowGeometry.SquareBox(anchor, new DisplayPoint(110, 130)));
        Assert.Equal(new PageRect(70, 70, 100, 100), ArrowGeometry.SquareBox(anchor, new DisplayPoint(70, 90)));
        Assert.Equal(new PageRect(100, 100, 100, 100), ArrowGeometry.SquareBox(anchor, anchor));
    }
}
