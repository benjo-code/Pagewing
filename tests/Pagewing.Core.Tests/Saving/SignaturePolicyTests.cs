using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Xunit;

namespace Pagewing.Core.Tests.Saving;

public sealed class SignaturePolicyTests
{
    [Fact]
    public void NoSignatureGivesNone()
    {
        Assert.Equal(SignatureAdvice.None, SignaturePolicy.Evaluate(new PdfSignatureInfo(0, DocMdpLevel.NotSet)));
    }

    [Fact]
    public void LevelThreeGivesAppendedOnly()
    {
        Assert.Equal(SignatureAdvice.AppendedOnly, SignaturePolicy.Evaluate(new PdfSignatureInfo(1, DocMdpLevel.FormsAndAnnotations)));
    }

    [Fact]
    public void SignedWithoutDocMdpGivesAppendedOnly()
    {
        Assert.Equal(SignatureAdvice.AppendedOnly, SignaturePolicy.Evaluate(new PdfSignatureInfo(2, DocMdpLevel.NotSet)));
    }

    [Theory]
    [InlineData(DocMdpLevel.NoChanges)]
    [InlineData(DocMdpLevel.FormsOnly)]
    public void LevelOneAndTwoGiveWouldInvalidate(DocMdpLevel level)
    {
        Assert.Equal(SignatureAdvice.WouldInvalidate, SignaturePolicy.Evaluate(new PdfSignatureInfo(1, level)));
    }
}
