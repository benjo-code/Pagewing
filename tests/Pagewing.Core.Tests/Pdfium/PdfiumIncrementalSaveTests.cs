using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed class PdfiumIncrementalSaveTests
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    [Fact]
    public async Task IncrementalSaveOfAnUnchangedDocumentStartsWithTheOriginalBytes()
    {
        var thread = PdfiumThread.Shared;
        var path = TestData.Pdf("text-basic.pdf");
        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var saved = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, null);
                using var stream = new MemoryStream();
                document.SaveIncremental(stream);
                return stream.ToArray();
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(saved.Length >= original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
    }

    [Fact]
    public async Task IncrementalSaveOfAnEncryptedDocumentDoesNotThrow()
    {
        var thread = PdfiumThread.Shared;
        var path = TestData.Pdf("encrypted_hello_world_r6.pdf");

        var original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        var saved = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, path, UserPassword);
                using var stream = new MemoryStream();
                document.SaveIncremental(stream);
                return stream.ToArray();
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(saved.Length >= original.Length);
        Assert.True(saved.AsSpan(0, original.Length).SequenceEqual(original));
    }
}
