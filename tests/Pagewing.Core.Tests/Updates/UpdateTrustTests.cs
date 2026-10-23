using System.Security.Cryptography;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class UpdateTrustTests
{
    [Fact]
    public void EveryKeyIsAValidP256PublicKeyWithAUniqueId()
    {
        Assert.NotEmpty(UpdateTrust.Keys);
        Assert.Equal(UpdateTrust.Keys.Count, UpdateTrust.Keys.Select(key => key.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var key in UpdateTrust.Keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(key.Id));
            Assert.DoesNotContain(':', key.Id);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey, out _);
            Assert.Equal(256, ecdsa.KeySize);
        }
    }

    /// <summary>
    /// Armed only in the signing job of the release workflow (<c>PAGEWING_REQUIRE_RELEASE_KEY=1</c>, job
    /// <c>manifest</c> in <c>.github/workflows/release.yml</c>): a release must never ship the placeholder as its only
    /// trust. Everywhere else it passes, because the placeholder stays until the owner enters the real key
    /// (<c>packaging/README.md</c>, section "Signaturschlüssel für Updates").
    /// </summary>
    [Fact]
    public void ReleaseKeysAreNotThePlaceholderWhenReleasing()
    {
        if (Environment.GetEnvironmentVariable("PAGEWING_REQUIRE_RELEASE_KEY") != "1")
        {
            return;
        }

        Assert.NotEmpty(UpdateTrust.Keys);
        Assert.DoesNotContain(UpdateTrust.Keys, key => key.Id == UpdateTrust.PlaceholderKeyId);
        Assert.DoesNotContain(UpdateTrust.Keys, key => Convert.ToBase64String(key.PublicKey) == PlaceholderPublicKey);
    }

    /// <summary>The public half of the design-time placeholder (its private half was discarded).</summary>
    private const string PlaceholderPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfEyCOMU4IJ14osOpBY3eFP4/fxK726BrpSdQoUOGeMJ+2DsQaDm4maWvFaZ22iI1oti6dIVOk7thJ8J5Lkp59Q==";

    [Fact]
    public void TheAddressesAreHttps()
    {
        Assert.StartsWith("https://", UpdateTrust.ReleasesLatestUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", UpdateTrust.DownloadBaseUrl, StringComparison.Ordinal);
    }
}
