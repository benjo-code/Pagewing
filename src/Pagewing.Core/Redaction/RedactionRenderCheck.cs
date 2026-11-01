using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>The rectangles of one page that were redacted (what the render check leaves out of its comparison).</summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="Areas">The rectangles in points from the top left of the displayed page.</param>
public sealed record RedactionPageAreas(int Page, IReadOnlyList<PageRect> Areas)
{
    /// <summary>
    /// The areas the render check leaves out for a page that was redacted: the rectangles of the marks and the places where
    /// <see cref="PageRedactionImpact.RemovedAreas"/> says that content was removed besides them.
    /// </summary>
    /// <param name="marks">The rectangles the page was redacted with.</param>
    /// <param name="impact">What redacting the page did (<see cref="PdfDocument.RedactPageAsync"/>).</param>
    public static RedactionPageAreas For(IReadOnlyList<PageRect> marks, PageRedactionImpact impact)
    {
        ArgumentNullException.ThrowIfNull(marks);
        ArgumentNullException.ThrowIfNull(impact);
        return new RedactionPageAreas(impact.Page, [.. marks, .. impact.RemovedAreas ?? []]);
    }
}

/// <summary>
/// The pixels of an image object that lie under a rectangle of a redacted page. A size of 0 (and no pixels) stands for an image that could
/// not be read; an image that is turned or sheared comes whole, because it cannot be cut at the rectangle.
/// </summary>
/// <param name="Width">The number of columns.</param>
/// <param name="Height">The number of rows.</param>
/// <param name="Bgra">The pixels, four bytes each (blue, green, red, alpha), top row first.</param>
public sealed record ImageAreaPixels(int Width, int Height, byte[] Bgra);

/// <summary>Hooks for tests of the render check (the real work never sets them).</summary>
internal sealed class RedactionVerifySeam
{
    /// <summary>Gets or sets the hook called with the path of the temporary copy once it is written.</summary>
    public Action<string>? WrittenCopy { get; set; }

    /// <summary>Gets or sets the hook called with the document that is about to be rendered.</summary>
    public Action<PdfDocument>? Rendering { get; set; }
}

/// <summary>How two renders of a page compare outside the redacted rectangles.</summary>
/// <param name="Different">The number of pixels that differ by more than <see cref="RedactionRenderCheck.ChannelTolerance"/> in a channel.</param>
/// <param name="Box">The rectangle around those pixels in points from the top left, or <see langword="null"/> when there are none.</param>
/// <param name="SizeDiffers">Whether the two images have different sizes (then nothing was compared).</param>
public readonly record struct RenderCheckResult(int Different, PageRect? Box, bool SizeDiffers)
{
    /// <summary>Gets whether the page passes: same size and no more differing pixels than <see cref="RedactionRenderCheck.MaxDifferentPixels"/>.</summary>
    public bool Passes => !SizeDiffers && Different <= RedactionRenderCheck.MaxDifferentPixels;
}

/// <summary>
/// The render check of a redacted page (milestone 14): PDFium writes a page again from its objects after any change and does not write
/// everything it reads (patterns, shadings, Type 3 text, inline images, a clip path or marked content of a text that was split ...), so every
/// changed page is rendered from the freshly written copy and compared with the original outside the rectangles plus a margin.
/// </summary>
public static class RedactionRenderCheck
{
    /// <summary>The render scale of the check (pixels per point): 2 is 144 dpi.</summary>
    public const double Scale = 2;

    /// <summary>The largest difference of one colour channel that still counts as equal.</summary>
    public const int ChannelTolerance = 16;

    /// <summary>The margin, in points, around the rectangles that the comparison leaves out (the area is drawn over the rectangle, and PDFium rounds to whole pixels).</summary>
    public const double MarginPoints = 1.5;

    /// <summary>The number of pixels that may differ outside the rectangles (none).</summary>
    public const int MaxDifferentPixels = 0;

    /// <summary>The most pixels of one rendered page (a larger page is rendered at a smaller scale so that the check stays within memory).</summary>
    public const long MaxPixels = 36_000_000;

    /// <summary>The scale to render a page of this size at: <see cref="Scale"/>, or less for a very large page.</summary>
    public static double ScaleFor(PdfPageSize size)
    {
        var pixels = size.Width * size.Height * Scale * Scale;
        return pixels <= MaxPixels ? Scale : Math.Max(0.1, Scale * Math.Sqrt(MaxPixels / pixels));
    }

    /// <summary>Compares two renders of the same page (at <paramref name="scale"/> pixels per point) outside the rectangles grown by <see cref="MarginPoints"/>.</summary>
    /// <param name="original">The render of the original page.</param>
    /// <param name="result">The render of the page as written.</param>
    /// <param name="scale">The scale both were rendered at.</param>
    /// <param name="areas">The redacted rectangles in points from the top left.</param>
    public static RenderCheckResult Compare(RenderedImage original, RenderedImage result, double scale, IReadOnlyList<PageRect> areas)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(areas);
        if (original.Width != result.Width || original.Height != result.Height)
        {
            return new RenderCheckResult(0, null, SizeDiffers: true);
        }

        var width = original.Width;
        var height = original.Height;

        // A difference array over the pixels counts how many grown rectangles cover each one.
        var cover = new short[(long)(width + 1) * (height + 1)];
        foreach (var area in areas)
        {
            var left = (int)Math.Clamp(Math.Floor((area.Left - MarginPoints) * scale), 0, width);
            var right = (int)Math.Clamp(Math.Ceiling((area.Right + MarginPoints) * scale), 0, width);
            var top = (int)Math.Clamp(Math.Floor((area.Top - MarginPoints) * scale), 0, height);
            var bottom = (int)Math.Clamp(Math.Ceiling((area.Bottom + MarginPoints) * scale), 0, height);
            if (right <= left || bottom <= top)
            {
                continue;
            }

            cover[(top * (width + 1)) + left]++;
            cover[(top * (width + 1)) + right]--;
            cover[(bottom * (width + 1)) + left]--;
            cover[(bottom * (width + 1)) + right]++;
        }

        var a = original.Pixels.Span;
        var b = result.Pixels.Span;
        var column = new int[width + 1];
        var different = 0;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var running = 0;
            for (var x = 0; x < width; x++)
            {
                column[x] += cover[(y * (width + 1)) + x];
                running += column[x];
                if (running > 0)
                {
                    continue;
                }

                var ia = (y * original.Stride) + (x * 4);
                var ib = (y * result.Stride) + (x * 4);
                if (Math.Abs(a[ia] - b[ib]) > ChannelTolerance || Math.Abs(a[ia + 1] - b[ib + 1]) > ChannelTolerance || Math.Abs(a[ia + 2] - b[ib + 2]) > ChannelTolerance)
                {
                    different++;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        PageRect? box = different == 0 ? null : new PageRect(minX / scale, minY / scale, (maxX + 1) / scale, (maxY + 1) / scale);
        return new RenderCheckResult(different, box, SizeDiffers: false);
    }
}
