using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed class PdfiumDocumentTests
{
    [Fact]
    public async Task CallFromForeignThreadThrows()
    {
        var thread = PdfiumThread.Shared;
        var ct = TestContext.Current.CancellationToken;
        var path = TestData.Pdf("text-basic.pdf");
        var document = await thread.InvokeAsync(() => PdfiumDocument.Open(thread, path, null), cancellationToken: ct);
        try
        {
            Assert.Throws<PdfiumThreadViolationException>(() => document.PageCount);
            Assert.Throws<PdfiumThreadViolationException>(() => document.FileVersion);
            Assert.Throws<PdfiumThreadViolationException>(() => document.LoadPage(0));
            Assert.Throws<PdfiumThreadViolationException>(() => document.GetMetaText("Title"));
            Assert.Throws<PdfiumThreadViolationException>(() => PdfiumDocument.Open(thread, path, null));

            // The same calls work on the PDFium thread.
            var pageCount = await thread.InvokeAsync(() => document.PageCount, cancellationToken: ct);
            Assert.Equal(2, pageCount);
        }
        finally
        {
            document.Dispose();
        }
    }

    [Fact]
    public async Task DocumentClosesAfterItsPages()
    {
        var thread = PdfiumThread.Shared;
        var ct = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("pagewing-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "copy.pdf");
            File.Copy(TestData.Pdf("text-basic.pdf"), path);

            var (lockedAfterDocumentDispose, lockedAfterPageDispose, width) = await thread.InvokeAsync(
                () =>
                {
                    var document = PdfiumDocument.Open(thread, path, null);
                    var page = document.LoadPage(0);

                    // On the PDFium thread disposing closes immediately, unless a page still holds the document.
                    document.Dispose();
                    var lockedAfterDocument = IsOpenElsewhere(path);
                    var pageWidth = page.Width;
                    page.Dispose();
                    return (lockedAfterDocument, IsOpenElsewhere(path), pageWidth);
                },
                cancellationToken: ct);

            Assert.True(lockedAfterDocumentDispose);
            Assert.False(lockedAfterPageDispose);
            Assert.Equal(300, width);

            var pageCount = await thread.InvokeAsync(
                () =>
                {
                    using var document = PdfiumDocument.Open(thread, path, null);
                    return document.PageCount;
                },
                cancellationToken: ct);
            Assert.Equal(2, pageCount);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PageIndexOutOfRangeThrows()
    {
        var thread = PdfiumThread.Shared;
        await thread.InvokeAsync(
            () =>
            {
                using var document = PdfiumDocument.Open(thread, TestData.Pdf("text-basic.pdf"), null);
                Assert.Throws<ArgumentOutOfRangeException>(() => document.LoadPage(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => document.LoadPage(document.PageCount));
                using var last = document.LoadPage(document.PageCount - 1);
                Assert.Equal(300, last.Width);
                Assert.Equal(200, last.Height);
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FileVersionAndMetaTextAreRead()
    {
        var thread = PdfiumThread.Shared;
        var (version, title, producer, author, helloTitle) = await thread.InvokeAsync(
            () =>
            {
                using var textBasic = PdfiumDocument.Open(thread, TestData.Pdf("text-basic.pdf"), null);
                using var helloWorld = PdfiumDocument.Open(thread, TestData.Pdf("hello_world.pdf"), null);
                return (
                    textBasic.FileVersion,
                    textBasic.GetMetaText("Title"),
                    textBasic.GetMetaText("Producer"),
                    textBasic.GetMetaText("Author"),
                    helloWorld.GetMetaText("Title"));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(17, version);
        Assert.Equal("Pagewing Textprobe: Größe", title);
        Assert.Equal("Pagewing test generator", producer);
        Assert.Null(author);
        Assert.Null(helloTitle);
    }

    [Fact]
    public async Task OpenFailureReportsThePdfiumError()
    {
        var thread = PdfiumThread.Shared;
        var errors = await thread.InvokeAsync(
            () => (
                Assert.Throws<PdfiumOpenException>(() => PdfiumDocument.Open(thread, TestData.Pdf("not-a-pdf.pdf"), null)).Error,
                Assert.Throws<PdfiumOpenException>(() => PdfiumDocument.Open(thread, TestData.Pdf("encrypted_hello_world_r3.pdf"), null)).Error),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal((PdfiumError.Format, PdfiumError.Password), errors);
    }

    [Fact]
    public void PasswordIsEncodedAsNullTerminatedUtf8()
    {
        Assert.Equal([0x68, 0xC3, 0xB4, 0x74, 0x65, 0x6C, 0x00], PdfiumDocument.EncodePassword("hôtel"));
        Assert.Equal([0x00], PdfiumDocument.EncodePassword(null));
    }

    /// <summary>True when another open handle prevents opening the file exclusively.</summary>
    private static bool IsOpenElsewhere(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }
}
