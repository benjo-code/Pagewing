using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed class PdfiumTextPageTests
{
    [Fact]
    public async Task CallFromForeignThreadThrows()
    {
        var thread = PdfiumThread.Shared;
        var ct = TestContext.Current.CancellationToken;
        var path = TestData.Pdf("text-basic.pdf");
        var (document, page, textPage) = await thread.InvokeAsync(
            () =>
            {
                var openedDocument = PdfiumDocument.Open(thread, path, null);
                var loadedPage = openedDocument.LoadPage(0);
                return (openedDocument, loadedPage, loadedPage.LoadTextPage());
            },
            cancellationToken: ct);
        try
        {
            Assert.Throws<PdfiumThreadViolationException>(() => page.LoadTextPage());
            Assert.Throws<PdfiumThreadViolationException>(() => textPage.CharCount);
            Assert.Throws<PdfiumThreadViolationException>(() => textPage.GetText());

            var text = await thread.InvokeAsync(textPage.GetText, cancellationToken: ct);
            Assert.Equal("Hello, Pagewing", text);
        }
        finally
        {
            textPage.Dispose();
            page.Dispose();
            document.Dispose();
        }
    }

    [Fact]
    public async Task RawTextKeepsPdfiumLineBreaksAndCountsCharacters()
    {
        var thread = PdfiumThread.Shared;
        var (text, count) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("hello_world.pdf"), null);
                using var page = document.LoadPage(0);
                using var textPage = page.LoadTextPage();
                return (textPage.GetText(), textPage.CharCount);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Hello, world!\r\nGoodbye, world!", text);
        Assert.Equal(text.Length, count);
    }

    [Fact]
    public async Task EmptyPageHasNoCharactersAndEmptyText()
    {
        var thread = PdfiumThread.Shared;
        var (text, count) = await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("vector-shapes.pdf"), null);
                using var page = document.LoadPage(0);
                using var textPage = page.LoadTextPage();
                return (textPage.GetText(), textPage.CharCount);
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, text);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task UseAfterDisposeThrowsObjectDisposed()
    {
        var thread = PdfiumThread.Shared;
        await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("text-basic.pdf"), null);
                using var page = document.LoadPage(0);
                var textPage = page.LoadTextPage();
                textPage.Dispose();
                textPage.Dispose();
                Assert.Throws<ObjectDisposedException>(() => textPage.GetText());
                Assert.Throws<ObjectDisposedException>(() => textPage.CharCount);

                page.Dispose();
                Assert.Throws<ObjectDisposedException>(() => page.LoadTextPage());
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }
}
