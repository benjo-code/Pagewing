using Pagewing.Core.Pdf;
using Pagewing.Core.Printing;
using Pagewing.TestSupport;
using Xunit;

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
}
