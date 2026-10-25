using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class DrawingGeometryTests
{
    [Fact]
    public void ZeroExtentRectangleIsEmpty()
    {
        Assert.True(DrawingGeometry.Rectangle(new PageRect(10, 10, 10, 50), 2).IsEmpty);
        Assert.True(DrawingGeometry.Rectangle(new PageRect(10, 10, 50, 10), 2).IsEmpty);
        Assert.True(DrawingGeometry.Ellipse(new PageRect(10, 10, 10, 10), 2).IsEmpty);
        Assert.True(DrawingGeometry.Rectangle(new PageRect(double.NaN, 10, 50, 50), 2).IsEmpty);
        Assert.True(DrawingGeometry.Line(new DisplayPoint(5, 5), new DisplayPoint(5, 5), 2).IsEmpty);
        Assert.True(DrawingGeometry.Arrow(new DisplayPoint(5, 5), new DisplayPoint(5, 5), 2).IsEmpty);

        Assert.False(DrawingGeometry.Rectangle(new PageRect(10, 10, 50, 40), 2).IsEmpty);
        Assert.False(DrawingGeometry.Ellipse(new PageRect(10, 10, 50, 40), 2).IsEmpty);
        Assert.False(DrawingGeometry.Line(new DisplayPoint(5, 5), new DisplayPoint(50, 5), 2).IsEmpty);
    }

    [Fact]
    public void SinglePointStrokeIsEmpty()
    {
        Assert.True(DrawingGeometry.Ink([[new DisplayPoint(10, 10)]], 2).IsEmpty);
        Assert.True(DrawingGeometry.Ink([], 2).IsEmpty);
        Assert.True(DrawingGeometry.Ink([[]], 2).IsEmpty);
        Assert.True(DrawingGeometry.Ink([[new DisplayPoint(10, 10), new DisplayPoint(10, 10)]], 2).IsEmpty);

        Assert.False(DrawingGeometry.Ink([[new DisplayPoint(10, 10), new DisplayPoint(12, 10)]], 2).IsEmpty);
    }

    [Fact]
    public void FactoriesSetKindBoxAndStrokes()
    {
        var ink = DrawingGeometry.Ink([[new DisplayPoint(10, 40), new DisplayPoint(30, 20)], [new DisplayPoint(5, 25), new DisplayPoint(8, 26)]], 2);
        Assert.Equal(AnnotationKind.Ink, ink.Kind);
        Assert.Equal(new PageRect(5, 20, 30, 40), ink.Box);
        Assert.Equal(2, ink.Strokes.Count);
        Assert.Equal(2, ink.Width);

        var line = DrawingGeometry.Line(new DisplayPoint(10, 40), new DisplayPoint(30, 20), 1);
        Assert.Equal(AnnotationKind.Line, line.Kind);
        Assert.Equal(new PageRect(10, 20, 30, 40), line.Box);
        Assert.Single(line.Strokes);

        var arrow = DrawingGeometry.Arrow(new DisplayPoint(100, 100), new DisplayPoint(200, 100), 4);
        Assert.Equal(AnnotationKind.Arrow, arrow.Kind);
        Assert.Equal(2, arrow.Strokes.Count);

        // The box of an arrow contains its head.
        Assert.True(arrow.Box.Top < 100);
        Assert.True(arrow.Box.Bottom > 100);

        var rectangle = DrawingGeometry.Rectangle(new PageRect(50, 60, 10, 20), 4);
        Assert.Equal(AnnotationKind.Rectangle, rectangle.Kind);
        Assert.Equal(new PageRect(10, 20, 50, 60), rectangle.Box);
        Assert.Empty(rectangle.Strokes);

        Assert.Equal(AnnotationKind.Ellipse, DrawingGeometry.Ellipse(new PageRect(0, 0, 9, 9), 1).Kind);
    }

    [Fact]
    public void TheStrokesAreCopiedSoTheGeometryCannotChangeUnderneath()
    {
        var points = new[] { new DisplayPoint(0, 0), new DisplayPoint(10, 10) };
        var geometry = DrawingGeometry.Ink([points], 2);

        points[1] = new DisplayPoint(500, 500);

        Assert.Equal(new DisplayPoint(10, 10), geometry.Strokes[0][1]);
        Assert.Equal(new PageRect(0, 0, 10, 10), geometry.Box);
    }

    [Fact]
    public void ThePaletteAndWidthsAreComplete()
    {
        Assert.Equal(5, DrawPalette.All.Count);
        Assert.Equal(5, DrawPalette.All.Select(DrawPalette.ColorOf).Distinct().Count());
        Assert.Equal("DrawColorRed", DrawPalette.NameKey(DrawColor.Red));
        Assert.Equal(DrawColor.Blue, DrawPalette.Match(DrawPalette.ColorOf(DrawColor.Blue)));
        Assert.Null(DrawPalette.Match(new AnnotationColor(1, 2, 3)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DrawPalette.ColorOf((DrawColor)99));

        Assert.Equal([1d, 2d, 4d], new[] { StrokeWidth.Thin, StrokeWidth.Medium, StrokeWidth.Thick }.Select(StrokeWidths.PointsOf));
        Assert.Throws<ArgumentOutOfRangeException>(() => StrokeWidths.PointsOf((StrokeWidth)9));
        Assert.Equal(new ToolStyle(DrawColor.Red, StrokeWidth.Medium), new ToolStyle(DrawColor.Red, StrokeWidth.Medium));
    }
}
