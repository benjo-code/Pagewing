using System.Globalization;
using System.Text;
using Pagewing.Cli;
using Pagewing.Core.Imaging;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Core.Tests.Annotations;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pages;

/// <summary>
/// <see cref="PageImageExporter"/>: the PNG files of <c>pagewing render</c> and of the page export, with the size limit, the
/// refusal to replace files, and the cancellation between pages.
/// </summary>
public sealed class PageImageExporterTests
{
    private readonly PdfEngine engine = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("text-basic.pdf", 144.0, true)]
    [InlineData("annotations.pdf", 72.0, true)]
    [InlineData("annotations.pdf", 72.0, false)]
    public async Task ExportWritesOneSizedPngPerPageWithTheRenderNames(string fixture, double dpi, bool annotations)
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf(fixture), null, Ct);
        var scale = dpi / 72.0;
        var stem = Path.GetFileNameWithoutExtension(fixture);
        var pages = Enumerable.Range(0, document.PageCount).ToArray();

        var written = await PageImageExporter.ExportAsync(document, stem, pages, scale, annotations, folder.Path, overwrite: false, progress: null, Ct);

        // The names and bytes are those of the old "pagewing render" (its own logic, repeated here).
        Assert.Equal(pages.Length, written.Count);
        for (var i = 0; i < pages.Length; i++)
        {
            var name = $"{stem}-p{(pages[i] + 1).ToString("D3", CultureInfo.InvariantCulture)}.png";
            Assert.Equal(Path.Combine(folder.Path, name), written[i]);
            var image = await document.RenderPageAsync(pages[i], scale, new RenderOptions { Annotations = annotations }, Ct);
            Assert.Equal(PngEncoder.Encode(image), await File.ReadAllBytesAsync(written[i], Ct));
            var (width, height, _) = PngDecoder.Decode(await File.ReadAllBytesAsync(written[i], Ct));
            Assert.Equal((image.Width, image.Height), (width, height));
        }

        // Nothing else is left in the folder (no temporary file).
        Assert.Equal(pages.Length, Directory.GetFileSystemEntries(folder.Path).Length);
    }

    [Fact]
    public async Task ExportNamesFilesWithAsManyDigitsAsThePageCountNeeds()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), null, Ct);

        var written = await PageImageExporter.ExportAsync(document, "thousand-pages", [6, 999], 0.5, annotations: true, folder.Path, overwrite: false, progress: null, Ct);

        Assert.Equal(
            [Path.Combine(folder.Path, "thousand-pages-p0007.png"), Path.Combine(folder.Path, "thousand-pages-p1000.png")],
            written);
    }

    [Fact]
    public async Task ExportRefusesPagesAboveTheMegapixelLimit()
    {
        using var folder = new TempFolder();
        var output = Directory.CreateDirectory(Path.Combine(folder.Path, "png")).FullName;
        var path = Path.Combine(folder.Path, "big.pdf");
        await File.WriteAllTextAsync(path, TwoPages(200, 100, 14400, 14400), Encoding.ASCII, Ct);
        await using var document = await engine.OpenAsync(path, null, Ct);

        // 14400 pt at 144 dpi is 28800 x 28800 pixels: far above 64 megapixels. The small first page is not written either.
        var tooLarge = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "big", [0, 1], 2, annotations: true, output, overwrite: false, progress: null, Ct));
        Assert.Equal(PageExportError.TooLarge, tooLarge.Error);
        Assert.Equal(1, tooLarge.Page);
        Assert.Contains("megapixel", tooLarge.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFileSystemEntries(output));

        // The same page at 72 dpi is 207 megapixels (still above the limit), at 18 dpi it is 13 megapixels and works.
        await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "big", [1], 1, annotations: true, output, overwrite: false, progress: null, Ct));
        var written = await PageImageExporter.ExportAsync(document, "big", [1], 0.25, annotations: true, output, overwrite: false, progress: null, Ct);
        Assert.Single(written);

        // A silly resolution cannot overflow the arithmetic.
        var absurd = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "big", [0], 1e300, annotations: true, output, overwrite: false, progress: null, Ct));
        Assert.Equal(PageExportError.TooLarge, absurd.Error);
    }

    [Fact]
    public async Task ExportDoesNotOverwriteWithoutPermission()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);
        var existing = Path.Combine(folder.Path, "text-basic-p002.png");
        await File.WriteAllTextAsync(existing, "precious", Ct);

        var refused = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "text-basic", [0, 1], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct));

        // Everything is checked before the first byte: page 1 was not written either, and the file in the way is named.
        Assert.Equal(PageExportError.TargetExists, refused.Error);
        Assert.Equal([existing], refused.Paths);
        Assert.Equal("precious", await File.ReadAllTextAsync(existing, Ct));
        Assert.Equal([existing], Directory.GetFileSystemEntries(folder.Path));

        // With the explicit choice both files are written.
        var written = await PageImageExporter.ExportAsync(document, "text-basic", [0, 1], 1, annotations: true, folder.Path, overwrite: true, progress: null, Ct);
        Assert.Equal(2, written.Count);
        Assert.NotEqual("precious", await File.ReadAllTextAsync(existing, Ct));
        Assert.Equal(2, Directory.GetFileSystemEntries(folder.Path).Length);
    }

    [Fact]
    public async Task ExportNeverReplacesTheSourceFileNorAFolder()
    {
        using var folder = new TempFolder();
        var source = Path.Combine(folder.Path, "doc-p001.png");
        File.Copy(TestData.Pdf("text-basic.pdf"), source);
        var before = await File.ReadAllBytesAsync(source, Ct);
        await using var document = await engine.OpenAsync(source, null, Ct);

        var refused = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "doc", [0], 1, annotations: true, folder.Path, overwrite: true, progress: null, Ct, sourcePath: source));

        Assert.Equal(PageExportError.TargetIsSource, refused.Error);
        Assert.Equal(before, await File.ReadAllBytesAsync(source, Ct));

        Directory.CreateDirectory(Path.Combine(folder.Path, "doc-p002.png"));
        var folderInTheWay = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "doc", [1], 1, annotations: true, folder.Path, overwrite: true, progress: null, Ct));
        Assert.Equal(PageExportError.TargetExists, folderInTheWay.Error);
    }

    [Fact]
    public async Task ExportCanBeCanceledBetweenPages()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), null, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var progress = new SyncProgress(value =>
        {
            if (value > 0)
            {
                cancel.Cancel();
            }
        });
        var written = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PageImageExporter.ExportAsync(document, "t", [0, 1, 2, 3], 0.5, annotations: true, folder.Path, overwrite: false, progress, cancel.Token, written));

        // The page that was complete stays, is listed, and no partial file or temporary file is left.
        Assert.Equal([Path.Combine(folder.Path, "t-p0001.png")], written);
        Assert.Equal(written, Directory.GetFileSystemEntries(folder.Path));
        PngDecoder.Decode(await File.ReadAllBytesAsync(written[0], Ct));
    }

    [Fact]
    public async Task ExportReportsProgressPerPage()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);
        var values = new List<double>();

        await PageImageExporter.ExportAsync(document, "p", [0, 1], 0.5, annotations: true, folder.Path, overwrite: false, new SyncProgress(values.Add), Ct);

        Assert.Equal([0.5, 1.0], values);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b/c")]
    [InlineData("C:\\temp\\x")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    public async Task ExportKeepsEveryFileInsideTheTargetFolder(string stem)
    {
        using var folder = new TempFolder();
        var output = Directory.CreateDirectory(Path.Combine(folder.Path, "inner")).FullName;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);

        var written = await PageImageExporter.ExportAsync(document, stem, [0], 0.5, annotations: true, output, overwrite: false, progress: null, Ct);

        var file = Assert.Single(written);
        Assert.Equal(output, Path.GetDirectoryName(file));
        Assert.EndsWith("-p001.png", file, StringComparison.Ordinal);
        Assert.Equal([file], Directory.GetFileSystemEntries(output));
        Assert.Equal([output], Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task ExportRefusesBadArguments()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);

        foreach (var scale in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => PageImageExporter.ExportAsync(document, "x", [0], scale, annotations: true, folder.Path, overwrite: false, progress: null, Ct));
        }

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PageImageExporter.ExportAsync(document, "x", [2], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PageImageExporter.ExportAsync(document, "x", [-1], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(
            () => PageImageExporter.ExportAsync(document, "x", [0, 0], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct));
        var missing = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "x", [0], 1, annotations: true, Path.Combine(folder.Path, "nope"), overwrite: false, progress: null, Ct));
        Assert.Equal(PageExportError.DirectoryMissing, missing.Error);
        var tooMany = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "x", new int[PageImageExporter.MaxPages + 1], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct));
        Assert.Equal(PageExportError.TooManyPages, tooMany.Error);

        // Nothing to export is not an error.
        Assert.Empty(await PageImageExporter.ExportAsync(document, "x", [], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct));
        Assert.Empty(Directory.GetFileSystemEntries(folder.Path));
    }

    [Fact]
    public async Task RenderCommandStillPrintsThePaths()
    {
        using var folder = new TempFolder();
        var output = Path.Combine(folder.Path, "png");
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["render", TestData.Pdf("text-basic.pdf"), "--dpi", "72", "--output", output], stdout, stderr, _ => null, Ct);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        Assert.Equal(
            $"{Path.Combine(output, "text-basic-p001.png")}\n{Path.Combine(output, "text-basic-p002.png")}\n",
            new UTF8Encoding(false, true).GetString(stdout.ToArray()));

        // As before, a second run replaces the files without asking.
        var again = await CliApp.RunAsync(["render", TestData.Pdf("text-basic.pdf"), "--dpi", "72", "--output", output], new MemoryStream(), stderr, _ => null, Ct);
        Assert.Equal(0, again);
    }

    [Fact]
    public async Task RenderCommandReportsAPageAboveTheLimitWithExitCodeOne()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "big.pdf");
        await File.WriteAllTextAsync(path, TwoPages(200, 100, 14400, 14400), Encoding.ASCII, Ct);
        var output = Path.Combine(folder.Path, "png");
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["render", path, "--dpi", "144", "--output", output, "--pages", "1,2"], stdout, stderr, _ => null, Ct);

        Assert.Equal(1, exit);
        Assert.StartsWith("pagewing: ", stderr.ToString(), StringComparison.Ordinal);

        // The text of the render itself (its limit is 268435456 pixels), not the export's: render behaves as it always did.
        Assert.Contains("larger than 268435456 pixels", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", stderr.ToString(), StringComparison.Ordinal);

        // The page before it was written and printed.
        Assert.Equal([Path.Combine(output, "big-p001.png")], new UTF8Encoding(false, true).GetString(stdout.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task RenderStillRendersAPageBetweenTheExportLimitAndTheRenderLimit()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, "wide.pdf");

        // 8400 x 8400 pt at 72 dpi is 70.56 megapixels: above the export limit (64), far below the limit of the render (268).
        await File.WriteAllTextAsync(path, TwoPages(8400, 8400, 200, 100), Encoding.ASCII, Ct);
        var output = Path.Combine(folder.Path, "png");
        using var stdout = new MemoryStream();
        using var stderr = new StringWriter();

        var exit = await CliApp.RunAsync(["render", path, "--dpi", "72", "--output", output, "--pages", "1"], stdout, stderr, _ => null, Ct);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr.ToString());
        var file = Path.Combine(output, "wide-p001.png");
        Assert.Equal([file], Directory.GetFileSystemEntries(output));
        var (width, height, _) = PngDecoder.Decode(await File.ReadAllBytesAsync(file, Ct));
        Assert.Equal((8400, 8400), (width, height));

        // The export's default refuses the same page.
        await using var document = await engine.OpenAsync(path, null, Ct);
        var refused = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "wide", [0], 1, annotations: true, Directory.CreateDirectory(Path.Combine(folder.Path, "export")).FullName, overwrite: false, progress: null, Ct));
        Assert.Equal(PageExportError.TooLarge, refused.Error);
    }

    [Fact]
    public void TheDefaultLimitIsExactlySixtyFourMillionPixels()
    {
        Assert.Equal(64_000_000, PageImageExporter.MaxPixels);
        Assert.False(PageImageExporter.Exceeds(new PdfPageSize(8000, 8000), 1, PageImageExporter.MaxPixels));
        Assert.True(PageImageExporter.Exceeds(new PdfPageSize(64_000_001, 1), 1, PageImageExporter.MaxPixels));
        Assert.False(PageImageExporter.Exceeds(new PdfPageSize(64_000_000, 1), 1, PageImageExporter.MaxPixels));
        Assert.True(PageImageExporter.Exceeds(new PdfPageSize(8000, 8000.5), 1, PageImageExporter.MaxPixels));
        Assert.True(PageImageExporter.Exceeds(new PdfPageSize(1e300, 1), 1, PageImageExporter.MaxPixels));
        Assert.False(PageImageExporter.Exceeds(new PdfPageSize(1e300, 1), 1, PageImageExporter.RenderLimitOnly));
    }

    [Fact]
    public async Task ExportAcceptsExactlyTheGivenPixelLimit()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);

        // text-basic.pdf pages are 300 x 200 pt: 60,000 pixels at scale 1.
        var written = await PageImageExporter.ExportAsync(document, "t", [0], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct, maxPixels: 60_000);
        Assert.Single(written);
        var refused = await Assert.ThrowsAsync<PageExportException>(
            () => PageImageExporter.ExportAsync(document, "u", [0], 1, annotations: true, folder.Path, overwrite: false, progress: null, Ct, maxPixels: 59_999));
        Assert.Equal(PageExportError.TooLarge, refused.Error);
        Assert.Equal([written[0]], Directory.GetFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData("größe 日本")]
    [InlineData("con")]
    [InlineData("a.")]
    [InlineData(" a")]
    [InlineData("a ")]
    [InlineData("report.v2")]
    [InlineData("report..")]
    [InlineData("")]
    [InlineData("200")]
    public async Task ExportNamesRealFileNamesExactlyLikeTheOldRenderCommand(string stem)
    {
        if (stem == "200")
        {
            stem = new string('x', 200);
        }

        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);

        var written = await PageImageExporter.ExportAsync(document, stem, [0, 1], 0.25, annotations: true, folder.Path, overwrite: false, progress: null, Ct);

        // The old formula of RenderCommand: the stem as it is, "-p", the number with at least three digits, ".png".
        Assert.Equal(
            [Path.Combine(folder.Path, $"{stem}-p001.png"), Path.Combine(folder.Path, $"{stem}-p002.png")],
            written);
        Assert.All(written, file => Assert.True(File.Exists(file)));
    }

    [Fact]
    public async Task ExportMakesAStemSafeOnlyWhenNoFileNameCanBeMadeOfIt()
    {
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);

        // 250 characters and the suffix are more than the 255 a file name may have: cut, but still a file in the folder.
        var longStem = new string('y', 250);
        var written = await PageImageExporter.ExportAsync(document, longStem, [0], 0.25, annotations: true, folder.Path, overwrite: false, progress: null, Ct);

        var name = Path.GetFileName(Assert.Single(written));
        Assert.True(name.Length <= 255);
        Assert.NotEqual($"{longStem}-p001.png", name);
        Assert.EndsWith("-p001.png", name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportReportsTheReasonWhenAFileCannotBeWritten()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "A read-only file blocks the replace on Windows; Linux replaces it by rename.");
        using var folder = new TempFolder();
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), null, Ct);
        var target = Path.Combine(folder.Path, "text-basic-p001.png");
        await File.WriteAllTextAsync(target, "keep", Ct);
        File.SetAttributes(target, FileAttributes.ReadOnly);
        try
        {
            var failed = await Assert.ThrowsAsync<PageExportException>(
                () => PageImageExporter.ExportAsync(document, "text-basic", [0], 0.25, annotations: true, folder.Path, overwrite: true, progress: null, Ct));

            Assert.Equal(PageExportError.WriteFailed, failed.Error);
            Assert.Equal(0, failed.Page);
            Assert.Equal([target], failed.Paths);
            Assert.NotNull(failed.InnerException);
            Assert.Contains(target, failed.Message, StringComparison.Ordinal);
            var reason = failed.InnerException is SaveFailureException { InnerException: { } inner } ? inner : failed.InnerException;
            Assert.Contains(reason.Message, failed.Message, StringComparison.Ordinal);
            Assert.Equal("keep", await File.ReadAllTextAsync(target, Ct));
            Assert.Equal([target], Directory.GetFileSystemEntries(folder.Path));

            // The command shows the same detail, with exit code 1.
            using var stdout = new MemoryStream();
            using var stderr = new StringWriter();
            var exit = await CliApp.RunAsync(["render", TestData.Pdf("text-basic.pdf"), "--dpi", "18", "--output", folder.Path, "--pages", "1"], stdout, stderr, _ => null, Ct);
            Assert.Equal(1, exit);
            Assert.StartsWith("pagewing: An image could not be written: ", stderr.ToString(), StringComparison.Ordinal);
            Assert.Contains(reason.Message, stderr.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.SetAttributes(target, FileAttributes.Normal);
        }
    }

    // A minimal PDF without a cross reference table (PDFium rebuilds it) whose two pages have the given sizes in points.
    private static string TwoPages(int firstWidth, int firstHeight, int secondWidth, int secondHeight) =>
        "%PDF-1.4\n"
        + "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
        + "2 0 obj\n<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>\nendobj\n"
        + string.Create(CultureInfo.InvariantCulture, $"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {firstWidth} {firstHeight}] >>\nendobj\n")
        + string.Create(CultureInfo.InvariantCulture, $"4 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {secondWidth} {secondHeight}] >>\nendobj\n")
        + "trailer\n<< /Root 1 0 R /Size 5 >>\n%%EOF\n";

    /// <summary>Reports synchronously (the framework's <see cref="Progress{T}"/> posts to another thread).</summary>
    private sealed class SyncProgress(Action<double> action) : IProgress<double>
    {
        public void Report(double value) => action(value);
    }
}
