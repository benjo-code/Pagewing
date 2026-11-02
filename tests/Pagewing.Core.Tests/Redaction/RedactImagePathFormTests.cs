using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Tests.Pdfium;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.Pdfium.Native;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Redaction;

/// <summary>
/// Redacting images, paths and form objects, and the render check of the written page (milestone 14, task 5), on copies of the redaction
/// fixtures and on small synthetic files for the cases no fixture has (turned images, nesting, huge images).
/// </summary>
public sealed class RedactImagePathFormTests
{
    private const string Term = "Zephyrstraße 17";

    private static readonly double[] ImageLefts = [10, 150, 290];
    private static readonly double[] KindLefts = [10, 120, 340];
    private static readonly int[] OverwrittenKinds = [0, 1, 3];

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Copy(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

    private static bool White(ProbeChar c) => c.Unicode <= 0x20;

    private static PageRect Display(ProbeRect user, double pageHeight, double grow = 0) =>
        new(user.Left - grow, pageHeight - user.Top - grow, user.Right + grow, pageHeight - user.Bottom + grow);

    /// <summary>A rectangle from user space (y up) in points from the top left of a page of this height.</summary>
    private static PageRect FromUser(double left, double bottom, double right, double top, double pageHeight) => new(left, pageHeight - top, right, pageHeight - bottom);

    private static ProbeRect ToUser(PageRect display, double pageHeight) => new(display.Left, pageHeight - display.Bottom, display.Right, pageHeight - display.Top);

    private static async Task<ProbeChar[]> ReadExactAsync(string path, int pageIndex)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, pageIndex);
                var textPage = page.LoadTextPage();
                try
                {
                    return RedactionProbe.ReadChars(textPage).Where(static c => !c.Generated).ToArray();
                }
                finally
                {
                    page.CloseTextPage(textPage);
                }
            },
            cancellationToken: Ct);
    }

    private static List<int> Find(List<ProbeChar> letters, string term)
    {
        var wanted = term.Where(static c => c > ' ').ToArray();
        var found = new List<int>();
        for (var i = 0; i + wanted.Length <= letters.Count; i++)
        {
            var match = true;
            for (var k = 0; k < wanted.Length && match; k++)
            {
                match = letters[i + k].Unicode == wanted[k];
            }

            if (match)
            {
                found.Add(i);
            }
        }

        return found;
    }

    private static ProbeRect Union(IEnumerable<ProbeChar> chars) => chars.Select(static c => c.Box).Aggregate(static (a, b) => a.Union(b));

    private static async Task<string> SaveAsync(PdfDocument document, TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        await using (var stream = File.Create(path))
        {
            await document.SaveRewriteAsync(stream, Ct);
        }

        return path;
    }

    /// <summary>The pixels of every image object of a page of a file (blue, green, red, alpha), at the pixel size of the image.</summary>
    private static async Task<List<(int Width, int Height, byte[] Bgra)>> ReadImagesAsync(string path, int pageIndex)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, pageIndex);
                var images = new List<(int, int, byte[])>();
                for (var i = 0; i < page.CountObjects(); i++)
                {
                    var obj = page.GetObject(i);
                    if (page.Describe(obj).Type != FpdfConstants.PageObjImage)
                    {
                        continue;
                    }

                    var facts = RedactionProbe.ReadImageFacts(page, obj);
                    var pixels = RedactionProbe.ReadImagePixels(page, obj, facts.Width, facts.Height)!.Value;
                    images.Add((pixels.Width, pixels.Height, pixels.Bgra));
                }

                return images;
            },
            cancellationToken: Ct);
    }

    /// <summary>The number of page objects of each type on a page of a file.</summary>
    private static async Task<int> CountObjectsAsync(string path, int pageIndex, int type)
    {
        var thread = PdfiumThread.Shared;
        return await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = PdfiumRedaction.OpenPage(document, pageIndex);
                var count = 0;
                for (var i = 0; i < page.CountObjects(); i++)
                {
                    if (page.Describe(page.GetObject(i)).Type == type)
                    {
                        count++;
                    }
                }

                return count;
            },
            cancellationToken: Ct);
    }

    /// <summary>Compares two renders of page 1 outside the rectangles (grown by the margin of the check); 0 differing pixels are expected.</summary>
    private static (int Different, int Compared, int MaxDelta) CompareOutside(RenderedImage a, RenderedImage b, double pageHeight, IReadOnlyList<PageRect> areas) =>
        RedactionProbe.CompareOutside(a, b, 2, [.. areas.Select(r => ToUser(r, pageHeight))], RedactionRenderCheck.MarginPoints);

    // ---- Images ----

    [Fact]
    public async Task ImagePixelsInTheAreaAreOverwrittenAndOutsideStaysEqual()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image.pdf");
        var before = await ReadImagesAsync(path, 0);
        Assert.Equal(3, before.Count);

        // Each image is 64 x 64 px shown at 128 x 128 pt (2 pt per pixel) with its lower left corner at (10|150|290, 16): the pixels
        // 16 to 32 across and 36 to 52 down lie at x offset 32 to 64 pt, y 40 to 72 pt.
        var areas = ImageLefts.Select(x => FromUser(x + 32, 40, x + 64, 72, 170)).ToList();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, areas, RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(3, impact.ImagesOverwritten);
        Assert.Equal(0, impact.ImagesRemovedWhole);
        Assert.DoesNotContain(impact.Fallbacks, static f => f.Kind == RedactionFallbackKind.ImageRemovedWhole);

        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        Assert.Equal(3, after.Count);
        for (var n = 0; n < 3; n++)
        {
            var (w, h, was) = before[n];
            var (w2, h2, now) = after[n];
            Assert.Equal((w, h), (w2, h2));
            var inside = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var at = ((y * w) + x) * 4;
                    var under = x is >= 16 and < 32 && y is >= 36 and < 52;
                    if (under)
                    {
                        inside++;
                        Assert.True(now[at] == 0 && now[at + 1] == 0 && now[at + 2] == 0 && now[at + 3] == 255, $"Image {n} pixel ({x}, {y}) is not the fill: {now[at]},{now[at + 1]},{now[at + 2]},{now[at + 3]}.");
                    }
                    else
                    {
                        Assert.True(
                            now[at] == was[at] && now[at + 1] == was[at + 1] && now[at + 2] == was[at + 2] && now[at + 3] == was[at + 3],
                            $"Image {n} pixel ({x}, {y}) changed: {was[at]},{was[at + 1]},{was[at + 2]},{was[at + 3]} to {now[at]},{now[at + 1]},{now[at + 2]},{now[at + 3]}.");
                    }
                }
            }

            Assert.Equal(16 * 16, inside);
        }

        // The mask of the third image keeps its alpha outside the area (rows of 8 px alternate 255 and 128).
        Assert.Equal(128, after[2].Bgra[(((8 * 64) + 0) * 4) + 3]);
    }

    [Fact]
    public async Task AWhiteFillOverwritesWithWhite()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(42, 40, 74, 72, 170)], RedactionFill.White, Ct);
        Assert.Equal(1, impact.ImagesOverwritten);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        var at = (((40 * 64) + 20) * 4);
        Assert.Equal([255, 255, 255, 255], after[0].Bgra[at..(at + 4)]);
    }

    [Fact]
    public async Task ImagePixelsTakeTheChosenColor()
    {
        // #336699 is blue 0x99, green 0x66, red 0x33 in the order of the pixels. Every kind of image (RGB, indexed, CMYK, stencil) is
        // written back as RGB with alpha, so each takes the colour, and the alpha is 255.
        var chosen = new RedactionFill(0x33, 0x66, 0x99);
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image.pdf");
        await using (var document = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            var impact = await document.RedactPageAsync(0, [FromUser(42, 40, 74, 72, 170)], chosen, Ct);
            Assert.Equal(1, impact.ImagesOverwritten);
            var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"), 0);
            var at = ((40 * 64) + 20) * 4;
            Assert.Equal([0x99, 0x66, 0x33, 255], after[0].Bgra[at..(at + 4)]);
        }

        var kinds = Copy(folder, "redact-image-kinds.pdf");
        var areas = KindLefts.Select(x => FromUser(x + 20, 30, x + 60, 60, 130)).ToList();
        await using var other = await engine.OpenAsync(kinds, cancellationToken: Ct);
        Assert.Equal(3, (await other.RedactPageAsync(0, areas, chosen, Ct)).ImagesOverwritten);
        var written = await ReadImagesAsync(await SaveAsync(other, folder, "kinds-out.pdf"), 0);
        foreach (var n in OverwrittenKinds)
        {
            var (w, h, _) = written[n];
            var middle = (((h * 60 / 100) * w) + (w * 40 / 100)) * 4;
            Assert.Equal([0x99, 0x66, 0x33, 255], written[n].Bgra[middle..(middle + 4)]);
        }
    }

    [Fact]
    public async Task FillRectangleHasTheChosenColor()
    {
        // The area is a rectangle of the page content: rendered, every pixel well inside it has exactly the colour (no tolerance).
        var chosen = new RedactionFill(0x33, 0x66, 0x99);
        using var folder = new TempFolder();
        var area = new PageRect(200, 150, 290, 190);
        string output;
        var source = Copy(folder, "text-basic.pdf");
        await using (var document = await engine.OpenAsync(source, cancellationToken: Ct))
        {
            var impact = await document.RedactPageAsync(0, [area], chosen, Ct);
            Assert.Null(impact.Refusal);
            output = await SaveAsync(document, folder, "out.pdf");
        }

        await using var reopened = await engine.OpenAsync(output, cancellationToken: Ct);
        const double Scale = 2;
        var image = await reopened.RenderPageAsync(0, Scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);
        var inside = 0;
        for (var y = (int)((area.Top + 2) * Scale); y < (int)((area.Bottom - 2) * Scale); y++)
        {
            for (var x = (int)((area.Left + 2) * Scale); x < (int)((area.Right - 2) * Scale); x++)
            {
                var at = (y * image.Stride) + (x * 4);
                var pixel = image.Pixels.Span;
                Assert.True(pixel[at] == 0x99 && pixel[at + 1] == 0x66 && pixel[at + 2] == 0x33, $"Pixel {x},{y} is {pixel[at]:X2}{pixel[at + 1]:X2}{pixel[at + 2]:X2}.");
                inside++;
            }
        }

        Assert.True(inside > 1000);

        // The default stays black.
        await using var blackDocument = await engine.OpenAsync(source, cancellationToken: Ct);
        await blackDocument.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        var blackOut = await SaveAsync(blackDocument, folder, "black.pdf");
        await using var blackReopened = await engine.OpenAsync(blackOut, cancellationToken: Ct);
        var black = await blackReopened.RenderPageAsync(0, Scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);
        var center = (((int)(170 * Scale)) * black.Stride) + (((int)(245 * Scale)) * 4);
        Assert.Equal([0, 0, 0], black.Pixels.Span[center..(center + 3)].ToArray());
    }

    [Fact]
    public async Task ImagePixelsOfAllKindsAreOverwrittenAndEveryOtherPixelStays()
    {
        // Indexed, CMYK, the inline image and the stencil of redact-image-kinds.pdf (each 100 x 100 pt at y 16, x 10 | 120 | 230 | 340):
        // overwritten as RGB. Every pixel outside the area keeps its value, the inline image included.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-kinds.pdf");
        var before = await ReadImagesAsync(path, 0);
        Assert.Equal(4, before.Count);
        var areas = KindLefts.Select(x => FromUser(x + 20, 30, x + 60, 60, 130)).ToList();
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, areas, RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(3, impact.ImagesOverwritten);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        Assert.Equal(4, after.Count);

        // The inline image (third) is untouched but survived as a new image object.
        Assert.Equal(before[2].Bgra, after[2].Bgra);
        foreach (var n in OverwrittenKinds)
        {
            var (w, h, was) = before[n];
            var now = after[n].Bgra;
            var x0 = w * 20 / 100;
            var x1 = (w * 60 / 100) + 1;
            var y0 = h * (100 - 70) / 100;
            var y1 = (h * (100 - 14) / 100) + 1;
            var inside = 0;
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var at = ((y * w) + x) * 4;
                    if (now[at] == 0 && now[at + 1] == 0 && now[at + 2] == 0 && now[at + 3] == 255 && !(was[at] == 0 && was[at + 1] == 0 && was[at + 2] == 0))
                    {
                        inside++;
                    }
                    else if (x < x0 - 1 || x > x1 + 1 || y < y0 - 1 || y > y1 + 1)
                    {
                        Assert.True(now[at] == was[at] && now[at + 1] == was[at + 1] && now[at + 2] == was[at + 2], $"Image {n} pixel ({x}, {y}) changed.");
                    }
                }
            }

            Assert.True(inside > 0, $"Image {n} has no overwritten pixel.");
        }
    }

    [Fact]
    public async Task RotatedOrSkewedImageIsRemovedWholeAndListed()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var pixels = new byte[16 * 16 * 3];
        Array.Fill(pixels, (byte)90);
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceRGB", pixels);
        pdf.AddPage(
            "/MediaBox [0 0 200 200]",
            $"<< /Font << /F1 {pdf.Font} 0 R >> /XObject << /Im1 {image} 0 R >> >>",
            "q 70.7 70.7 -70.7 70.7 100 40 cm /Im1 Do Q\nBT /F1 12 Tf 10 10 Td (Stays) Tj ET\n");
        var path = Path.Combine(folder.Path, "turned.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(90, 90, 110, 110, 200)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImagesRemovedWhole);
        Assert.Equal(0, impact.ImagesOverwritten);
        var fallback = Assert.Single(impact.Fallbacks);
        Assert.Equal(RedactionFallbackKind.ImageRemovedWhole, fallback.Kind);
        Assert.Contains("turned or sheared", fallback.Reason, StringComparison.Ordinal);
        var saved = await SaveAsync(document, folder, "out.pdf");
        Assert.Empty(await ReadImagesAsync(saved, 0));
        Assert.Contains(await ReadExactAsync(saved, 0), static c => c.Unicode == 'S');
    }

    [Fact]
    public async Task InlineImageIsReplacedByAnImageObjectAndSurvivesTheWrite()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-kinds.pdf");

        // An area over the Indexed image changes the page; the inline image next to it would be lost with the page content, and is not.
        var area = FromUser(30, 30, 60, 60, 130);
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        var saved = await SaveAsync(document, folder, "out.pdf");
        Assert.Equal(4, (await ReadImagesAsync(saved, 0)).Count);
        var after = await RedactionRender.RenderAsync(engine, saved, 2);
        var (different, compared, _) = CompareOutside(before, after, 130, [area]);
        Assert.True(compared > 0);
        Assert.Equal(0, different);
    }

    [Fact]
    public async Task ImageOf100MegapixelsIsRefusedWithReasonNotLoaded()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();

        // The dictionary says 10,000 x 10,000 pixels (100 megapixels); the data is a few bytes. Loading it would take 400 MB.
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 10000 /Height 10000 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Im1 {image} 0 R >> >>", "q 100 0 0 100 50 50 cm /Im1 Do Q\n");
        var path = Path.Combine(folder.Path, "huge-image.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var impact = await document.RedactPageAsync(0, [FromUser(60, 60, 90, 90, 200)], RedactionFill.Black, Ct);
        var dry = await document.AnalyzeRedactionAsync(0, [FromUser(60, 60, 90, 90, 200)], Ct);
        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - allocated < 100_000_000, "The image seems to have been loaded.");
        Assert.NotNull(impact.Refusal);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, impact.Refusal!.Kind);
        Assert.Equal([0], impact.Refusal.Pages);
        Assert.Contains("10000 x 10000", impact.Refusal.Reason, StringComparison.Ordinal);
        Assert.NotNull(dry.Refusal);
        Assert.False(document.IsRedactionTainted);
    }

    [Fact]
    public void TheLimitsOfImagesAndPathsArePinned()
    {
        Assert.Equal(64_000_000, PdfiumRedaction.MaxImagePixels);
        Assert.Equal(4_096, PdfiumRedaction.MaxPathPieces);
        Assert.Equal(0.5, PdfiumRedaction.LargeShapeShare);
        Assert.Equal(16, PdfiumRedaction.MaxFormDepth);
        Assert.Equal(20_000, PdfiumRedaction.MaxFormChildren);
        Assert.Equal(100_000, PdfiumRedaction.MaxObjects);
    }

    [Fact]
    public async Task ImageAreaPixelsReturnsWhatLiesUnderTheArea()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var area = FromUser(42, 40, 74, 72, 170);

        // Before redaction the pixels under the area are the stripes of the image; afterwards they are all the fill.
        var original = Assert.Single(await document.GetImageAreaPixelsAsync(0, area, Ct));
        Assert.Equal((16, 16), (original.Width, original.Height));
        Assert.Equal(16 * 16 * 4, original.Bgra.Length);
        Assert.Contains(original.Bgra.Where((_, i) => i % 4 == 0), static b => b != 0);
        await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        await using var written = await engine.OpenAsync(await SaveAsync(document, folder, "out.pdf"), cancellationToken: Ct);
        var after = Assert.Single(await written.GetImageAreaPixelsAsync(0, area, Ct));
        Assert.Equal((16, 16), (after.Width, after.Height));
        for (var i = 0; i < after.Bgra.Length; i += 4)
        {
            Assert.Equal([0, 0, 0, 255], after.Bgra[i..(i + 4)]);
        }

        // A rectangle with no image under it finds none.
        Assert.Empty(await written.GetImageAreaPixelsAsync(0, FromUser(5, 150, 8, 160, 170), Ct));
    }

    // ---- Paths ----

    [Fact]
    public async Task PathRectangleIsCroppedIntoStrips()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        var area = FromUser(60, 205, 120, 235, 300);
        var pathsBefore = await CountObjectsAsync(path, 0, FpdfConstants.PageObjPath);
        var before = await RedactionRender.RenderAsync(engine, path, 2);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);

        // The blue rectangle and the page background (a rectangle of the whole page) are cut into four strips each.
        Assert.Equal(2, impact.PathsCropped);
        Assert.Equal(0, impact.PathsRemovedWhole);
        Assert.Empty(impact.Fallbacks);
        var saved = await SaveAsync(document, folder, "out.pdf");
        Assert.Equal(pathsBefore + (2 * 3) + 1, await CountObjectsAsync(saved, 0, FpdfConstants.PageObjPath));
        var after = await RedactionRender.RenderAsync(engine, saved, 2);
        var (different, compared, _) = CompareOutside(before, after, 300, [area]);
        Assert.True(compared > 100_000);
        Assert.Equal(0, different);
    }

    [Fact]
    public async Task NoSeamBetweenStripsAtFractionalEdges()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        var area = FromUser(30.3, 205.7, 110.1, 230.2, 300);
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        var after = await RedactionRender.RenderAsync(engine, await SaveAsync(document, folder, "out.pdf"), 2);
        var (different, compared, max) = CompareOutside(before, after, 300, [area]);
        Assert.True(compared > 100_000);
        Assert.True(different == 0, $"{different} pixels differ (largest difference {max}).");
    }

    [Fact]
    public async Task PathWithCurvesTouchingTheAreaIsRemovedWholeAndListed()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");

        // The orange arch (a Bezier curve from 230,100 over 230,150 and 280,150 to 280,100) lies in the lower right.
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(240, 110, 270, 130, 300)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.PathsRemovedWhole);
        var fallback = Assert.Single(impact.Fallbacks, static f => f.Kind == RedactionFallbackKind.PathRemovedWhole);
        Assert.Contains("not a plain filled rectangle", fallback.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BezierWithRectangleLikePointsIsNotARectangle()
    {
        // The arch has the points (230,100), (230,150), (280,150), (280,100): the corners of a rectangle, joined by one curve. Cut into
        // strips it would turn into a rectangle; it must be removed as a whole instead (only the background is cut).
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(250, 105, 260, 120, 300)], RedactionFill.Black, Ct);
        Assert.Equal(1, impact.PathsCropped);
        Assert.Equal(1, impact.PathsRemovedWhole);
        var saved = await SaveAsync(document, folder, "out.pdf");

        // Nothing orange is left: a strip of the arch would still be orange, so look for the colour in the rendering of its bounds.
        var after = await RedactionRender.RenderAsync(engine, saved, 2);
        var orange = 0;
        for (var y = 2 * (300 - 140); y < 2 * (300 - 96); y++)
        {
            for (var x = 2 * 226; x < 2 * 284; x++)
            {
                var at = (y * after.Stride) + (x * 4);
                if (after.Pixels.Span[at + 2] > 240 && after.Pixels.Span[at + 1] is > 140 and < 170 && after.Pixels.Span[at] < 40)
                {
                    orange++;
                }
            }
        }

        Assert.Equal(0, orange);
    }

    [Fact]
    public async Task StrokedAndRotatedPathsAreRemovedWholeWhenTouched()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);

        // The stroked rectangle 20,60 100 x 30 (4 pt stroke) and the green square turned by 45 degrees around 150,110.
        var impact = await document.RedactPageAsync(0, [FromUser(30, 65, 50, 85, 300), FromUser(140, 120, 160, 140, 300)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(2, impact.PathsRemovedWhole);
        Assert.Equal(2, impact.Fallbacks.Count(static f => f.Kind == RedactionFallbackKind.PathRemovedWhole));
    }

    [Fact]
    public async Task BackgroundCoveringMoreThanHalfThePageIsNeverRemovedWhole()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        var area = FromUser(150, 245, 190, 262, 300);
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.PathsCropped);
        Assert.DoesNotContain(impact.Fallbacks, static f => f.Kind == RedactionFallbackKind.PathRemovedWhole);
        var after = await RedactionRender.RenderAsync(engine, await SaveAsync(document, folder, "out.pdf"), 2);
        var (different, _, _) = CompareOutside(before, after, 300, [area]);
        Assert.Equal(0, different);

        // The corner of the page is still the yellow of the background.
        var at = (5 * after.Stride) + (5 * 4);
        Assert.True(after.Pixels.Span[at + 2] > 240 && after.Pixels.Span[at + 1] > 240 && after.Pixels.Span[at] is > 180 and < 220);
    }

    [Fact]
    public async Task ALargeShapeThatIsNotARectangleRefusesThePageInsteadOfBeingRemoved()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /Font << /F1 {pdf.Font} 0 R >> >>", "0.8 g 0 0 m 200 0 l 0 200 l f\nBT /F1 12 Tf 10 190 Td (Stays) Tj ET\n");
        var path = Path.Combine(folder.Path, "triangle.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(20, 20, 60, 60, 200)], RedactionFill.Black, Ct);
        Assert.NotNull(impact.Refusal);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, impact.Refusal!.Kind);
        Assert.Contains("more than half", impact.Refusal.Reason, StringComparison.Ordinal);
    }

    // ---- Form objects ----

    [Fact]
    public async Task FormObjectIsFlattenedAndItsTextRemoved()
    {
        // A form with three texts (one in a nested form with its own matrix); the area covers the middle one. The others stay exactly
        // where they were, and nothing is removed as a whole.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var inner = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Matrix [1 0 0 1 5 7] /Resources << /Font << /F1 {pdf.Font} 0 R >> >>", "BT /F1 12 Tf 10 20 Td (Inner survivor) Tj ET\n"u8.ToArray());
        var outer = pdf.AddStream(
            $"/Type /XObject /Subtype /Form /BBox [0 0 400 400] /Matrix [1 0 0 1 3 4] /Resources << /Font << /F1 {pdf.Font} 0 R >> /XObject << /In {inner} 0 R >> >>",
            "BT /F1 12 Tf 10 120 Td (First survivor) Tj ET\nBT /F1 12 Tf 10 80 Td (Zephyrstrasse 17) Tj ET\nq 1 0 0 1 100 0 cm /In Do Q\n"u8.ToArray());
        pdf.AddPage("/MediaBox [0 0 300 300]", $"<< /XObject << /Fm1 {outer} 0 R >> >>", "q 1 0 0 1 20 30 cm /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, "nested.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var middle = Union(letters.Skip(Find(letters, "Zephyrstrasse 17")[0]).Take(15));
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [Display(middle, 300)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.FormObjectsTouched);
        Assert.Empty(impact.Fallbacks);
        Assert.Contains("Zephyrstrasse 17", impact.RemovedText);

        var after = await ReadExactAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        var survivors = original.Where(c => !White(c) && !Touches(middle, c.Box)).ToList();
        var now = after.Where(static c => !White(c)).ToList();
        Assert.Equal(survivors.Count, now.Count);
        for (var i = 0; i < survivors.Count; i++)
        {
            Assert.Equal(survivors[i].Unicode, now[i].Unicode);
            Assert.True(Math.Abs(survivors[i].OriginX - now[i].OriginX) < 0.01 && Math.Abs(survivors[i].OriginY - now[i].OriginY) < 0.01, $"Character {i} moved.");
        }

        Assert.Empty(Find(now, "Zephyrstrasse 17"));
        Assert.Single(Find(now, "Inner survivor"));
        Assert.Single(Find(now, "First survivor"));
    }

    [Theory]
    [InlineData("q 1 0 0 1 50 0 cm 0 0 40 40 re f Q", "q 1 0 0 1 0 100 cm /Fm1 Do Q", 60, 110, 80, 130)]
    [InlineData("q 2 0 0 2 20 20 cm 0 0 20 20 re f Q", "q 1 0 0 1 100 100 cm /Fm1 Do Q", 130, 130, 150, 150)]
    public async Task APathInsideAFormObjectIsCroppedAtItsPlaceOnThePage(string formContent, string pageContent, double left, double bottom, double right, double top)
    {
        // The rectangle is drawn after a cm inside the form, so its own matrix is not the identity; the form is placed with a second
        // matrix. The strips have to lie around the mark on the page, and the page has to look the same outside the mark.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", Encoding.ASCII.GetBytes("1 0 0 rg " + formContent + "\n"));
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Fm1 {form} 0 R >> >>", pageContent + "\n");
        var path = Path.Combine(folder.Path, "form-path.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        var area = FromUser(left, bottom, right, top, 200);
        var before = await RedactionRender.RenderAsync(engine, path, 2);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.PathsCropped);
        Assert.Equal(0, impact.PathsRemovedWhole);
        Assert.Equal(1, impact.FormObjectsTouched);
        var saved = await SaveAsync(document, folder, "out.pdf");

        // Four strips and the area, all on the page now.
        Assert.Equal(5, await CountObjectsAsync(saved, 0, FpdfConstants.PageObjPath));
        var after = await RedactionRender.RenderAsync(engine, saved, 2);
        var (different, compared, _) = CompareOutside(before, after, 200, [area]);
        Assert.True(compared > 10_000);
        Assert.Equal(0, different);

        // Inside the mark the page is black; the red of the rectangle is gone there (no red pixel in the mark).
        var red = 0;
        for (var y = (int)(2 * (200 - top)) + 1; y < (int)(2 * (200 - bottom)) - 1; y++)
        {
            for (var x = (int)(2 * left) + 1; x < (int)(2 * right) - 1; x++)
            {
                var at = (y * after.Stride) + (x * 4);
                if (after.Pixels.Span[at + 2] > 100 || after.Pixels.Span[at] > 100)
                {
                    red++;
                }
            }
        }

        Assert.Equal(0, red);
    }

    [Fact]
    public async Task ATurnedPathInsideAFormObjectIsRemovedWhole()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "1 0 0 rg q 0.7071 0.7071 -0.7071 0.7071 100 50 cm 0 0 40 40 re f Q\n"u8.ToArray());
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Fm1 {form} 0 R >> >>", "q /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, "turned-form-path.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(95, 73, 105, 83, 200)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(0, impact.PathsCropped);
        Assert.Equal(1, impact.PathsRemovedWhole);
        Assert.Equal(1, impact.FormObjectsTouched);
    }

    [Fact]
    public async Task APathInsideANestedFormObjectIsCroppedAtItsPlaceOnThePage()
    {
        // Inner form: a rectangle after a cm. The outer form has its own /Matrix and places the inner one with a cm; the page places the outer.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var inner = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "1 0 0 rg q 1 0 0 1 30 0 cm 0 0 20 20 re f Q\n"u8.ToArray());
        var outer = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Matrix [1 0 0 1 10 0] /Resources << /XObject << /In {inner} 0 R >> >>", "q 1 0 0 1 0 20 cm /In Do Q\n"u8.ToArray());
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Fm1 {outer} 0 R >> >>", "q 1 0 0 1 0 100 cm /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, "nested-form-path.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        // The rectangle lies at x 40 to 60, y 120 to 140 on the page.
        var area = FromUser(45, 125, 55, 135, 200);
        var before = await RedactionRender.RenderAsync(engine, path, 2);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.PathsCropped);
        var saved = await SaveAsync(document, folder, "out.pdf");
        Assert.Equal(5, await CountObjectsAsync(saved, 0, FpdfConstants.PageObjPath));
        var after = await RedactionRender.RenderAsync(engine, saved, 2);
        var (different, compared, _) = CompareOutside(before, after, 200, [area]);
        Assert.True(compared > 10_000);
        Assert.Equal(0, different);
    }

    [Fact]
    public async Task AnImageInsideAFormObjectWithItsOwnMatrixIsOverwrittenAtTheRightPixels()
    {
        // An 8 x 8 image drawn after a cm (40 pt square at 50,10) inside a form that the page places at 0,100: 5 pt per pixel.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var pixels = new byte[8 * 8];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(60 + (i * 3));
        }

        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 /ColorSpace /DeviceGray", pixels);
        var form = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /Im {image} 0 R >> >>", "q 40 0 0 40 50 10 cm /Im Do Q\n"u8.ToArray());
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Fm1 {form} 0 R >> >>", "q 1 0 0 1 0 100 cm /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, "form-image.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        // The image lies at x 50 to 90, y 110 to 150 on the page: the mark x 60 to 75, y 120 to 135 covers the columns 2 to 4 and the rows 3 to 5.
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(60, 120, 75, 135, 200)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImagesOverwritten);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        var image2 = Assert.Single(after);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var at = ((y * 8) + x) * 4;
                var under = x is >= 2 and <= 4 && y is >= 3 and <= 5;
                if (under)
                {
                    Assert.Equal([0, 0, 0, 255], image2.Bgra[at..(at + 4)]);
                }
                else
                {
                    var g = (byte)(60 + (((y * 8) + x) * 3));
                    Assert.Equal([g, g, g, 255], image2.Bgra[at..(at + 4)]);
                }
            }
        }
    }

    private static bool Touches(ProbeRect a, ProbeRect b) =>
        a.Left <= b.Right + 1e-6 && b.Left <= a.Right + 1e-6 && a.Bottom <= b.Top + 1e-6 && b.Bottom <= a.Top + 1e-6;

    [Fact]
    public async Task TextInsideAFormObjectIsFoundAfterFlattening()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var occurrences = Find(letters, Term);
        var inForm = Union(letters.Skip(occurrences[1]).Take(Term.Count(static c => c > ' ')));

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [Display(inForm, 400)], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.FormObjectsTouched);
        Assert.Empty(impact.Fallbacks);
        Assert.Contains(Term, impact.RemovedText);
        Assert.Equal(Term.Length, impact.CharsRemoved);
        var saved = await SaveAsync(document, folder, "out.pdf");

        // The form's text is gone from page 1 (the visible line and the invisible text stay) and nothing else of the page changed.
        var after = await ReadExactAsync(saved, 0);
        Assert.Equal(2, Find(after.Where(static c => !White(c)).ToList(), Term).Count);
        var survivors = original.Where(c => !White(c) && !Touches(inForm, c.Box)).ToList();
        var now = after.Where(static c => !White(c)).ToList();
        Assert.Equal(survivors.Count, now.Count);
        for (var i = 0; i < survivors.Count; i++)
        {
            Assert.Equal(survivors[i].Unicode, now[i].Unicode);
            Assert.True(Math.Abs(survivors[i].OriginX - now[i].OriginX) < 0.01 && Math.Abs(survivors[i].OriginY - now[i].OriginY) < 0.01, $"Character {i} moved.");
        }
    }

    [Fact]
    public async Task SharedFormObjectDoesNotChangeTheOtherPage()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-everywhere.pdf");
        var original = await ReadExactAsync(path, 0);
        var letters = original.Where(static c => !White(c)).ToList();
        var inForm = Union(letters.Skip(Find(letters, Term)[1]).Take(Term.Count(static c => c > ' ')));
        var pageTwoBefore = await ReadExactAsync(path, 1);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await document.RedactPageAsync(0, [Display(inForm, 400)], RedactionFill.Black, Ct);
        var saved = await SaveAsync(document, folder, "out.pdf");

        var pageTwo = await ReadExactAsync(saved, 1);
        Assert.Equal(pageTwoBefore.Length, pageTwo.Length);
        Assert.Single(Find(pageTwo.Where(static c => !White(c)).ToList(), Term));
        for (var i = 0; i < pageTwo.Length; i++)
        {
            Assert.Equal(pageTwoBefore[i].Unicode, pageTwo[i].Unicode);
            Assert.True(Math.Abs(pageTwoBefore[i].OriginX - pageTwo[i].OriginX) < 0.01 && Math.Abs(pageTwoBefore[i].OriginY - pageTwo[i].OriginY) < 0.01);
        }

        // The XObject stays in the file for page 2.
        var bytes = await File.ReadAllBytesAsync(saved, Ct);
        Assert.True(RawPdf.Count(RawPdf.InflateAllStreams(bytes), Term) >= 1);
    }

    [Fact]
    public async Task FormObjectNestedDeeperThanTheLimitIsRemovedWholeAndListed()
    {
        using var folder = new TempFolder();
        Assert.Equal(16, PdfiumRedaction.MaxFormDepth);

        // 16 levels are taken apart, 17 are removed as a whole.
        foreach (var levels in new[] { 16, 17 })
        {
            var path = await WriteNestedAsync(folder, levels);
            await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
            var impact = await document.RedactPageAsync(0, [FromUser(15, 195, 90, 215, 300)], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            Assert.Equal(1, impact.FormObjectsTouched);
            if (levels == 16)
            {
                Assert.Empty(impact.Fallbacks);
            }
            else
            {
                var fallback = Assert.Single(impact.Fallbacks);
                Assert.Equal(RedactionFallbackKind.FormObjectRemovedWhole, fallback.Kind);
                Assert.Contains("deeper than 16", fallback.Reason, StringComparison.Ordinal);
            }

            var after = await ReadExactAsync(await SaveAsync(document, folder, $"nested-{levels}-out.pdf"), 0);
            Assert.DoesNotContain(after, static c => c.Unicode == 'D');
        }
    }

    [Fact]
    public async Task APathInAFormObjectWithAnUnusableMatrixUnderTheAreaMakesTheFormGoWholeAndIsListed()
    {
        // A singular cm gives the path a matrix PDFium reports but nothing can undo: the path cannot be placed on the page, so it
        // cannot be cropped. It must not stay under the mark; the form object goes as a whole and the list says why.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var form = pdf.AddStream("/Type /XObject /Subtype /Form /BBox [0 0 200 200]", "1 0 0 rg q 0 0 0 0 100 50 cm 0 0 40 40 re f Q 0 0 1 rg 10 150 30 30 re f\n"u8.ToArray());
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Fm1 {form} 0 R >> >>", "q /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, "unusable-matrix-path.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var before = await CountObjectsAsync(path, 0, FpdfConstants.PageObjForm);
        Assert.Equal(1, before);
        var impact = await document.RedactPageAsync(0, [FromUser(95, 45, 105, 55, 200)], RedactionFill.Black, Ct);

        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.FormObjectsTouched);
        var fallback = Assert.Single(impact.Fallbacks);
        Assert.Equal(RedactionFallbackKind.FormObjectRemovedWhole, fallback.Kind);
        Assert.Contains("matrix", fallback.Reason, StringComparison.Ordinal);
        var saved = await SaveAsync(document, folder, "unusable-matrix-out.pdf");
        Assert.Equal(1, await CountObjectsAsync(saved, 0, FpdfConstants.PageObjPath)); // only the cover of the mark is left
        Assert.Equal(0, await CountObjectsAsync(saved, 0, FpdfConstants.PageObjForm));
    }

    private static async Task<string> WriteNestedAsync(TempFolder folder, int levels)
    {
        // Form k holds form k+1 (k = 1 is placed on the page); the last one shows "Deep text" at 10,200 (page 20,200 after the shifts).
        var pdf = new MiniPdf();
        var ids = new int[levels + 1];
        for (var k = levels; k >= 1; k--)
        {
            var content = k == levels ? "BT /F1 12 Tf 0 190 Td (Deep text) Tj ET\n" : "q /Next Do Q\n";
            var resources = k == levels ? $"<< /Font << /F1 {pdf.Font} 0 R >> >>" : $"<< /XObject << /Next {ids[k + 1]} 0 R >> >>";
            ids[k] = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Matrix [1 0 0 1 {(k == 2 ? 15 : 0)} 0] /Resources {resources}", Encoding.ASCII.GetBytes(content));
        }

        pdf.AddPage("/MediaBox [0 0 300 300]", $"<< /XObject << /Fm1 {ids[1]} 0 R >> >>", "q /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, $"nested-{levels}.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        return path;
    }

    [Fact]
    public async Task FormObjectsThatTogetherHoldMoreThanTheObjectLimitRefuseThePage()
    {
        // Six form objects at one place, each with 20,000 small paths (the limit for one form) and one text: 120,000 objects in all,
        // more than PdfiumRedaction.MaxObjects. The traversal stops with a refusal instead of walking them all.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var body = new StringBuilder("BT /F1 12 Tf 20 100 Td (Inside) Tj ET\n");
        for (var i = 0; i < PdfiumRedaction.MaxFormChildren - 1; i++)
        {
            body.Append("0 0 1 1 re f\n");
        }

        var forms = new StringBuilder();
        var content = new StringBuilder();
        for (var k = 0; k < 6; k++)
        {
            var form = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 300 300] /Resources << /Font << /F1 {pdf.Font} 0 R >> >>", Encoding.ASCII.GetBytes(body.ToString()));
            forms.Append(CultureInfo.InvariantCulture, $"/Fm{k} {form} 0 R ");
            content.Append(CultureInfo.InvariantCulture, $"/Fm{k} Do\n");
        }

        pdf.AddPage("/MediaBox [0 0 300 300]", $"<< /XObject << {forms}>> >>", content.ToString());
        var path = Path.Combine(folder.Path, "many-forms.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(15, 95, 60, 115, 300)], RedactionFill.Black, Ct);
        Assert.NotNull(impact.Refusal);
        Assert.Contains(PdfiumRedaction.MaxObjects.ToString(CultureInfo.InvariantCulture), impact.Refusal!.Reason, StringComparison.Ordinal);
    }

    // ---- The render check ----

    [Fact]
    public void TheRenderCheckConstantsArePinned()
    {
        Assert.Equal(2d, RedactionRenderCheck.Scale);
        Assert.Equal(16, RedactionRenderCheck.ChannelTolerance);
        Assert.Equal(1.5, RedactionRenderCheck.MarginPoints);
        Assert.Equal(0, RedactionRenderCheck.MaxDifferentPixels);
    }

    [Fact]
    public void TheComparisonLeavesOutTheAreasAndTheMarginAndCountsEveryOtherDifference()
    {
        const int size = 100;
        byte[] Plain() => Enumerable.Repeat((byte)255, size * size * 4).ToArray();
        var original = new RenderedImage(size, size, size * 4, Plain());
        var changed = Plain();
        void Set(int x, int y, byte value) => changed[(((y * size) + x) * 4) + 1] = value;

        // A difference inside the area, and one inside the margin (1.5 pt = 3 px at scale 2): ignored.
        Set(20, 20, 0);
        Set(42, 20, 0);

        // The tolerance: 16 is equal, 17 differs.
        Set(80, 80, 255 - RedactionRenderCheck.ChannelTolerance);
        var area = new PageRect(10, 10, 20, 20);
        var result = RedactionRenderCheck.Compare(original, new RenderedImage(size, size, size * 4, changed), 2, [area]);
        Assert.True(result.Passes);
        Assert.Equal(0, result.Different);

        Set(80, 81, 255 - RedactionRenderCheck.ChannelTolerance - 1);
        Set(50, 20, 0); // 5 px to the right of the area (10 pt = 20 px): 20 + 3 = 23 is the edge of the margin, so this one counts too
        result = RedactionRenderCheck.Compare(original, new RenderedImage(size, size, size * 4, changed), 2, [area]);
        Assert.False(result.Passes);
        Assert.Equal(2, result.Different);
        Assert.Equal(new PageRect(25, 10, 40.5, 41), result.Box!.Value);

        Assert.True(RedactionRenderCheck.Compare(original, new RenderedImage(size + 1, size, (size + 1) * 4, new byte[(size + 1) * size * 4]), 2, [area]).SizeDiffers);
    }

    [Fact]
    public async Task PageThatPassesTheRenderCheckIsNotRefused()
    {
        foreach (var (name, area) in new[]
        {
            ("redact-paths.pdf", FromUser(60, 205, 120, 235, 300)),
            ("redact-image.pdf", FromUser(42, 40, 74, 72, 170)),
            ("redact-rotated.pdf", await RotatedTermAreaAsync()),
        })
        {
            using var folder = new TempFolder();
            var path = Copy(folder, name);
            await using var original = await engine.OpenAsync(TestData.Pdf(name), cancellationToken: Ct);
            await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
            var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            var refusals = await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct);
            Assert.True(refusals.Count == 0, $"{name}: {string.Join("; ", refusals.Select(static r => r.Reason))}");
        }
    }

    private static async Task<PageRect> RotatedTermAreaAsync()
    {
        // The box of the term on the page that is turned and cropped, in points from the top left of the shown page, as the engine reports it.
        await using var reader = await new PdfEngine().OpenAsync(TestData.Pdf("redact-rotated.pdf"), cancellationToken: Ct);
        var text = await reader.GetPageTextAsync(0, Ct);
        var letters = text.Chars.Where(static c => !c.IsGenerated && c.CodePoint > 0x20).ToList();
        var wanted = Term.Where(static c => c > ' ').Select(static c => (int)c).ToArray();
        var at = Enumerable.Range(0, letters.Count - wanted.Length + 1).First(i => Enumerable.Range(0, wanted.Length).All(k => letters[i + k].CodePoint == wanted[k]));
        return letters.Skip(at).Take(wanted.Length).Select(static c => c.Box).Aggregate(static (a, b) => a.Union(b)).Inflate(0.5);
    }

    [Fact]
    public async Task PageWithPatternShadingType3OrCidToGidMapIsRefusedWithPageAndReason()
    {
        // The area is a harmless one (a line of text, or a shape); what makes the page fail is something else on the page that PDFium
        // cannot write again: a tiling pattern fill, a Type 3 font, a soft mask, a shading, a CID font with a CIDToGIDMap stream.
        var cases = new (string Name, PageRect Area)[]
        {
            ("redact-everywhere.pdf", FromUser(15, 335, 200, 355, 400)),
            ("redact-hidden.pdf", FromUser(15, 275, 200, 292, 300)),
            ("redact-fonts.pdf", FromUser(15, 385, 100, 403, 420)),
            ("vector-shapes.pdf", FromUser(25, 205, 60, 235, 300)),
        };
        foreach (var (name, area) in cases)
        {
            using var folder = new TempFolder();
            var path = Copy(folder, name);
            await using var original = await engine.OpenAsync(TestData.Pdf(name), cancellationToken: Ct);
            await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
            var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            var refusals = await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct);
            var refusal = Assert.Single(refusals);
            Assert.Equal(RedactionRefusalKind.PageNotRedactable, refusal.Kind);
            Assert.Equal([0], refusal.Pages);
            Assert.Contains("outside the marked areas", refusal.Reason, StringComparison.Ordinal);

            // Nothing is written to the folder by the check except the copy that is deleted again.
            Assert.Equal(new[] { path }, Directory.GetFiles(folder.Path));
        }
    }

    [Fact]
    public async Task SplitTextThatLosesItsClipPathIsCaughtByTheRenderCheck()
    {
        // The text "Zephyrstrasse 17" of redact-paths.pdf is clipped to 150,60 60 x 30 and runs on to the right of the clip. Characters of it
        // inside the clip are redacted: the text object is split, the new objects have no clip path (PDFium cannot copy one), and the part
        // of the text that the clip hid would show. The check must see that.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        await using var original = await engine.OpenAsync(TestData.Pdf("redact-paths.pdf"), cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
        var area = FromUser(195, 70, 209, 84, 300);
        var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.TextObjectsSplit);
        var refusal = Assert.Single(await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, refusal.Kind);
        Assert.Equal([0], refusal.Pages);
        Assert.Contains("x 21", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SplitTextThatLosesItsHiddenLayerIsCaughtByTheRenderCheck()
    {
        // The text in the layer that is switched off (redact-structure.pdf) is not shown; the survivors of a split would not belong to the
        // layer any more and show up. The check must see that.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-structure.pdf");
        await using var original = await engine.OpenAsync(TestData.Pdf("redact-structure.pdf"), cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
        var area = FromUser(18, 238, 40, 252, 300);
        var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.TextObjectsSplit);
        Assert.Contains("Zep", impact.RemovedText);
        var refusal = Assert.Single(await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));
        Assert.Equal([0], refusal.Pages);
        Assert.Contains("outside the marked areas", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASingleGlyphForTwoCharactersIsRemovedWholeAndTheRenderCheckAcceptsThat()
    {
        // redact-ligature.pdf: one glyph (like fi) that PDFium reports as two characters. Rewritten from the two characters it would be two
        // glyphs: the read-back of the survivors finds that and the whole text object goes (listed); the page then differs from the original
        // only where the object was, which the check leaves out because the result says so.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-ligature.pdf");
        await using var original = await engine.OpenAsync(TestData.Pdf("redact-ligature.pdf"), cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
        var area = FromUser(18, 45, 70, 70, 100);
        var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(0, impact.TextObjectsSplit);
        var fallback = Assert.Single(impact.Fallbacks);
        Assert.Equal(RedactionFallbackKind.TextObjectRemovedWhole, fallback.Kind);
        Assert.Contains("read back", fallback.Reason, StringComparison.Ordinal);
        Assert.Empty(await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));

        // Without the removed region the same page fails the check: it is what makes the survivors' disappearance count as intended.
        Assert.Single(await copy.VerifyRedactionRenderAsync(original, [new RedactionPageAreas(0, [area])], Ct));
    }

    [Fact]
    public async Task ACharacterCutByTheEdgeOfTheAreaDoesNotFailTheRenderCheck()
    {
        // A character counts as soon as its box touches the area, so its glyph goes as a whole even where it reaches out of the area.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        pdf.AddPage("/MediaBox [0 0 200 100]", $"<< /Font << /F1 {pdf.Font} 0 R >> >>", "BT /F1 24 Tf 20 40 Td (Hello World) Tj ET\n");
        var path = Path.Combine(folder.Path, "edge.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        var work = Path.Combine(folder.Path, "work.pdf");
        File.Copy(path, work);
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(work, cancellationToken: Ct);

        // The area ends in the middle of the "e" (Helvetica 24: H from x 20 to 37.3, e from 37.3 to 50.6).
        var area = FromUser(20, 30, 44, 70, 100);
        var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.TextObjectsSplit);
        Assert.NotEmpty(impact.RemovedAreas!);
        Assert.Empty(await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));
        Assert.Single(await copy.VerifyRedactionRenderAsync(original, [new RedactionPageAreas(0, [area])], Ct));
    }

    [Fact]
    public async Task AnInlineImageThatIsAlsoUnderTheAreaIsOverwrittenAsANewImageObject()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-kinds.pdf");
        var before = await ReadImagesAsync(path, 0);
        var area = FromUser(230 + 20, 30, 230 + 60, 60, 130);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImagesOverwritten);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"), 0);
        Assert.Equal(4, after.Count);

        // The inline image has 16 x 16 pixels at 6.25 pt each: the area covers the columns 3 to 9 (x 20 to 60 pt) and rows 4 to 11.
        var (w, h, was) = before[2];
        var now = after[2].Bgra;
        var covered = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var at = ((y * w) + x) * 4;
                if (now[at] == 0 && now[at + 1] == 0 && now[at + 2] == 0 && now[at + 3] == 255)
                {
                    covered++;
                }
                else
                {
                    Assert.Equal(was[at..(at + 4)], now[at..(at + 4)]);
                }
            }
        }

        Assert.True(covered >= 6 * 7, $"{covered} pixels were overwritten.");
    }

    [Fact]
    public async Task AnalyzeSaysTheSameAsApplyingForImagesPathsAndForms()
    {
        using var folder = new TempFolder();
        foreach (var (name, area) in new (string, PageRect)[]
        {
            ("redact-image.pdf", FromUser(42, 40, 74, 72, 170)),
            ("redact-image-kinds.pdf", FromUser(30, 30, 60, 60, 130)),
            ("redact-paths.pdf", FromUser(60, 205, 120, 235, 300)),
            ("redact-paths.pdf", FromUser(240, 110, 270, 130, 300)),
            ("redact-everywhere.pdf", FromUser(20, 305, 120, 325, 400)),
        })
        {
            var path = Path.Combine(folder.Path, $"{Guid.NewGuid():N}.pdf");
            File.Copy(TestData.Pdf(name), path);
            await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
            var dry = await document.AnalyzeRedactionAsync(0, [area], Ct);
            var done = await document.RedactPageAsync(0, [area], RedactionFill.Black, Ct);
            Assert.Equal(done.ImagesOverwritten, dry.ImagesOverwritten);
            Assert.Equal(done.ImagesRemovedWhole, dry.ImagesRemovedWhole);
            Assert.Equal(done.PathsCropped, dry.PathsCropped);
            Assert.Equal(done.PathsRemovedWhole, dry.PathsRemovedWhole);
            Assert.Equal(done.FormObjectsTouched, dry.FormObjectsTouched);
            Assert.Equal(done.CharsRemoved, dry.CharsRemoved);
            Assert.Equal(done.Fallbacks, dry.Fallbacks);
            Assert.Equal(done.RemovedAreas, dry.RemovedAreas);
            Assert.Equal(done.Refusal?.Reason, dry.Refusal?.Reason);
        }
    }

    [Fact]
    public async Task ABackgroundHitByManyAreasIsCutOrRefusedAndNeverSlow()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        pdf.AddPage("/MediaBox [0 0 612 792]", "<< >>", "0.9 g 0 0 612 792 re f\n");
        var path = Path.Combine(folder.Path, "background.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        PageRect[] Grid(int columns, int rows)
        {
            var areas = new List<PageRect>();
            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    areas.Add(new PageRect(10 + (x * 6), 10 + (y * 6), 13 + (x * 6), 13 + (y * 6)));
                }
            }

            return [.. areas];
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await using (var cut = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            var impact = await cut.RedactPageAsync(0, Grid(10, 10), RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            Assert.Equal(1, impact.PathsCropped);
        }

        // 80 x 100 = 8,000 holes make far more than MaxPathPieces pieces of a background: the page is refused, quickly.
        await using (var refused = await engine.OpenAsync(path, cancellationToken: Ct))
        {
            var impact = await refused.RedactPageAsync(0, Grid(80, 100), RedactionFill.Black, Ct);
            Assert.NotNull(impact.Refusal);
            Assert.Contains("pieces", impact.Refusal!.Reason, StringComparison.Ordinal);
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Took {watch.Elapsed}.");
    }

    [Fact]
    public async Task RenderCheckRunsOnTheFreshlyWrittenCopyNotOnTheWorkingCopy()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        await using var original = await engine.OpenAsync(TestData.Pdf("redact-paths.pdf"), cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
        var area = FromUser(60, 205, 120, 235, 300);
        var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);

        var rendered = new List<PdfDocument>();
        string? writtenPath = null;
        var existedWhileRendering = false;
        copy.VerifySeam = new RedactionVerifySeam
        {
            WrittenCopy = p => writtenPath = p,
            Rendering = d =>
            {
                rendered.Add(d);
                existedWhileRendering |= writtenPath is not null && File.Exists(writtenPath);
            },
        };
        var refusals = await copy.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct);
        Assert.Empty(refusals);

        // Rendered: the original and the written copy, never the working copy; the written copy is another file that is gone afterwards.
        Assert.Equal(2, rendered.Count);
        Assert.DoesNotContain(rendered, d => ReferenceEquals(d, copy));
        Assert.Contains(rendered, d => ReferenceEquals(d, original));
        Assert.NotNull(writtenPath);
        Assert.NotEqual(path, writtenPath);
        Assert.True(existedWhileRendering);
        Assert.False(File.Exists(writtenPath));

        // The working copy still holds the edits (the check did not touch it) and can be written.
        Assert.False(copy.IsRedactionTainted);
        var saved = await SaveAsync(copy, folder, "out.pdf");
        Assert.True(File.Exists(saved));
    }

    [Fact]
    public async Task RenderCheckWritesOnceForAllChangedPages()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        for (var page = 0; page < 3; page++)
        {
            pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /Font << /F1 {pdf.Font} 0 R >> >>", $"BT /F1 12 Tf 20 150 Td (Secret {page}) Tj ET\nBT /F1 12 Tf 20 50 Td (Public {page}) Tj ET\n");
        }

        var original = Path.Combine(folder.Path, "original.pdf");
        await File.WriteAllBytesAsync(original, pdf.Build(), Ct);
        var work = Path.Combine(folder.Path, "work.pdf");
        File.Copy(original, work);
        await using var reference = await engine.OpenAsync(original, cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(work, cancellationToken: Ct);
        var area = FromUser(15, 145, 100, 165, 200);
        var pages = new List<RedactionPageAreas>();
        for (var page = 0; page < 3; page++)
        {
            var impact = await copy.RedactPageAsync(page, [area], RedactionFill.Black, Ct);
            Assert.Null(impact.Refusal);
            pages.Add(RedactionPageAreas.For([area], impact));
        }

        Assert.Equal(0, copy.RedactionDryRunWrites);
        var rendered = 0;
        copy.VerifySeam = new RedactionVerifySeam { Rendering = _ => rendered++ };
        var refusals = await copy.VerifyRedactionRenderAsync(reference, pages, Ct);
        Assert.Empty(refusals);

        // One write for the three pages; three pages rendered twice each.
        Assert.Equal(1, copy.RedactionDryRunWrites);
        Assert.Equal(6, rendered);
    }

    [Fact]
    public async Task TheDryRunCopyIsPrivateWhileItExistsAndGoneAfterwards()
    {
        // The copy of the whole working copy holds the not yet redacted rest of the document (information dictionary and XMP included): on
        // Unix only its owner may read it. The mode is checked on Linux only (unverified on the development machine, which runs Windows).
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /Font << /F1 {pdf.Font} 0 R >> >>", "BT /F1 12 Tf 20 150 Td (Secret) Tj ET\n");
        var original = Path.Combine(folder.Path, "original.pdf");
        await File.WriteAllBytesAsync(original, pdf.Build(), Ct);
        var work = Path.Combine(folder.Path, "work.pdf");
        File.Copy(original, work);
        await using var reference = await engine.OpenAsync(original, cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(work, cancellationToken: Ct);
        var area = FromUser(15, 145, 100, 165, 200);
        var impact = await copy.RedactPageAsync(0, [area], RedactionFill.Black, Ct);

        string? seenPath = null;
        UnixFileMode? seenMode = null;
        copy.VerifySeam = new RedactionVerifySeam
        {
            WrittenCopy = path =>
            {
                seenPath = path;
                seenMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(path);
            },
        };
        Assert.Empty(await copy.VerifyRedactionRenderAsync(reference, [RedactionPageAreas.For([area], impact)], Ct));

        Assert.NotNull(seenPath);
        Assert.False(File.Exists(seenPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, seenMode);
        }
    }

    [Fact]
    public async Task TheRenderCheckThrowsForAWorkingCopyThatCannotBeWritten()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-paths.pdf");
        await using var original = await engine.OpenAsync(TestData.Pdf("redact-paths.pdf"), cancellationToken: Ct);
        await using var copy = await engine.OpenAsync(path, cancellationToken: Ct);
        copy.RedactionSeam = new PdfiumRedactionSeam
        {
            AfterEdits = scratch =>
            {
                if (!scratch)
                {
                    throw new InvalidOperationException("stop");
                }
            },
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => copy.RedactPageAsync(0, [FromUser(60, 205, 120, 235, 300)], RedactionFill.Black, Ct));
        Assert.True(copy.IsRedactionTainted);
        await Assert.ThrowsAsync<RedactionTaintedException>(() => copy.VerifyRedactionRenderAsync(original, [new RedactionPageAreas(0, [FromUser(60, 205, 120, 235, 300)])], Ct));
        Assert.Equal(0, copy.RedactionDryRunWrites);
    }
}

/// <summary>Builds small PDF files for the tests of this task: pages, streams and Helvetica as object 3.</summary>
internal sealed class MiniPdf
{
    private readonly List<byte[]?> objects = [null, null];
    private readonly List<int> pages = [];

    public MiniPdf()
    {
        Font = Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
    }

    /// <summary>Gets the object number of Helvetica.</summary>
    public int Font { get; }

    public int Add(string body)
    {
        objects.Add(Encoding.Latin1.GetBytes(body));
        return objects.Count;
    }

    public int AddStream(string dictionary, byte[] data)
    {
        var head = Encoding.Latin1.GetBytes($"<< {dictionary} /Length {data.Length} >>\nstream\n");
        var tail = Encoding.Latin1.GetBytes("\nendstream");
        objects.Add([.. head, .. data, .. tail]);
        return objects.Count;
    }

    public int AddPage(string pageEntries, string resources, string content)
    {
        var contentId = AddStream(string.Empty, Encoding.Latin1.GetBytes(content));
        var id = Add($"<< /Type /Page /Parent 2 0 R {pageEntries} /Contents {contentId} 0 R /Resources {resources} >>");
        pages.Add(id);
        return id;
    }

    public byte[] Build()
    {
        objects[0] = Encoding.Latin1.GetBytes("<< /Type /Catalog /Pages 2 0 R >>");
        objects[1] = Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"<< /Type /Pages /Kids [{string.Join(' ', pages.Select(static p => $"{p} 0 R"))}] /Count {pages.Count} >>"));
        using var output = new MemoryStream();
        output.Write("%PDF-1.7\n"u8);
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(output.Position);
            output.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n")));
            output.Write(objects[i]!);
            output.Write("\nendobj\n"u8);
        }

        var xref = output.Position;
        output.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n")));
        foreach (var offset in offsets)
        {
            output.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n")));
        }

        output.Write(Encoding.Latin1.GetBytes(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n")));
        return output.ToArray();
    }
}
