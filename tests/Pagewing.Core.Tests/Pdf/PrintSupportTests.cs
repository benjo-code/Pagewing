using System.Runtime.InteropServices;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.Core.Text;
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

    /// <summary>Prints page 1 with <see cref="PdfDocument.PrintPageToDeviceAsync"/> on a memory device context; BGRA, top-down.</summary>
    private static async Task<byte[]> PrintToMemoryAsync(PdfDocument document, int width, int height, CancellationToken ct)
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
                await document.PrintPageToDeviceAsync(0, dc, new DevicePlacement(0, 0, width, height, 0), ct);
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
