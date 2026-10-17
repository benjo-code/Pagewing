using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

public sealed class ViewportPlannerTests
{
    private static readonly PdfPageSize A4 = new(595.28, 841.89);

    private static DocumentLayout Pages(int count, double zoom)
    {
        var layout = new DocumentLayout(count, A4);
        layout.SetSizes(0, Enumerable.Repeat(A4, count).ToArray());
        layout.Zoom = zoom;
        return layout;
    }

    [Fact]
    public void VisiblePreviewsComeFirstThenSharpTilesThenBufferPreviews()
    {
        // At a zoom of 0.5 a page is about 561 DIPs high: pages 0 and 1 are visible, page 2 lies in the buffer.
        var layout = Pages(10, 0.5);

        var plan = ViewportPlanner.Plan(layout, new DipRect(0, 0, 800, 900), 1.0);

        var visiblePreviews = plan.TakeWhile(request => request.Key.IsPreview && !request.Background).ToArray();
        Assert.Equal([0, 1], visiblePreviews.Select(request => request.Key.Page).Order());
        var rest = plan.Skip(visiblePreviews.Length).ToArray();
        var sharp = rest.TakeWhile(request => !request.Key.IsPreview).ToArray();
        Assert.Equal([0, 1], sharp.Select(request => request.Key.Page).Order());
        Assert.All(sharp, request => Assert.False(request.Background));
        var buffer = rest.Skip(sharp.Length).ToArray();
        Assert.NotEmpty(buffer);
        Assert.All(buffer, request =>
        {
            Assert.True(request.Key.IsPreview);
            Assert.True(request.Background);
            Assert.True(request.Key.Page >= 2);
        });
        Assert.All(visiblePreviews, request => Assert.True(request.WholePage));
    }

    [Fact]
    public void BufferPagesGetBackgroundPreviewsOnly()
    {
        var layout = Pages(10, 0.5);

        var plan = ViewportPlanner.Plan(layout, new DipRect(0, 0, 800, 900), 1.0);

        var bufferRequests = plan.Where(request => request.Key.Page >= 2).ToArray();
        Assert.NotEmpty(bufferRequests);
        Assert.All(bufferRequests, request =>
        {
            Assert.True(request.Key.IsPreview);
            Assert.True(request.Background);
        });
        Assert.DoesNotContain(plan, request => request.Key.Page > 3);
    }

    [Fact]
    public void ARequestFloodIsCapped()
    {
        var layout = Pages(1000, 0.1);

        var plan = ViewportPlanner.Plan(layout, new DipRect(0, 0, 1200, 1080), 1.0);

        Assert.InRange(plan.Count(request => request.Key.IsPreview), 1, ViewportPlanner.MaxPreviewRequests);
        Assert.True(plan.Count < 200, $"Planned {plan.Count} requests.");
    }

    [Fact]
    public void RenderScalingIsAppliedToTheScaleKey()
    {
        var layout = Pages(3, 1.0);
        var viewport = new DipRect(0, 0, 800, 600);

        var at125 = ViewportPlanner.Plan(layout, viewport, 1.25);
        var at150 = ViewportPlanner.Plan(layout, viewport, 1.5);

        Assert.Contains(at125, request => request.Key.ScaleKey == 1667);
        Assert.Contains(at150, request => request.Key.ScaleKey == 2000);
        Assert.DoesNotContain(at125, request => !request.Key.IsPreview && request.Key.ScaleKey != 1667);
        Assert.DoesNotContain(at150, request => !request.Key.IsPreview && request.Key.ScaleKey != 2000);
    }

    [Fact]
    public void HorizontalScrollLimitsTheTiles()
    {
        // Zoom 8: a page is about 6349 DIPs wide; the 800 DIP viewport sees only a part of it.
        var layout = Pages(1, 8.0);
        var page = layout.GetPageRect(0);
        var viewport = new DipRect(page.X + 2000, 100, 800, 600);

        var plan = ViewportPlanner.Plan(layout, viewport, 1.0);

        var sharp = plan.Where(request => !request.Key.IsPreview).ToArray();
        Assert.NotEmpty(sharp);
        var scale = sharp[0].Scale;
        var visibleLeft = 2000 * scale / (8.0 * DocumentLayout.DipsPerPoint);
        var visibleRight = 2800 * scale / (8.0 * DocumentLayout.DipsPerPoint);
        Assert.All(sharp, request =>
        {
            Assert.True(request.Rect.X + request.Rect.Width > visibleLeft - 1);
            Assert.True(request.Rect.X < visibleRight + 1);
        });
        Assert.True(sharp.Length <= 12, $"Planned {sharp.Length} tiles.");
    }

    [Fact]
    public void EmptyDocumentPlansNothing()
    {
        var layout = new DocumentLayout(0, A4);

        Assert.Empty(ViewportPlanner.Plan(layout, new DipRect(0, 0, 800, 600), 1.0));
    }

    [Fact]
    public void ViewportInAGapStillPlansTheNeighbourPages()
    {
        var layout = Pages(5, 0.5);
        var first = layout.GetPageRect(0);
        var gapTop = first.Bottom + 4;
        var viewport = new DipRect(0, gapTop, 800, 10);
        Assert.Equal((0, -1), layout.GetVisiblePages(viewport.Y, viewport.Bottom));

        var plan = ViewportPlanner.Plan(layout, viewport, 1.0);

        Assert.Contains(plan, request => request.Key.Page == 0);
        Assert.Contains(plan, request => request.Key.Page == 1);
        Assert.Contains(plan, request => request.Key.IsPreview && !request.Background);
        Assert.DoesNotContain(plan, request => !request.Key.IsPreview);
    }

    [Fact]
    public void AViewportWithoutAreaPlansNothing()
    {
        var layout = Pages(3, 1.0);

        Assert.Empty(ViewportPlanner.Plan(layout, new DipRect(0, 0, 0, 0), 1.0));
    }
}
