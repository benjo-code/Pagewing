using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class PageSizesTests
{
    private readonly PdfEngine engine = new();

    [Fact]
    public async Task AllPagesOfAThousandPageDocumentComeInOneBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);

        var sizes = await document.GetPageSizesAsync(0, 1000, cancellationToken: ct);

        Assert.Equal(1000, sizes.Count);
        Assert.All(sizes, size => Assert.Equal(new PdfPageSize(200, 100), size));
    }

    [Fact]
    public async Task SinglePageAtTheEndAndEmptyBatchAreAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);

        var last = await document.GetPageSizesAsync(999, 1, cancellationToken: ct);
        var none = await document.GetPageSizesAsync(1000, 0, cancellationToken: ct);

        Assert.Equal([new PdfPageSize(200, 100)], last);
        Assert.Empty(none);
    }

    [Theory]
    [InlineData(990, 100)]
    [InlineData(-1, 1)]
    [InlineData(0, -1)]
    [InlineData(1000, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public async Task RangeOutsideTheDocumentThrows(int firstPage, int count)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("thousand-pages.pdf"), cancellationToken: ct);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => document.GetPageSizesAsync(firstPage, count, cancellationToken: ct));
    }

    [Theory]
    [InlineData("rotated-page.pdf")]
    [InlineData("cropbox.pdf")]
    [InlineData("text-basic.pdf")]
    public async Task BatchEntriesEqualTheSinglePageSizes(string file)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(file), cancellationToken: ct);

        var sizes = await document.GetPageSizesAsync(0, document.PageCount, PdfPriority.Background, ct);

        Assert.Equal(document.PageCount, sizes.Count);
        for (var page = 0; page < sizes.Count; page++)
        {
            Assert.Equal(await document.GetPageSizeAsync(page, ct), sizes[page]);
        }
    }

    [Fact]
    public async Task UseAfterDisposeThrowsObjectDisposed()
    {
        var ct = TestContext.Current.CancellationToken;
        var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        document.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => document.GetPageSizesAsync(0, 1, cancellationToken: ct));
    }

    [Fact]
    public async Task AlreadyCanceledTokenThrowsOperationCanceled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("text-basic.pdf"), cancellationToken: ct);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => document.GetPageSizesAsync(0, 2, cancellationToken: source.Token));
    }
}
