using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class ManifestVerifierTests
{
    private static readonly UpdateVersion Current = new(0, 1, 0);

    private static readonly string Hash = new('a', 64);

    private sealed class TestKey : IDisposable
    {
        private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public TestKey(string id) => Key = new UpdateKey(id, _ecdsa.ExportSubjectPublicKeyInfo());

        public UpdateKey Key { get; }

        public string Sign(byte[] data) =>
            Key.Id + ":" + Convert.ToBase64String(
                _ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void Dispose() => _ecdsa.Dispose();
    }

    private static string ManifestJson(
        int format = 1,
        string version = "0.2.0",
        string name = "Pagewing-0.2.0-win-x64-setup.exe",
        string size = "1234567")
    {
        var nameJson = JsonSerializer.Serialize(name);
        var versionJson = JsonSerializer.Serialize(version);
        return "{\"format\":" + format + ",\"version\":" + versionJson + ",\"files\":[{\"kind\":\"win-x64-setup\",\"name\":"
            + nameJson + ",\"size\":" + size + ",\"sha256\":\"" + Hash + "\"}]}";
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void AcceptsAValidNewerManifest()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Rejection);
        Assert.NotNull(result.Manifest);
        Assert.Equal(1, result.Manifest.Format);
        Assert.Equal("0.2.0", result.Manifest.Version);
        var file = Assert.Single(result.Manifest.Files);
        Assert.Equal("win-x64-setup", file.Kind);
        Assert.Equal("Pagewing-0.2.0-win-x64-setup.exe", file.Name);
        Assert.Equal(1234567, file.Size);
        Assert.Equal(Hash, file.Sha256);
    }

    [Fact]
    public void AcceptsAVersionWithPrefix()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(version: "v0.2.0"));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Rejection);
    }

    [Fact]
    public void RejectsATamperedManifest()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());
        var signature = key.Sign(json);
        json[10] ^= 1;

        var result = ManifestVerifier.Verify(json, signature, [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.BadSignature, result.Rejection);
    }

    [Fact]
    public void RejectsAWrongSignature()
    {
        using var key = new TestKey("k1");
        using var other = new TestKey("k1");
        var json = Bytes(ManifestJson());

        var result = ManifestVerifier.Verify(json, other.Sign(json), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.BadSignature, result.Rejection);
    }

    [Theory]
    [InlineData("")]
    [InlineData("k1")]
    [InlineData("k1:")]
    [InlineData("k1:!!!not base64")]
    [InlineData(":AAAA")]
    public void RejectsSignatureTextThatIsNotWellFormed(string signature)
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());

        var result = ManifestVerifier.Verify(json, signature, [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.NotNull(result.Rejection);
        Assert.True(result.Rejection is ManifestRejection.BadSignature or ManifestRejection.UnknownKey);
    }

    [Fact]
    public void RejectsASignatureOfTheWrongLength()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());
        var signature = "k1:" + Convert.ToBase64String(new byte[63]);

        var result = ManifestVerifier.Verify(json, signature, [key.Key], Current);

        Assert.Equal(ManifestRejection.BadSignature, result.Rejection);
    }

    [Fact]
    public void RejectsAnUnknownKeyId()
    {
        using var key = new TestKey("k1");
        using var stranger = new TestKey("k2");
        var json = Bytes(ManifestJson());

        var result = ManifestVerifier.Verify(json, stranger.Sign(json), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.UnknownKey, result.Rejection);
    }

    [Fact]
    public void RejectsAnEmptyKeyList()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());

        var result = ManifestVerifier.Verify(json, key.Sign(json), [], Current);

        Assert.Equal(ManifestRejection.UnknownKey, result.Rejection);
    }

    [Fact]
    public void RejectsTheSameVersion()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(version: "0.1.0"));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.NotNewer, result.Rejection);
    }

    [Fact]
    public void RejectsAnOlderVersion()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(version: "0.0.9"));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Equal(ManifestRejection.NotNewer, result.Rejection);
    }

    [Theory]
    [InlineData("0.2.0-rc.1")]
    [InlineData("garbage")]
    [InlineData("")]
    public void RejectsAVersionThatIsNotPlain(string version)
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(version: version));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Equal(ManifestRejection.Malformed, result.Rejection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void RejectsAnUnsupportedFormat(int format)
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(format: format));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.UnsupportedFormat, result.Rejection);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"format\":1}")]
    [InlineData("{\"format\":1,\"version\":\"0.2.0\"}")]
    [InlineData("{\"format\":1,\"version\":\"0.2.0\",\"files\":[]}")]
    [InlineData("{\"format\":1,\"version\":\"0.2.0\",\"files\":[null]}")]
    [InlineData("{\"format\":1,\"version\":\"0.2.0\",\"files\":[{\"kind\":\"win-x64-setup\"}]}")]
    [InlineData("{\"format\":1,\"version\":\"0.2.0\",\"files\":[{\"name\":\"a.exe\",\"size\":5}]}")]
    public void RejectsAMalformedManifest(string json)
    {
        using var key = new TestKey("k1");
        var bytes = Bytes(json);

        var result = ManifestVerifier.Verify(bytes, key.Sign(bytes), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.Malformed, result.Rejection);
    }

    [Fact]
    public void RejectsAChecksumThatIsNotSha256Hex()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson().Replace(Hash, "zz", StringComparison.Ordinal));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Equal(ManifestRejection.Malformed, result.Rejection);
    }

    [Fact]
    public void ChecksTheSignatureBeforeParsing()
    {
        using var key = new TestKey("k1");
        var broken = Bytes("{ this is not json");

        var signed = ManifestVerifier.Verify(broken, key.Sign(broken), [key.Key], Current);
        var unsigned = ManifestVerifier.Verify(broken, "k1:" + Convert.ToBase64String(new byte[64]), [key.Key], Current);

        Assert.Equal(ManifestRejection.Malformed, signed.Rejection);
        Assert.Equal(ManifestRejection.BadSignature, unsigned.Rejection);
    }

    [Fact]
    public void AcceptsAnyKeyOfTheList()
    {
        using var first = new TestKey("k1");
        using var second = new TestKey("k2");
        var json = Bytes(ManifestJson());

        var result = ManifestVerifier.Verify(json, second.Sign(json), [first.Key, second.Key], Current);

        Assert.Null(result.Rejection);
        Assert.NotNull(result.Manifest);
    }

    [Fact]
    public void RejectsAKeyWithTheRightIdButUnusableKeyBytes()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());
        var broken = new UpdateKey("k1", [1, 2, 3]);

        var result = ManifestVerifier.Verify(json, key.Sign(json), [broken], Current);

        Assert.Equal(ManifestRejection.BadSignature, result.Rejection);
    }

    [Theory]
    [InlineData("..\\a.exe")]
    [InlineData("../a.exe")]
    [InlineData("..\\..\\evil.exe")]
    [InlineData("a/b.exe")]
    [InlineData("a\\b.exe")]
    [InlineData("C:\\a.exe")]
    [InlineData("C:a.exe")]
    [InlineData("/etc/x")]
    [InlineData("\\\\server\\share\\a.exe")]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData(".hidden.exe")]
    [InlineData("a\u0001.exe")]
    [InlineData("a\n.exe")]
    [InlineData("a.exe\n")]
    [InlineData("a\0.exe")]
    [InlineData("a .exe")]
    [InlineData("a:stream.exe")]
    [InlineData("\u00dcnicode.exe")]
    public void RejectsFileNamesWithPathParts(string name)
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(name: name));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.UnsafeFileName, result.Rejection);
    }

    [Fact]
    public void RejectsAFileNameThatIsTooLong()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(name: new string('a', 129)));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Equal(ManifestRejection.UnsafeFileName, result.Rejection);
    }

    [Fact]
    public void AcceptsTheLongestFileName()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(name: new string('a', 128)));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Rejection);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1073741825")]
    [InlineData("9223372036854775807")]
    public void RejectsAbsurdSizes(string size)
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(size: size));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.UnsafeSize, result.Rejection);
    }

    [Fact]
    public void AcceptsTheLargestAllowedSize()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson(size: "1073741824"));

        var result = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current);

        Assert.Null(result.Rejection);
    }

    private static string FileJson(string kind, string name = "a.exe", string size = "10", string? hash = null) =>
        "{\"kind\":\"" + kind + "\",\"name\":\"" + name + "\",\"size\":" + size + ",\"sha256\":\"" + (hash ?? Hash) + "\"}";

    private static string ManifestWithFiles(params string[] files) =>
        "{\"format\":1,\"version\":\"0.2.0\",\"files\":[" + string.Join(',', files) + "]}";

    private static ManifestResult VerifySigned(string json)
    {
        using var key = new TestKey("k1");
        var bytes = Bytes(json);
        return ManifestVerifier.Verify(bytes, key.Sign(bytes), [key.Key], Current);
    }

    [Fact]
    public void IgnoresAnUnknownKind()
    {
        var result = VerifySigned(ManifestWithFiles(FileJson("linux-arm64-appimage")));

        Assert.Null(result.Rejection);
        Assert.NotNull(result.Manifest);
        Assert.Empty(result.Manifest.Files);
    }

    [Fact]
    public void IgnoresAnUnknownKindWhateverItsContentIs()
    {
        // A later release may describe a new kind in ways this program cannot judge; it must not refuse the manifest.
        var result = VerifySigned(ManifestWithFiles(
            FileJson("linux-arm64-appimage", name: "../x", size: "-5", hash: "zz"),
            FileJson("linux-arm64-appimage", name: "../y"),
            FileJson("win-x64-setup", name: "Pagewing-0.2.0-win-x64-setup.exe")));

        Assert.Null(result.Rejection);
        Assert.NotNull(result.Manifest);
        Assert.Equal("Pagewing-0.2.0-win-x64-setup.exe", Assert.Single(result.Manifest.Files).Name);
    }

    [Fact]
    public void AcceptsAnUnknownKindAlongsideAKnownFileAndSelectsTheKnownOne()
    {
        var result = VerifySigned(ManifestWithFiles(
            FileJson("linux-arm64-appimage", name: "arm.AppImage"),
            FileJson("linux-appimage", name: "x64.AppImage")));

        Assert.Null(result.Rejection);
        Assert.NotNull(result.Manifest);
        Assert.Equal("x64.AppImage", result.Manifest.FileOfKind("linux-appimage")?.Name);
        Assert.Null(result.Manifest.FileOfKind("linux-arm64-appimage"));
    }

    [Fact]
    public void StillChecksTheNameAndSizeOfAKnownKindNextToAnUnknownOne()
    {
        Assert.Equal(
            ManifestRejection.UnsafeFileName,
            VerifySigned(ManifestWithFiles(FileJson("future-kind"), FileJson("linux-deb", name: "..\\\\x.deb"))).Rejection);
        Assert.Equal(
            ManifestRejection.UnsafeSize,
            VerifySigned(ManifestWithFiles(FileJson("future-kind"), FileJson("linux-deb", size: "0"))).Rejection);
    }

    [Fact]
    public void RejectsTheSameKnownKindTwice()
    {
        var result = VerifySigned(ManifestWithFiles(
            FileJson("linux-deb", name: "a.deb"),
            FileJson("linux-deb", name: "b.deb")));

        Assert.Null(result.Manifest);
        Assert.Equal(ManifestRejection.Malformed, result.Rejection);
    }

    [Fact]
    public void AllowsTheSameUnknownKindTwice()
    {
        var result = VerifySigned(ManifestWithFiles(
            FileJson("future-kind", name: "a.bin"),
            FileJson("future-kind", name: "b.bin"),
            FileJson("linux-deb", name: "a.deb")));

        Assert.Null(result.Rejection);
    }

    [Fact]
    public void AcceptsManyEntriesButNotAbsurdlyMany()
    {
        var many = Enumerable.Range(0, 64).Select(i => FileJson("future-kind-" + i)).ToArray();
        var tooMany = Enumerable.Range(0, 65).Select(i => FileJson("future-kind-" + i)).ToArray();

        Assert.Null(VerifySigned(ManifestWithFiles(many)).Rejection);
        Assert.Equal(ManifestRejection.Malformed, VerifySigned(ManifestWithFiles(tooMany)).Rejection);
    }

    [Fact]
    public void FindsTheFileOfAKind()
    {
        using var key = new TestKey("k1");
        var json = Bytes(ManifestJson());

        var manifest = ManifestVerifier.Verify(json, key.Sign(json), [key.Key], Current).Manifest;

        Assert.NotNull(manifest);
        Assert.Equal("Pagewing-0.2.0-win-x64-setup.exe", manifest.FileOfKind("win-x64-setup")?.Name);
        Assert.Null(manifest.FileOfKind("linux-deb"));
    }
}
