using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class TextTests
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private readonly PdfEngine engine = new();

    [Fact]
    public async Task TextOfBasicPages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        Assert.Equal("Hello, Pagewing", await document.GetTextAsync(0, ct));
        Assert.Equal("Größe äöü", await document.GetTextAsync(1, ct));
    }

    [Fact]
    public async Task TextOfThousandPagesLastPage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);

        Assert.Equal("Page 1", await document.GetTextAsync(0, ct));
        Assert.Equal("Page 1000", await document.GetTextAsync(999, ct));
    }

    [Fact]
    public async Task PageWithoutTextReturnsEmptyString()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("vector-shapes.pdf"), cancellationToken: ct);

        Assert.Equal(string.Empty, await document.GetTextAsync(0, ct));
    }

    [Fact]
    public async Task RotatedPageTextIsExtracted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("rotated-page.pdf"), cancellationToken: ct);

        Assert.Equal("Rotated", await document.GetTextAsync(0, ct));
    }

    [Fact]
    public async Task EncryptedDocumentTextIsExtracted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("encrypted_hello_world_r3.pdf"), UserPassword, ct);

        Assert.Equal("Hello, world!\nGoodbye, world!", await document.GetTextAsync(0, ct));
    }

    [Theory]
    [InlineData("hello_world.pdf", "Hello, world!\nGoodbye, world!")]
    [InlineData("annotations.pdf", "Highlighted text\nLink area")]
    public async Task TextUsesLfLineBreaks(string file, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: ct);

        var text = await document.GetTextAsync(0, ct);

        Assert.DoesNotContain('\r', text);
        Assert.Equal(expected, text);
    }

    [Fact]
    public async Task TextPageIndexOutOfRangeThrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetTextAsync(-1, ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetTextAsync(2, ct));
    }

    [Fact]
    public async Task TextAfterDisposeThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);

        document.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.GetTextAsync(0, ct));
    }

    [Fact]
    public async Task AlreadyCanceledTokenThrowsOperationCanceled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetTextAsync(0, source.Token));
    }
}
