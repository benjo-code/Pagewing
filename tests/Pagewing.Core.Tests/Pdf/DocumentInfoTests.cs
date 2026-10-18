using Pagewing.Core.Pdf;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class DocumentInfoTests
{
    private readonly PdfEngine engine = new();

    [Fact]
    public async Task InfoOfHelloWorld()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TestData.Pdf("hello_world.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: ct);

        var info = await DocumentInfoReader.ReadAsync(document, path, ct);

        Assert.Equal("hello_world.pdf", info.FileName);
        Assert.Equal(Path.GetDirectoryName(path), info.Folder);
        Assert.Equal(1, info.PageCount);
        Assert.Equal(document.PdfVersion, info.PdfVersion);
        Assert.False(string.IsNullOrEmpty(info.PdfVersion));
        Assert.Equal(new FileInfo(path).Length, info.FileSizeBytes);
    }

    [Fact]
    public async Task MissingMetadataGivesNulls()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TestData.Pdf("hello_world.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: ct);

        var info = await DocumentInfoReader.ReadAsync(document, path, ct);

        Assert.Null(info.Title);
        Assert.Null(info.Author);
        Assert.Null(info.Created);
    }

    [Fact]
    public async Task TitleIsTakenFromTheInfoDictionary()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TestData.Pdf("text-basic.pdf");
        await using var document = await engine.OpenAsync(path, cancellationToken: ct);

        var info = await DocumentInfoReader.ReadAsync(document, path, ct);

        Assert.Equal("Pagewing Textprobe: Größe", info.Title);
        Assert.Null(info.Author);
    }

    [Fact]
    public async Task AnUnreadableFileGivesSizeZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("hello_world.pdf"), cancellationToken: ct);
        var missing = Path.Combine(Path.GetTempPath(), "pagewing-does-not-exist", "gone.pdf");

        var info = await DocumentInfoReader.ReadAsync(document, missing, ct);

        Assert.Equal(0, info.FileSizeBytes);
        Assert.Equal("gone.pdf", info.FileName);
        Assert.Equal(1, info.PageCount);
    }
}
