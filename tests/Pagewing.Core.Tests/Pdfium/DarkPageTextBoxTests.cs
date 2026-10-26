using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// Text boxes on the dark page (milestone 10). Measured: PDFium's colour scheme with <c>FPDF_CONVERT_FILL_TO_STROKE</c> turns
/// the filled glyphs of a text box into their outlines in the page foreground, hollow and without the colour. The dark render
/// paints Pagewing's text boxes from their own appearance: the outline takes the text colour and the glyphs are filled again,
/// in the colour lightened to the contrast of text. Each check compares three renders: without the text box, as PDFium alone
/// draws it dark, and painted.
/// </summary>
public sealed class DarkPageTextBoxTests : IDisposable
{
    private static readonly AnnotationColor TextBlue = new(30, 80, 220);

    private static readonly PageRect Box = new(20, 20, 280, 70);

    private readonly TempFolder folder = new();

    public DarkPageTextBoxTests() => UseTextBoxFont();

    private static RenderOptions Dark { get; } = new() { PageTheme = PageTheme.Dark };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task TextBoxKeepsItsColourAndIsSolidOnTheDarkPage(double scale)
    {
        var (plain, pdfium, painted) = await RenderAsync(BlankPage(), scale, new TextBoxSpec(Box, "Hello Wörld 5 €", 24, TextBlue));
        var color = PdfiumDarkMarkups.TextColor(Rgb(TextBlue), PageThemeColors.DarkBackground);

        // PDFium alone: the glyphs are outlines in the foreground, no pixel has the text colour.
        var outline = Count(pdfium, scale, (x, y) => Pixel(pdfium, x, y) != Pixel(plain, x, y));
        Assert.True(outline > 100 * scale, $"PDFium drew only {outline} pixels of the text at scale {scale}.");
        Assert.Equal(0, Count(pdfium, scale, (x, y) => Near(Pixel(pdfium, x, y), color, 24)));

        // Painted: solid glyphs in the colour (at least as many tinted pixels as the outline had, and pixels fully in the
        // colour), no pixel left in the light foreground.
        var tinted = Count(painted, scale, (x, y) => IsBlue(Pixel(painted, x, y)));
        var full = Count(painted, scale, (x, y) => Near(Pixel(painted, x, y), color, 24));
        Assert.Equal(0, Count(pdfium, scale, (x, y) => IsBlue(Pixel(pdfium, x, y))));
        Assert.True(tinted >= 0.8 * outline, $"Only {tinted} tinted pixels against {outline} outline pixels at scale {scale}: not filled.");
        Assert.True(full > 30 * scale * scale, $"Only {full} pixels fully in the text colour at scale {scale}.");
        Assert.Equal(0, Count(painted, scale, (x, y) => Pixel(painted, x, y) == PageThemeColors.DarkForeground));

        // The text keeps the contrast of text on the dark ground.
        Assert.True(Contrast(color, PageThemeColors.DarkBackground) >= PdfiumDarkMarkups.TextContrast);

        // Nothing changes far from the box.
        var far = Box.Inflate(16);
        foreach (var (x, y) in AllPoints(painted))
        {
            if (!far.Contains((x + 0.5) / scale, (y + 0.5) / scale))
            {
                Assert.True(Pixel(painted, x, y) == Pixel(pdfium, x, y), $"({x}, {y}) changed outside the text box.");
            }
        }
    }

    [Fact]
    public async Task AForeignTextBoxIsLeftAsPdfiumDrawsIt()
    {
        var path = WritePdf(folder, "foreign.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FreeText /Rect [20 120 220 180] /F 4 /Contents (Foreign) /DA (/Helv 14 Tf 0 0 1 rg) /AP << /N 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [20 120 220 180] /Length 31 >>\nstream\nq 0 0 1 rg 30 130 100 30 re f Q\nendstream",
        ]);
        await using var document = await OpenAsync(path);
        await document.ReplaceDarkMarkupReaderAsync((_, _) => DarkMarkupPaint.Empty);
        var pdfium = await document.RenderPageAsync(0, 2, Dark, Ct);
        await document.ReplaceDarkMarkupReaderAsync(null);
        var painted = await document.RenderPageAsync(0, 2, Dark, Ct);

        Assert.True(pdfium.Pixels.Span.SequenceEqual(painted.Pixels.Span));
    }

    /// <summary>Renders page 1 of <paramref name="path"/> dark without the text box, with it as PDFium draws it, and painted.</summary>
    private static async Task<(RenderedImage Plain, RenderedImage Pdfium, RenderedImage Painted)> RenderAsync(string path, double scale, TextBoxSpec spec)
    {
        await using var plainDocument = await OpenAsync(path);
        await using var document = await OpenAsync(path);
        await document.CreateTextBoxAsync(0, spec, null, Ct);

        var plain = await plainDocument.RenderPageAsync(0, scale, Dark, Ct);
        await document.ReplaceDarkMarkupReaderAsync((_, _) => DarkMarkupPaint.Empty);
        var pdfium = await document.RenderPageAsync(0, scale, Dark, Ct);
        await document.ReplaceDarkMarkupReaderAsync(null);
        var painted = await document.RenderPageAsync(0, scale, Dark, Ct);
        return (plain, pdfium, painted);
    }

    private static int Count(RenderedImage image, double scale, Func<int, int, bool> predicate)
    {
        var count = 0;
        foreach (var (x, y) in AllPoints(image))
        {
            if (Box.Contains((x + 0.5) / scale, (y + 0.5) / scale) && predicate(x, y))
            {
                count++;
            }
        }

        return count;
    }

    private string BlankPage() => WritePdf(folder, Guid.NewGuid().ToString("N")[..8] + "-blank.pdf",
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] >>",
    ]);

    private static uint Rgb(AnnotationColor color) => 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    private static uint Pixel(RenderedImage image, int x, int y)
    {
        var o = (y * image.Stride) + (x * 4);
        var p = image.Pixels.Span;
        return 0xFF000000u | ((uint)p[o + 2] << 16) | ((uint)p[o + 1] << 8) | p[o];
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

    /// <summary>Whether a pixel leans to blue (the text colour) rather than the grey of the dark page and its foreground.</summary>
    private static bool IsBlue(uint pixel) => (int)(pixel & 0xFF) - (int)((pixel >> 16) & 0xFF) > 30;

    private static bool Near(uint a, uint b, int tolerance) =>
        Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) <= tolerance;

    private static double Contrast(uint a, uint b)
    {
        static double Luminance(uint c)
        {
            static double Linear(uint channel)
            {
                var s = channel / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return (0.2126 * Linear((c >> 16) & 0xFF)) + (0.7152 * Linear((c >> 8) & 0xFF)) + (0.0722 * Linear(c & 0xFF));
        }

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
