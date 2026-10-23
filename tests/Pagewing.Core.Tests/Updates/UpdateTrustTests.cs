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

    [Fact]
    public void TheAddressesAreHttps()
    {
        Assert.StartsWith("https://", UpdateTrust.ReleasesLatestUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://", UpdateTrust.DownloadBaseUrl, StringComparison.Ordinal);
    }
}
