using System.Globalization;
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
/// Pixelating and blurring the images under a rectangle (milestone 14.1, task 12) on copies of <c>redact-image-blocks.pdf</c> and on
/// small synthetic pages (turned page, CropBox, crowded areas, a huge image): the pixels in the box are exactly the blocks the calculation
/// gives, the pixels outside are untouched, and the opaque area is only drawn where no treated image shows.
/// </summary>
public sealed class RedactImageTreatmentTests
{
    // redact-image-blocks.pdf: 440 x 330 pt. Pixel rows are counted from the top of the image.
    //   image 0 (ImGrad)  96 x 96 px at 10,160 shown 144 x 144 pt  (1.5 pt per pixel, top edge at y = 304)
    //   image 1 (ImText)  64 x 64 px at 170,160 shown 128 x 128 pt (2 pt per pixel, top edge at y = 288), text "Secret 4711" at 180,220
    //   image 2 (ImTurn)  32 x 32 px turned by 30 degrees
    //   image 3 (ImTiny)  7 x 7 px at 10,90 shown 56 x 56 pt (8 pt per pixel, top edge at y = 146)
    //   image 4 (ImClip)  96 x 96 px at 320,20 shown 96 x 96 pt (1 pt per pixel, top edge at y = 116), clipped to 330,30 80 x 60
    // a red rectangle 100,90 70 x 56 and the text "Plain 1234" at 180,110 stand on the bare page.
    private const double PageHeight = 330;
    private static readonly PixelRect GradBox = new(24, 20, 48, 48);

    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PageRect FromUser(double left, double bottom, double right, double top, double pageHeight = PageHeight) => new(left, pageHeight - top, right, pageHeight - bottom);

    /// <summary>The area over the box of image 0 (columns 24 to 72, rows 20 to 68), on pixel edges.</summary>
    private static PageRect GradArea() => FromUser(46, 202, 118, 274);

    private static string Copy(TempFolder folder, string name)
    {
        var path = Path.Combine(folder.Path, name);
        File.Copy(TestData.Pdf(name), path);
        return path;
    }

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
    private static async Task<List<(int Width, int Height, byte[] Bgra)>> ReadImagesAsync(string path, int pageIndex = 0)
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

    private static async Task<int> CountObjectsAsync(string path, int type, int pageIndex = 0)
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

    private async Task<string> TextOfAsync(string path)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var text = await document.GetPageTextAsync(0, Ct);
        return string.Concat(text.Chars.Select(static c => c.Text));
    }

    private static byte[] Region(byte[] bgra, int width, PixelRect box)
    {
        var part = new byte[box.Width * box.Height * 4];
        for (var y = 0; y < box.Height; y++)
        {
            Buffer.BlockCopy(bgra, (((box.Y + y) * width) + box.X) * 4, part, y * box.Width * 4, box.Width * 4);
        }

        return part;
    }

    private static bool Inside(PixelRect box, int x, int y) => x >= box.X && x < box.X + box.Width && y >= box.Y && y < box.Y + box.Height;

    /// <summary>Every pixel of <paramref name="after"/> outside all boxes equals the one of <paramref name="before"/>, bit for bit.</summary>
    private static void AssertOutsideIdentical(byte[] before, byte[] after, int width, int height, params PixelRect[] boxes)
    {
        Assert.Equal(before.Length, after.Length);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (boxes.Any(box => Inside(box, x, y)))
                {
                    continue;
                }

                var at = ((y * width) + x) * 4;
                Assert.True(
                    before[at] == after[at] && before[at + 1] == after[at + 1] && before[at + 2] == after[at + 2] && before[at + 3] == after[at + 3],
                    $"The pixel ({x}, {y}) outside the boxes changed from {before[at]},{before[at + 1]},{before[at + 2]},{before[at + 3]} to {after[at]},{after[at + 1]},{after[at + 2]},{after[at + 3]}.");
            }
        }
    }

    private static void AssertSolid(byte[] bgra, int width, PixelRect box, byte blue, byte green, byte red)
    {
        for (var y = box.Y; y < box.Y + box.Height; y++)
        {
            for (var x = box.X; x < box.X + box.Width; x++)
            {
                var at = ((y * width) + x) * 4;
                Assert.True(
                    bgra[at] == blue && bgra[at + 1] == green && bgra[at + 2] == red && bgra[at + 3] == 255,
                    $"The pixel ({x}, {y}) is {bgra[at]},{bgra[at + 1]},{bgra[at + 2]},{bgra[at + 3]} and not the fill.");
            }
        }
    }

    private static (byte Blue, byte Green, byte Red) PixelOf(RenderedImage image, int x, int y)
    {
        var at = (y * image.Stride) + (x * 4);
        var span = image.Pixels.Span;
        return (span[at], span[at + 1], span[at + 2]);
    }

    private async Task<RenderedImage> RenderAsync(string path, double scale, int pageIndex = 0)
    {
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        return await document.RenderPageAsync(pageIndex, scale, new RenderOptions { Priority = PdfPriority.Background }, Ct);
    }

    // ---- The box gets the blocks, the rest stays ----

    [Theory]
    [InlineData(RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse)]
    [InlineData(RedactionImageTreatment.Pixelate, RedactionImageStrength.VeryCoarse)]
    [InlineData(RedactionImageTreatment.Blur, RedactionImageStrength.Coarse)]
    [InlineData(RedactionImageTreatment.Blur, RedactionImageStrength.VeryCoarse)]
    public async Task PixelsInTheBoxEqualTheExpectedBlocks(RedactionImageTreatment treatment, RedactionImageStrength strength)
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var before = await ReadImagesAsync(path);
        Assert.Equal(5, before.Count);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [GradArea()], RedactionFill.Black, treatment, strength, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImagesOverwritten);
        Assert.Equal(1, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"));
        Assert.Equal(5, after.Count);

        var grid = ImageBlocks.Plan(GradBox.Width, GradBox.Height, strength)!;
        var expected = ImageBlocks.Expected(before[0].Bgra, 96, 96, GradBox, grid, treatment);
        Assert.NotEqual(Region(before[0].Bgra, 96, GradBox), expected);
        Assert.Equal(expected, Region(after[0].Bgra, 96, GradBox));
        AssertOutsideIdentical(before[0].Bgra, after[0].Bgra, 96, 96, GradBox);

        // The pipeline of the render check accepts what was written: outside the area the page looks as before.
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        Assert.Empty(await document.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([GradArea()], impact)], Ct));
    }

    [Fact]
    public async Task PixelsOutsideTheBoxAreBitIdentical()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var before = await ReadImagesAsync(path);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        Assert.Null((await document.RedactPageAsync(0, [GradArea()], RedactionFill.Black, RedactionImageTreatment.Blur, RedactionImageStrength.Coarse, Ct)).Refusal);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"));
        Assert.Equal(before.Count, after.Count);
        AssertOutsideIdentical(before[0].Bgra, after[0].Bgra, 96, 96, GradBox);

        // The images the area does not touch are untouched in every pixel.
        for (var n = 1; n < before.Count; n++)
        {
            Assert.Equal((before[n].Width, before[n].Height), (after[n].Width, after[n].Height));
            Assert.Equal(before[n].Bgra, after[n].Bgra);
        }
    }

    [Fact]
    public async Task AnImageAreaCarriesNoOriginalDetailBeyondItsBlockMeans()
    {
        // Within each block the written pixels of Pixelate are one colour and alpha is opaque, even where the original was not.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        await document.RedactPageAsync(0, [GradArea()], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        var after = (await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf")))[0];
        var grid = ImageBlocks.Plan(GradBox.Width, GradBox.Height, RedactionImageStrength.Coarse)!;
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                var first = (((GradBox.Y + grid.RowEdges[row]) * 96) + GradBox.X + grid.ColumnEdges[column]) * 4;
                for (var y = grid.RowEdges[row]; y < grid.RowEdges[row + 1]; y++)
                {
                    for (var x = grid.ColumnEdges[column]; x < grid.ColumnEdges[column + 1]; x++)
                    {
                        var at = (((GradBox.Y + y) * 96) + GradBox.X + x) * 4;
                        Assert.Equal(after.Bgra[first..(first + 4)], after.Bgra[at..(at + 4)]);
                    }
                }

                Assert.Equal(255, after.Bgra[first + 3]);
            }
        }
    }

    [Fact]
    public async Task SolidTreatmentCountsNothingAndKeepsTheOldResult()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [GradArea(), FromUser(360, 228, 375, 243)], RedactionFill.Black, Ct);
        Assert.Equal(1, impact.ImagesOverwritten);
        Assert.Equal(1, impact.ImagesRemovedWhole);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        Assert.True(impact.ImageSolidReasons is null or { Count: 0 });
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"));
        AssertSolid(after[0].Bgra, 96, GradBox, 0, 0, 0);
    }

    // ---- Text, vector and the mixed area ----

    [Fact]
    public async Task TextAndVectorInTheAreaGetTheSolidFill()
    {
        // The area holds the red rectangle and the line "Plain 1234" and no image: even with pixelating chosen it is the opaque area.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var area = FromUser(95, 85, 260, 150);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        Assert.True(impact.PathsCropped + impact.PathsRemovedWhole >= 1);
        Assert.True(impact.CharsRemoved >= 8);
        var output = await SaveAsync(document, folder, "out.pdf");
        Assert.DoesNotContain("Plain", await TextOfAsync(output), StringComparison.Ordinal);
        Assert.Contains("Secret", await TextOfAsync(output), StringComparison.Ordinal);

        var render = await RenderAsync(output, 1);
        foreach (var (x, y) in new[] { (110, 330 - 100), (130, 330 - 118), (190, 330 - 115), (250, 330 - 140) })
        {
            Assert.Equal(((byte)0, (byte)0, (byte)0), PixelOf(render, x, y));
        }
    }

    [Fact]
    public async Task TextOverATreatedImageIsRemovedAndTheImageIsTreated()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var before = await ReadImagesAsync(path);

        // Over image 1 (2 pt per pixel, top edge at 288): columns 3 to 46 and rows 24 to 44 hold the text "Secret 4711".
        var area = FromUser(176, 200, 262, 240);
        var box = new PixelRect(3, 24, 43, 20);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Blur, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        var output = await SaveAsync(document, folder, "out.pdf");
        Assert.DoesNotContain("Secret", await TextOfAsync(output), StringComparison.Ordinal);
        var after = await ReadImagesAsync(output);
        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
        Assert.Equal(ImageBlocks.Expected(before[1].Bgra, 64, 64, box, grid, RedactionImageTreatment.Blur), Region(after[1].Bgra, 64, box));
        AssertOutsideIdentical(before[1].Bgra, after[1].Bgra, 64, 64, box);

        // No opaque area lies over the treated image: the only paths are the red rectangle.
        Assert.Equal(await CountObjectsAsync(path, FpdfConstants.PageObjPath), await CountObjectsAsync(output, FpdfConstants.PageObjPath));
    }

    [Fact]
    public async Task MixedAreaDrawsTheSolidFillOnlyOutsideTheTreatedImage()
    {
        // The area reaches from inside image 0 (right edge at x = 154) to the bare page: x 100 to 165, y 200 to 260.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var before = await ReadImagesAsync(path);
        var area = FromUser(100, 200, 165, 260);
        var box = new PixelRect(60, 29, 36, 41);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        var output = await SaveAsync(document, folder, "out.pdf");

        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
        var after = await ReadImagesAsync(output);
        Assert.Equal(ImageBlocks.Expected(before[0].Bgra, 96, 96, box, grid, RedactionImageTreatment.Pixelate), Region(after[0].Bgra, 96, box));

        // One opaque piece beside the red rectangle: the strip x 154 to 165 outside the image, not a rectangle over the whole area.
        Assert.Equal(1 + await CountObjectsAsync(path, FpdfConstants.PageObjPath), await CountObjectsAsync(output, FpdfConstants.PageObjPath));
        var render = await RenderAsync(output, 2);
        Assert.Equal(((byte)0, (byte)0, (byte)0), PixelOf(render, 2 * 160, 2 * (330 - 230)));
        Assert.Equal(((byte)0, (byte)0, (byte)0), PixelOf(render, 2 * 164, 2 * (330 - 202)));
        var inside = PixelOf(render, 2 * 127, 2 * (330 - 230));
        Assert.NotEqual(((byte)0, (byte)0, (byte)0), inside);
    }

    // ---- Where the rule does not apply ----

    [Fact]
    public async Task RotatedImageFallsBackToSolidAndIsCounted()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var area = FromUser(360, 228, 375, 243);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImagesRemovedWhole);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(1, impact.ImageAreasSolid);
        Assert.Equal([ImageSolidReason.TurnedOrSheared], impact.ImageSolidReasons);
        var output = await SaveAsync(document, folder, "out.pdf");
        Assert.Equal(4, (await ReadImagesAsync(output)).Count);
        var render = await RenderAsync(output, 1);
        Assert.Equal(((byte)0, (byte)0, (byte)0), PixelOf(render, 367, 330 - 235));
    }

    [Fact]
    public async Task ATooSmallBoxFallsBackToSolidAndIsCounted()
    {
        // Image 3 is 7 x 7 pixels: the box under the area is 3 x 3, far from 2 x 2 blocks of 8 pixels.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var before = await ReadImagesAsync(path);
        var area = FromUser(20, 100, 40, 120);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.White, RedactionImageTreatment.Blur, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImagesOverwritten);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(1, impact.ImageAreasSolid);
        Assert.Equal([ImageSolidReason.TooSmall], impact.ImageSolidReasons);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"));
        AssertSolid(after[3].Bgra, 7, new PixelRect(1, 3, 3, 3), 255, 255, 255);
        AssertOutsideIdentical(before[3].Bgra, after[3].Bgra, 7, 7, new PixelRect(1, 3, 3, 3));
    }

    [Fact]
    public async Task AClippedImageFallsBackToSolidAndIsCounted()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var area = FromUser(340, 40, 400, 80);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(1, impact.ImageAreasSolid);
        Assert.Equal([ImageSolidReason.Clipped], impact.ImageSolidReasons);
        var after = await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"));
        AssertSolid(after[4].Bgra, 96, new PixelRect(20, 36, 60, 40), 0, 0, 0);
    }

    [Fact]
    public async Task OverlappingAreasOnOneImageFallBack()
    {
        // Two areas whose boxes share columns 48 to 72 fall back; a third one on the same image, apart from them, is pixelated.
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var before = await ReadImagesAsync(path);
        var areas = new[] { GradArea(), FromUser(82, 202, 154, 274), FromUser(10, 172, 34, 196) };
        var apart = new PixelRect(0, 72, 16, 16);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, areas, RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        Assert.Equal(2, impact.ImageAreasSolid);
        Assert.Equal([ImageSolidReason.Overlapping, ImageSolidReason.Overlapping], impact.ImageSolidReasons);
        var output = await SaveAsync(document, folder, "out.pdf");
        var after = await ReadImagesAsync(output);
        AssertSolid(after[0].Bgra, 96, GradBox, 0, 0, 0);
        AssertSolid(after[0].Bgra, 96, new PixelRect(48, 20, 48, 48), 0, 0, 0);
        var grid = ImageBlocks.Plan(apart.Width, apart.Height, RedactionImageStrength.Coarse)!;
        Assert.Equal(ImageBlocks.Expected(before[0].Bgra, 96, 96, apart, grid, RedactionImageTreatment.Pixelate), Region(after[0].Bgra, 96, apart));
        AssertOutsideIdentical(before[0].Bgra, after[0].Bgra, 96, 96, GradBox, new PixelRect(48, 20, 48, 48), apart);

        // The two areas that fell back have their whole rectangle (the third one lies inside the image and needs none).
        Assert.Equal(2 + await CountObjectsAsync(path, FpdfConstants.PageObjPath), await CountObjectsAsync(output, FpdfConstants.PageObjPath));
    }

    [Fact]
    public async Task ManyImagesInOneAreaFallBackBeforeAnythingIsWritten()
    {
        // A staggered grid of 6 x 6 images of 16 x 16 pixels under one area: the opaque area would be cut into more pieces than allowed, so every
        // image gets the solid fill instead, and the whole rectangle is drawn.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var resources = new System.Text.StringBuilder("<< /XObject << ");
        var content = new System.Text.StringBuilder();
        for (var n = 0; n < 36; n++)
        {
            var pixels = new byte[16 * 16 * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)((i * 7) + (n * 11));
            }

            var id = pdf.AddStream("/Type /XObject /Subtype /Image /Width 16 /Height 16 /BitsPerComponent 8 /ColorSpace /DeviceRGB", pixels);
            resources.Append(CultureInfo.InvariantCulture, $"/Im{n} {id} 0 R ");
            content.Append(CultureInfo.InvariantCulture, $"q 16 0 0 16 {10 + (30 * (n % 6)) + ((n * 3) % 7)} {10 + (30 * (n / 6)) + ((n * 5) % 11)} cm /Im{n} Do Q\n");
        }

        resources.Append(">> >>");
        pdf.AddPage("/MediaBox [0 0 220 220]", resources.ToString(), content.ToString());
        var path = Path.Combine(folder.Path, "crowd.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [FromUser(5, 5, 200, 200, 220)], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(36, impact.ImagesOverwritten);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(36, impact.ImageAreasSolid);
        Assert.All(impact.ImageSolidReasons!, static r => Assert.Equal(ImageSolidReason.TooManyImages, r));
        var output = await SaveAsync(document, folder, "out.pdf");
        foreach (var image in await ReadImagesAsync(output))
        {
            AssertSolid(image.Bgra, 16, new PixelRect(0, 0, 16, 16), 0, 0, 0);
        }

        Assert.Equal(1, await CountObjectsAsync(output, FpdfConstants.PageObjPath));
    }

    [Fact]
    public async Task AnalyzeCountsTheSameAsApplying()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        var areas = new[] { GradArea(), FromUser(10, 172, 34, 196), FromUser(360, 228, 375, 243), FromUser(20, 100, 40, 120) };
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var dry = await document.AnalyzeRedactionAsync(0, areas, RedactionImageTreatment.Blur, RedactionImageStrength.VeryCoarse, Ct);
        var done = await document.RedactPageAsync(0, areas, RedactionFill.Black, RedactionImageTreatment.Blur, RedactionImageStrength.VeryCoarse, Ct);
        Assert.Null(dry.Refusal);
        Assert.Equal(2, done.ImageAreasTreated);
        Assert.Equal(2, done.ImageAreasSolid);
        Assert.Equal(done.ImageAreasTreated, dry.ImageAreasTreated);
        Assert.Equal(done.ImageAreasSolid, dry.ImageAreasSolid);
        Assert.Equal(done.ImageSolidReasons, dry.ImageSolidReasons);
        Assert.Equal(done.ImagesOverwritten, dry.ImagesOverwritten);
    }

    [Fact]
    public async Task HundredMegapixelImageIsRefusedByTheLimitNotTreated()
    {
        using var folder = new TempFolder();
        var pdf = new MiniPdf();

        // The dictionary says 10,000 x 10,000 pixels; the data is a few bytes. Loading it would take 400 MB.
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 10000 /Height 10000 /BitsPerComponent 8 /ColorSpace /DeviceGray /Filter /FlateDecode", [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01]);
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Im1 {image} 0 R >> >>", "q 100 0 0 100 50 50 cm /Im1 Do Q\n");
        var path = Path.Combine(folder.Path, "huge-image.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var area = FromUser(60, 60, 90, 90, 200);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        var dry = await document.AnalyzeRedactionAsync(0, [area], RedactionImageTreatment.Blur, RedactionImageStrength.VeryCoarse, Ct);
        Assert.True(GC.GetTotalAllocatedBytes(precise: false) - allocated < 100_000_000, "The image seems to have been loaded.");
        Assert.NotNull(impact.Refusal);
        Assert.Equal(RedactionRefusalKind.PageNotRedactable, impact.Refusal!.Kind);
        Assert.Contains("10000 x 10000", impact.Refusal.Reason, StringComparison.Ordinal);
        Assert.NotNull(dry.Refusal);
        Assert.Equal(0, impact.ImageAreasTreated);
        Assert.Equal(0, dry.ImageAreasTreated);
        Assert.False(document.IsRedactionTainted);
    }

    // ---- Rotated page and CropBox ----

    private static byte[] Gradient(int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 3;
                pixels[i] = (byte)((x * 4) % 256);
                pixels[i + 1] = (byte)((y * 8) % 256);
                pixels[i + 2] = (byte)(((x * y * 3) + (x ^ y)) % 256);
            }
        }

        return pixels;
    }

    [Fact]
    public async Task TreatmentOnRotatedPageSitsWhereTheUserSees()
    {
        // MediaBox 200 x 100 with /Rotate 90 is shown 100 wide and 200 high: the user space point (x, y) is shown at (y, x) from the top left.
        // The image (64 x 32 px, 2 pt per pixel, lower left corner at 20,10) lies in user space at x 20 to 148 and y 10 to 74.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 64 /Height 32 /BitsPerComponent 8 /ColorSpace /DeviceRGB", Gradient(64, 32));
        pdf.AddPage("/MediaBox [0 0 200 100] /Rotate 90", $"<< /XObject << /Im1 {image} 0 R >> >>", "q 128 0 0 64 20 10 cm /Im1 Do Q\n");
        var path = Path.Combine(folder.Path, "rotated.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        var before = (await ReadImagesAsync(path))[0];

        // Shown x 26 to 58 and y 20 to 68 is user x 20 to 68 (columns 0 to 24) and y 26 to 58 (rows 8 to 24 from the top);
        // shown y 100 to 170 is user x 100 to 170: columns 40 to 64 (the image ends at 148) and the same rows, and x 148 to 170 is bare page.
        var inImage = new PageRect(26, 20, 58, 68);
        var overTheEdge = new PageRect(26, 100, 58, 170);
        var boxOne = new PixelRect(0, 8, 24, 16);
        var boxTwo = new PixelRect(40, 8, 24, 16);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [inImage, overTheEdge], RedactionFill.Black, RedactionImageTreatment.Blur, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(2, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        var output = await SaveAsync(document, folder, "out.pdf");
        var after = (await ReadImagesAsync(output))[0];
        foreach (var box in new[] { boxOne, boxTwo })
        {
            var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
            Assert.Equal(ImageBlocks.Expected(before.Bgra, 64, 32, box, grid, RedactionImageTreatment.Blur), Region(after.Bgra, 64, box));
        }

        AssertOutsideIdentical(before.Bgra, after.Bgra, 64, 32, boxOne, boxTwo);

        // The opaque piece of the second area is user x 148 to 170, shown at y 148 to 170 (x 26 to 58), and not anywhere mirrored.
        var render = await RenderAsync(output, 1);
        Assert.Equal(100, render.Width);
        Assert.Equal(200, render.Height);
        Assert.Equal(((byte)0, (byte)0, (byte)0), PixelOf(render, 42, 160));
        Assert.NotEqual(((byte)0, (byte)0, (byte)0), PixelOf(render, 42, 120));
        Assert.NotEqual(((byte)0, (byte)0, (byte)0), PixelOf(render, 42, 40));
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        Assert.Empty(await document.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([inImage, overTheEdge], impact)], Ct));
    }

    [Fact]
    public async Task CropBoxPageTreatmentIsAligned()
    {
        // MediaBox 300 x 200 with the CropBox 20,10 to 280,190: the shown point (x, y) is the user space point (20 + x, 190 - y). The image
        // (48 x 48 px, 2 pt per pixel, lower left corner at 60,50) lies at x 60 to 156 and y 50 to 146.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 48 /Height 48 /BitsPerComponent 8 /ColorSpace /DeviceRGB", Gradient(48, 48));
        pdf.AddPage("/MediaBox [0 0 300 200] /CropBox [20 10 280 190]", $"<< /XObject << /Im1 {image} 0 R >> >>", "q 96 0 0 96 60 50 cm /Im1 Do Q\n");
        var path = Path.Combine(folder.Path, "crop.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        var before = (await ReadImagesAsync(path))[0];

        // Shown x 56 to 120, y 60 to 124 is user x 76 to 140, y 66 to 130: columns 8 to 40 and rows 8 to 40.
        var area = new PageRect(56, 60, 120, 124);
        var box = new PixelRect(8, 8, 32, 32);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.VeryCoarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        var after = (await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf")))[0];
        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.VeryCoarse)!;
        Assert.Equal(ImageBlocks.Expected(before.Bgra, 48, 48, box, grid, RedactionImageTreatment.Pixelate), Region(after.Bgra, 48, box));
        AssertOutsideIdentical(before.Bgra, after.Bgra, 48, 48, box);
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        Assert.Empty(await document.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));
    }

    [Fact]
    public async Task AsymmetricCropBoxTreatmentOfANonSquareImageIsAligned()
    {
        // MediaBox 300 x 200 with the CropBox 30,15 to 270,185 (different offsets on the two axes): the shown point (x, y) is the user space
        // point (30 + x, 185 - y). The image (60 x 40 px) is shown 120 x 120 pt, so a pixel is 2 pt wide and 3 pt high; its lower left corner
        // is at 50,40 (x 50 to 170, y 40 to 160).
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 60 /Height 40 /BitsPerComponent 8 /ColorSpace /DeviceRGB", Gradient(60, 40));
        pdf.AddPage("/MediaBox [0 0 300 200] /CropBox [30 15 270 185]", $"<< /XObject << /Im1 {image} 0 R >> >>", "q 120 0 0 120 50 40 cm /Im1 Do Q\n");
        var path = Path.Combine(folder.Path, "crop-asymmetric.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        var before = (await ReadImagesAsync(path))[0];

        // Shown x 40 to 100 is user x 70 to 130: columns (70 - 50) / 2 = 10 to (130 - 50) / 2 = 40. Shown y 31 to 91 is user y 154 down to 94:
        // rows (160 - 154) / 3 = 2 to (160 - 94) / 3 = 22.
        var area = new PageRect(40, 31, 100, 91);
        var box = new PixelRect(10, 2, 30, 20);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Blur, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        var output = await SaveAsync(document, folder, "out.pdf");
        var after = (await ReadImagesAsync(output))[0];
        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
        Assert.Equal(ImageBlocks.Expected(before.Bgra, 60, 40, box, grid, RedactionImageTreatment.Blur), Region(after.Bgra, 60, box));
        AssertOutsideIdentical(before.Bgra, after.Bgra, 60, 40, box);

        // The box the check reads back from the written file is the same rectangle of the same pixels.
        await using var reopened = await engine.OpenAsync(output, cancellationToken: Ct);
        var parts = await reopened.GetImageAreaPixelsAsync(0, area, Ct);
        Assert.Equal((box.Width, box.Height), (parts.Single().Width, parts.Single().Height));
        Assert.Equal(Region(after.Bgra, 60, box), parts.Single().Bgra);
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        Assert.Empty(await document.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));
    }

    [Fact]
    public async Task AnImageWithASoftMaskIsTreatedOpaqueInsideTheBoxAndKeepsItsMaskOutside()
    {
        // 32 x 32 pixels with an /SMask whose alpha varies from pixel to pixel, shown 64 x 64 pt at 20,20 (2 pt per pixel) on 120 x 120.
        // The area is user x 36 to 68, y 36 to 68: columns 8 to 24 and rows (84 - 68) / 2 = 8 to 24.
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var mask = new byte[32 * 32];
        for (var y = 0; y < 32; y++)
        {
            for (var x = 0; x < 32; x++)
            {
                mask[(y * 32) + x] = (byte)(40 + (((x * 5) + (y * 3)) % 160));
            }
        }

        var maskId = pdf.AddStream("/Type /XObject /Subtype /Image /Width 32 /Height 32 /BitsPerComponent 8 /ColorSpace /DeviceGray", mask);
        var image = pdf.AddStream($"/Type /XObject /Subtype /Image /Width 32 /Height 32 /BitsPerComponent 8 /ColorSpace /DeviceRGB /SMask {maskId} 0 R", Gradient(32, 32));
        pdf.AddPage("/MediaBox [0 0 120 120]", $"<< /XObject << /Im1 {image} 0 R >> >>", "q 64 0 0 64 20 20 cm /Im1 Do Q\n");
        var path = Path.Combine(folder.Path, "smask.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);
        var before = (await ReadImagesAsync(path)).Single();
        Assert.Contains(Enumerable.Range(0, 32 * 32), i => before.Bgra[(i * 4) + 3] is > 0 and < 255);

        var area = FromUser(36, 36, 68, 68, 120);
        var box = new PixelRect(8, 8, 16, 16);
        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        var after = (await ReadImagesAsync(await SaveAsync(document, folder, "out.pdf"))).Single();

        // Inside the box the blocks of the colours, opaque (the original alpha does not reach the result), and outside every pixel,
        // alpha included, as it was.
        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
        var inside = Region(after.Bgra, 32, box);
        Assert.Equal(ImageBlocks.Expected(before.Bgra, 32, 32, box, grid, RedactionImageTreatment.Pixelate), inside);
        for (var i = 3; i < inside.Length; i += 4)
        {
            Assert.Equal(255, inside[i]);
        }

        AssertOutsideIdentical(before.Bgra, after.Bgra, 32, 32, box);
    }

    [Fact]
    public async Task AnImageInsideAFormXObjectIsTreatedWhereItIsShown()
    {
        // The form is placed at 40,30 on a 200 x 200 page; inside it the image (48 x 48 px, 2 pt per pixel) has its lower left corner at 10,20,
        // so on the page it lies at x 50 to 146 and y 50 to 146. The area is user x 66 to 114 (columns 8 to 32) and y 70 to 118 (rows
        // (146 - 118) / 2 = 14 to (146 - 70) / 2 = 38).
        using var folder = new TempFolder();
        var pdf = new MiniPdf();
        var image = pdf.AddStream("/Type /XObject /Subtype /Image /Width 48 /Height 48 /BitsPerComponent 8 /ColorSpace /DeviceRGB", Gradient(48, 48));
        var form = pdf.AddStream($"/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /Im1 {image} 0 R >> >>", System.Text.Encoding.Latin1.GetBytes("q 96 0 0 96 10 20 cm /Im1 Do Q\n"));
        pdf.AddPage("/MediaBox [0 0 200 200]", $"<< /XObject << /Fm1 {form} 0 R >> >>", "q 1 0 0 1 40 30 cm /Fm1 Do Q\n");
        var path = Path.Combine(folder.Path, "in-form.pdf");
        await File.WriteAllBytesAsync(path, pdf.Build(), Ct);

        var area = FromUser(66, 70, 114, 118, 200);
        var box = new PixelRect(8, 14, 24, 24);
        await using var original = await engine.OpenAsync(path, cancellationToken: Ct);
        var originalPart = (await original.GetImageAreaPixelsAsync(0, area, Ct)).Single();
        Assert.Equal((box.Width, box.Height), (originalPart.Width, originalPart.Height));
        var generated = GradientBgra(48, 48);
        Assert.Equal(Region(generated, 48, box), originalPart.Bgra);

        await using var document = await engine.OpenAsync(path, cancellationToken: Ct);
        var impact = await document.RedactPageAsync(0, [area], RedactionFill.Black, RedactionImageTreatment.Pixelate, RedactionImageStrength.Coarse, Ct);
        Assert.Null(impact.Refusal);
        Assert.Equal(1, impact.ImageAreasTreated);
        Assert.Equal(0, impact.ImageAreasSolid);
        var output = await SaveAsync(document, folder, "out.pdf");
        await using var reopened = await engine.OpenAsync(output, cancellationToken: Ct);
        var after = (await reopened.GetImageAreaPixelsAsync(0, area, Ct)).Single();
        var grid = ImageBlocks.Plan(box.Width, box.Height, RedactionImageStrength.Coarse)!;
        Assert.Equal((box.Width, box.Height), (after.Width, after.Height));
        Assert.Equal(ImageBlocks.Expected(generated, 48, 48, box, grid, RedactionImageTreatment.Pixelate), after.Bgra);
        Assert.Empty(await document.VerifyRedactionRenderAsync(original, [RedactionPageAreas.For([area], impact)], Ct));
    }

    private static byte[] GradientBgra(int width, int height)
    {
        var rgb = Gradient(width, height);
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            bgra[i * 4] = rgb[(i * 3) + 2];
            bgra[(i * 4) + 1] = rgb[(i * 3) + 1];
            bgra[(i * 4) + 2] = rgb[i * 3];
            bgra[(i * 4) + 3] = 255;
        }

        return bgra;
    }

    // ---- The analysis and the report say it (milestone 14.1, task 13) ----

    private static RedactionPlan TreatedPlan(RedactionImageTreatment treatment, params PageRect[] areas) =>
        RedactionPlan.Empty.WithImageTreatment(treatment).WithImageStrength(RedactionImageStrength.Coarse)
            .With(new RedactionMark(Guid.NewGuid(), RedactionKind.Area, 0, areas, null, false));

    // Shown coordinates (top left origin, 330 high): the area over image 0, the 7 x 7 image, the turned image and the clipped image.
    private static readonly PageRect[] FourAreas = [new(46, 56, 118, 128), new(20, 210, 40, 230), new(360, 87, 375, 102), new(340, 250, 400, 290)];

    [Fact]
    public async Task AnalysisCountsTreatedAndSolidAreas()
    {
        using var folder = new TempFolder();
        var path = Copy(folder, "redact-image-blocks.pdf");
        await using var shown = await engine.OpenAsync(path, cancellationToken: Ct);

        var analysis = await new RedactionExecutor(engine).AnalyzeAsync(TreatedPlan(RedactionImageTreatment.Pixelate, FourAreas), shown, path, null, Ct);

        Assert.False(analysis.IsRefused);
        Assert.Equal(RedactionImageTreatment.Pixelate, analysis.ImageTreatment);
        var page = Assert.Single(analysis.Pages);
        Assert.Equal(1, page.ImageAreasTreated);
        Assert.Equal(3, page.ImageAreasSolid);
        Assert.Equivalent(new[] { ImageSolidReason.TooSmall, ImageSolidReason.TurnedOrSheared, ImageSolidReason.Clipped }, page.ImageSolidReasons!);

        // The English report, before applying: the numbers, the reasons and the caution about text in images.
        var report = RedactionReportText.Of(analysis);
        Assert.Contains("Image areas:", report, StringComparison.Ordinal);
        Assert.Contains("1 image area will be pixelated", report, StringComparison.Ordinal);
        Assert.Contains("3 image areas get the solid fill instead", report, StringComparison.Ordinal);
        foreach (var reason in page.ImageSolidReasons!)
        {
            Assert.Contains(RedactionReportText.ImageSolidReasonText(reason), report, StringComparison.Ordinal);
        }

        Assert.Contains(RedactionReportText.ImageTextCaution, report, StringComparison.Ordinal);

        // Nothing is promised that the check does not do: the "Will be checked" part does not say text in images is covered.
        Assert.DoesNotContain("legible text", report, StringComparison.OrdinalIgnoreCase);

        // The same plan blurred says "blurred"; the solid plan has no such part.
        var blurred = await new RedactionExecutor(engine).AnalyzeAsync(TreatedPlan(RedactionImageTreatment.Blur, FourAreas), shown, path, null, Ct);
        Assert.Contains("1 image area will be blurred", RedactionReportText.Of(blurred), StringComparison.Ordinal);
        var solid = await new RedactionExecutor(engine).AnalyzeAsync(TreatedPlan(RedactionImageTreatment.Solid, FourAreas), shown, path, null, Ct);
        Assert.Equal(0, solid.Pages.Sum(static p => p.ImageAreasTreated + p.ImageAreasSolid));
        Assert.DoesNotContain("Image areas:", RedactionReportText.Of(solid), StringComparison.Ordinal);
        Assert.DoesNotContain(RedactionReportText.ImageTextCaution, RedactionReportText.Of(solid), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReasonForTheSolidFillHasASentence()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in Enum.GetValues<ImageSolidReason>())
        {
            var text = RedactionReportText.ImageSolidReasonText(reason);
            Assert.False(string.IsNullOrWhiteSpace(text), $"{reason} has no sentence");
            Assert.True(seen.Add(text), $"{reason} shares its sentence with another reason");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => RedactionReportText.ImageSolidReasonText((ImageSolidReason)99));
    }
}
