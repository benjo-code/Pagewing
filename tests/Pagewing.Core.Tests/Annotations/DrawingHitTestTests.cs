using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class DrawingHitTestTests
{
    [Fact]
    public void DrawingWinsOverMarkup()
    {
        // The highlight comes later in the page (a higher index) and covers the point; the line under it is a drawing.
        var line = Drawing(0, DrawingGeometry.Line(new DisplayPoint(100, 105), new DisplayPoint(300, 105), 2), "line");
        var highlight = Markup(1, new PageRect(90, 95, 310, 115), "mark");
        Assert.Same(line, DrawingHitTest.Pick([line, highlight], new DisplayPoint(200, 105), 3));

        // Away from the line, inside the highlight: the highlight.
        Assert.Same(highlight, DrawingHitTest.Pick([line, highlight], new DisplayPoint(200, 112), 1));

        // Of two drawings the later one (higher index) is on top.
        var later = Drawing(2, DrawingGeometry.Line(new DisplayPoint(100, 105), new DisplayPoint(300, 106), 2), "later");
        Assert.Same(later, DrawingHitTest.Pick([line, highlight, later], new DisplayPoint(200, 105), 3));

        // Off everything: nothing; and an annotation that is not editable (a reply points at it) or no drawing or markup
        // (a link, a note) is never picked.
        Assert.Null(DrawingHitTest.Pick([line, highlight], new DisplayPoint(50, 50), 3));
        var locked = line with { Index = 3, Name = "replied", Editable = false };
        var link = Markup(4, new PageRect(90, 95, 310, 115), "link") with { Kind = AnnotationKind.Other, Subtype = "Link" };
        Assert.Null(DrawingHitTest.Pick([locked, link], new DisplayPoint(200, 105), 3));
    }

    [Fact]
    public void ToleranceHitsAThinLine()
    {
        // A freehand stroke of a hair's width: hit within the tolerance of a segment (plus half the width), not beyond it.
        var ink = Drawing(0, DrawingGeometry.Ink([[new DisplayPoint(100, 100), new DisplayPoint(200, 100), new DisplayPoint(200, 200)]], 0.5), "ink");
        Assert.Same(ink, DrawingHitTest.Pick([ink], new DisplayPoint(150, 104), 4));
        Assert.Same(ink, DrawingHitTest.Pick([ink], new DisplayPoint(204, 150), 4));
        Assert.Null(DrawingHitTest.Pick([ink], new DisplayPoint(150, 105), 4));

        // The segment ends, not the infinite line: beyond the end of the stroke only within the tolerance of the end point.
        Assert.Null(DrawingHitTest.Pick([ink], new DisplayPoint(80, 100), 4));
        Assert.Same(ink, DrawingHitTest.Pick([ink], new DisplayPoint(97, 101), 4));

        // An arrow is hit on its head as well as on its shaft.
        var arrow = Drawing(1, DrawingGeometry.Arrow(new DisplayPoint(300, 300), new DisplayPoint(400, 300), 1), "arrow");
        var head = arrow.Drawing!.Strokes[1][0];
        Assert.Same(arrow, DrawingHitTest.Pick([arrow], head, 1));
        Assert.Same(arrow, DrawingHitTest.Pick([arrow], new DisplayPoint(350, 301), 2));
    }

    [Fact]
    public void ShapeHitsOnlyTheOutline()
    {
        // The box is the outer edge: the stroke of 2 pt runs 1 pt inside it.
        var rectangle = Drawing(0, DrawingGeometry.Rectangle(new PageRect(100, 100, 300, 200), 2), "rectangle");
        Assert.Same(rectangle, DrawingHitTest.Pick([rectangle], new DisplayPoint(200, 101), 2));
        Assert.Same(rectangle, DrawingHitTest.Pick([rectangle], new DisplayPoint(299, 150), 2));
        Assert.Same(rectangle, DrawingHitTest.Pick([rectangle], new DisplayPoint(98, 150), 2));
        Assert.Null(DrawingHitTest.Pick([rectangle], new DisplayPoint(96, 150), 2));
        Assert.Null(DrawingHitTest.Pick([rectangle], new DisplayPoint(200, 150), 2));
        Assert.Null(DrawingHitTest.Pick([rectangle], new DisplayPoint(200, 110), 2));

        // The ellipse by its outline: not in the middle and not in the corner of its box, which is outside it.
        var ellipse = Drawing(1, DrawingGeometry.Ellipse(new PageRect(100, 300, 300, 400), 2), "ellipse");
        Assert.Same(ellipse, DrawingHitTest.Pick([ellipse], new DisplayPoint(200, 301), 2));
        Assert.Same(ellipse, DrawingHitTest.Pick([ellipse], new DisplayPoint(101, 350), 2));

        // On the outline at 45 degrees from the centre (radii 99 and 49 for the middle of the stroke).
        var angle = Math.PI / 4;
        var onOutline = new DisplayPoint(200 + (99 * Math.Cos(angle)), 350 + (49 * Math.Sin(angle)));
        Assert.Same(ellipse, DrawingHitTest.Pick([ellipse], onOutline, 1));
        Assert.Null(DrawingHitTest.Pick([ellipse], new DisplayPoint(200, 350), 2));
        Assert.Null(DrawingHitTest.Pick([ellipse], new DisplayPoint(104, 304), 2));
    }

    private static PdfAnnotation Drawing(int index, DrawingGeometry geometry, string name) => new(
        Page: 0,
        Index: index,
        Name: name,
        Kind: geometry.Kind,
        Subtype: geometry.Kind switch { AnnotationKind.Rectangle => "Square", AnnotationKind.Ellipse => "Circle", _ => "Ink" },
        Color: new AnnotationColor(220, 30, 30),
        Quads: [],
        QuadRects: [],
        Bounds: geometry.Box,
        Contents: null,
        Author: null,
        Modified: null,
        Editable: true,
        Drawing: geometry,
        GeometryEditable: true);

    private static PdfAnnotation Markup(int index, PageRect line, string name) => new(
        Page: 0,
        Index: index,
        Name: name,
        Kind: AnnotationKind.Highlight,
        Subtype: "Highlight",
        Color: new AnnotationColor(255, 230, 0),
        Quads: [],
        QuadRects: [line],
        Bounds: line,
        Contents: null,
        Author: null,
        Modified: null,
        Editable: true);
}
