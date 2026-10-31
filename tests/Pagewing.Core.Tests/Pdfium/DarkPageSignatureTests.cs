using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Signatures;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// Visible signatures on the dark page (milestone 12, task 8). A drawn signature is an ink and keeps its colour like the
/// drawings of M10 (lightened to 3:1 where it is too dark). An image signature is an image PDFium leaves as it is, so dark ink
/// would vanish on the dark ground: the dark render paints its ink (the alpha-weighted mean colour, lightened to 3:1) with the
/// image's own alpha. Light ink is left alone, and a light page or a print reads nothing.
/// </summary>
public sealed class DarkPageSignatureTests : IDisposable
{
    private const double Scale = 2;

    private readonly TempFolder folder = new();

    private static RenderOptions Dark { get; } = new() { PageTheme = PageTheme.Dark };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static uint Ground => PageThemeColors.DarkBackground;

    public void Dispose() => folder.Dispose();

    /// <summary>A uniform image of 100 x 40 px: one ink colour, every pixel at <paramref name="alpha"/>.</summary>
    private static SignatureImage Uniform(byte r, byte g, byte b, byte alpha = 255)
    {
        var bgra = new byte[100 * 40 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = alpha;
        }

        return new SignatureImage(100, 40, bgra);
    }

    /// <summary>100 x 40 px: ink in the top left quarter only, the rest fully transparent.</summary>
    private static SignatureImage TopLeftQuarter(byte r, byte g, byte b)
    {
        var bgra = new byte[100 * 40 * 4];
        for (var y = 0; y < 20; y++)
        {
            for (var x = 0; x < 50; x++)
            {
                var o = ((y * 100) + x) * 4;
                bgra[o] = b;
                bgra[o + 1] = g;
                bgra[o + 2] = r;
                bgra[o + 3] = 255;
            }
        }

        return new SignatureImage(100, 40, bgra);
    }

    private string BlankPage(string extra = "") => WritePdf(folder, Guid.NewGuid().ToString("N")[..8] + "-blank.pdf",
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] {extra} >>",
    ]);

    private static uint Pixel(RenderedImage image, double xPoints, double yPoints)
    {
        var p = image.Pixels.Span.Slice(((int)(yPoints * Scale) * image.Stride) + ((int)(xPoints * Scale) * 4), 4);
        return 0xFF000000u | ((uint)p[2] << 16) | ((uint)p[1] << 8) | p[0];
    }

    private static double Contrast(uint a, uint b)
    {
        static double Luminance(uint argb)
        {
            static double Linear(uint channel)
            {
                var s = channel / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear((argb >> 16) & 0xFF)) + (0.7152 * Linear((argb >> 8) & 0xFF)) + (0.0722 * Linear(argb & 0xFF));
        }

        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static bool Near(uint a, uint b, int tolerance) =>
        Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) <= tolerance;

    private static uint Argb(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;

    /// <summary>Renders page 1 dark with the markup painting of the dark page off (as PDFium draws it) and on.</summary>
    private static async Task<(RenderedImage Pdfium, RenderedImage Painted)> RenderBothAsync(PdfDocument document, double scale = Scale)
    {
        await document.ReplaceDarkMarkupReaderAsync((_, _) => DarkMarkupPaint.Empty);
        var pdfium = await document.RenderPageAsync(0, scale, Dark, Ct);
        await document.ReplaceDarkMarkupReaderAsync(null);
        var painted = await document.RenderPageAsync(0, scale, Dark, Ct);
        return (pdfium, painted);
    }

    [Fact]
    public async Task BlackDrawnSignatureIsLightenedToThreeToOne()
    {
        var geometry = DrawingGeometry.Signature([[new(20, 60), new(150, 40), new(280, 60)]], 4);
        await using var document = await OpenAsync(BlankPage());
        await document.CreateDrawingAsync(0, geometry, new AnnotationColor(0, 0, 0), null, null, Ct);

        var (pdfium, painted) = await RenderBothAsync(document);

        var lightened = PdfiumDarkMarkups.LineColor(0xFF000000, Ground);
        var middle = Pixel(painted, 150, 40);
        Assert.Equal(lightened, middle);
        Assert.True(Contrast(middle, Ground) >= 3, $"{middle:X8} has {Contrast(middle, Ground):F2}:1");
        Assert.NotEqual(Pixel(pdfium, 150, 40), middle);
    }

    [Fact]
    public async Task BlueDrawnSignatureKeepsItsHue()
    {
        var blue = new AnnotationColor(30, 80, 220);
        await using var document = await OpenAsync(BlankPage());
        await document.CreateDrawingAsync(0, DrawingGeometry.Signature([[new(20, 60), new(150, 40), new(280, 60)]], 4), blue, null, null, Ct);

        var (_, painted) = await RenderBothAsync(document);

        var expected = PdfiumDarkMarkups.LineColor(Argb(30, 80, 220), Ground);
        var middle = Pixel(painted, 150, 40);
        Assert.Equal(expected, middle);
        Assert.True(((middle >> 16) & 0xFF) < (middle & 0xFF), $"The blue lost its hue: {middle:X8}");
    }

    [Fact]
    public async Task DarkInkImageIsVisibleAtThreeToOne()
    {
        await using var document = await OpenAsync(BlankPage());
        await document.CreateSignatureImageAsync(0, new PageRect(50, 50, 250, 130), Uniform(0, 0, 0), null, Ct);

        var (pdfium, painted) = await RenderBothAsync(document);

        // PDFium alone leaves the black ink on the dark ground (invisible); the painted page shows it at 3:1.
        var plain = Pixel(pdfium, 150, 90);
        Assert.True(Contrast(plain, Ground) < 1.5, $"PDFium's black ink has {Contrast(plain, Ground):F2}:1 on the dark ground");
        var ink = Pixel(painted, 150, 90);
        Assert.Equal(PdfiumDarkMarkups.LineColor(0xFF000000, Ground), ink);
        Assert.True(Contrast(ink, Ground) >= 3, $"{ink:X8} has {Contrast(ink, Ground):F2}:1");

        // Outside the signature nothing changes.
        Assert.Equal(Pixel(pdfium, 20, 20), Pixel(painted, 20, 20));
        Assert.Equal(Pixel(pdfium, 280, 180), Pixel(painted, 280, 180));
    }

    [Fact]
    public async Task DarkBlueInkImageKeepsItsHueAndTransparentPartsStayTheGround()
    {
        await using var document = await OpenAsync(BlankPage());
        await document.CreateSignatureImageAsync(0, new PageRect(50, 50, 250, 130), TopLeftQuarter(0, 0, 90), null, Ct);

        var (pdfium, painted) = await RenderBothAsync(document);

        var expected = PdfiumDarkMarkups.LineColor(Argb(0, 0, 90), Ground);
        var ink = Pixel(painted, 100, 70);
        Assert.Equal(expected, ink);
        Assert.True((ink & 0xFF) > ((ink >> 16) & 0xFF), $"The blue lost its hue: {ink:X8}");
        Assert.Equal(Pixel(pdfium, 200, 70), Pixel(painted, 200, 70));
        Assert.Equal(Pixel(pdfium, 100, 110), Pixel(painted, 100, 110));
        Assert.Equal(Ground, Pixel(painted, 200, 110));
    }

    [Fact]
    public async Task LightInkImageIsLeftToPdfium()
    {
        await using var document = await OpenAsync(BlankPage());
        await document.CreateSignatureImageAsync(0, new PageRect(50, 50, 250, 130), Uniform(255, 200, 40), null, Ct);

        var (pdfium, painted) = await RenderBothAsync(document);

        Assert.True(pdfium.Pixels.Span.SequenceEqual(painted.Pixels.Span), "A light ink needs no painting, yet the page changed.");
        Assert.Equal(Argb(255, 200, 40), Pixel(painted, 150, 90));
    }

    [Fact]
    public async Task SoftInkImageIsPaintedWithItsOwnAlpha()
    {
        await using var document = await OpenAsync(BlankPage());
        await document.CreateSignatureImageAsync(0, new PageRect(50, 50, 250, 130), Uniform(0, 0, 0, 128), null, Ct);

        var (pdfium, painted) = await RenderBothAsync(document);

        // Half transparent black: brighter than PDFium's (almost the ground), darker than the opaque lightened ink.
        var lightened = PdfiumDarkMarkups.LineColor(0xFF000000, Ground);
        var middle = Pixel(painted, 150, 90);
        Assert.True((middle & 0xFF) > (Pixel(pdfium, 150, 90) & 0xFF) + 20, $"{middle:X8} is not brighter than PDFium's {Pixel(pdfium, 150, 90):X8}");
        Assert.True((middle & 0xFF) < (lightened & 0xFF), $"{middle:X8} is not darker than {lightened:X8}");
    }

    [Fact]
    public async Task ImageOnARotatedPageIsPaintedWhereItIs()
    {
        // /Rotate 90: displayed 200 x 300. The ink is the top left quarter of the image on screen, whatever the page's rotation.
        await using var document = await OpenAsync(BlankPage("/Rotate 90"));
        await document.CreateSignatureImageAsync(0, new PageRect(20, 100, 180, 180), TopLeftQuarter(0, 0, 0), null, Ct);

        var (pdfium, painted) = await RenderBothAsync(document);

        var lightened = PdfiumDarkMarkups.LineColor(0xFF000000, Ground);
        Assert.Equal(lightened, Pixel(painted, 60, 120));
        Assert.Equal(Pixel(pdfium, 140, 120), Pixel(painted, 140, 120));
        Assert.Equal(Pixel(pdfium, 60, 170), Pixel(painted, 60, 170));
        Assert.Equal(Ground, Pixel(painted, 140, 170));
    }

    [Fact]
    public async Task TilesPaintTheSameAsTheWholePage()
    {
        await using var document = await OpenAsync(BlankPage());
        await document.CreateSignatureImageAsync(0, new PageRect(50, 50, 250, 130), TopLeftQuarter(0, 0, 0), null, Ct);
        var whole = await document.RenderPageAsync(0, 4, Dark, Ct);

        var tile = await document.RenderTileAsync(0, 4, new PixelRect(256, 160, 256, 160), Dark, Ct);

        for (var y = 0; y < tile.Height; y += 7)
        {
            for (var x = 0; x < tile.Width; x += 7)
            {
                var a = whole.Pixels.Span.Slice(((160 + y) * whole.Stride) + ((256 + x) * 4), 4);
                var b = tile.Pixels.Span.Slice((y * tile.Stride) + (x * 4), 4);
                Assert.True(a.SequenceEqual(b), $"The tile differs from the page at ({x}, {y}).");
            }
        }
    }

    [Fact]
    public async Task LightPageReadsNothing()
    {
        await using var document = await OpenAsync(BlankPage());
        await document.CreateSignatureImageAsync(0, new PageRect(50, 50, 250, 130), Uniform(0, 0, 0), null, Ct);
        var scans = document.DarkMarkupScans;

        var light = await document.RenderPageAsync(0, Scale, cancellationToken: Ct);
        await document.RenderTileAsync(0, Scale, new PixelRect(0, 0, 100, 100), cancellationToken: Ct);

        Assert.Equal(scans, document.DarkMarkupScans);
        Assert.Equal(Argb(0, 0, 0), Pixel(light, 150, 90));
        await document.RenderPageAsync(0, Scale, Dark, Ct);
        Assert.Equal(scans + 1, document.DarkMarkupScans);
    }

    [Fact]
    public async Task AForeignStampIsNotRepainted()
    {
        // The stamp with an image of another program stays as PDFium draws it, also on the dark page (display 20..100 x 70..110).
        var copy = Path.Combine(folder.Path, "stamps-copy.pdf");
        File.Copy(TestData.Pdf(StampsForeign), copy);
        await using var document = await OpenAsync(copy);

        var (pdfium, painted) = await RenderBothAsync(document);

        foreach (var (x, y) in new[] { (30.0, 80.0), (60.0, 90.0), (90.0, 100.0), (60.0, 35.0) })
        {
            Assert.Equal(Pixel(pdfium, x, y), Pixel(painted, x, y));
        }
    }
}
