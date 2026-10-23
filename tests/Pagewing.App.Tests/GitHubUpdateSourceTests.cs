using System.Net;
using System.Text;
using Pagewing.App.Services;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// <see cref="GitHubUpdateSource"/> against a fake <see cref="HttpMessageHandler"/>; no test touches the network.
/// </summary>
public sealed class GitHubUpdateSourceTests : IDisposable
{
    private const string LatestJson = """{"tag_name":"v0.2.0","name":"Pagewing 0.2.0","assets":[{"browser_download_url":"https://evil.example/update-manifest.json"}]}""";

    private static readonly UpdateVersion Version = new(0, 2, 0);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "pagewing-updatesource-" + Guid.NewGuid().ToString("N"));

    public GitHubUpdateSourceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }

    private string Destination => Path.Combine(_directory, "Pagewing-0.2.0-setup.exe");

    private string PartFile => Destination + ".part";

    private static UpdateFile FileOf(int size) => new(UpdateFileKinds.WindowsSetup, "Pagewing-0.2.0-setup.exe", size, new string('a', 64));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };

    private static byte[] Payload(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    private sealed class Recorder : IProgress<long>
    {
        public List<long> Values { get; } = [];

        public void Report(long value) => Values.Add(value);
    }

    [Fact]
    public async Task SendsOnlyTheFixedUserAgentAndNoCredentials()
    {
        var handler = new FakeHandler(_ => Json(LatestJson), _ => Bytes([1]), _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        await source.FetchManifestAsync(CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("Pagewing", request.UserAgent);
            Assert.Equal(["User-Agent"], request.HeaderNames);
            Assert.Equal(HttpMethod.Get, request.Method);
        }
    }

    [Fact]
    public async Task BuildsAssetAddressesFromTheTagNotFromTheResponse()
    {
        var handler = new FakeHandler(_ => Json(LatestJson), _ => Bytes([1, 2, 3]), _ => Bytes(Encoding.UTF8.GetBytes("k:AAAA")));
        using var source = new GitHubUpdateSource(handler);

        var documents = await source.FetchManifestAsync(CancellationToken.None);

        Assert.NotNull(documents);
        Assert.Equal([1, 2, 3], documents.Manifest);
        Assert.Equal("k:AAAA", documents.Signature);
        Assert.Equal(UpdateTrust.ReleasesLatestUrl, handler.Requests[0].Url);
        Assert.Equal(UpdateTrust.DownloadBaseUrl + "/v0.2.0/update-manifest.json", handler.Requests[1].Url);
        Assert.Equal(UpdateTrust.DownloadBaseUrl + "/v0.2.0/update-manifest.json.sig", handler.Requests[2].Url);
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("evil.example", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AcceptsATagWithoutThePrefixAndBuildsTheSameAddress()
    {
        var handler = new FakeHandler(_ => Json("""{"tag_name":"1.4.2"}"""), _ => Bytes([1]), _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        await source.FetchManifestAsync(CancellationToken.None);

        Assert.Equal(UpdateTrust.DownloadBaseUrl + "/v1.4.2/update-manifest.json", handler.Requests[1].Url);
    }

    [Fact]
    public async Task ReturnsNullForNetworkErrors()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("no route to host"));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ReturnsNullWhenTheSecondDocumentFailsToLoad()
    {
        var handler = new FakeHandler(_ => Json(LatestJson), _ => Bytes([1]), _ => throw new HttpRequestException("reset"));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ReturnsNullForServerErrors(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => Json("{}", status));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ReturnsNullForRateLimit(HttpStatusCode status)
    {
        var handler = new FakeHandler(_ => Json("""{"message":"API rate limit exceeded"}""", status));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ReturnsNullForHtmlInsteadOfJson()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Please log in to the Wi-Fi</body></html>", Encoding.UTF8, "text/html"),
        });
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("""{"name":"no tag"}""")]
    [InlineData("""{"tag_name":42}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"tag_name":null}""")]
    public async Task ReturnsNullForJsonWithoutATagString(string json)
    {
        var handler = new FakeHandler(_ => Json(json));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("v1.2.3/../../evil")]
    [InlineData("")]
    public async Task RejectsATagThatIsNoVersion(string tag)
    {
        var handler = new FakeHandler(_ => Json($$"""{"tag_name":"{{tag}}"}"""));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task RejectsAResponseOverOneMiB()
    {
        var oversized = new byte[(1024 * 1024) + 1];
        var handler = new FakeHandler(_ => Json(LatestJson), _ => Bytes(oversized), _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsAResponseOverOneMiBWithoutALengthHeader()
    {
        var oversized = new byte[(1024 * 1024) + 1];
        var handler = new FakeHandler(
            _ => Json(LatestJson),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(oversized) },
            _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AcceptsAResponseOfExactlyOneMiB()
    {
        var handler = new FakeHandler(_ => Json(LatestJson), _ => Bytes(new byte[1024 * 1024]), _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        var documents = await source.FetchManifestAsync(CancellationToken.None);

        Assert.NotNull(documents);
        Assert.Equal(1024 * 1024, documents.Manifest.Length);
    }

    [Fact]
    public async Task RefusesARedirectToAnotherHost()
    {
        var handler = new FakeHandler(
            _ => Json(LatestJson),
            _ => Redirect("https://evil.example/update-manifest.json"),
            _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.DoesNotContain(handler.Requests, request => request.Url.Contains("evil.example", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://github.com/benjo-code/Pagewing/x")]
    [InlineData("https://github.com.evil.example/x")]
    [InlineData("https://evilgithub.com/x")]
    [InlineData("https://githubusercontent.com.evil.example/x")]
    [InlineData("https://notgithubusercontent.com/x")]
    public async Task RefusesLookAlikeAndInsecureRedirectTargets(string target)
    {
        var handler = new FakeHandler(_ => Json(LatestJson), _ => Redirect(target));
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FollowsRedirectsWithinGitHubHosts()
    {
        var handler = new FakeHandler(
            _ => Json(LatestJson),
            _ => Redirect("https://release-assets.githubusercontent.com/some/object?token=abc"),
            _ => Bytes([7, 7]),
            _ => Bytes([2]));
        using var source = new GitHubUpdateSource(handler);

        var documents = await source.FetchManifestAsync(CancellationToken.None);

        Assert.NotNull(documents);
        Assert.Equal([7, 7], documents.Manifest);
        Assert.Equal("https://release-assets.githubusercontent.com/some/object?token=abc", handler.Requests[2].Url);
        Assert.All(handler.Requests, request => Assert.Equal(["User-Agent"], request.HeaderNames));
    }

    [Fact]
    public async Task StopsAfterTooManyRedirects()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => Json(LatestJson));
        handler.Fallback = _ =>
        {
            calls++;
            return Redirect("https://github.com/loop");
        };
        using var source = new GitHubUpdateSource(handler);

        Assert.Null(await source.FetchManifestAsync(CancellationToken.None));
        Assert.InRange(calls, 1, 10);
    }

    [Fact]
    public async Task CancellingTheFetchThrows()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(_ =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });
        using var source = new GitHubUpdateSource(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchManifestAsync(cts.Token));
    }

    [Fact]
    public async Task ReportsProgress()
    {
        var payload = Payload(200_000);
        var handler = new FakeHandler(_ => Bytes(payload));
        using var source = new GitHubUpdateSource(handler);
        var progress = new Recorder();

        var done = await source.DownloadAsync(Version, FileOf(payload.Length), Destination, progress, CancellationToken.None);

        Assert.True(done);
        Assert.Equal(UpdateTrust.DownloadBaseUrl + "/v0.2.0/Pagewing-0.2.0-setup.exe", handler.Requests[0].Url);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(payload.Length, progress.Values[^1]);
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
        Assert.Equal(payload, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(PartFile));
    }

    [Fact]
    public async Task RenamesOnlyAfterTheFullLength()
    {
        var payload = Payload(1000);
        var handler = new FakeHandler(_ => Bytes(payload));
        using var source = new GitHubUpdateSource(handler);

        var done = await source.DownloadAsync(Version, FileOf(payload.Length + 1), Destination, new Recorder(), CancellationToken.None);

        Assert.False(done);
        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(PartFile));
    }

    [Fact]
    public async Task ReplacesAnExistingDestinationOnlyWhenTheDownloadIsComplete()
    {
        await File.WriteAllBytesAsync(Destination, [9, 9, 9], TestContext.Current.CancellationToken);
        var handler = new FakeHandler(_ => Bytes(Payload(10)), _ => Bytes(Payload(5)));
        using var source = new GitHubUpdateSource(handler);

        Assert.False(await source.DownloadAsync(Version, FileOf(11), Destination, new Recorder(), CancellationToken.None));
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));

        Assert.True(await source.DownloadAsync(Version, FileOf(5), Destination, new Recorder(), CancellationToken.None));
        Assert.Equal(Payload(5), await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeletesThePartFileOnCancel()
    {
        using var cts = new CancellationTokenSource();
        var progress = new CancelAfterFirstReport(cts);
        var payload = Payload(1_000_000);
        var handler = new FakeHandler(_ => Bytes(payload));
        using var source = new GitHubUpdateSource(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.DownloadAsync(Version, FileOf(payload.Length), Destination, progress, cts.Token));

        Assert.True(progress.Reported);
        Assert.False(File.Exists(PartFile));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task DeletesThePartFileWhenTooManyBytesArrive()
    {
        var payload = Payload(5000);
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(payload) });
        using var source = new GitHubUpdateSource(handler);

        var done = await source.DownloadAsync(Version, FileOf(1000), Destination, new Recorder(), CancellationToken.None);

        Assert.False(done);
        Assert.False(File.Exists(PartFile));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task RefusesADeclaredLengthBeyondTheListedSizeBeforeReceivingAnything()
    {
        var payload = Payload(5000);
        var handler = new FakeHandler(_ => Bytes(payload));
        using var source = new GitHubUpdateSource(handler);
        var progress = new Recorder();

        var done = await source.DownloadAsync(Version, FileOf(1000), Destination, progress, CancellationToken.None);

        Assert.False(done);
        Assert.Empty(progress.Values);
        Assert.False(File.Exists(PartFile));
    }

    [Fact]
    public async Task DeletesThePartFileWhenTheDiskIsFull()
    {
        var payload = Payload(100_000);
        var handler = new FakeHandler(_ => Bytes(payload));
        using var source = new GitHubUpdateSource(handler)
        {
            OpenDestination = path => new FullDiskStream(new FileStream(path, FileMode.Create, FileAccess.Write)),
        };

        var done = await source.DownloadAsync(Version, FileOf(payload.Length), Destination, new Recorder(), CancellationToken.None);

        Assert.False(done);
        Assert.False(File.Exists(PartFile));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task ReturnsFalseForServerErrorsAndLeavesNoFile()
    {
        var handler = new FakeHandler(_ => Json("{}", HttpStatusCode.TooManyRequests), _ => throw new HttpRequestException("reset"));
        using var source = new GitHubUpdateSource(handler);

        Assert.False(await source.DownloadAsync(Version, FileOf(10), Destination, new Recorder(), CancellationToken.None));
        Assert.False(await source.DownloadAsync(Version, FileOf(10), Destination, new Recorder(), CancellationToken.None));
        Assert.False(File.Exists(PartFile));
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task DownloadRefusesARedirectToAnotherHost()
    {
        var handler = new FakeHandler(_ => Redirect("https://evil.example/Pagewing-0.2.0-setup.exe"), _ => Bytes(Payload(10)));
        using var source = new GitHubUpdateSource(handler);

        Assert.False(await source.DownloadAsync(Version, FileOf(10), Destination, new Recorder(), CancellationToken.None));
        Assert.Single(handler.Requests);
        Assert.False(File.Exists(Destination));
    }

    [Fact]
    public async Task OverwritesAStalePartFileFromAnEarlierRun()
    {
        await File.WriteAllBytesAsync(PartFile, new byte[5000], TestContext.Current.CancellationToken);
        var payload = Payload(100);
        var handler = new FakeHandler(_ => Bytes(payload));
        using var source = new GitHubUpdateSource(handler);

        Assert.True(await source.DownloadAsync(Version, FileOf(payload.Length), Destination, new Recorder(), CancellationToken.None));
        Assert.Equal(payload, await File.ReadAllBytesAsync(Destination, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CreateMakesASourceWithoutTouchingTheNetwork()
    {
        using var source = GitHubUpdateSource.Create();

        Assert.NotNull(source);
    }

    private sealed class CancelAfterFirstReport(CancellationTokenSource cts) : IProgress<long>
    {
        public bool Reported { get; private set; }

        public void Report(long value)
        {
            Reported = true;
            cts.Cancel();
        }
    }

    /// <summary>Content without a known length, so the declared-length checks cannot help.</summary>
    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FullDiskStream(Stream inner) : Stream
    {
        private int _writes;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_writes++ >= 1)
            {
                throw new IOException("There is not enough space on the disk.");
            }

            inner.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed record Seen(string Url, HttpMethod Method, string? UserAgent, IReadOnlyList<string> HeaderNames);

    private sealed class FakeHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _queue = new(responses);

        public List<Seen> Requests { get; } = [];

        /// <summary>Answers requests once the queue is empty; without it an empty queue is a test error.</summary>
        public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new Seen(
                request.RequestUri!.AbsoluteUri,
                request.Method,
                request.Headers.UserAgent.ToString(),
                request.Headers.Select(header => header.Key).ToList()));
            var next = _queue.Count > 0 ? _queue.Dequeue() : Fallback ?? throw new InvalidOperationException("Unexpected request: " + request.RequestUri);
            return Task.FromResult(next(request));
        }
    }
}
