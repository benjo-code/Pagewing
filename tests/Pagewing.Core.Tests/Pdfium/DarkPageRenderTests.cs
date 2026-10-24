using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

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

    [Fact]
    public async Task HighlightStaysAFilledAreaOnTheDarkPage()
    {
        // PDFium alone turns the filled quad of the highlight into an outline that it multiplies with the dark ground
        // (#1A1D23 on #1E2127, invisible); the dark page paints the area in the colour of the highlight instead.
        const double Scale = 4;
        var (plain, marked, quad) = await RenderMarkupAsync(AnnotationKind.Highlight, "Hello", Yellow, Scale);
        var tint = ToBgra(PdfiumDarkMarkups.FillTint(Rgb(Yellow), PageThemeColors.DarkBackground, PageThemeColors.DarkForeground));
        var inside = Pixels(quad, Scale, inset: 2).ToList();

        // Every pixel of the bare ground inside the quad has the tint now; the light text on it stays as it was.
        var ground = inside.Where(p => Pixel(plain, p.X, p.Y) == Ground).ToList();
        Assert.True(ground.Count > 500, $"only {ground.Count} ground pixels in the quad");
        Assert.All(ground, p => Assert.Equal(tint, Pixel(marked, p.X, p.Y)));
        var foreground = ToBgra(PageThemeColors.DarkForeground);
        // Away from the edge of the quad, where PDFium multiplied its outline into the letters as well.
        var text = Pixels(quad, Scale, inset: (int)Scale + 2).Where(p => Pixel(plain, p.X, p.Y) == foreground).ToList();
        Assert.True(text.Count > 50, "the word has no light pixels");
        Assert.All(text, p => Assert.Equal(foreground, Pixel(marked, p.X, p.Y)));

        // The smoothed edges of the letters lie between the tint and the text, never darker than the tint.
        Assert.All(inside, p => Assert.True(Pixel(marked, p.X, p.Y).G >= tint.G, $"({p.X}, {p.Y}) is {Pixel(marked, p.X, p.Y)}"));

        // The tint is the yellow of the highlight, translucent over the ground: its hue, brighter than the ground,
        // and the page text keeps at least 4.5:1 on it.
        Assert.InRange(Hue(tint) - Hue(ToBgra(Rgb(Yellow))), -6, 6);
        Assert.True(Contrast(tint, Ground) >= 2, $"the tint stands out only {Contrast(tint, Ground):F2}:1 from the ground");
        Assert.True(Contrast(ToBgra(PageThemeColors.DarkForeground), tint) >= 4.5);

        // Outside the quad nothing changed but the outline PDFium drew at its edge, which stays as dark as the ground.
        foreach (var (x, y) in Pixels(quad, Scale, inset: -6).Except(Pixels(quad, Scale, inset: -1)))
        {
            var before = Pixel(plain, x, y);
            var after = Pixel(marked, x, y);
            Assert.True(before == after || Math.Abs(after.G - Ground.G) <= 6, $"({x}, {y}): {before} became {after}");
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0 / 3)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task UnderlineAndStrikeOutKeepTheirColourOnTheDarkPage(double scale)
    {
        var ct = TestContext.Current.CancellationToken;
        var pink = AnnotationPalette.ColorOf(MarkupColor.Pink);
        var blue = AnnotationPalette.ColorOf(MarkupColor.Blue);
        await using var plainDocument = await OpenFixtureAsync("text-basic.pdf");
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        var hello = await document.CreateMarkupAsync(0, AnnotationKind.Underline, await WordRectsAsync(document, 0, "Hello"), blue, null, null, ct);
        var pagewing = await document.CreateMarkupAsync(0, AnnotationKind.StrikeOut, await WordRectsAsync(document, 0, "Pagewing"), pink, null, null, ct);
        var plain = await plainDocument.RenderPageAsync(0, scale, Dark, ct);
        var marked = await document.RenderPageAsync(0, scale, Dark, ct);

        // Where PDFium's own appearance puts the 1 pt line: 1 pt above the bottom of the quad, through its middle.
        var underline = Band(hello.QuadRects[0].Left, hello.QuadRects[0].Right, hello.QuadRects[0].Bottom - 1, scale);
        var strike = Band(pagewing.QuadRects[0].Left, pagewing.QuadRects[0].Right, (pagewing.QuadRects[0].Top + pagewing.QuadRects[0].Bottom) / 2, scale);

        // Both lines are drawn in the colour of their annotation, not in the light page foreground PDFium would use.
        var underlineColor = ToBgra(Rgb(blue));
        var strikeColor = ToBgra(Rgb(pink));
        Assert.All(underline.Interior, p => Assert.Equal(underlineColor, Pixel(marked, p.X, p.Y)));
        Assert.All(strike.Interior, p => Assert.Equal(strikeColor, Pixel(marked, p.X, p.Y)));

        // Nothing else changed: PDFium's light line under them is covered completely, no fringe of it is left.
        for (var y = 0; y < marked.Height; y++)
        {
            for (var x = 0; x < marked.Width; x++)
            {
                var after = Pixel(marked, x, y);
                if (after == Pixel(plain, x, y))
                {
                    continue;
                }

                var expected = underline.Contains(x, y) ? underlineColor : strike.Contains(x, y) ? strikeColor : (Bgra?)null;
                Assert.True(expected == after, $"({x}, {y}) at scale {scale}: {Pixel(plain, x, y)} became {after}");
            }
        }

        // Distinguishable: the underline lies below the middle of its word, the strike-out on it.
        Assert.True(underline.Top > (hello.QuadRects[0].Top + hello.QuadRects[0].Bottom) / 2 * scale);
        Assert.True(strike.Top < pagewing.QuadRects[0].Bottom * scale - 2);
    }

    [Fact]
    public async Task MarkupOnARotatedPageCoversPdfiumsLine()
    {
        // /Rotate 90: the line of PDFium's appearance runs along a side of the displayed quad, wherever that is.
        var ct = TestContext.Current.CancellationToken;
        const double Scale = 3;
        await using var plainDocument = await OpenFixtureAsync("rotated-page.pdf");
        await using var document = await OpenFixtureAsync("rotated-page.pdf");
        var light = await document.RenderPageAsync(0, Scale, cancellationToken: ct);
        await document.CreateMarkupAsync(0, AnnotationKind.Underline, await WordRectsAsync(document, 0, "Rotated"), Blue, null, null, ct);
        var lightMarked = await document.RenderPageAsync(0, Scale, cancellationToken: ct);
        var plain = await plainDocument.RenderPageAsync(0, Scale, Dark, ct);
        var marked = await document.RenderPageAsync(0, Scale, Dark, ct);
        var color = ToBgra(Rgb(Blue));

        // The pixels of the underline in the light page are exactly the ones painted in its colour on the dark page.
        var lineOnLight = AllPoints(light).Where(p => Pixel(light, p.X, p.Y) != Pixel(lightMarked, p.X, p.Y)).ToHashSet();
        var changedOnDark = AllPoints(marked).Where(p => Pixel(plain, p.X, p.Y) != Pixel(marked, p.X, p.Y)).ToHashSet();
        Assert.True(lineOnLight.Count > 20, "no underline on the light page");
        Assert.All(changedOnDark, p => Assert.Equal(color, Pixel(marked, p.X, p.Y)));
        Assert.Subset(changedOnDark, lineOnLight.Where(p => Pixel(plain, p.X, p.Y) != color).ToHashSet());
    }

    [Fact]
    public async Task TilesWithMarkupsMatchTheWholePageRender()
    {
        const double Scale = 4;
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        await document.CreateMarkupAsync(0, AnnotationKind.Highlight, await WordRectsAsync(document, 0, "Hello"), Yellow, null, null, ct);
        await document.CreateMarkupAsync(0, AnnotationKind.StrikeOut, await WordRectsAsync(document, 0, "Pagewing"), Blue, null, null, ct);
        var full = await document.RenderPageAsync(0, Scale, Dark, ct);

        // Tiles that cut through both markups: each paints its part, and together they are the page.
        const int TileSize = 100;
        for (var y = 0; y < full.Height; y += TileSize)
        {
            for (var x = 0; x < full.Width; x += TileSize)
            {
                var rect = new PixelRect(x, y, Math.Min(TileSize, full.Width - x), Math.Min(TileSize, full.Height - y));
                var tile = await document.RenderTileAsync(0, Scale, rect, Dark, ct);
                for (var row = 0; row < tile.Height; row++)
                {
                    for (var column = 0; column < tile.Width; column++)
                    {
                        // PDFium itself draws the edge of an annotation in a tile up to one level apart from the whole page
                        // (seen without painting as well); the painted markups add nothing to that.
                        var (expected, actual) = (Pixel(full, x + column, y + row), Pixel(tile, column, row));
                        Assert.True(
                            Math.Abs(expected.B - actual.B) <= 2 && Math.Abs(expected.G - actual.G) <= 2 && Math.Abs(expected.R - actual.R) <= 2,
                            $"({x + column}, {y + row}): page {expected}, tile {actual}");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task NothingIsReadInLightMode()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        await using var plainDocument = await OpenFixtureAsync("text-basic.pdf");
        await document.CreateMarkupAsync(0, AnnotationKind.Highlight, await WordRectsAsync(document, 0, "Hello"), Yellow, null, null, ct);
        var scansAfterCreate = document.DarkMarkupScans;

        // Light: the page as PDFium draws it, and the annotations are not looked at for the dark page.
        var light = await document.RenderPageAsync(0, 2, cancellationToken: ct);
        await document.RenderTileAsync(0, 2, new PixelRect(0, 0, 100, 100), cancellationToken: ct);
        Assert.Equal(scansAfterCreate, document.DarkMarkupScans);
        var plainLight = await plainDocument.RenderPageAsync(0, 2, cancellationToken: ct);
        Assert.False(light.Pixels.Span.SequenceEqual(plainLight.Pixels.Span));

        // Dark without annotations: nothing is read or painted either.
        var bare = await document.RenderPageAsync(0, 2, Dark with { Annotations = false }, ct);
        Assert.Equal(scansAfterCreate, document.DarkMarkupScans);
        var plainBare = await plainDocument.RenderPageAsync(0, 2, Dark with { Annotations = false }, ct);
        Assert.True(bare.Pixels.Span.SequenceEqual(plainBare.Pixels.Span));

        // Dark: one read per render.
        await document.RenderPageAsync(0, 2, Dark, ct);
        Assert.Equal(scansAfterCreate + 1, document.DarkMarkupScans);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(4 | 2, false)]
    [InlineData(4 | 32, false)]
    public async Task HiddenMarkupsAreNotPainted(int flags, bool painted)
    {
        // A page without content and one highlight without appearance: hidden (F bit 2) or not viewed (bit 6), PDFium does
        // not draw it, and the dark page does not paint it either.
        using var folder = new TempFolder();
        var path = WritePdf(folder, "hidden.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Highlight /Rect [20 20 180 80] /F {flags} /C [1 0.8 0.2] /QuadPoints [20 80 180 80 20 20 180 20] >>",
        ]);
        await using var document = await OpenAsync(path);
        var image = await document.RenderPageAsync(0, 1, Dark, TestContext.Current.CancellationToken);

        var tint = ToBgra(PdfiumDarkMarkups.FillTint(0xFFFFCC33, PageThemeColors.DarkBackground, PageThemeColors.DarkForeground));
        Assert.Equal(painted ? tint : Ground, Pixel(image, 100, 50));
    }

    [Fact]
    public void OverlayMeetsContrastOnTheDarkPage()
    {
        // Lines: at least 3:1 against the dark ground (WCAG 2.2, non-text contrast), a colour too dark is lightened towards
        // white and keeps its hue. Areas: the page text keeps at least 4.5:1 on the tint (as on search.hitOnDark).
        uint[] colors =
        [
            .. AnnotationPalette.All.Select(c => Rgb(AnnotationPalette.ColorOf(c))),
            0xFF000000, 0xFF000080, 0xFF800000, 0xFF1E2127, 0xFFFFFFFF, 0xFFFFFF00, 0xFF00FF00, 0xFF2010A0,
        ];
        var ground = ToBgra(PageThemeColors.DarkBackground);
        var text = ToBgra(PageThemeColors.DarkForeground);
        foreach (var color in colors)
        {
            var line = ToBgra(PdfiumDarkMarkups.LineColor(color, PageThemeColors.DarkBackground));
            Assert.True(Contrast(line, ground) >= 3, $"line {color:X8} -> {line}: {Contrast(line, ground):F2}:1");
            var tint = ToBgra(PdfiumDarkMarkups.FillTint(color, PageThemeColors.DarkBackground, PageThemeColors.DarkForeground));
            Assert.True(Contrast(text, tint) >= 4.5, $"text on the tint of {color:X8}: {Contrast(text, tint):F2}:1");
            Assert.NotEqual(ground, tint);
            if (Saturation(ToBgra(color)) > 0.3)
            {
                Assert.InRange(Math.Abs(Hue(line) - Hue(ToBgra(color))), 0, 8);
            }
        }

        // The colours of the palette are light enough as they are: their lines keep exactly the colour of the annotation.
        Assert.All(AnnotationPalette.All, c => Assert.Equal(Rgb(AnnotationPalette.ColorOf(c)), PdfiumDarkMarkups.LineColor(Rgb(AnnotationPalette.ColorOf(c)), PageThemeColors.DarkBackground)));
    }

    /// <summary>Renders text-basic.pdf dark without and with one markup over <paramref name="word"/>.</summary>
    private static async Task<(RenderedImage Plain, RenderedImage Marked, PageRect Quad)> RenderMarkupAsync(AnnotationKind kind, string word, AnnotationColor color, double scale)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var plainDocument = await OpenFixtureAsync("text-basic.pdf");
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        var markup = await document.CreateMarkupAsync(0, kind, await WordRectsAsync(document, 0, word), color, null, null, ct);
        return (await plainDocument.RenderPageAsync(0, scale, Dark, ct), await document.RenderPageAsync(0, scale, Dark, ct), Assert.Single(markup.QuadRects));
    }

    /// <summary>The pixels whose centre lies in <paramref name="rect"/> (points) shrunk by <paramref name="inset"/> pixels.</summary>
    private static IEnumerable<(int X, int Y)> Pixels(PageRect rect, double scale, int inset)
    {
        for (var y = (int)Math.Floor(rect.Top * scale) + inset; y < (int)Math.Ceiling(rect.Bottom * scale) - inset; y++)
        {
            for (var x = (int)Math.Floor(rect.Left * scale) + inset; x < (int)Math.Ceiling(rect.Right * scale) - inset; x++)
            {
                yield return (x, y);
            }
        }
    }

    private static IEnumerable<(int X, int Y)> AllPoints(RenderedImage image)
    {
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                yield return (x, y);
            }
        }
    }

    /// <summary>The pixels a horizontal 1 pt line centred on <paramref name="centre"/> touches (at least one pixel high).</summary>
    private static PixelBand Band(double left, double right, double centre, double scale)
    {
        var half = Math.Max(0.5 * scale, 0.5);
        return new PixelBand(
            (int)Math.Floor(left * scale),
            (int)Math.Floor((centre * scale) - half),
            (int)Math.Ceiling(right * scale),
            (int)Math.Ceiling((centre * scale) + half));
    }

    private static uint Rgb(AnnotationColor color) => 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static Bgra ToBgra(uint argb) => new((byte)argb, (byte)(argb >> 8), (byte)(argb >> 16), 0xFF);

    private static double Luminance(Bgra c)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static double Contrast(Bgra a, Bgra b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Hue(Bgra c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var delta = max - Math.Min(r, Math.Min(g, b));
        if (delta == 0)
        {
            return 0;
        }

        var hue = max == r ? 60 * (((g - b) / delta) % 6) : max == g ? 60 * (((b - r) / delta) + 2) : 60 * (((r - g) / delta) + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    private static double Saturation(Bgra c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B));
        return max == 0 ? 0 : (max - Math.Min(c.R, Math.Min(c.G, c.B))) / (double)max;
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

    /// <summary>A rectangle of whole pixels (right and bottom exclusive).</summary>
    private readonly record struct PixelBand(int Left, int Top, int Right, int Bottom)
    {
        /// <summary>The pixels of the band without its first and last column and row, which a line may touch only partly.</summary>
        public IEnumerable<(int X, int Y)> Interior
        {
            get
            {
                for (var y = Top + 1; y < Bottom - 1; y++)
                {
                    for (var x = Left + 1; x < Right - 1; x++)
                    {
                        yield return (x, y);
                    }
                }
            }
        }

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }
}
