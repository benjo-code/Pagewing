using System.Runtime.InteropServices;
using Pagewing.Core.Annotations;
using Pagewing.Core.Forms;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Tests.Annotations;
using Pagewing.Core.Text;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;
using static Pagewing.Core.Tests.Annotations.AnnotationTestFiles;

namespace Pagewing.Core.Tests.Pdf;

public sealed class PrintSupportTests : IDisposable
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private readonly PdfEngine engine = new();
    private readonly string work = Directory.CreateTempSubdirectory("pagewing-print-").FullName;

    public void Dispose() => Directory.Delete(work, recursive: true);

    [Fact]
    public async Task AnUnencryptedFileIsNotEncryptedAndMayBePrinted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);

        var info = await document.GetPrintInfoAsync(ct);

        Assert.False(info.IsEncrypted);
        Assert.True(info.PrintAllowed);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task EncryptedFilesReportEncryption(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(name), UserPassword, ct);

        var info = await document.GetPrintInfoAsync(ct);

        Assert.True(info.IsEncrypted);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf", false)]
    [InlineData("encrypted_hello_world_r6.pdf", true)]
    public async Task PrintPermissionFollowsTheDocument(string name, bool allowed)
    {
        // The r3 file forbids printing to whoever opens it with the user password; the r6 file allows it.
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(name), UserPassword, ct);

        Assert.Equal(allowed, (await document.GetPrintInfoAsync(ct)).PrintAllowed);
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf")]
    [InlineData("encrypted_hello_world_r6.pdf")]
    public async Task DecryptedCopyOpensWithoutAPassword(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var copy = Path.Combine(work, "copy.pdf");
        string original;
        int pages;
        await using (var document = await engine.OpenAsync(TestData.Pdf(name), UserPassword, ct))
        {
            pages = document.PageCount;
            original = await document.GetTextAsync(0, ct);
            await document.SaveDecryptedCopyAsync(copy, ct);
        }

        await using var decrypted = await engine.OpenAsync(copy, cancellationToken: ct);

        Assert.Equal(pages, decrypted.PageCount);
        Assert.Equal(original, await decrypted.GetTextAsync(0, ct));
        Assert.False((await decrypted.GetPrintInfoAsync(ct)).IsEncrypted);
        Assert.NotEmpty(original);
    }

    [Fact]
    public async Task DecryptedCopyOfAnUnencryptedFileIsValid()
    {
        var ct = TestContext.Current.CancellationToken;
        var copy = Path.Combine(work, "copy.pdf");
        await using (var document = await engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct))
        {
            await document.SaveDecryptedCopyAsync(copy, ct);
        }

        await using var saved = await engine.OpenAsync(copy, cancellationToken: ct);

        Assert.Equal(4, saved.PageCount);
        Assert.Equal("Seite 2 A4 quer", await saved.GetTextAsync(1, ct));
        Assert.Equal(new PdfPageSize(842, 595), await saved.GetPageSizeAsync(1, ct));
    }

    [Fact]
    public async Task DecryptedCopyIsWrittenAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        var copy = Path.Combine(work, "copy.pdf");
        await File.WriteAllTextAsync(copy, "old content", ct);
        await using var document = await engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);

        await document.SaveDecryptedCopyAsync(copy, ct);

        Assert.Equal(["copy.pdf"], Directory.GetFiles(work).Select(Path.GetFileName).ToArray());
        Assert.True(new FileInfo(copy).Length > 100);

        var missing = Path.Combine(work, "missing", "copy.pdf");
        await Assert.ThrowsAnyAsync<IOException>(() => document.SaveDecryptedCopyAsync(missing, ct));
        Assert.Equal(["copy.pdf"], Directory.GetFileSystemEntries(work).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task PrintingToADeviceContextIsRejectedOffWindows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows prints to a device context; see WindowsPrintToPdfTests.");
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<PlatformNotSupportedException>(
            () => document.PrintPageToDeviceAsync(0, 1, new DevicePlacement(0, 0, 100, 100, 0), ct));
    }

    [Fact]
    public async Task PrintingRejectsABadPageOrPlacement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.PrintPageToDeviceAsync(4, 1, new DevicePlacement(0, 0, 100, 100, 0), ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.PrintPageToDeviceAsync(-1, 1, new DevicePlacement(0, 0, 100, 100, 0), ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.PrintPageToDeviceAsync(0, 1, new DevicePlacement(0, 0, 0, 100, 0), ct));
    }

    [Fact]
    public async Task PrintedPageIncludesTheMarkupInLightColours()
    {
        // The Windows route (FPDF_RenderPage with FPDF_ANNOT | FPDF_PRINTING on a device context), here on a memory DC with
        // a 32-bit DIB instead of a printer. The tab shows the page dark meanwhile: printing has no theme at all.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        var hello = await document.CreateMarkupAsync(0, AnnotationKind.Highlight, await WordRectsAsync(document, 0, "Hello"), Yellow, null, null, ct);
        var pagewing = await document.CreateMarkupAsync(0, AnnotationKind.Underline, await WordRectsAsync(document, 0, "Pagewing"), Blue, null, null, ct);
        await document.RenderPageAsync(0, 2, new RenderOptions { PageTheme = PageTheme.Dark }, ct);

        const int Width = 600, Height = 400;
        var pixels = await PrintToMemoryAsync(document, Width, Height, ct);

        // White paper, black text, the highlight yellow behind it, the underline blue: nothing of the dark page.
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixels, Width, 5, 5));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixels, Width, 590, 390));
        var quad = hello.QuadRects[0];
        var yellow = 0;
        var black = 0;
        for (var y = (int)(quad.Top * 2) + 2; y < (int)(quad.Bottom * 2) - 2; y++)
        {
            for (var x = (int)(quad.Left * 2) + 2; x < (int)(quad.Right * 2) - 2; x++)
            {
                var (r, g, b) = At(pixels, Width, x, y);
                yellow += r > 230 && g > 200 && b < 60 ? 1 : 0;
                black += Math.Max(r, Math.Max(g, b)) < 60 ? 1 : 0;
            }
        }

        Assert.True(yellow > 500, $"{yellow} yellow pixels in the highlight");
        Assert.True(black > 50, $"{black} black pixels of text in the highlight");
        var line = pagewing.QuadRects[0];
        var blue = 0;
        for (var y = (int)((line.Bottom - 2) * 2); y <= (int)(line.Bottom * 2); y++)
        {
            for (var x = (int)(line.Left * 2) + 2; x < (int)(line.Right * 2) - 2; x++)
            {
                var (r, g, b) = At(pixels, Width, x, y);
                blue += b > 200 && r < 80 && g < 140 ? 1 : 0;
            }
        }

        Assert.True(blue > 100, $"{blue} blue pixels in the underline");
        Assert.DoesNotContain(Enumerable.Range(0, Width * Height), i => pixels[(i * 4) + 2] == 0x1E && pixels[(i * 4) + 1] == 0x21 && pixels[i * 4] == 0x27);
    }

    [Fact]
    public async Task PrintedPageIncludesAnUnsavedDrawingInItsColour()
    {
        // A drawing that is not saved (in memory only) prints light, in its own colour, as PDFium draws it; the tab showing
        // the page dark meanwhile changes nothing, and printing reads nothing for the dark page.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        await document.CreateDrawingAsync(0, DrawingGeometry.Line(new DisplayPoint(20, 150), new DisplayPoint(180, 150), 4), new AnnotationColor(220, 30, 30), null, null, ct);
        await document.CreateDrawingAsync(0, DrawingGeometry.Rectangle(new PageRect(200, 120, 280, 190), 3), Blue, null, null, ct);
        await document.RenderPageAsync(0, 2, new RenderOptions { PageTheme = PageTheme.Dark }, ct);
        var scans = document.DarkMarkupScans;

        const int Width = 600, Height = 400;
        var pixels = await PrintToMemoryAsync(document, Width, Height, ct);

        Assert.Equal(scans, document.DarkMarkupScans);
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(pixels, Width, 5, 5));
        var red = 0;
        for (var y = 296; y < 304; y++)
        {
            for (var x = 50; x < 350; x++)
            {
                var (r, g, b) = At(pixels, Width, x, y);
                red += r > 200 && g < 60 && b < 60 ? 1 : 0;
            }
        }

        var blue = 0;
        for (var y = 250; y < 370; y++)
        {
            for (var x = 400; x < 406; x++)
            {
                var (r, g, b) = At(pixels, Width, x, y);
                blue += b > 200 && r < 80 && g < 140 ? 1 : 0;
            }
        }

        Assert.True(red > 1500, $"{red} red pixels in the line");
        Assert.True(blue > 400, $"{blue} blue pixels in the left side of the rectangle");
        Assert.DoesNotContain(Enumerable.Range(0, Width * Height), i => pixels[(i * 4) + 2] == 0x1E && pixels[(i * 4) + 1] == 0x21 && pixels[i * 4] == 0x27);
    }

    [Fact]
    public async Task PrintedPageIncludesATextBoxInItsColour()
    {
        // A text box prints light, as its own appearance draws it: solid glyphs in its colour; the dark page read nothing.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        UseTextBoxFont();
        await using var document = await OpenFixtureAsync("text-basic.pdf");
        await document.CreateTextBoxAsync(0, new TextBoxSpec(new PageRect(20, 120, 280, 190), "Gedruckt 5 €", 36, Blue), null, ct);
        await document.RenderPageAsync(0, 2, new RenderOptions { PageTheme = PageTheme.Dark }, ct);
        var scans = document.DarkMarkupScans;

        const int Width = 600, Height = 400;
        var pixels = await PrintToMemoryAsync(document, Width, Height, ct);

        Assert.Equal(scans, document.DarkMarkupScans);
        var blue = 0;
        for (var y = 240; y < 380; y++)
        {
            for (var x = 40; x < 560; x++)
            {
                var (r, g, b) = At(pixels, Width, x, y);
                blue += b > 200 && r < 80 && g < 140 ? 1 : 0;
            }
        }

        Assert.True(blue > 3000, $"{blue} blue pixels in the text box");
        Assert.DoesNotContain(Enumerable.Range(0, Width * Height), i => pixels[(i * 4) + 2] == 0x1E && pixels[(i * 4) + 1] == 0x21 && pixels[i * 4] == 0x27);
    }

    [Fact]
    public async Task DecryptedCopyIncludesUnsavedMarkups()
    {
        // The copy that CUPS gets (Linux) is written from the document in memory: a markup that is not saved is in it,
        // with its appearance, so it prints.
        var ct = TestContext.Current.CancellationToken;
        var copy = Path.Combine(work, "copy.pdf");
        await using (var document = await OpenFixtureAsync("text-basic.pdf"))
        {
            await document.CreateMarkupAsync(0, AnnotationKind.StrikeOut, await WordRectsAsync(document, 0, "Pagewing"), Blue, "unsaved", null, ct);
            await document.SaveDecryptedCopyAsync(copy, ct);
        }

        await using var printed = await engine.OpenAsync(copy, cancellationToken: ct);
        var annotation = Assert.Single(await printed.GetAnnotationsAsync(0, ct));
        Assert.Equal(AnnotationKind.StrikeOut, annotation.Kind);
        Assert.Equal("unsaved", annotation.Contents);
        var image = await printed.RenderPageAsync(0, 4, cancellationToken: ct);
        var rect = annotation.QuadRects[0];
        var middle = (rect.Top + rect.Bottom) / 2;
        var (r, g, b) = MeanColor(image, 4, new PageRect(rect.Left + 2, middle - 0.5, rect.Right - 2, middle + 0.5));
        Assert.True(b > 150 && r < 120, $"the strike-out is not blue: ({r:F0}, {g:F0}, {b:F0})");
    }

    [Fact]
    public async Task PrintedPageIncludesAnUnsavedFormValue()
    {
        // FPDF_RenderPage on a device context draws no widget; a page with widgets prints in strips (bitmap render plus
        // FPDF_FFLDraw). The value is only in memory, the tab showed the page dark and highlighted before: the print is light,
        // with the value and without the highlight.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync(FormsBasic);
        var field = (await document.GetFormFieldsAsync(1, ct)).First(f => f.Key.FieldName == "Telefon");
        var size = await document.GetPageSizeAsync(1, ct);
        var (width, height) = ((int)Math.Ceiling(size.Width * 2), (int)Math.Ceiling(size.Height * 2));
        var inner = field.Bounds.Inflate(-3);
        var empty = await PrintToMemoryAsync(document, width, height, ct, pageIndex: 1);

        await document.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), ct);
        await document.RenderPageAsync(1, 2, new RenderOptions { PageTheme = PageTheme.Dark, FormHighlight = true }, ct);
        await document.RenderPageAsync(1, 2, new RenderOptions { FormHighlight = true }, ct);
        var printed = await PrintToMemoryAsync(document, width, height, ct, pageIndex: 1);

        // The empty field shows its border, the filled one its value in dark text on white paper, without a tint.
        Assert.True(CountWhere(empty, width, field.Bounds.Inflate(1), Dark) > 100, "The border of the empty field is not printed.");
        Assert.Equal(0, CountWhere(empty, width, inner, Dark));
        Assert.True(CountWhere(printed, width, inner, Dark) > 50, "The value is not printed.");
        Assert.Equal(((byte)255, (byte)255, (byte)255), ModeIn(printed, width, inner));
        Assert.Equal(((byte)255, (byte)255, (byte)255), At(printed, width, 5, 5));
        Assert.True(document.PrintStrips > 0);

        // No tint: the field prints exactly as in a document that was never shown highlighted. (PDFium draws the value of a
        // field it filled with subpixel-coloured edges, so "grey only" is no test for the tint.)
        await using var neverHighlighted = await OpenFixtureAsync(FormsBasic);
        await neverHighlighted.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), ct);
        var reference = await PrintToMemoryAsync(neverHighlighted, width, height, ct, pageIndex: 1);
        Assert.Equal(PixelsIn(reference, width, field.Bounds.Inflate(2)), PixelsIn(printed, width, field.Bounds.Inflate(2)));
    }

    [Fact]
    public async Task PrintedPageWithoutWidgetsUsesTheVectorPath()
    {
        // Page 2 has content and a square annotation but no widget: it prints with FPDF_RenderPage as before, byte for byte
        // like the same page of a document without a form; page 1 has a widget and prints in strips.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
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
        await using var plain = await OpenFixtureAsync("text-basic.pdf");

        var expected = await PrintToMemoryAsync(withoutForm, 600, 400, ct, pageIndex: 1);
        var actual = await PrintToMemoryAsync(withForm, 600, 400, ct, pageIndex: 1);
        await PrintToMemoryAsync(plain, 600, 400, ct);

        Assert.Equal(expected, actual);
        Assert.Equal((1, 0), (withForm.VectorPrints, withForm.PrintStrips));
        Assert.Equal((1, 0), (withoutForm.VectorPrints, withoutForm.PrintStrips));
        Assert.Equal((1, 0), (plain.VectorPrints, plain.PrintStrips));

        var page1 = await PrintToMemoryAsync(withForm, 600, 400, ct);
        Assert.Equal(1, withForm.VectorPrints);
        Assert.Equal(1, withForm.PrintStrips);
        Assert.True(CountWhere(page1, 600, new PageRect(22, 52, 278, 78), Dark) > 20, "The value of the field is not printed.");
    }

    [Fact]
    public async Task PrintedFormDoesNotLeakTheHighlightIntoTheNextScreenRender()
    {
        // The print removes the highlight from the document's environment; the next highlighted render sets it again.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync(FormsBasic);
        var highlighted = new RenderOptions { FormHighlight = true };
        var before = await document.RenderPageAsync(1, 2, highlighted, ct);
        var plainBefore = await document.RenderPageAsync(1, 2, RenderOptions.Default, ct);

        await PrintToMemoryAsync(document, 600, 800, ct, pageIndex: 1);
        var after = await document.RenderPageAsync(1, 2, highlighted, ct);
        await PrintToMemoryAsync(document, 600, 800, ct, pageIndex: 1);
        var plainAfter = await document.RenderPageAsync(1, 2, RenderOptions.Default, ct);

        Assert.True(document.PrintStrips > 0);
        Assert.True(before.Pixels.Span.SequenceEqual(after.Pixels.Span));
        Assert.True(plainBefore.Pixels.Span.SequenceEqual(plainAfter.Pixels.Span));
        Assert.False(before.Pixels.Span.SequenceEqual(plainBefore.Pixels.Span));
    }

    [Theory]
    [InlineData(1200, 900, 0)]
    [InlineData(1200, 900, 1)]
    [InlineData(900, 1300, 0)]
    public async Task PrintedStripsMatchAWholePageRender(int width, int height, int quarterTurns)
    {
        // The same page in strips of 512 and of 97 rows against one strip as high as the page (one bitmap render of the whole
        // page plus FPDF_FFLDraw), with a value written in memory. Mostly byte for byte the same; PDFium rounds a position that
        // falls on half a device pixel relative to the bitmap's origin, so a glyph or a border line in a later strip may land
        // one row off, or an edge pixel one level off (measured: 0 to 3,093 of 4.3 to 4.7 million bytes). Each request prints at least
        // one strip; with no time to spare, exactly one.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Printing to a device context needs Windows.");
        var ct = TestContext.Current.CancellationToken;
        await using var document = await OpenFixtureAsync(FormsBasic);
        await document.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), ct);
        var stripsOf512 = (height + 511) / 512;
        var stripsOf97 = (height + 96) / 97;

        var whole = await PrintToMemoryAsync(document, width, height, ct, pageIndex: 1, quarterTurns: quarterTurns, stripRows: height);
        Assert.Equal(1, document.PrintStrips);
        var strips = await PrintToMemoryAsync(document, width, height, ct, pageIndex: 1, quarterTurns: quarterTurns);
        Assert.Equal(1 + stripsOf512, document.PrintStrips);
        var requests = document.PrintRequests;
        var narrow = await PrintToMemoryAsync(document, width, height, ct, pageIndex: 1, quarterTurns: quarterTurns, stripRows: 97, slice: TimeSpan.Zero);
        Assert.Equal(1 + stripsOf512 + stripsOf97, document.PrintStrips);
        Assert.Equal(requests + stripsOf97, document.PrintRequests);

        AssertSameUpToARow(whole, strips, width);
        AssertSameUpToARow(whole, narrow, width);
        Assert.True(CountWhere(whole, width, new PageRect(0, 0, width / 2.0, height / 2.0), Dark) > 1000, "The page is not printed.");
    }

    [Fact]
    public async Task DecryptedCopyIncludesAnUnsavedFormValue()
    {
        // The copy that CUPS gets (Linux) is written from the document in memory: a form value that is not saved is in it,
        // with its appearance, so it prints.
        var ct = TestContext.Current.CancellationToken;
        var copy = Path.Combine(work, "copy.pdf");
        PageRect bounds;
        await using (var document = await OpenFixtureAsync(FormsBasic))
        {
            await document.SetFormValueAsync("Telefon", FormFieldState.OfText("0221 123456"), ct);
            bounds = (await document.GetFormFieldsAsync(1, ct)).First(f => f.Key.FieldName == "Telefon").Bounds;
            await document.SaveDecryptedCopyAsync(copy, ct);
        }

        await using var printed = await engine.OpenAsync(copy, cancellationToken: ct);
        var field = (await printed.GetFormFieldsAsync(1, ct)).First(f => f.Key.FieldName == "Telefon");
        Assert.Equal("0221 123456", field.State.Text);
        Assert.Equal(bounds, field.Bounds);
        var image = await printed.RenderPageAsync(1, 2, cancellationToken: ct);
        var (r, g, b) = MeanColor(image, 2, field.Bounds.Inflate(-3));
        Assert.True(r < 250 && g < 250 && b < 250, $"The value is not drawn in the field of the copy: ({r:F0}, {g:F0}, {b:F0}).");
    }

    [Fact]
    public async Task APrintedPageIsNeverDarkAndKeepsItsSize()
    {
        // Printing has no theme parameter at all; the page sizes are the ones the placement is planned with.
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("print-mixed.pdf"), cancellationToken: ct);

        Assert.Equal(new PdfPageSize(595, 842), await document.GetPageSizeAsync(0, ct));
        Assert.Equal(new PdfPageSize(842, 595), await document.GetPageSizeAsync(1, ct));
        Assert.Equal(new PdfPageSize(612, 792), await document.GetPageSizeAsync(2, ct));
        Assert.Equal(new PdfPageSize(595, 420), await document.GetPageSizeAsync(3, ct));
    }

    private static (byte R, byte G, byte B) At(byte[] pixels, int width, int x, int y)
    {
        var offset = ((y * width) + x) * 4;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    /// <summary>
    /// Asserts that every pixel of <paramref name="actual"/> equals the pixel of <paramref name="expected"/> at the same place
    /// or one row above or below, each channel within one level, and that at most 0.1 % of the bytes differ at all.
    /// </summary>
    private static void AssertSameUpToARow(byte[] expected, byte[] actual, int width)
    {
        Assert.Equal(expected.Length, actual.Length);
        var height = expected.Length / 4 / width;
        var differing = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            differing += expected[i] == actual[i] ? 0 : 1;
        }

        bool Near(int x, int y, int otherY)
        {
            if (otherY < 0 || otherY >= height)
            {
                return false;
            }

            var a = ((y * width) + x) * 4;
            var e = ((otherY * width) + x) * 4;
            return Math.Abs(actual[a] - expected[e]) <= 1 && Math.Abs(actual[a + 1] - expected[e + 1]) <= 1 && Math.Abs(actual[a + 2] - expected[e + 2]) <= 1;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Assert.True(Near(x, y, y) || Near(x, y, y - 1) || Near(x, y, y + 1), $"The pixel ({x}, {y}) is {At(actual, width, x, y)}, the whole page has {At(expected, width, x, y)}.");
            }
        }

        Assert.True(differing <= expected.Length / 1000, $"{differing} of {expected.Length} bytes differ.");
    }

    private static bool Dark((byte R, byte G, byte B) p) => Math.Max(p.R, Math.Max(p.G, p.B)) < 100;

    /// <summary>The device pixels of <paramref name="rect"/>, given in page points of a print at scale 2.</summary>
    private static IEnumerable<(byte R, byte G, byte B)> PixelsIn(byte[] pixels, int width, PageRect rect)
    {
        var height = pixels.Length / 4 / width;
        for (var y = Math.Max(0, (int)Math.Floor(rect.Top * 2)); y < Math.Min(height, (int)Math.Ceiling(rect.Bottom * 2)); y++)
        {
            for (var x = Math.Max(0, (int)Math.Floor(rect.Left * 2)); x < Math.Min(width, (int)Math.Ceiling(rect.Right * 2)); x++)
            {
                yield return At(pixels, width, x, y);
            }
        }
    }

    private static int CountWhere(byte[] pixels, int width, PageRect rect, Func<(byte R, byte G, byte B), bool> predicate) =>
        PixelsIn(pixels, width, rect).Count(predicate);

    private static (byte R, byte G, byte B) ModeIn(byte[] pixels, int width, PageRect rect) =>
        PixelsIn(pixels, width, rect).GroupBy(p => p).OrderByDescending(g => g.Count()).First().Key;

    /// <summary>
    /// Prints a page with <see cref="PdfDocument.PrintPageToDeviceAsync(int, nint, DevicePlacement, CancellationToken)"/> (or,
    /// with <paramref name="stripRows"/> or <paramref name="slice"/>, in strips of that height and requests of that time) on a
    /// memory device context; BGRA, top-down.
    /// </summary>
    private static async Task<byte[]> PrintToMemoryAsync(
        PdfDocument document,
        int width,
        int height,
        CancellationToken ct,
        int pageIndex = 0,
        int quarterTurns = 0,
        int? stripRows = null,
        TimeSpan? slice = null)
    {
        var dc = CreateCompatibleDC(0);
        Assert.NotEqual(0, dc);
        try
        {
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
            };
            var bitmap = CreateDIBSection(dc, ref header, 0, out var bits, 0, 0);
            Assert.NotEqual(0, bitmap);
            try
            {
                var old = SelectObject(dc, bitmap);
                var pixels = new byte[width * height * 4];
                Array.Fill(pixels, (byte)0xFF);
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                var placement = new DevicePlacement(0, 0, width, height, quarterTurns);
                await (stripRows is null && slice is null
                    ? document.PrintPageToDeviceAsync(pageIndex, dc, placement, ct)
                    : document.PrintPageToDeviceAsync(pageIndex, dc, placement, stripRows ?? PdfiumPrint.StripRows, slice ?? PdfiumPrint.Slice, ct));
                GdiFlush();
                Marshal.Copy(bits, pixels, 0, pixels.Length);
                SelectObject(dc, old);
                return pixels;
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        finally
        {
            DeleteDC(dc);
        }
    }

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);

    [DllImport("gdi32.dll")]
    private static extern nint SelectObject(nint dc, nint gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint dc);

    [DllImport("gdi32.dll")]
    private static extern bool GdiFlush();

    /// <summary><c>BITMAPINFOHEADER</c>; the colour table of a 32-bit DIB is empty, so it stands for <c>BITMAPINFO</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }
}
