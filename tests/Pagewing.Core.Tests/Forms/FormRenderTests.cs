using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Tests.Viewing;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Forms;

/// <summary>
/// Form fields in the render pipeline (milestone 11, task 9): PDFium draws widgets only through <c>FPDF_FFLDraw</c>, so every
/// page, tile and thumbnail of a page with widgets is drawn again with it after the render; pages without widgets render
/// exactly as before. The highlight is a render option with its own epoch in the scheduler, and on the dark page the fields
/// keep their light appearance (spike: Docs/DECISIONS.md, "Formulare: Formular-Engine …", point 3).
/// </summary>
public sealed class FormRenderTests : IDisposable
{
    private const double Scale = 2;

    private readonly TempFolder folder = new();

    private static RenderOptions Light { get; } = RenderOptions.Default;

    private static RenderOptions Highlighted { get; } = new() { FormHighlight = true };

    private static RenderOptions Dark { get; } = new() { PageTheme = PageTheme.Dark };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => folder.Dispose();

    [Fact]
    public async Task FormFieldIsDrawnOnlyWithTheEnvironment()
    {
        await using var document = await OpenCopyAsync(FormsBasic);
        var field = await FieldAsync(document, 1, "Ort");
        var empty = await document.RenderPageAsync(1, Scale, Light, Ct);

        // The empty field shows its black border (/MK /BC), drawn by FPDF_FFLDraw only.
        Assert.True(CountDark(empty, field.Bounds.Inflate(1)) > 100, "The empty field is not drawn.");
        Assert.Equal(1, document.FormDraws);

        await document.SetFormValueAsync("Ort", FormFieldState.OfText("Köln am Rhein"), Ct);
        var filled = await document.RenderPageAsync(1, Scale, Light, Ct);
        Assert.True(CountDark(filled, Inner(field.Bounds)) > CountDark(empty, Inner(field.Bounds)) + 50, "The filled field looks like the empty one.");
        Assert.Equal(2, document.FormDraws);

        // A document without a form has no environment and never draws fields.
        await using var plain = await OpenFixtureAsync("text-basic.pdf");
        await plain.RenderPageAsync(0, Scale, Light, Ct);
        Assert.Equal(0, plain.FormDraws);
    }

    [Fact]
    public async Task FilledValueIsVisibleInTheRenderedPage()
    {
        await using var document = await OpenCopyAsync(FormsBasic);
        var field = await FieldAsync(document, 1, "Telefon");
        await document.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), Ct);

        var page = await document.RenderPageAsync(1, Scale, Light, Ct);

        Assert.True(CountDark(page, Inner(field.Bounds)) > 50, "The value is not in the rendered page.");
    }

    [Fact]
    public async Task FilledValueIsVisibleInATile()
    {
        await using var document = await OpenCopyAsync(FormsBasic);
        var field = await FieldAsync(document, 1, "Ort");
        await document.SetFormValueAsync("Ort", FormFieldState.OfText("Köln am Rhein"), Ct);
        var page = await document.RenderPageAsync(1, Scale, Light, Ct);

        // A tile right of the left page edge, so it is rendered with the 64 px overlap, holding the whole field.
        var (pageWidth, _) = (await document.GetPageSizeAsync(1, Ct)).ToPixels(Scale);
        var top = (int)Math.Floor(field.Bounds.Top * Scale) - 16;
        var tileRect = new PixelRect(256, top, pageWidth - 256, (int)Math.Ceiling(field.Bounds.Height * Scale) + 32);
        var tile = await document.RenderTileAsync(1, Scale, tileRect, Light, Ct);

        Assert.True(CountDark(page, Inner(field.Bounds)) > 50, "The value is not in the page.");
        var (x0, y0, x1, y1) = PixelsOf(field.Bounds.Inflate(2));
        var differences = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = Math.Max(x0, tileRect.X); x < Math.Min(x1, tileRect.X + tileRect.Width); x++)
            {
                if (Pixel(page, x, y) != Pixel(tile, x - tileRect.X, y - tileRect.Y))
                {
                    differences++;
                }
            }
        }

        Assert.Equal(0, differences);
    }

    [Fact]
    public async Task HighlightColoursTheFieldsOnlyWhenAsked()
    {
        await using var document = await OpenCopyAsync(FormsBasic);
        var telephone = await FieldAsync(document, 1, "Telefon");
        var locked = await FieldAsync(document, 0, "Gesperrt");

        var plain = await document.RenderPageAsync(1, Scale, Light, Ct);
        var highlighted = await document.RenderPageAsync(1, Scale, Highlighted, Ct);
        var plainAgain = await document.RenderPageAsync(1, Scale, Light, Ct);

        Assert.Equal(0xFFFFFFFFu, Mode(plain, Inner(telephone.Bounds)));
        var tint = Mode(highlighted, Inner(telephone.Bounds));
        var expected = Over(FormHighlightColors.Color, FormHighlightColors.Alpha, 0xFFFFFFFF);
        Assert.True(Near(tint, expected, 4), $"The highlight is #{tint:X8}, expected about #{expected:X8}.");

        // The highlight does not stay with the environment for the next render.
        Assert.True(plain.Pixels.Span.SequenceEqual(plainAgain.Pixels.Span));

        // Outside the fields nothing changes; a read-only field gets no highlight (PDFium skips it).
        Assert.Equal(Pixel(plain, 10, 10), Pixel(highlighted, 10, 10));
        var page0Plain = await document.RenderPageAsync(0, Scale, Light, Ct);
        var page0Highlighted = await document.RenderPageAsync(0, Scale, Highlighted, Ct);
        Assert.Equal(Mode(page0Plain, Inner(locked.Bounds)), Mode(page0Highlighted, Inner(locked.Bounds)));

        // A value stays readable on the highlight (text 4.5:1 against the tinted field).
        await document.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), Ct);
        var filled = await document.RenderPageAsync(1, Scale, Highlighted, Ct);
        var ground = Mode(filled, Inner(telephone.Bounds));
        var text = Darkest(filled, Inner(telephone.Bounds));
        Assert.True(Contrast(text, ground) >= 4.5, $"Text on the highlight has {Contrast(text, ground):F2}:1.");

        // PDFium lays the tint over the appearance, value included: black text comes out tinted. This is why a stronger tint
        // (3:1 against the page) would push the value below 4.5:1 (Docs/DECISIONS.md, "Formulare: Felder im Rendern …").
        var tintedBlack = Over(FormHighlightColors.Color, FormHighlightColors.Alpha, 0xFF000000);
        Assert.True(Near(text, tintedBlack, 4), $"The darkest text pixel is #{text:X8}, expected the tinted black #{tintedBlack:X8}.");
    }

    [Fact]
    public async Task ChangingTheHighlightDropsOldResults()
    {
        // The fake delivers its image although the scheduler cancelled it, as a render that finishes in a race would.
        var renderer = new FakeRenderer { HonorsCancellation = false };
        var cache = new RenderCache();
        cache.Add(Request(7).Key, Image());
        using var scheduler = new RenderScheduler(renderer.Render, cache);
        using var events = new EventLog(scheduler);
        Assert.False(scheduler.FormHighlight);

        scheduler.Update([Request(1), Request(2)]);
        var old = await renderer.NextCallAsync();
        Assert.False(old.Options.FormHighlight);

        scheduler.SetFormHighlight(true);

        Assert.True(scheduler.FormHighlight);
        Assert.True(old.Token.IsCancellationRequested);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, scheduler.PendingCount);
        old.Complete(Image());
        await events.AssertNoReadyAsync();
        Assert.False(cache.Contains(Request(1).Key));

        // The same key is rendered again, now with the highlight, in the page theme it had.
        scheduler.Update([Request(1)]);
        var again = await renderer.NextCallAsync();
        Assert.True(again.Options.FormHighlight);
        Assert.Equal(PageTheme.Light, again.Options.PageTheme);
        again.Complete(Image());
        Assert.Equal(Request(1).Key, await events.NextReadyAsync());

        // The same value does nothing; a theme switch keeps the highlight.
        scheduler.SetFormHighlight(true);
        Assert.True(cache.Contains(Request(1).Key));
        scheduler.SetPageTheme(PageTheme.Dark);
        scheduler.Update([Request(1)]);
        var dark = await renderer.NextCallAsync();
        Assert.Equal((PageTheme.Dark, true), (dark.Options.PageTheme, dark.Options.FormHighlight));
        dark.Complete(Image());
        await events.NextReadyAsync();

        scheduler.Dispose();
        scheduler.SetFormHighlight(false);
        Assert.True(scheduler.FormHighlight);
    }

    [Fact]
    public async Task TheSessionHighlightsTheMainViewButNeverTheThumbnails()
    {
        using var session = new ViewerSession(new PdfEngine());
        session.SetFormHighlight(true);
        await session.OpenAsync(TestData.Pdf(FormsBasic));

        Assert.True(session.FormHighlight);
        Assert.True(session.Scheduler!.FormHighlight);
        Assert.False(session.EnsureThumbnails()!.Scheduler.FormHighlight);

        session.Cache!.Add(Request(0).Key, Image());
        session.SetFormHighlight(false);
        Assert.False(session.Scheduler.FormHighlight);
        Assert.Equal(0, session.Cache.Count);
        Assert.False(session.EnsureThumbnails()!.Scheduler.FormHighlight);
    }

    [Fact]
    public async Task DarkPageKeepsTheFilledTextReadable()
    {
        var path = WritePdf(folder, "dark-form.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] /DA (/Helv 14 Tf 0 g) /DR << /Font << /Helv 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 120 280 150] /P 3 0 R /F 4 /FT /Tx /T (Plain) /V (Hallo Welt) /DA (/Helv 14 Tf 0 g) >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 60 280 90] /P 3 0 R /F 4 /MK << /BG [1 1 0.6] >> /FT /Tx /T (Tinted) /V (Gelb) /DA (/Helv 14 Tf 0 g) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ]);
        await using var document = await OpenAsync(path);
        var plain = await FieldAsync(document, 0, "Plain");
        var tinted = await FieldAsync(document, 0, "Tinted");

        var page = await document.RenderPageAsync(0, Scale, Dark, Ct);

        // A field without a background (/MK /BG) is filled with paper white first, so its black text reads.
        var ground = Mode(page, Inner(plain.Bounds));
        var text = Darkest(page, Inner(plain.Bounds));
        Assert.Equal(0xFFFFFFFFu, ground);
        Assert.True(Contrast(text, ground) >= 4.5, $"The text has {Contrast(text, ground):F2}:1 against the field.");

        // A field with its own background keeps it, and the page around the fields stays dark.
        Assert.True(Near(Mode(page, Inner(tinted.Bounds)), 0xFFFFFF99, 2), $"The field background is #{Mode(page, Inner(tinted.Bounds)):X8}.");
        Assert.Equal(PageThemeColors.DarkBackground, Pixel(page, 4, 4));
        Assert.Equal(PageThemeColors.DarkBackground, Pixel(page, (int)(150 * Scale), (int)(25 * Scale)));

        // The same in a tile with the overlap.
        var tileRect = new PixelRect(128, 0, 400, 400);
        var tile = await document.RenderTileAsync(0, Scale, tileRect, Dark, Ct);
        var (x0, y0, x1, y1) = PixelsOf(plain.Bounds);
        for (var y = y0; y < y1; y++)
        {
            for (var x = Math.Max(x0, tileRect.X); x < Math.Min(x1, tileRect.X + tileRect.Width); x++)
            {
                Assert.Equal(Pixel(page, x, y), Pixel(tile, x - tileRect.X, y - tileRect.Y));
            }
        }
    }

    [Fact]
    public async Task DarkRotatedPageWithACropBoxKeepsTheFieldInPlaceAndReadable()
    {
        // Page 1 of forms-rotated.pdf: /Rotate 90 and a CropBox offset. The white field ground (its /MK /BG, drawn by
        // FPDF_FFLDraw) lies where FormField.Bounds says, and the written value reads on it.
        await using var document = await OpenCopyAsync(FormsRotated);
        await document.SetFormValueAsync("Text1", FormFieldState.OfText("Gedreht"), Ct);
        var field = await FieldAsync(document, 0, "Text1");
        var page = await document.RenderPageAsync(0, Scale, Dark, Ct);
        AssertWhiteFieldInPlace(page, field.Bounds, "forms-rotated.pdf");

        // A field without /MK on the same kind of page: only the paper-white fill (PdfiumFormDrawing.FillWhite) makes its
        // ground white, so the fill and FPDF_FFLDraw must agree on rotation and CropBox.
        var path = WritePdf(folder, "dark-rotated.pdf",
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 5 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /CropBox [20 10 380 290] /Rotate 90 /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [80 220 260 242] /P 3 0 R /F 4 /FT /Tx /T (Bare) /V (Hallo) /DA (/Helv 12 Tf 0 g) >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ]);
        await using var bare = await OpenAsync(path);
        await bare.SetFormValueAsync("Bare", FormFieldState.OfText("Ohne Grund"), Ct);
        var bareField = await FieldAsync(bare, 0, "Bare");
        AssertWhiteFieldInPlace(await bare.RenderPageAsync(0, Scale, Dark, Ct), bareField.Bounds, "field without /MK");

        static void AssertWhiteFieldInPlace(RenderedImage page, PageRect bounds, string what)
        {
            // Pure white is only the field ground: the dark page draws its text in DarkForeground.
            var (x0, y0, x1, y1) = PixelsOf(bounds.Inflate(8));
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            for (var y = Math.Max(0, y0); y < Math.Min(page.Height, y1); y++)
            {
                for (var x = Math.Max(0, x0); x < Math.Min(page.Width, x1); x++)
                {
                    if (Pixel(page, x, y) == 0xFFFFFFFFu)
                    {
                        (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x + 1), Math.Max(bottom, y + 1));
                    }
                }
            }

            var (ex0, ey0, ex1, ey1) = PixelsOf(bounds);
            Assert.True(
                Math.Abs(left - ex0) <= 2 && Math.Abs(top - ey0) <= 2 && Math.Abs(right - ex1) <= 2 && Math.Abs(bottom - ey1) <= 2,
                $"{what}: the white field is at ({left}, {top}, {right}, {bottom}), the bounds map to ({ex0}, {ey0}, {ex1}, {ey1}).");
            var ground = Mode(page, Inner(bounds));
            var text = Darkest(page, Inner(bounds));
            Assert.Equal(0xFFFFFFFFu, ground);
            Assert.True(CountDark(page, Inner(bounds)) > 20, $"{what}: the value is not drawn in the field.");
            Assert.True(Contrast(text, ground) >= 4.5, $"{what}: the text has {Contrast(text, ground):F2}:1 against the field.");
        }
    }

    [Fact]
    public async Task PageWithoutWidgetsRendersLikeBefore()
    {
        // The same two pages with and without a form: page 2 has content and a square annotation, but no widget.
        string Write(string name, bool form) => WritePdf(folder, name,
        [
            form
                ? "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 10 Tf 0 g) /DR << /Font << /Helv 8 0 R >> >> >> >>"
                : "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Annots [5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 6 0 R /Annots [7 0 R] /Resources << /Font << /F1 8 0 R >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Rect [20 120 280 150] /P 3 0 R /F 4 /MK << /BC [0 0 0] >> /FT /Tx /T (Name) /V (Wert) /DA (/Helv 10 Tf 0 g) >>",
            "<< /Length 64 >>\nstream\n0 0 1 rg 20 20 100 50 re f BT /F1 18 Tf 140 40 Td (Seite) Tj ET\nendstream",
            "<< /Type /Annot /Subtype /Square /Rect [150 100 280 180] /F 4 /C [1 0 0] /BS << /W 3 >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        ]);
        await using var withForm = await OpenAsync(Write("with-form.pdf", form: true));
        await using var withoutForm = await OpenAsync(Write("without-form.pdf", form: false));

        foreach (var options in new[] { Light, Dark, Highlighted })
        {
            var expected = await withoutForm.RenderPageAsync(1, Scale, options, Ct);
            var actual = await withForm.RenderPageAsync(1, Scale, options, Ct);
            Assert.True(expected.Pixels.Span.SequenceEqual(actual.Pixels.Span), $"Page 2 differs ({options.PageTheme}, highlight {options.FormHighlight}).");
            var tile = new PixelRect(100, 50, 300, 200);
            var expectedTile = await withoutForm.RenderTileAsync(1, Scale, tile, options, Ct);
            var actualTile = await withForm.RenderTileAsync(1, Scale, tile, options, Ct);
            Assert.True(expectedTile.Pixels.Span.SequenceEqual(actualTile.Pixels.Span));
        }

        // No FPDF_FFLDraw for the page without widgets, and its widgets were looked for once.
        Assert.Equal(0, withForm.FormDraws);
        Assert.Equal(1, withForm.FormWidgetScans);

        // The page with the widget is drawn with it.
        await withForm.RenderPageAsync(0, Scale, Light, Ct);
        Assert.Equal(1, withForm.FormDraws);
        Assert.Equal(2, withForm.FormWidgetScans);
        await withForm.RenderPageAsync(0, Scale, Light, Ct);
        Assert.Equal(2, withForm.FormDraws);
        Assert.Equal(2, withForm.FormWidgetScans);
    }

    [Fact]
    public async Task ThumbnailsShowTheFields()
    {
        using var session = new ViewerSession(new PdfEngine());
        await session.OpenAsync(TestData.Pdf(FormsBasic));
        session.SetFormHighlight(true);
        var fields = await session.Document!.GetFormFieldsAsync(0, Ct);
        var remark = fields.Single(f => f.Key.FieldName == "Bemerkung");
        var thumbnails = session.EnsureThumbnails()!;
        var ready = new TaskCompletionSource<TileKey>(TaskCreationOptions.RunContinuationsAsynchronously);
        thumbnails.Scheduler.TileReady += key => ready.TrySetResult(key);
        var requests = ThumbnailPlanner.Plan(session.LoadedSizes, [0], 1.0);

        thumbnails.Scheduler.Update(requests);
        var key = await ready.Task.WaitAsync(SchedulerWait.Due, Ct);

        Assert.True(thumbnails.Cache.TryGet(key, out var image));
        var scale = requests.Single(r => r.Key == key).Scale;
        var bounds = remark.Bounds;
        var x0 = (int)Math.Floor(bounds.Left * scale);
        var x1 = (int)Math.Ceiling(bounds.Right * scale);
        var y0 = (int)Math.Floor(bounds.Top * scale);
        var y1 = (int)Math.Ceiling(bounds.Bottom * scale);
        var border = 0;
        var blue = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var pixel = Pixel(image, x, y);
                border += Luminance(pixel) < 0.6 ? 1 : 0;
                blue += (int)(pixel & 0xFF) - (int)((pixel >> 16) & 0xFF) > 20 ? 1 : 0;
            }
        }

        // The border of the field is in the thumbnail, the highlight of the main view is not.
        Assert.True(border > 10, $"Only {border} border pixels in the thumbnail.");
        Assert.Equal(0, blue);
    }

    [Fact]
    public async Task RenderAfterAWriteShowsTheNewValue()
    {
        // The environment maps a field to the page view loaded last, and a view that drew before a write keeps the old
        // picture: every render loads its own page, so each render around a write shows the value of its time.
        await using var document = await OpenCopyAsync(FormsBasic);
        var field = await FieldAsync(document, 1, "Telefon");
        var inner = Inner(field.Bounds);

        Assert.Equal(0, CountDark(await document.RenderPageAsync(1, Scale, Light, Ct), inner));
        await document.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), Ct);
        Assert.True(CountDark(await document.RenderPageAsync(1, Scale, Light, Ct), inner) > 50);
        var tile = new PixelRect(256, (int)(field.Bounds.Top * Scale) - 8, 512, 80);
        var before = CountDark(await document.RenderTileAsync(1, Scale, tile, Light, Ct), new PageRect(0, 0, tile.Width / Scale, tile.Height / Scale));
        Assert.True(before > 50);

        await document.SetFormValueAsync("Telefon", FormFieldState.OfText(string.Empty), Ct);
        Assert.Equal(0, CountDark(await document.RenderPageAsync(1, Scale, Light, Ct), inner));
        var after = CountDark(await document.RenderTileAsync(1, Scale, tile, Light, Ct), new PageRect(0, 0, tile.Width / Scale, tile.Height / Scale));
        Assert.True(after < before - 50, $"The tile still shows the old value ({after} against {before} dark pixels).");
    }

    private async Task<PdfDocument> OpenCopyAsync(string name)
    {
        var path = Path.Combine(folder.Path, Guid.NewGuid().ToString("N")[..8] + "-" + name);
        File.Copy(TestData.Pdf(name), path);
        return await OpenAsync(path);
    }

    private static async Task<FormField> FieldAsync(PdfDocument document, int page, string name) =>
        (await document.GetFormFieldsAsync(page, Ct)).First(f => f.Key.FieldName == name);

    private static RenderRequest Request(int page) =>
        new(new TileKey(page, 1000, 0, 0), 1.0, new PixelRect(0, 0, 4, 4), true, false);

    private static RenderedImage Image() => new(1, 1, 4, new byte[4]);

    /// <summary>The rectangle shrunk by 3 points, inside the border of the field.</summary>
    private static PageRect Inner(PageRect bounds) => bounds.Inflate(-3);

    private static (int X0, int Y0, int X1, int Y1) PixelsOf(PageRect rect) =>
        ((int)Math.Floor(rect.Left * Scale), (int)Math.Floor(rect.Top * Scale), (int)Math.Ceiling(rect.Right * Scale), (int)Math.Ceiling(rect.Bottom * Scale));

    private static IEnumerable<uint> PixelsIn(RenderedImage image, PageRect rect)
    {
        var (x0, y0, x1, y1) = PixelsOf(rect);
        for (var y = Math.Max(0, y0); y < Math.Min(image.Height, y1); y++)
        {
            for (var x = Math.Max(0, x0); x < Math.Min(image.Width, x1); x++)
            {
                yield return Pixel(image, x, y);
            }
        }
    }

    /// <summary>The pixels darker than mid grey.</summary>
    private static int CountDark(RenderedImage image, PageRect rect) => PixelsIn(image, rect).Count(p => Luminance(p) < 0.2);

    /// <summary>The most frequent colour (the ground of a field).</summary>
    private static uint Mode(RenderedImage image, PageRect rect) =>
        PixelsIn(image, rect).GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key;

    private static uint Darkest(RenderedImage image, PageRect rect) => PixelsIn(image, rect).MinBy(Luminance);

    private static uint Pixel(RenderedImage image, int x, int y)
    {
        var o = (y * image.Stride) + (x * 4);
        var p = image.Pixels.Span;
        return 0xFF000000u | ((uint)p[o + 2] << 16) | ((uint)p[o + 1] << 8) | p[o];
    }

    /// <summary><paramref name="color"/> (RGB) at <paramref name="alpha"/> over <paramref name="ground"/>, opaque.</summary>
    private static uint Over(uint color, byte alpha, uint ground)
    {
        uint Channel(int shift)
        {
            var c = (color >> shift) & 0xFF;
            var g = (ground >> shift) & 0xFF;
            return (uint)Math.Round(((c * alpha) + (g * (255 - alpha))) / 255.0);
        }

        return 0xFF000000u | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static bool Near(uint a, uint b, int tolerance) =>
        Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF)) <= tolerance
        && Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF)) <= tolerance;

    private static double Luminance(uint c)
    {
        static double Linear(uint channel)
        {
            var s = channel / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear((c >> 16) & 0xFF)) + (0.7152 * Linear((c >> 8) & 0xFF)) + (0.0722 * Linear(c & 0xFF));
    }

    private static double Contrast(uint a, uint b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
