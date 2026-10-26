using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Pdfium;

/// <summary>
/// Drawings (ink, square, circle) on the dark page (milestone 10, task 9). PDFium's colour scheme draws every stroke of a
/// drawing in the page foreground, like the text; the dark render paints each pixel PDFium coloured in the drawing's own
/// colour, lightened to 3:1 against the dark ground where needed, with PDFium's share of the pixel. Each check compares
/// three renders of the same page: without the drawing, with it as PDFium alone draws it dark (the painting switched off
/// through <c>PdfiumDocument.DarkMarkupReader</c>), and with it painted.
/// </summary>
public sealed class DarkPageDrawingTests : IDisposable
{
    private static readonly AnnotationColor Red = new(220, 30, 30);
    private static readonly AnnotationColor Black = new(0, 0, 0);
    private static readonly AnnotationColor Green = new(40, 170, 80);

    private readonly TempFolder folder = new();

    private static RenderOptions Dark { get; } = new() { PageTheme = PageTheme.Dark };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task RedInkKeepsItsHueOnTheDarkPage(double scale)
    {
        // A freehand stroke with a gentle bend, a sharp turn within the miter limit, a hairpin beyond it (beveled by the
        // limit, PDFium's clipped miter) and a smooth wave of short segments (as the smoothing leaves them).
        var wave = Enumerable.Range(0, 160).Select(i => new DisplayPoint(40 + (i * 0.5), 160 + (6 * Math.Sin(i / 8.0)))).ToArray();
        IReadOnlyList<DisplayPoint>[] strokes =
        [
            [new(20, 120), new(60, 30), new(100, 120), new(130, 110)],
            [new(150, 40), new(270, 45), new(150, 50)],
            wave,
        ];
        var renders = await RenderAsync(BlankPage(), scale, DrawingGeometry.Ink(strokes, 3), Red);

        var full = AssertRecoloured(renders, (_, _) => Rgb(Red));
        Assert.True(full > 200 * scale, $"only {full} pixels fully in the stroke at scale {scale}");

        // Red is light enough on the dark ground as it is (3:1): the drawing keeps exactly its colour.
        Assert.Equal(Rgb(Red), PdfiumDarkMarkups.LineColor(Rgb(Red), PageThemeColors.DarkBackground));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task BlackInkIsLightenedToThreeToOne(double scale)
    {
        // A black line of 1 pt and a black arrow of 2 pt: black would vanish on the dark ground, so it is lightened (like
        // the lines of M9) to the first grey with 3:1, and it is not the foreground PDFium alone would draw.
        var renders = await RenderAsync(
            BlankPage(),
            scale,
            [
                (DrawingGeometry.Line(new DisplayPoint(20, 30), new DisplayPoint(280, 90), 1), Black),
                (DrawingGeometry.Arrow(new DisplayPoint(30, 170), new DisplayPoint(250, 120), 2), Black),
            ]);
        var grey = PdfiumDarkMarkups.LineColor(0xFF000000, PageThemeColors.DarkBackground);

        var full = AssertRecoloured(renders, (_, _) => grey);
        Assert.True(full > 100 * scale, $"only {full} pixels fully in the strokes at scale {scale}");
        Assert.True(Contrast(grey, PageThemeColors.DarkBackground) >= 3, $"{grey:X8} has {Contrast(grey, PageThemeColors.DarkBackground):F2}:1");
        Assert.NotEqual(PageThemeColors.DarkForeground, grey);
        Assert.True(Contrast(PageThemeColors.DarkForeground, grey) > 1.5, "the lightened black looks like the page text");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(4.0)]
    public async Task SquareAndCircleKeepTheirHue(double scale)
    {
        // A blue rectangle of 4 pt on the left, a red ellipse of 2 pt and a green one of 0.5 pt (thinner than a pixel at
        // scale 1: PDFium draws it one pixel wide) on the right.
        var renders = await RenderAsync(
            BlankPage(),
            scale,
            [
                (DrawingGeometry.Rectangle(new PageRect(15.25, 20.5, 135.75, 180), 4), Blue),
                (DrawingGeometry.Ellipse(new PageRect(160, 15, 290, 105.5), 2), Red),
                (DrawingGeometry.Ellipse(new PageRect(170.3, 120, 280, 190), 0.5), Green),
            ]);

        var full = AssertRecoloured(renders, (x, _) => x < 150 * scale ? Rgb(Blue) : 0);
        Assert.True(full > 400 * scale, $"only {full} pixels fully in the rectangle and ellipses at scale {scale}");
        AssertRecoloured(renders, (x, y) => x < 150 * scale ? 0 : y < 112 * scale ? Rgb(Red) : Rgb(Green));
    }

    [Theory]
    [InlineData(1.5)]
    [InlineData(3.0)]
    public async Task RotatedPageStillCoversPdfiumsStroke(double scale)
    {
        // /Rotate 90 with a CropBox offset: PDFium's conversion is the only source of rotation and crop; the painted pixels
        // are the ones PDFium coloured, wherever the stroke lies on the displayed page (270 x 180 pt).
        var path = WritePdf(folder, "rotated.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /CropBox [10 20 190 290] /Rotate 90 >>",
        ]);
        var renders = await RenderAsync(
            path,
            scale,
            [
                (DrawingGeometry.Ink([[new(20, 20), new(80, 150), new(120, 30), new(125, 100)]], 2.5), Red),
                (DrawingGeometry.Arrow(new DisplayPoint(140, 160), new DisplayPoint(250, 30), 3), Red),
                (DrawingGeometry.Rectangle(new PageRect(150.5, 100, 260, 170), 2), Red),
                (DrawingGeometry.Ellipse(new PageRect(160, 20, 220, 90), 1.5), Red),
            ]);

        Assert.Equal(((int)Math.Ceiling(270 * scale), (int)Math.Ceiling(180 * scale)), (renders.Painted.Width, renders.Painted.Height));
        var full = AssertRecoloured(renders, (_, _) => Rgb(Red));
        Assert.True(full > 300 * scale, $"only {full} pixels fully in the strokes at scale {scale}");
    }

    [Fact]
    public async Task StrokeOverTextRecoloursOnlyThePixelsUnderIt()
    {
        // A line of 2 pt straight through "Hello, Pagewing" (text-basic.pdf, baseline at y 80): the text under the stroke is
        // hidden by it on the light page too, so its pixels take the colour; every pixel of the text away from the stroke
        // keeps exactly its light colour.
        const double Scale = 4;
        var copy = Path.Combine(folder.Path, "text.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        var (start, end, width) = (new DisplayPoint(10, 71), new DisplayPoint(290, 74), 2.0);
        var renders = await RenderAsync(copy, Scale, DrawingGeometry.Line(start, end, width), Red);

        var changed = 0;
        foreach (var (x, y) in AllPoints(renders.Painted))
        {
            if (Pixel(renders.Painted, x, y) == Pixel(renders.Pdfium, x, y))
            {
                continue;
            }

            // A pixel touched by the stroke: its square is at most half the width (in pixels, one at least) from the line.
            var distance = Distance((x + 0.5) / Scale, (y + 0.5) / Scale, start, end) * Scale;
            Assert.True(distance <= (width * Scale / 2) + 0.75, $"({x}, {y}) changed {distance:F2} px from the line");
            changed++;
        }

        Assert.True(changed > 2000, $"only {changed} pixels painted");
        var text = AllPoints(renders.Plain).Count(p => Pixel(renders.Plain, p.X, p.Y) != Ground && Pixel(renders.Painted, p.X, p.Y) == Rgb(Red));
        Assert.True(text > 50, "no pixel of the text under the stroke took its colour");
    }

    [Fact]
    public async Task TilesMatchTheWholePageRender()
    {
        // Tiles of 100 px at scale 4 cut through every drawing (and render 64 px more to their left, dropped afterwards):
        // each paints its part, and together they are the page (PDFium itself differs at annotation edges by up to 2).
        const double Scale = 4;
        var copy = Path.Combine(folder.Path, "tiles.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        await using var document = await OpenAsync(copy);
        await document.CreateDrawingAsync(0, DrawingGeometry.Ink([[new(10, 10), new(60, 190), new(110, 20), new(160, 180)]], 3), Red, null, null, Ct);
        await document.CreateDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(170, 110, 290, 190), 2), Blue, null, null, Ct);
        await document.CreateDrawingAsync(0, DrawingGeometry.Ellipse(new PageRect(180, 10, 280, 100), 1), Black, null, null, Ct);
        var full = await document.RenderPageAsync(0, Scale, Dark, Ct);

        const int TileSize = 100;
        for (var y = 0; y < full.Height; y += TileSize)
        {
            for (var x = 0; x < full.Width; x += TileSize)
            {
                var rect = new PixelRect(x, y, Math.Min(TileSize, full.Width - x), Math.Min(TileSize, full.Height - y));
                var tile = await document.RenderTileAsync(0, Scale, rect, Dark, Ct);
                for (var row = 0; row < tile.Height; row++)
                {
                    for (var column = 0; column < tile.Width; column++)
                    {
                        var (expected, actual) = (Pixel(full, x + column, y + row), Pixel(tile, column, row));
                        Assert.True(Near(expected, actual, 2), $"({x + column}, {y + row}): page {expected:X8}, tile {actual:X8}");
                    }
                }
            }
        }
    }

    [Fact]
    public async Task LightPageReadsNothing()
    {
        // The drawings are read in the same read as the markups (PdfDocument.DarkMarkupScans counts it).
        var copy = Path.Combine(folder.Path, "light.pdf");
        File.Copy(TestData.Pdf("text-basic.pdf"), copy);
        await using var document = await OpenAsync(copy);
        await document.CreateDrawingAsync(0, DrawingGeometry.Ink([[new(20, 150), new(150, 180), new(280, 150)]], 3), Red, null, null, Ct);
        var scans = document.DarkMarkupScans;

        // Light: the page as PDFium draws it, and nothing is read for the dark page.
        await document.RenderPageAsync(0, 2, cancellationToken: Ct);
        await document.RenderTileAsync(0, 2, new PixelRect(0, 0, 100, 100), cancellationToken: Ct);
        Assert.Equal(scans, document.DarkMarkupScans);

        // Dark without annotations: nothing either.
        await document.RenderPageAsync(0, 2, Dark with { Annotations = false }, Ct);
        Assert.Equal(scans, document.DarkMarkupScans);

        // Dark: one read per render, and the drawing is painted (the render differs from PDFium's own).
        var painted = await document.RenderPageAsync(0, 2, Dark, Ct);
        Assert.Equal(scans + 1, document.DarkMarkupScans);
        await document.ReplaceDarkMarkupReaderAsync((_, _) => DarkMarkupPaint.Empty);
        var pdfium = await document.RenderPageAsync(0, 2, Dark, Ct);
        Assert.False(painted.Pixels.Span.SequenceEqual(pdfium.Pixels.Span), "the drawing was not painted");
    }

    [Fact]
    public async Task HostilePageKeepsWhatItHas()
    {
        // Points: an ink with one stroke of 40,000 points, listed 30 times: the read stops after MaxTotalPoints (25 of them)
        // and keeps those. Layers: a page-wide stroke listed 2,000 times: the painting stops after MaxPaintedLayers covers
        // of the bitmap, and the page renders, painted.
        var zigzag = string.Join(' ', Enumerable.Range(0, 40_000).Select(i => $"{10 + (i % 180)} {20 + (i % 2 * 60)}"));
        var many = WritePdf(folder, "points.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [{string.Join(' ', Enumerable.Repeat("4 0 R", 30))}] >>",
            $"<< /Type /Annot /Subtype /Ink /Rect [0 0 200 100] /F 4 /C [1 0 0] /Border [0 0 1] /InkList [[{zigzag}]] >>",
        ]);
        var paint = await ReadPaintAsync(many);
        Assert.Equal(PdfiumDarkDrawings.MaxTotalPoints / 40_000, paint.Drawings.Count);

        var wide = WritePdf(folder, "layers.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [{string.Join(' ', Enumerable.Repeat("4 0 R", 2_000))}] >>",
            "<< /Type /Annot /Subtype /Ink /Rect [-500 -500 700 600] /F 4 /C [1 0 0] /Border [0 0 400] /InkList [[0 50 200 50]] >>",
        ]);
        Assert.Equal(2_000, (await ReadPaintAsync(wide)).Drawings.Count);
        await using var document = await OpenAsync(wide);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var image = await document.RenderPageAsync(0, 1, Dark, Ct);
        var elapsed = watch.Elapsed;
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"the dark render took {elapsed}");
        Assert.Equal(PdfiumDarkMarkups.LineColor(0xFFFF0000, PageThemeColors.DarkBackground), Pixel(image, 100, 50));
    }

    [Fact]
    public async Task StrokesOutsideTheBitmapCostLittle()
    {
        // A left tile of a page whose drawings lie mostly outside it (OutsidePage): it renders in time, and the line inside
        // it is still painted. Before pieces outside the bitmap were charged, painting this tile took 3.2 s (Release).
        const double Scale = 4;
        var path = OutsidePage();
        await using var document = await OpenAsync(path);
        var tile = new PixelRect(0, 0, 256, 400);
        await document.RenderTileAsync(0, Scale, tile, Dark, Ct);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var image = await document.RenderTileAsync(0, Scale, tile, Dark, Ct);
        var painted = watch.Elapsed;
        await document.ReplaceDarkMarkupReaderAsync((_, _) => DarkMarkupPaint.Empty);
        watch.Restart();
        await document.RenderTileAsync(0, Scale, tile, Dark, Ct);
        var pdfium = watch.Elapsed;

        Assert.Equal(0xFFFF0000u, Pixel(image, 120, 200));
        Assert.True(painted - pdfium < TimeSpan.FromSeconds(1), $"painting took {painted - pdfium} (render {painted}, PDFium alone {pdfium})");
    }

    [Fact]
    public async Task PaintWorkOutsideTheBitmapIsCharged()
    {
        // The same drawings painted directly into the left tile: every path, piece and row looked at costs at least one of
        // the budget besides the pixels, so the work stays within MaxPaintedLayers covers of the bitmap however much of it
        // lies outside; the line under them (painted last) still gets its colour.
        const int Width = 256, Height = 400;
        const double Scale = 4;
        var paint = await ReadPaintAsync(OutsidePage());
        Assert.Equal(5, paint.Drawings.Count);
        var colors = new PdfiumPageColors(PageThemeColors.DarkBackground, PageThemeColors.DarkForeground);

        var (pixels, budget) = (LineTile(), PdfiumDarkMarkups.MaxPaintedLayers * (long)Width * Height);
        var initial = budget;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        PdfiumDarkDrawings.Apply(paint.Drawings, colors, pixels, Width, Height, Width * 4, Scale, Scale, 0, 0, ref budget, out var work);
        var elapsed = watch.Elapsed;
        Assert.True(work > 100_000, $"only {work} pieces and rows looked at: the hostile drawings were not reached");
        Assert.True(work <= initial - budget, $"{work} pieces and rows looked at, {initial - budget} charged");
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"painting took {elapsed}");
        Assert.True(At(pixels, 120, 200) == 0xFFFF0000u, $"line {At(pixels, 120, 200):X8}, work {work}, budget left {budget} of {initial}");
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(0xFF, pixels[(i * 4) + 3]));

        // A small budget: painting stops once it is used up, and nothing looked at is free.
        (pixels, budget) = (LineTile(), 10_000);
        Assert.False(PdfiumDarkDrawings.Apply(paint.Drawings, colors, pixels, Width, Height, Width * 4, Scale, Scale, 0, 0, ref budget, out work));
        Assert.Equal(0, budget);
        Assert.InRange(work, 1, 10_000);
        Assert.All(Enumerable.Range(0, Width * Height), i => Assert.Equal(0xFF, pixels[(i * 4) + 3]));

        // The dark ground with the light line of PDFium where the 4 pt line from (10, 50) to (50, 50) lies.
        byte[] LineTile()
        {
            var tile = new byte[Width * Height * 4];
            for (var i = 0; i < Width * Height; i++)
            {
                var (x, y) = (i % Width, i / Width);
                var c = x >= 40 && x < 200 && y >= 192 && y < 208 ? PageThemeColors.DarkForeground : PageThemeColors.DarkBackground;
                (tile[i * 4], tile[(i * 4) + 1], tile[(i * 4) + 2], tile[(i * 4) + 3]) = ((byte)c, (byte)(c >> 8), (byte)(c >> 16), 0xFF);
            }

            return tile;
        }

        static uint At(byte[] tile, int x, int y) =>
            0xFF000000u | ((uint)tile[(((y * Width) + x) * 4) + 2] << 16) | ((uint)tile[(((y * Width) + x) * 4) + 1] << 8) | tile[((y * Width) + x) * 4];
    }

    [Fact]
    public async Task HiddenDrawingsAreNotPainted()
    {
        // Hidden (F bit 2) or not viewed (bit 6): PDFium draws nothing, and nothing is read for painting.
        var path = WritePdf(folder, "hidden.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R 5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Ink /Rect [0 0 200 100] /F 6 /C [1 0 0] /Border [0 0 4] /InkList [[10 50 190 50]] >>",
            "<< /Type /Annot /Subtype /Square /Rect [10 10 190 90] /F 36 /C [1 0 0] /Border [0 0 4] >>",
            "<< /Type /Annot /Subtype /Circle /Rect [10 10 190 90] /F 4 /C [1 0 0] /Border [0 0 4] >>",
        ]);
        var paint = await ReadPaintAsync(path);
        Assert.Single(paint.Drawings);
    }

    /// <summary>
    /// Checks every pixel of the three renders: one PDFium did not colour stays exactly as it was; one it coloured (its
    /// light stroke over the dark ground) takes <paramref name="colorAt"/> with PDFium's share of it (0 skips the pixel).
    /// Returns the number of pixels fully in the stroke, which have exactly the colour.
    /// </summary>
    private static int AssertRecoloured(Renders renders, Func<int, int, uint> colorAt)
    {
        var (plain, pdfium, painted) = (renders.Plain, renders.Pdfium, renders.Painted);
        Assert.Equal((plain.Width, plain.Height), (painted.Width, painted.Height));
        var foreground = PageThemeColors.DarkForeground;
        var stroked = 0;
        var full = 0;
        foreach (var (x, y) in AllPoints(painted))
        {
            var color = colorAt(x, y);
            if (color == 0)
            {
                continue;
            }

            var before = Pixel(pdfium, x, y);
            var after = Pixel(painted, x, y);
            if (before == Pixel(plain, x, y))
            {
                Assert.True(after == before, $"({x}, {y}): untouched by PDFium, but {before:X8} became {after:X8}");
                continue;
            }

            stroked++;
            var share = Share(before);
            var expected = Shifted(before, color, foreground, share);
            Assert.True(Near(expected, after, 2), $"({x}, {y}): PDFium {before:X8} (share {share:F2}), expected {expected:X8}, painted {after:X8}");
            if (before == foreground)
            {
                Assert.Equal(color, after);
                full++;
            }
        }

        Assert.True(stroked > 0, "PDFium coloured no pixel");
        return full;
    }

    private static async Task<Renders> RenderAsync(string path, double scale, DrawingGeometry geometry, AnnotationColor color) =>
        await RenderAsync(path, scale, [(geometry, color)]);

    /// <summary>Renders page 1 of <paramref name="path"/> dark without the drawings, with them as PDFium draws them, and painted.</summary>
    private static async Task<Renders> RenderAsync(string path, double scale, IReadOnlyList<(DrawingGeometry Geometry, AnnotationColor Color)> drawings)
    {
        await using var plainDocument = await OpenAsync(path);
        await using var document = await OpenAsync(path);
        foreach (var (geometry, color) in drawings)
        {
            await document.CreateDrawingAsync(0, geometry, color, null, null, Ct);
        }

        var plain = await plainDocument.RenderPageAsync(0, scale, Dark, Ct);
        await document.ReplaceDarkMarkupReaderAsync((_, _) => DarkMarkupPaint.Empty);
        var pdfium = await document.RenderPageAsync(0, scale, Dark, Ct);
        await document.ReplaceDarkMarkupReaderAsync(null);
        var painted = await document.RenderPageAsync(0, scale, Dark, Ct);
        return new Renders(plain, pdfium, painted);
    }

    /// <summary>
    /// A page of 200 x 100 pt whose drawings lie mostly outside a left tile at scale 4 (0..256 px), in /Annots order: a red
    /// line of 4 pt from (10, 50) to (50, 50) inside the tile (painted last); an ink of 10,000 points from just inside the tile
    /// to x 5,000,000 pt and back (each segment spans the tile's rows but meets almost none of its columns; all of them meet in
    /// the same corner, so they cost about a third of the budget); inks of 100,000 points zigzagging over the whole height near
    /// the right edge (starting in the tile, so the path meets it), far to the right (x 5,000,000 pt) over every row, and far
    /// below the page. In this order all five are read before their appearances use up MaxAppearanceBytes (which ends a read).
    /// </summary>
    private string OutsidePage()
    {
        static string Ink(string points, int width = 1) =>
            $"<< /Type /Annot /Subtype /Ink /Rect [0 0 200 100] /F 4 /C [1 0 0] /Border [0 0 {width}] /InkList [[{points}]] >>";
        static string Zigzag(Func<int, string> point, int count = 100_000) => string.Join(' ', Enumerable.Range(0, count).Select(point));
        return WritePdf(folder, Guid.NewGuid().ToString("N")[..8] + "-outside.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Annots [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R] >>",
            Ink("10 50 50 50", 4),
            Ink(Zigzag(i => i % 2 == 0 ? $"60 {i / 2 % 2 * 100}" : "5000000 50", 10_000)),
            Ink("10 90 " + Zigzag(i => $"{196 + (i % 2 * 3)} {i % 2 * 100}", 99_999)),
            Ink(Zigzag(i => $"{5_000_000 + (i % 7)} {(i % 2 * 2_000_000) - 1_000_000}")),
            Ink(Zigzag(i => $"{i % 2 * 200} {-5_000_000 - (i % 5)}")),
        ]);
    }

    /// <summary>A page of 300 x 200 pt without content.</summary>
    private string BlankPage() => WritePdf(folder, Guid.NewGuid().ToString("N")[..8] + "-blank.pdf",
    [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] >>",
    ]);

    /// <summary>Reads the markups and drawings of page 1 of a file as a dark render would (on the PDFium thread).</summary>
    private static Task<DarkMarkupPaint> ReadPaintAsync(string path)
    {
        var thread = PdfiumThread.Shared;
        return thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var page = document.LoadPage(0);
                return PdfiumDarkMarkups.Read(page, new PdfiumPageColors(PageThemeColors.DarkBackground, PageThemeColors.DarkForeground));
            },
            cancellationToken: Ct);
    }

    private static uint Ground => PageThemeColors.DarkBackground;

    /// <summary>How much of the light foreground over the dark ground a pixel holds, from the sum of its channels.</summary>
    private static double Share(uint pixel)
    {
        static int Sum(uint c) => (int)((c & 0xFF) + ((c >> 8) & 0xFF) + ((c >> 16) & 0xFF));
        return Math.Clamp((Sum(pixel) - Sum(Ground)) / (double)(Sum(PageThemeColors.DarkForeground) - Sum(Ground)), 0, 1);
    }

    /// <summary><paramref name="pixel"/> with its share of <paramref name="foreground"/> replaced by <paramref name="color"/>.</summary>
    private static uint Shifted(uint pixel, uint color, uint foreground, double share)
    {
        uint Channel(int shift) => (uint)Math.Clamp(
            (int)Math.Round(((pixel >> shift) & 0xFF) + (share * ((int)((color >> shift) & 0xFF) - (int)((foreground >> shift) & 0xFF)))),
            0,
            255);
        return 0xFF000000u | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static bool Near(uint a, uint b, int tolerance) =>
        Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) <= tolerance;

    /// <summary>The distance of a point from the segment <paramref name="a"/>–<paramref name="b"/>, in points.</summary>
    private static double Distance(double x, double y, DisplayPoint a, DisplayPoint b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var t = Math.Clamp((((x - a.X) * dx) + ((y - a.Y) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
        var (px, py) = (a.X + (t * dx) - x, a.Y + (t * dy) - y);
        return Math.Sqrt((px * px) + (py * py));
    }

    private static uint Rgb(AnnotationColor color) => 0xFF000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    /// <summary>A pixel as ARGB (opaque).</summary>
    private static uint Pixel(RenderedImage image, int x, int y)
    {
        var p = image.Pixels.Span.Slice((y * image.Stride) + (x * 4), 4);
        return 0xFF000000u | ((uint)p[2] << 16) | ((uint)p[1] << 8) | p[0];
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

    private sealed record Renders(RenderedImage Plain, RenderedImage Pdfium, RenderedImage Painted);
}
