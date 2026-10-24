using Pagewing.Core.Pdf;
using Pagewing.Pdfium;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Pdf;

public sealed class SignatureInfoTests
{
    /// <summary>User password of the encrypted PDFium test files (see <c>testdata/pdfs/LICENSES.md</c>).</summary>
    private const string UserPassword = "hôtel";

    private readonly PdfEngine engine = new();

    [Theory]
    [InlineData("signed-docmdp-none.pdf", DocMdpLevel.NotSet)]
    [InlineData("signed-docmdp-1.pdf", DocMdpLevel.NoChanges)]
    [InlineData("signed-docmdp-2.pdf", DocMdpLevel.FormsOnly)]
    [InlineData("signed-docmdp-3.pdf", DocMdpLevel.FormsAndAnnotations)]
    public async Task ReadsLevelsOfTheFixtures(string name, DocMdpLevel expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(name), cancellationToken: ct);

        var info = await document.GetSignatureInfoAsync(ct);

        Assert.Equal(1, info.SignatureCount);
        Assert.Equal(expected, info.Permission);
    }

    [Theory]
    [InlineData("hello_world.pdf")]
    [InlineData("text-basic.pdf")]
    public async Task UnsignedDocumentHasNoSignatures(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(name), cancellationToken: ct);

        var info = await document.GetSignatureInfoAsync(ct);

        Assert.Equal(0, info.SignatureCount);
        Assert.Equal(DocMdpLevel.NotSet, info.Permission);
    }

    [Fact]
    public async Task UnencryptedDocumentMayBeAnnotated()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf("hello_world.pdf"), cancellationToken: ct);

        Assert.True(await document.MayAnnotateAsync(ct));
    }

    [Theory]
    [InlineData("encrypted_hello_world_r3.pdf", false)]
    [InlineData("encrypted_hello_world_r6.pdf", true)]
    public async Task EncryptedDocumentReportsItsAnnotatePermission(string name, bool allowed)
    {
        // The r3 file forbids annotating to whoever opens it with the user password; the r6 file (/P -4) allows it.
        var ct = TestContext.Current.CancellationToken;
        await using var document = await engine.OpenAsync(TestData.Pdf(name), UserPassword, ct);

        Assert.Equal(allowed, await document.MayAnnotateAsync(ct));
    }

    [Theory]
    [InlineData(0xFFFFFFFCu, true)]
    [InlineData(0u, false)]
    [InlineData(32u, true)]
    [InlineData(0xFFFFFFDFu, false)]
    [InlineData(4u, false)]
    public void TheAnnotateBitIsBitSixOfThePermissions(uint permissions, bool expected)
    {
        Assert.Equal(expected, PdfiumDocument.AllowsAnnotating(permissions));
    }
}
