using Pagewing.Core.Pdf;
using Pagewing.Core.Text;
using Pagewing.Ocr;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>
/// Where the tests find the native OCR library and the language data. The library is built by <c>packaging/native/</c> (or fetched with
/// <c>tools/package.cs native-fetch</c>) into <c>artifacts/native/&lt;rid&gt;/</c> with <c>tessdata/</c> beside it; <c>PAGEWING_TESSERACT_DIR</c>
/// names another folder. Without it a test that needs the library reports itself as skipped; with <c>PAGEWING_REQUIRE_OCR=1</c> (the CI)
/// a missing library is a failure.
/// </summary>
internal static class OcrTestLibrary
{
    private static readonly Lazy<string?> Folder = new(Find);

    /// <summary>Gets the folder with the library, or <see langword="null"/>.</summary>
    public static string? Directory => Folder.Value;

    /// <summary>Gets the folder with the language data.</summary>
    public static string Tessdata => Path.Combine(Directory ?? throw new InvalidOperationException("no OCR library"), "tessdata");

    /// <summary>Skips the test without the library (fails it with <c>PAGEWING_REQUIRE_OCR=1</c>); otherwise points the library at the folder.</summary>
    public static void Require()
    {
        if (Directory is null)
        {
            Assert.False(Environment.GetEnvironmentVariable("PAGEWING_REQUIRE_OCR") == "1", "PAGEWING_REQUIRE_OCR=1, but no OCR library was found (artifacts/native or PAGEWING_TESSERACT_DIR)");
            Assert.Skip("The OCR library is not built (packaging/native/, tools/package.cs native-fetch).");
        }

        TesseractLibrary.UseDirectory(Directory);
    }

    private static string? Find()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        var file = OperatingSystem.IsWindows() ? "tesseract.dll" : "libtesseract.so";
        var candidates = new[] { Environment.GetEnvironmentVariable("PAGEWING_TESSERACT_DIR"), Path.Combine(TestData.Root, "artifacts", "native", rid) };
        return candidates.FirstOrDefault(folder => !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, file)));
    }
}

/// <summary>Pages of the test files as gray images for the engine, with the words PDFium knows on them.</summary>
internal static class OcrTestImages
{
    /// <summary>Renders a page at a resolution into 8-bit gray (the format of the engine).</summary>
    public static async Task<(byte[] Gray, int Width, int Height, double Scale)> RenderGrayAsync(string pdf, int page, int dpi, CancellationToken cancellationToken)
    {
        await using var document = await new PdfEngine().OpenAsync(pdf, cancellationToken: cancellationToken);
        var scale = dpi / 72.0;
        var image = await document.RenderPageAsync(page, scale, cancellationToken: cancellationToken);
        var gray = new byte[image.Width * image.Height];
        var pixels = image.Pixels.Span;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var i = (y * image.Stride) + (x * 4);
                gray[(y * image.Width) + x] = (byte)(((pixels[i] * 29) + (pixels[i + 1] * 150) + (pixels[i + 2] * 77)) >> 8);
            }
        }

        return (gray, image.Width, image.Height, scale);
    }

    /// <summary>The box of a word of the page text of the file, in points from the top left, found by its characters.</summary>
    public static async Task<PageRect> WordBoxAsync(string pdf, int page, string word, CancellationToken cancellationToken)
    {
        await using var document = await new PdfEngine().OpenAsync(pdf, cancellationToken: cancellationToken);
        var text = await document.GetPageTextAsync(page, cancellationToken);
        var all = text.GetText(0, text.Chars.Count);
        var start = all.IndexOf(word, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{word}' is not on page {page + 1} of {Path.GetFileName(pdf)}");
        var rects = text.RectsOf(start, start + word.Length);
        return new PageRect(rects.Min(static r => r.Left), rects.Min(static r => r.Top), rects.Max(static r => r.Right), rects.Max(static r => r.Bottom));
    }
}
