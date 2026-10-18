using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pagewing.Core.Pdf;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// The dark page (<see cref="PageTheme.Dark"/>) on <c>dark-sample.pdf</c> (A4, 595 × 842 pt): heading and body text at
/// the top, a table with grey fills, a bar chart of four coloured rectangles, an RGB gradient image and a grey image.
/// At scale 1 a pixel is a point, measured from the top left.
/// </summary>
public sealed class DarkPageRenderTests
{
    // Blue, green, red, unused (BGRx, opaque).
    private static readonly Bgra Ground = new(0x27, 0x21, 0x1E, 0xFF);

    private readonly PdfEngine engine = new();

    private static RenderOptions Dark { get; } = new() { PageTheme = PageTheme.Dark };

    [Fact]
    public void ThePageColoursAreTheDecidedOnes()
    {
        Assert.Equal(0xFF1E2127u, PageThemeColors.DarkBackground);
        Assert.Equal(0xFFE3E5E8u, PageThemeColors.DarkForeground);
        Assert.Equal(PageTheme.Light, RenderOptions.Default.PageTheme);
    }

    [Fact]
    public void TheColourSchemeIsFourUnsignedLongs()
    {
        // FPDF_COLORSCHEME holds four FPDF_DWORD (unsigned long): 16 bytes on Windows, 32 on 64-bit Linux.
        Assert.Equal(4 * Unsafe.SizeOf<CULong>(), Unsafe.SizeOf<FpdfColorScheme>());
        Assert.Equal(Unsafe.SizeOf<CULong>() * 3, (int)Marshal.OffsetOf<FpdfColorScheme>(nameof(FpdfColorScheme.TextStrokeColor)));
    }

    [Fact]
    public async Task DarkThemeFillsThePageWithTheDarkBackground()
    {
        var image = await RenderAsync(Dark);

        Assert.Equal((595, 842), (image.Width, image.Height));
        Assert.Equal(Ground, Pixel(image, 5, 5));
        Assert.Equal(Ground, Pixel(image, 590, 837));
        Assert.Equal(Ground, Pixel(image, 560, 300));
        Assert.All(AllPixels(image), pixel => Assert.Equal(0xFF, pixel.A));
    }

    [Fact]
    public async Task DarkThemeDrawsTextLight()
    {
        var image = await RenderAsync(Dark);

        // The heading (22 pt bold, baseline at y 62) and two lines of body text (11 pt, baselines at y 92 and 106).
        var field = Region(image, 50, 40, 510, 70).ToList();
        Assert.Contains(field, pixel => Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)) >= 0xC0);

        // Black text would be darker than the ground; light text only blends between the ground and the foreground.
        Assert.DoesNotContain(field, pixel => pixel.B < Ground.B - 2 || pixel.G < Ground.G - 2 || pixel.R < Ground.R - 2);
    }

    [Fact]
    public async Task FilledPathsBecomeOutlinesOnTheDarkGround()
    {
        var image = await RenderAsync(Dark);

        // The red bar spans x 300..340 and y 232..322: its inside is the ground, its edge is drawn light.
        Assert.Equal(Ground, Pixel(image, 320, 277));
        Assert.Contains(Region(image, 297, 277, 7, 1), pixel => Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) >= 0x80);
    }

    [Fact]
    public async Task ImagesKeepTheirColoursInTheDarkTheme()
    {
        var light = await RenderAsync(RenderOptions.Default);
        var dark = await RenderAsync(Dark);

        // The centre of the RGB gradient image (x 50..178, y 384..512) and of the grey image (x 220..348).
        foreach (var (x, y) in new[] { (114, 448), (284, 448), (60, 394), (170, 504) })
        {
            var expected = Pixel(light, x, y);
            var actual = Pixel(dark, x, y);
            Assert.True(
                Math.Abs(expected.B - actual.B) <= 2 && Math.Abs(expected.G - actual.G) <= 2 && Math.Abs(expected.R - actual.R) <= 2,
                $"({x}, {y}): light {expected}, dark {actual}");
        }
    }

    [Fact]
    public async Task LightThemeIsUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("dark-sample.pdf"), cancellationToken: ct);

        var byDefault = await document.RenderPageAsync(0, 1, cancellationToken: ct);
        var light = await document.RenderPageAsync(0, 1, new RenderOptions { PageTheme = PageTheme.Light }, ct);
        var dark = await document.RenderPageAsync(0, 1, Dark, ct);
        var tile = await document.RenderTileAsync(0, 1, new PixelRect(100, 50, 300, 200), new RenderOptions { PageTheme = PageTheme.Light }, ct);
        var defaultTile = await document.RenderTileAsync(0, 1, new PixelRect(100, 50, 300, 200), cancellationToken: ct);

        Assert.True(byDefault.Pixels.Span.SequenceEqual(light.Pixels.Span), "The light theme differs from the default rendering.");
        Assert.True(defaultTile.Pixels.Span.SequenceEqual(tile.Pixels.Span), "A light tile differs from the default tile.");
        Assert.Equal(new Bgra(0xFF, 0xFF, 0xFF, 0xFF), Pixel(light, 5, 5));
        Assert.False(light.Pixels.Span.SequenceEqual(dark.Pixels.Span));

        // Black text on white, as before.
        Assert.Contains(Region(light, 50, 40, 510, 70), pixel => Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) < 0x40);
    }

    [Fact]
    public async Task TilesOfTheDarkThemeMatchTheWholePageRender()
    {
        // Same check as RenderTests.TilesStitchToTheFullPage: the tiles stitched together are the whole page.
        const int TileSize = 256;
        const double Scale = 1.5;
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("dark-sample.pdf"), cancellationToken: ct);
        var full = await document.RenderPageAsync(0, Scale, Dark, ct);

        var stitched = new byte[full.Pixels.Length];
        var tileCount = 0;
        for (var y = 0; y < full.Height; y += TileSize)
        {
            for (var x = 0; x < full.Width; x += TileSize)
            {
                var rect = new PixelRect(x, y, Math.Min(TileSize, full.Width - x), Math.Min(TileSize, full.Height - y));
                var tile = await document.RenderTileAsync(0, Scale, rect, Dark, ct);
                Assert.Equal((rect.Width, rect.Height), (tile.Width, tile.Height));
                for (var row = 0; row < tile.Height; row++)
                {
                    tile.Pixels.Span.Slice(row * tile.Stride, tile.Width * 4)
                        .CopyTo(stitched.AsSpan(((y + row) * full.Stride) + (x * 4)));
                }

                tileCount++;
            }
        }

        Assert.Equal(20, tileCount);
        Assert.True(full.Pixels.Span.SequenceEqual(stitched), DescribeDifference(full, stitched));
    }

    private static string DescribeDifference(RenderedImage full, byte[] stitched)
    {
        var expected = full.Pixels.Span;
        var count = 0;
        var maxDelta = 0;
        var first = -1;
        for (var i = 0; i < expected.Length; i++)
        {
            var delta = Math.Abs(expected[i] - stitched[i]);
            if (delta != 0)
            {
                count++;
                maxDelta = Math.Max(maxDelta, delta);
                first = first < 0 ? i : first;
            }
        }

        return first < 0
            ? "identical"
            : $"{count} bytes differ, max delta {maxDelta}, first at pixel ({first % full.Stride / 4}, {first / full.Stride}).";
    }

    private async Task<RenderedImage> RenderAsync(RenderOptions options)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("dark-sample.pdf"), cancellationToken: ct);
        return await document.RenderPageAsync(0, 1, options, ct);
    }

    private static Bgra Pixel(RenderedImage image, int x, int y)
    {
        var p = image.Pixels.Span.Slice((y * image.Stride) + (x * 4), 4);
        return new Bgra(p[0], p[1], p[2], p[3]);
    }

    private static IEnumerable<Bgra> Region(RenderedImage image, int left, int top, int width, int height)
    {
        for (var y = top; y < top + height; y++)
        {
            for (var x = left; x < left + width; x++)
            {
                yield return Pixel(image, x, y);
            }
        }
    }

    private static IEnumerable<Bgra> AllPixels(RenderedImage image) => Region(image, 0, 0, image.Width, image.Height);

    private readonly record struct Bgra(byte B, byte G, byte R, byte A);
}
