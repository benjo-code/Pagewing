using System.Security.Cryptography;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.Core.Tests.Updates;

public sealed class ChecksumVerifierTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pagewing-checksum-" + Guid.NewGuid().ToString("N"));

    public ChecksumVerifierTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string WriteFile(byte[] content)
    {
        var path = Path.Combine(_folder, "payload.bin");
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Content(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    private static string Hex(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    [Fact]
    public async Task MatchesTheFile()
    {
        var content = Content(5000);
        var path = WriteFile(content);

        Assert.True(await ChecksumVerifier.MatchesAsync(path, Hex(content), content.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AcceptsUpperCaseHex()
    {
        var content = Content(100);
        var path = WriteFile(content);

        Assert.True(await ChecksumVerifier.MatchesAsync(path, Hex(content).ToUpperInvariant(), content.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsOneChangedByte()
    {
        var content = Content(5000);
        var hash = Hex(content);
        content[4999] ^= 1;
        var path = WriteFile(content);

        Assert.False(await ChecksumVerifier.MatchesAsync(path, hash, content.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsAWrongSize()
    {
        var content = Content(5000);
        var path = WriteFile(content);

        Assert.False(await ChecksumVerifier.MatchesAsync(path, Hex(content), content.Length + 1, TestContext.Current.CancellationToken));
        Assert.False(await ChecksumVerifier.MatchesAsync(path, Hex(content), content.Length - 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RejectsAMissingFileWithoutThrowing()
    {
        var path = Path.Combine(_folder, "missing.bin");

        Assert.False(await ChecksumVerifier.MatchesAsync(path, new string('0', 64), 1, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("zz")]
    [InlineData("abcd")]
    public async Task RejectsAChecksumThatIsNotSha256Hex(string hash)
    {
        var content = Content(10);
        var path = WriteFile(content);

        Assert.False(await ChecksumVerifier.MatchesAsync(path, hash, content.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadsInChunks()
    {
        // Larger than any single read buffer, and not a multiple of it.
        var content = Content((3 * 81920) + 17);
        var path = WriteFile(content);

        Assert.True(await ChecksumVerifier.MatchesAsync(path, Hex(content), content.Length, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelsWhenAsked()
    {
        var content = Content(1000);
        var path = WriteFile(content);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ChecksumVerifier.MatchesAsync(path, Hex(content), content.Length, cts.Token));
    }
}
