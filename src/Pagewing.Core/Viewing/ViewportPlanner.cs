using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// Plans the render jobs for what the viewport shows: first the coarse previews of the visible pages, then
/// their sharp tiles, then background previews of the pages one viewport height above and below.
/// </summary>
public static class ViewportPlanner
{
    /// <summary>The most preview jobs one plan contains; the visible pages come first, so the buffer is cut off.</summary>
    public const int MaxPreviewRequests = 48;

    /// <summary>Plans the jobs for <paramref name="viewport"/>, most important first.</summary>
    /// <param name="layout">The document layout; its zoom decides the scale.</param>
    /// <param name="viewport">The visible part of the content in content coordinates (DIPs).</param>
    /// <param name="renderScaling">Device pixels per DIP of the output; greater than 0.</param>
    /// <returns>The jobs in priority order; empty without pages or without viewport area.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="renderScaling"/> is not finite and positive.</exception>
    public static IReadOnlyList<RenderRequest> Plan(DocumentLayout layout, DipRect viewport, double renderScaling)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(renderScaling), renderScaling, "The render scaling must be finite and greater than 0.");
        }

        if (layout.PageCount == 0 || !(viewport.Width > 0) || !(viewport.Height > 0)
            || !double.IsFinite(viewport.X) || !double.IsFinite(viewport.Y)
            || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height))
        {
            return [];
        }

        var dipsPerPoint = DocumentLayout.DipsPerPoint * layout.Zoom;
        var scale = dipsPerPoint * renderScaling;
        var scaleKey = TileKey.ToScaleKey(scale);

        // The visible pages; a viewport that only shows a gap or a margin sees none, but the nearest page still gets a preview.
        var (firstVisible, lastVisible) = layout.GetVisiblePages(viewport.Y, viewport.Bottom);
        var visible = new List<int>();
        for (var page = firstVisible; page <= lastVisible; page++)
        {
            visible.Add(page);
        }

        if (visible.Count == 0)
        {
            visible = [layout.GetMostVisiblePage(viewport.Y, viewport.Bottom)];
        }

        var centerY = viewport.Y + (viewport.Height / 2);
        var visibleSet = new HashSet<int>(visible);
        var plan = new List<RenderRequest>();
        var previewCount = 0;

        // 1. Previews of the visible pages, nearest to the viewport center first.
        foreach (var page in OrderByDistance(layout, visible, centerY))
        {
            plan.Add(Preview(layout, page, background: false));
            previewCount++;
        }

        // 2. Sharp tiles of the pages that really are visible.
        if (firstVisible <= lastVisible)
        {
            foreach (var page in OrderByDistance(layout, visible, centerY))
            {
                var pageRect = layout.GetPageRect(page);
                var area = Intersect(pageRect, viewport);
                if (area is null)
                {
                    continue;
                }

                var pixelsPerDip = (scaleKey / 1000.0) / dipsPerPoint;
                var x0 = ToPixel(Math.Floor((area.Value.X - pageRect.X) * pixelsPerDip));
                var y0 = ToPixel(Math.Floor((area.Value.Y - pageRect.Y) * pixelsPerDip));
                var x1 = ToPixel(Math.Ceiling((area.Value.Right - pageRect.X) * pixelsPerDip));
                var y1 = ToPixel(Math.Ceiling((area.Value.Bottom - pageRect.Y) * pixelsPerDip));
                var rect = new PixelRect(x0, y0, x1 - x0, y1 - y0);
                plan.AddRange(TilePlanner.PlanSharp(page, layout.GetPageSize(page), scale, rect, background: false));
            }
        }

        // 3. Background previews of the pages in the buffer range, nearest first, within the preview cap.
        var bufferTop = viewport.Y - viewport.Height;
        var bufferBottom = viewport.Bottom + viewport.Height;
        var (firstBuffer, lastBuffer) = layout.GetVisiblePages(bufferTop, bufferBottom);
        var buffer = new List<int>();
        for (var page = firstBuffer; page <= lastBuffer; page++)
        {
            if (!visibleSet.Contains(page))
            {
                buffer.Add(page);
            }
        }

        foreach (var page in OrderByDistance(layout, buffer, centerY))
        {
            if (previewCount >= MaxPreviewRequests)
            {
                break;
            }

            plan.Add(Preview(layout, page, background: true));
            previewCount++;
        }

        return plan;
    }

    private static RenderRequest Preview(DocumentLayout layout, int page, bool background)
    {
        var size = layout.GetPageSize(page);
        var scale = TilePlanner.PreviewScale(size);
        var (width, height) = size.ToPixels(scale);
        return new RenderRequest(new TileKey(page, TileKey.PreviewScaleKey, 0, 0), scale, new PixelRect(0, 0, width, height), true, background);
    }

    /// <summary>Orders the pages by the distance of their vertical center to <paramref name="centerY"/>; equal distances keep the page order.</summary>
    private static IEnumerable<int> OrderByDistance(DocumentLayout layout, List<int> pages, double centerY) =>
        pages.OrderBy(page =>
        {
            var rect = layout.GetPageRect(page);
            return Math.Abs(rect.Y + (rect.Height / 2) - centerY);
        }).ThenBy(page => page);

    private static DipRect? Intersect(DipRect a, DipRect b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > left && bottom > top ? new DipRect(left, top, right - left, bottom - top) : null;
    }

    private static int ToPixel(double value) => (int)Math.Clamp(value, 0, int.MaxValue / 2);
}
