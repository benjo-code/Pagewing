using System.Text;
using Pagewing.Core.Updates;
using Pagewing.TestSupport;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

/// <summary>
/// The release job writes and signs the manifest with <c>tools/package.cs</c> (<c>update-manifest</c>,
/// <c>update-sign</c>); the app must accept exactly that. <c>testdata/updates</c> holds the tool's output for fixed
/// inputs, signed once with a throwaway key whose private half was discarded. The other half of the chain is
/// <c>package.cs selftest</c>: it checks that <c>update-manifest</c> still writes exactly these bytes and that its
/// <c>update-verify</c> accepts them with the key below, which it reads from this file like <c>UpdateTrust.cs</c>.
/// No process is started here.
/// </summary>
public sealed class ReleaseToolTests
{
    /// <summary>The key of the test vector, as <c>package.cs update-keygen</c> printed it. Never trusted by the app.</summary>
    private static readonly UpdateKey[] VectorKeys =
    [
        new UpdateKey(
            "release-tool-vector",
            Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEVGEaB2tM9QGYYXRzYKahe4NY8Ca6LlRHniX7jcpKfS6ZEyOVixDR7mppT/mXvOGQH+RVAm7rTPg3oXT8aKGxPQ==")),
    ];

    private static byte[] Manifest() => File.ReadAllBytes(Path.Combine(TestData.Root, "testdata", "updates", "update-manifest.json"));

    private static string Signature() => File.ReadAllText(Path.Combine(TestData.Root, "testdata", "updates", "update-manifest.json.sig"), Encoding.UTF8);

    [Fact]
    public void TheManifestOfTheReleaseToolVerifiesInTheApp()
    {
        var result = ManifestVerifier.Verify(Manifest(), Signature(), VectorKeys, new UpdateVersion(0, 1, 0));

        Assert.Null(result.Rejection);
        var manifest = Assert.IsType<UpdateManifest>(result.Manifest);
        Assert.Equal("0.2.0", manifest.Version);
        Assert.Equal(
            [
                (UpdateFileKinds.WindowsSetup, "Pagewing-0.2.0-win-x64-setup.exe", 41_234_567L),
                (UpdateFileKinds.WindowsPortable, "Pagewing-0.2.0-win-x64-portable.zip", 43_210_987L),
                (UpdateFileKinds.LinuxAppImage, "Pagewing-0.2.0-x86_64.AppImage", 52_345_678L),
                (UpdateFileKinds.LinuxDeb, "pagewing_0.2.0_amd64.deb", 38_765_432L),
            ],
            manifest.Files.Select(file => (file.Kind, file.Name, file.Size)).ToList());
        Assert.All(manifest.Files, file => Assert.Equal(64, file.Sha256.Length));
    }

    [Fact]
    public void AChangedByteOfTheReleaseToolsManifestIsRejected()
    {
        var bytes = Manifest();
        var at = Array.IndexOf(bytes, (byte)'7');
        bytes[at] = (byte)'8';

        var result = ManifestVerifier.Verify(bytes, Signature(), VectorKeys, new UpdateVersion(0, 1, 0));

        Assert.Equal(ManifestRejection.BadSignature, result.Rejection);
    }

    [Fact]
    public void TheShippedKeysDoNotTrustTheTestVector()
    {
        var result = ManifestVerifier.Verify(Manifest(), Signature(), UpdateTrust.Keys, new UpdateVersion(0, 1, 0));

        Assert.Equal(ManifestRejection.UnknownKey, result.Rejection);
    }
}
