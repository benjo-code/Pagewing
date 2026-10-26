using Pagewing.Core.Annotations;
using Pagewing.Core.Text;
using Xunit;

namespace Pagewing.Core.Tests.Annotations;

public sealed class SelectionHandlesTests
{
    private static readonly PageRect Page = new(0, 0, 600, 800);

    [Fact]
    public void ArrowHasTwoHandles()
    {
        var arrow = Drawing(DrawingGeometry.Arrow(new DisplayPoint(100, 120), new DisplayPoint(300, 220), 2));
        var handles = SelectionHandles.For(arrow);
        Assert.Equal(new[] { new Handle(HandleKind.Start, new DisplayPoint(100, 120)), new Handle(HandleKind.End, new DisplayPoint(300, 220)) }, handles);

        var line = Drawing(DrawingGeometry.Line(new DisplayPoint(10, 20), new DisplayPoint(30, 40), 1));
        Assert.Equal(new[] { HandleKind.Start, HandleKind.End }, SelectionHandles.For(line).Select(h => h.Kind));
    }

    [Fact]
    public void InkHasEight()
    {
        var ink = Drawing(DrawingGeometry.Ink([[new DisplayPoint(100, 200), new DisplayPoint(300, 260), new DisplayPoint(200, 400)]], 2));
        var handles = SelectionHandles.For(ink);
        Assert.Equal(
            new[]
            {
                new Handle(HandleKind.TopLeft, new DisplayPoint(100, 200)),
                new Handle(HandleKind.Top, new DisplayPoint(200, 200)),
                new Handle(HandleKind.TopRight, new DisplayPoint(300, 200)),
                new Handle(HandleKind.Right, new DisplayPoint(300, 300)),
                new Handle(HandleKind.BottomRight, new DisplayPoint(300, 400)),
                new Handle(HandleKind.Bottom, new DisplayPoint(200, 400)),
                new Handle(HandleKind.BottomLeft, new DisplayPoint(100, 400)),
                new Handle(HandleKind.Left, new DisplayPoint(100, 300)),
            },
            handles);

        // The shapes have the same eight on their box.
        Assert.Equal(8, SelectionHandles.For(Drawing(DrawingGeometry.Rectangle(new PageRect(10, 10, 50, 40), 1))).Count);
        Assert.Equal(8, SelectionHandles.For(Drawing(DrawingGeometry.Ellipse(new PageRect(10, 10, 50, 40), 1))).Count);
    }

    [Fact]
    public void MarkupHasNone()
    {
        var highlight = new PdfAnnotation(
            0, 0, "mark", AnnotationKind.Highlight, "Highlight", null, [], [new PageRect(10, 10, 100, 20)], new PageRect(10, 10, 100, 20), null, null, null, Editable: true);
        Assert.Empty(SelectionHandles.For(highlight));

        // A drawing of another application that Pagewing cannot redraw without loss is selected without handles.
        var locked = Drawing(DrawingGeometry.Rectangle(new PageRect(10, 10, 50, 40), 1)) with { GeometryEditable = false };
        Assert.Empty(SelectionHandles.For(locked));
        Assert.Empty(SelectionHandles.For(locked with { Drawing = null }));
    }

    [Fact]
    public void ResizeKeepsMinimumSize()
    {
        var box = new PageRect(100, 100, 200, 160);

        // The right edge dragged past the left one stops 4 pt from it: no flip.
        Assert.Equal(new PageRect(100, 100, 104, 160), SelectionHandles.ResizeBy(box, HandleKind.Right, new DisplayPoint(20, 130), keepAspect: false, Page));
        Assert.Equal(new PageRect(196, 100, 200, 160), SelectionHandles.ResizeBy(box, HandleKind.Left, new DisplayPoint(500, 130), keepAspect: false, Page));
        Assert.Equal(new PageRect(100, 156, 200, 160), SelectionHandles.ResizeBy(box, HandleKind.Top, new DisplayPoint(150, 300), keepAspect: false, Page));
        Assert.Equal(new PageRect(100, 100, 104, 104), SelectionHandles.ResizeBy(box, HandleKind.BottomRight, new DisplayPoint(0, 0), keepAspect: false, Page));

        // A corner moves both of its edges, an edge only its own; the opposite ones stay.
        Assert.Equal(new PageRect(50, 70, 200, 160), SelectionHandles.ResizeBy(box, HandleKind.TopLeft, new DisplayPoint(50, 70), keepAspect: false, Page));
        Assert.Equal(new PageRect(100, 100, 200, 190), SelectionHandles.ResizeBy(box, HandleKind.Bottom, new DisplayPoint(400, 190), keepAspect: false, Page));

        // With Shift a corner keeps the proportions (5:3) from the opposite corner, also at the smallest size.
        Assert.Equal(new PageRect(100, 100, 300, 220), SelectionHandles.ResizeBy(box, HandleKind.BottomRight, new DisplayPoint(300, 170), keepAspect: true, Page));
        var tiny = SelectionHandles.ResizeBy(box, HandleKind.BottomRight, new DisplayPoint(0, 0), keepAspect: true, Page);
        Assert.Equal(100, tiny.Left, 6);
        Assert.Equal(100, tiny.Top, 6);
        Assert.True(tiny.Width >= DrawingTransform.MinimumSize - 1e-9 && tiny.Height >= DrawingTransform.MinimumSize - 1e-9, $"{tiny}");
        Assert.Equal(box.Width / box.Height, tiny.Width / tiny.Height, 6);

        // An edge with Shift grows the other side around its middle line.
        Assert.Equal(new PageRect(100, 70, 300, 190), SelectionHandles.ResizeBy(box, HandleKind.Right, new DisplayPoint(300, 999), keepAspect: true, Page));
    }

    [Fact]
    public void ResizeNeverLeavesThePage()
    {
        var box = new PageRect(100, 100, 200, 160);

        Assert.Equal(new PageRect(0, 0, 200, 160), SelectionHandles.ResizeBy(box, HandleKind.TopLeft, new DisplayPoint(-50, -80), keepAspect: false, Page));
        Assert.Equal(new PageRect(100, 100, 600, 800), SelectionHandles.ResizeBy(box, HandleKind.BottomRight, new DisplayPoint(900, 1200), keepAspect: false, Page));
        Assert.Equal(new PageRect(100, 100, 200, 800), SelectionHandles.ResizeBy(box, HandleKind.Bottom, new DisplayPoint(150, double.MaxValue), keepAspect: false, Page));

        // With Shift the proportions stay and the box stops at the first edge it meets (here the right one: 500 pt of room).
        var grown = SelectionHandles.ResizeBy(box, HandleKind.BottomRight, new DisplayPoint(2000, 2000), keepAspect: true, Page);
        Assert.Equal(new PageRect(100, 100, 600, 400), grown);

        // An edge with Shift: the other side cannot grow beyond the page on either side of its middle line (130 pt up).
        var wide = SelectionHandles.ResizeBy(box, HandleKind.Right, new DisplayPoint(2000, 0), keepAspect: true, Page);
        Assert.True(wide.Left >= -1e-9 && wide.Top >= -1e-9 && wide.Right <= 600 + 1e-9 && wide.Bottom <= 800 + 1e-9, $"{wide}");
        Assert.Equal(box.Width / box.Height, wide.Width / wide.Height, 6);
        Assert.Equal(0, wide.Top, 6);

        // Not a finite point: the box stays as it is.
        Assert.Equal(box, SelectionHandles.ResizeBy(box, HandleKind.Right, new DisplayPoint(double.NaN, 0), keepAspect: false, Page));
    }

    [Fact]
    public void AnOwnTextBoxHasEightHandlesOnItsBoxAndAForeignOneNone()
    {
        var box = new PageRect(100, 100, 260, 160);
        var own = SelectionHandles.For(DrawingHitTestTests.TextBox(0, box, own: true));

        Assert.Equal(8, own.Count);
        Assert.Equal(new DisplayPoint(100, 100), own[0].Position);
        Assert.Equal(new DisplayPoint(260, 160), own[4].Position);
        Assert.Empty(SelectionHandles.For(DrawingHitTestTests.TextBox(1, box, own: false)));
    }

    [Fact]
    public void TheSizeStepsOfTheTextBoxToolAreSmallMediumAndLarge()
    {
        Assert.Equal(12, TextBoxSpec.FontSizeOf(StrokeWidth.Thin));
        Assert.Equal(16, TextBoxSpec.FontSizeOf(StrokeWidth.Medium));
        Assert.Equal(24, TextBoxSpec.FontSizeOf(StrokeWidth.Thick));
        Assert.Equal(StrokeWidth.Medium, TextBoxSpec.StepOf(16));
        Assert.Null(TextBoxSpec.StepOf(14));
    }

    private static PdfAnnotation Drawing(DrawingGeometry geometry) => new(
        0, 0, "drawing", geometry.Kind, "Ink", new AnnotationColor(220, 30, 30), [], [], geometry.Box, null, null, null, Editable: true, Drawing: geometry, GeometryEditable: true);
}
