using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// Pages that become images (milestone 14.1): what the analysis tells about them, and how the rebuilt document gets them. A raster page
/// is never redacted on the working copy (the pass skips it, so it is never refused as <see cref="RedactionRefusalKind.PageNotRedactable"/>):
/// the page is rendered from the UNCHANGED original (light, annotations and form fields as they stand on the page, no highlight), the
/// rectangles of its marks are painted into the bitmap, and the bitmap becomes the one object of a new page of the displayed size.
/// </summary>
public sealed partial class RedactionExecutor
{
    /// <summary>
    /// Reads what each raster page of the plan costs, from the original page (size, text, form fields) and the working copy (the count of
    /// annotations; a raster page is never changed there), and refuses a page that cannot be rasterized within the limits. One short job per read.
    /// </summary>
    private static async Task<(IReadOnlyList<RasterPageInfo> Infos, IReadOnlyList<RedactionRefusal> Refusals)> RasterInfosAsync(
        RedactionPlan plan,
        PdfDocument original,
        PdfDocument working,
        CancellationToken cancellationToken)
    {
        var infos = new List<RasterPageInfo>(plan.RasterPages.Count);
        var refusals = new List<RedactionRefusal>();
        foreach (var page in plan.RasterPages.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = await original.GetPageSizeAsync(page, cancellationToken).ConfigureAwait(false);
            RasterSize resolved;
            try
            {
                resolved = RasterGeometry.Resolve(size.Width, size.Height, plan.RasterDpi);
            }
            catch (RedactionException ex)
            {
                refusals.Add(new RedactionRefusal(RedactionRefusalKind.PageNotRedactable, [page], ex.Message));
                continue;
            }

            var summary = await working.GetPageObjectSummaryAsync(page, cancellationToken).ConfigureAwait(false);
            var text = await original.GetPageTextAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
            var fields = await original.GetFormFieldsAsync(page, cancellationToken).ConfigureAwait(false);
            infos.Add(new RasterPageInfo(
                page,
                resolved.Dpi,
                resolved.WidthPx,
                resolved.HeightPx,
                resolved.Lowered,
                (long)resolved.WidthPx * resolved.HeightPx * 3,
                summary.Annotations,
                fields.Count,
                text.Links.Count,
                text.HasText));
        }

        return (infos, refusals);
    }

    /// <summary>
    /// The new document of a build: the pages of the working copy in their order, except that every raster page is the image of the
    /// original page. Runs of normal pages are imported together; a raster page is rendered, painted and added in its place.
    /// </summary>
    private async Task<PdfDocument> BuildCleanAsync(RedactionPlan plan, PdfDocument original, PdfDocument working, Action<double>? progress, CancellationToken cancellationToken)
    {
        var clean = await engine.CreateEmptyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var run = new List<int>();
            var destination = 0;
            var rasterDone = 0;
            for (var page = 0; page < working.PageCount; page++)
            {
                if (!plan.RasterPages.Contains(page))
                {
                    run.Add(page);
                    continue;
                }

                destination = await ImportRunAsync(clean, working, run, destination, cancellationToken).ConfigureAwait(false);
                var raster = await RenderRasterPageAsync(plan, original, page, cancellationToken).ConfigureAwait(false);
                await clean.AddRasterPageAsync(destination++, raster, cancellationToken).ConfigureAwait(false);
                rasterDone++;
                progress?.Invoke(rasterDone / (double)plan.RasterPages.Count);
            }

            await ImportRunAsync(clean, working, run, destination, cancellationToken).ConfigureAwait(false);
            return clean;
        }
        catch
        {
            await RedactionBuild.CloseQuietlyAsync(clean).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<int> ImportRunAsync(PdfDocument clean, PdfDocument working, List<int> run, int destination, CancellationToken cancellationToken)
    {
        if (run.Count == 0)
        {
            return destination;
        }

        await clean.ImportPagesAsync(working, [.. run], destination, cancellationToken).ConfigureAwait(false);
        var next = destination + run.Count;
        run.Clear();
        return next;
    }

    /// <summary>
    /// Renders a page of the unchanged original as the user sees it (after rotation and crop box) and paints the rectangles of its marks
    /// into the pixels. The page of the result has this displayed size and no rotation.
    /// </summary>
    private static async Task<RasterPage> RenderRasterPageAsync(RedactionPlan plan, PdfDocument original, int page, CancellationToken cancellationToken)
    {
        var size = await original.GetPageSizeAsync(page, cancellationToken).ConfigureAwait(false);
        var resolved = RasterGeometry.Resolve(size.Width, size.Height, plan.RasterDpi);
        var options = new RenderOptions { Annotations = true, Priority = PdfPriority.Background };
        byte[] pixels;
        int width;
        int height;
        try
        {
            var image = await original.RenderPageAsync(page, resolved.Dpi / 72.0, options, cancellationToken).ConfigureAwait(false);
            width = image.Width;
            height = image.Height;
            pixels = RasterPainter.Tighten(image);
        }
        catch (Exception ex) when (ex is PdfRenderException or InvalidDataException)
        {
            // A page that cannot be rendered cannot become an image, and a result without it cannot be checked: never "fine".
            throw new SaveFailureException(SaveFailureReason.RedactionFailed, "A page could not be turned into an image.", ex)
            {
                Details = new RedactionCheck(
                    [new RedactionFinding(RedactionFindingKind.UnreadableStream, page, "The page could not be rendered, so it cannot be turned into an image.")],
                    []),
            };
        }

        RasterPainter.Fill(pixels, width, height, width / size.Width, height / size.Height, plan.AreasOn(page), plan.Fill);
        return new RasterPage(size.Width, size.Height, 0, width, height, pixels);
    }
}

/// <summary>Paints the marked areas into the pixels of a raster page.</summary>
internal static class RasterPainter
{
    /// <summary>Copies a rendered image into a buffer of exactly <c>width * height * 4</c> bytes, row after row without the padding of the stride.</summary>
    /// <param name="image">The rendered page.</param>
    /// <returns>The pixels, blue, green, red, alpha.</returns>
    public static byte[] Tighten(RenderedImage image)
    {
        var row = image.Width * 4;
        var bytes = new byte[(long)row * image.Height];
        var source = image.Pixels.Span;
        for (var y = 0; y < image.Height; y++)
        {
            source.Slice(y * image.Stride, row).CopyTo(bytes.AsSpan(y * row, row));
        }

        return bytes;
    }

    /// <summary>
    /// Fills the rectangles, given in points from the top left of the page, with the colour. Each rectangle is rounded outward to whole
    /// pixels, there is no smoothing and nothing is blended; parts outside the bitmap are clipped.
    /// </summary>
    /// <param name="bgra">The pixels (<c>width * height * 4</c> bytes, blue, green, red, alpha), changed in place.</param>
    /// <param name="width">The width in pixels.</param>
    /// <param name="height">The height in pixels.</param>
    /// <param name="pixelsPerPointX">How many pixels a point is wide.</param>
    /// <param name="pixelsPerPointY">How many pixels a point is high.</param>
    /// <param name="areas">The rectangles.</param>
    /// <param name="fill">The colour; the alpha is 255.</param>
    public static void Fill(byte[] bgra, int width, int height, double pixelsPerPointX, double pixelsPerPointY, IReadOnlyList<PageRect> areas, RedactionFill fill)
    {
        var value = fill.Blue | ((uint)fill.Green << 8) | ((uint)fill.Red << 16) | (0xFFu << 24);
        if (!BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }

        var pixels = MemoryMarshal.Cast<byte, uint>(bgra.AsSpan());
        foreach (var area in areas)
        {
            var left = Math.Clamp((int)Math.Floor(area.Left * pixelsPerPointX), 0, width);
            var right = Math.Clamp((int)Math.Ceiling(area.Right * pixelsPerPointX), 0, width);
            var top = Math.Clamp((int)Math.Floor(area.Top * pixelsPerPointY), 0, height);
            var bottom = Math.Clamp((int)Math.Ceiling(area.Bottom * pixelsPerPointY), 0, height);
            if (right <= left)
            {
                continue;
            }

            for (var y = top; y < bottom; y++)
            {
                pixels.Slice((y * width) + left, right - left).Fill(value);
            }
        }
    }
}
