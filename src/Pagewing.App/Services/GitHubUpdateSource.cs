using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>
/// The release storage of the project on GitHub as the source of updates, and the only code of Pagewing that
/// touches the network. Never throws for network trouble (see <see cref="IUpdateSource"/>); cancelling throws.
/// </summary>
/// <remarks>
/// The addresses of the manifest, the signature and the files are built from <see cref="UpdateTrust"/> and the
/// version, never taken from an answer. Redirects are followed by hand and only within GitHub hosts over HTTPS.
/// The client is made with the source (<see cref="Create"/>), which the app calls only after the first frame,
/// never at startup. Time limits come from the tokens of the caller; the client itself has none.
/// </remarks>
internal sealed class GitHubUpdateSource : IUpdateSource, IDisposable
{
    /// <summary>The most bytes read for any of the two announcement documents (and the answer naming the release).</summary>
    internal const int MaxDocumentBytes = 1024 * 1024;

    private const int MaxRedirects = 5;
    private const int BufferSize = 81920;
    private const string ManifestFileName = "update-manifest.json";
    private const string SignatureFileName = "update-manifest.json.sig";

    private readonly HttpClient _client;

    /// <summary>Creates a source on <paramref name="handler"/>; the source owns and disposes it.</summary>
    internal GitHubUpdateSource(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(new ProductHeaderValue("Pagewing")));
    }

    /// <summary>Opens the file a download is written to; replaced by tests (a full disk).</summary>
    internal Func<string, Stream> OpenDestination { get; set; } = static path =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

    /// <summary>
    /// Creates the source of the shipped program: no cookies, no credentials, no automatic redirects (they are
    /// followed by hand, within GitHub only), the standard certificate check.
    /// </summary>
    public static GitHubUpdateSource Create() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        PreAuthenticate = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    });

    /// <inheritdoc />
    public async Task<UpdateDocuments?> FetchManifestAsync(CancellationToken ct)
    {
        var latest = await FetchLimitedAsync(UpdateTrust.ReleasesLatestUrl, ct).ConfigureAwait(false);
        if (latest is null || !TryReadTag(latest, out var version))
        {
            return null;
        }

        var manifest = await FetchLimitedAsync(AssetUrl(version, ManifestFileName), ct).ConfigureAwait(false);
        if (manifest is null)
        {
            return null;
        }

        var signature = await FetchLimitedAsync(AssetUrl(version, SignatureFileName), ct).ConfigureAwait(false);
        if (signature is null)
        {
            return null;
        }

        try
        {
            return new UpdateDocuments(manifest, new UTF8Encoding(false, true).GetString(signature));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DownloadAsync(
        UpdateVersion version,
        UpdateFile file,
        string destinationPath,
        IProgress<long> bytesReceived,
        CancellationToken ct)
    {
        var part = destinationPath + ".part";
        var complete = false;
        try
        {
            complete = await DownloadToPartAsync(AssetUrl(version, file.Name), file.Size, part, bytesReceived, ct).ConfigureAwait(false)
                && Promote(part, destinationPath);
            return complete;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or HttpRequestException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false;
        }
        finally
        {
            if (!complete)
            {
                TryDelete(part);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    private static string AssetUrl(UpdateVersion version, string name) =>
        UpdateTrust.DownloadBaseUrl + "/v" + version + "/" + name;

    private static bool TryReadTag(byte[] json, out UpdateVersion version)
    {
        version = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("tag_name", out var tag)
                && tag.ValueKind == JsonValueKind.String
                && UpdateVersion.TryParse(tag.GetString(), out version);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsAllowedRedirectTarget(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
        {
            return false;
        }

        var host = uri.Host;
        return IsHostOrSubdomain(host, "github.com") || IsHostOrSubdomain(host, "githubusercontent.com");
    }

    private static bool IsHostOrSubdomain(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private static bool Promote(string part, string destinationPath)
    {
        File.Move(part, destinationPath, overwrite: true);
        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing more can be done; the next download overwrites the partial file.
        }
    }

    /// <summary>Loads <paramref name="url"/> into memory, or returns <c>null</c> for any trouble or an answer over the limit.</summary>
    private async Task<byte[]?> FetchLimitedAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await GetAsync(url, ct).ConfigureAwait(false);
            if (response is null || response.Content.Headers.ContentLength > MaxDocumentBytes)
            {
                return null;
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[BufferSize];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > MaxDocumentBytes)
                    {
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException
            || (exception is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<bool> DownloadToPartAsync(
        string url,
        long expectedSize,
        string part,
        IProgress<long> bytesReceived,
        CancellationToken ct)
    {
        using var response = await GetAsync(url, ct).ConfigureAwait(false);
        if (response is null || (response.Content.Headers.ContentLength is { } declared && declared != expectedSize))
        {
            return false;
        }

        var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var destination = OpenDestination(part);
            await using (destination.ConfigureAwait(false))
            {
                var chunk = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    total += read;
                    if (total > expectedSize)
                    {
                        return false;
                    }

                    await destination.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesReceived.Report(total);
                }

                await destination.FlushAsync(ct).ConfigureAwait(false);
                return total == expectedSize;
            }
        }
    }

    /// <summary>
    /// Sends a GET and follows redirects by hand, each only to a GitHub host over HTTPS. Returns the successful
    /// response with its headers read, or <c>null</c> (network error, an unwanted status such as a rate limit,
    /// a foreign redirect, too many redirects). Cancelling throws.
    /// </summary>
    private async Task<HttpResponseMessage?> GetAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException
                || (exception is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return null;
            }

            if (response.StatusCode == HttpStatusCode.OK)
            {
                return response;
            }

            var location = IsRedirect(response.StatusCode) ? response.Headers.Location : null;
            response.Dispose();
            if (location is null)
            {
                return null;
            }

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            if (!IsAllowedRedirectTarget(uri))
            {
                return null;
            }
        }

        return null;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
}
