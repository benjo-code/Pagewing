using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class DrawingTransformTests
{
    private static readonly PageRect Page = new(0, 0, 600, 800);

    [Fact]
    public void MoveIsClampedToThePage()
    {
        var rectangle = DrawingGeometry.Rectangle(new PageRect(100, 100, 200, 160), 2);

        var moved = DrawingTransform.Move(rectangle, 30, 40, Page);
        Assert.Equal(new PageRect(130, 140, 230, 200), moved.Box);

        var left = DrawingTransform.Move(rectangle, -1000, -1000, Page);
        Assert.Equal(new PageRect(0, 0, 100, 60), left.Box);

        var right = DrawingTransform.Move(rectangle, 1000, 1000, Page);
        Assert.Equal(new PageRect(500, 740, 600, 800), right.Box);

        // Strokes follow the clamped shift, not the requested one.
        var ink = DrawingGeometry.Ink([[new DisplayPoint(10, 10), new DisplayPoint(30, 30)]], 2);
        var inkMoved = DrawingTransform.Move(ink, -50, 5, Page);
        Assert.Equal(new DisplayPoint(0, 15), inkMoved.Strokes[0][0]);
        Assert.Equal(new DisplayPoint(20, 35), inkMoved.Strokes[0][1]);
        Assert.Equal(new PageRect(0, 15, 20, 35), inkMoved.Box);
    }

    [Fact]
    public void MoveIgnoresNotFiniteShiftsAndBoxesLargerThanThePage()
    {
        var rectangle = DrawingGeometry.Rectangle(new PageRect(100, 100, 200, 160), 2);
        Assert.Equal(rectangle.Box, DrawingTransform.Move(rectangle, double.NaN, double.PositiveInfinity, Page).Box);

        var huge = DrawingGeometry.Rectangle(new PageRect(-10, 100, 700, 160), 2);
        Assert.Equal(new PageRect(-10, 120, 700, 180), DrawingTransform.Move(huge, 25, 20, Page).Box);
    }

    [Fact]
    public void ResizeKeepsMinimumSize()
    {
        var rectangle = DrawingGeometry.Rectangle(new PageRect(100, 100, 200, 160), 2);

        var tiny = DrawingTransform.Resize(rectangle, new PageRect(100, 100, 100.5, 90), Page);

        Assert.True(tiny.Box.Width >= 4);
        Assert.True(tiny.Box.Height >= 4);
        Assert.False(tiny.IsEmpty);

        // The minimum also holds at the edge of the page.
        var atEdge = DrawingTransform.Resize(rectangle, new PageRect(599, 799, 700, 900), Page);
        Assert.True(atEdge.Box.Width >= 4);
        Assert.True(atEdge.Box.Height >= 4);
        Assert.True(atEdge.Box.Right <= 600);
        Assert.True(atEdge.Box.Bottom <= 800);

        // A box dragged inside out is put right.
        var flipped = DrawingTransform.Resize(rectangle, new PageRect(300, 300, 200, 200), Page);
        Assert.Equal(new PageRect(200, 200, 300, 300), flipped.Box);
    }

    [Fact]
    public void ResizeIsClampedToThePage()
    {
        var ellipse = DrawingGeometry.Ellipse(new PageRect(100, 100, 200, 160), 2);

        var resized = DrawingTransform.Resize(ellipse, new PageRect(-50, -20, 900, 1000), Page);

        Assert.Equal(Page, resized.Box);
        Assert.Equal(AnnotationKind.Ellipse, resized.Kind);
        Assert.Equal(2, resized.Width);
    }

    [Fact]
    public void ScaleOfInkStrokesIsNonUniform()
    {
        var ink = DrawingGeometry.Ink(
            [[new DisplayPoint(100, 100), new DisplayPoint(150, 120), new DisplayPoint(200, 200)], [new DisplayPoint(100, 200), new DisplayPoint(200, 100)]],
            2);

        var resized = DrawingTransform.Resize(ink, new PageRect(100, 100, 300, 150), Page);

        Assert.Equal(new PageRect(100, 100, 300, 150), resized.Box);
        Assert.Equal(new DisplayPoint(100, 100), resized.Strokes[0][0]);
        Assert.Equal(new DisplayPoint(200, 110), resized.Strokes[0][1]);
        Assert.Equal(new DisplayPoint(300, 150), resized.Strokes[0][2]);
        Assert.Equal(new DisplayPoint(100, 150), resized.Strokes[1][0]);
        Assert.Equal(new DisplayPoint(300, 100), resized.Strokes[1][1]);
    }

    [Fact]
    public void ResizeOfAStraightInkStrokeDoesNotDivideByZero()
    {
        var flat = DrawingGeometry.Ink([[new DisplayPoint(100, 100), new DisplayPoint(200, 100)]], 2);

        var resized = DrawingTransform.Resize(flat, new PageRect(100, 100, 200, 130), Page);

        Assert.All(resized.Strokes[0], p => Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y)));

        // A stroke without height has nothing to scale: it is centred in the new height.
        Assert.Equal(new PageRect(100, 115, 200, 115), resized.Box);
    }

    [Fact]
    public void MoveEndpointKeepsTheOtherEnd()
    {
        var line = DrawingGeometry.Line(new DisplayPoint(100, 100), new DisplayPoint(200, 100), 2);

        var movedEnd = DrawingTransform.MoveEndpoint(line, start: false, new DisplayPoint(200, 180));
        Assert.Equal([new DisplayPoint(100, 100), new DisplayPoint(200, 180)], movedEnd.Strokes[0]);
        Assert.Equal(new PageRect(100, 100, 200, 180), movedEnd.Box);

        var movedStart = DrawingTransform.MoveEndpoint(line, start: true, new DisplayPoint(50, 60));
        Assert.Equal([new DisplayPoint(50, 60), new DisplayPoint(200, 100)], movedStart.Strokes[0]);

        // An arrow is rebuilt with a head turned to the new direction, not stretched.
        var arrow = DrawingGeometry.Arrow(new DisplayPoint(100, 100), new DisplayPoint(200, 100), 2);
        var turned = DrawingTransform.MoveEndpoint(arrow, start: false, new DisplayPoint(100, 200));
        Assert.Equal(2, turned.Strokes.Count);
        Assert.Equal(new DisplayPoint(100, 100), turned.Strokes[0][0]);
        Assert.Equal(new DisplayPoint(100, 200), turned.Strokes[0][1]);
        Assert.Equal(turned.Strokes[0][1], turned.Strokes[1][1]);
        Assert.True(ArrowGeometry.TryRecognize(turned.Strokes, out var start, out var end, out var head));
        Assert.True(head);
        Assert.Equal(new DisplayPoint(100, 100), start);
        Assert.Equal(new DisplayPoint(100, 200), end);
        Assert.Equal(AnnotationKind.Arrow, turned.Kind);
        Assert.Equal(arrow.Width, turned.Width);
    }

    [Fact]
    public void MoveEndpointClampsToThePageWhenGivenOne()
    {
        var line = DrawingGeometry.Line(new DisplayPoint(100, 100), new DisplayPoint(200, 100), 2);

        var moved = DrawingTransform.MoveEndpoint(line, start: false, new DisplayPoint(900, -30), Page);

        Assert.Equal(new DisplayPoint(600, 0), moved.Strokes[0][1]);
        Assert.Throws<ArgumentException>(() => DrawingTransform.MoveEndpoint(DrawingGeometry.Rectangle(new PageRect(1, 1, 9, 9), 2), true, new DisplayPoint(0, 0)));
    }

    [Fact]
    public void MoveAndResizeKeepArrowsRecognisable()
    {
        var arrow = DrawingGeometry.Arrow(new DisplayPoint(100, 100), new DisplayPoint(200, 140), 2);

        var moved = DrawingTransform.Move(arrow, 20, 30, Page);
        Assert.True(ArrowGeometry.TryRecognize(moved.Strokes, out _, out _, out var movedHead));
        Assert.True(movedHead);

        var resized = DrawingTransform.Resize(arrow, new PageRect(100, 100, 300, 250), Page);
        Assert.True(ArrowGeometry.TryRecognize(resized.Strokes, out var start, out var end, out var resizedHead));
        Assert.True(resizedHead);
        Assert.NotEqual(start, end);
    }
}
