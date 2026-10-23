namespace Pagewing.Core.Updates;

/// <summary>The two documents of a release announcement: the manifest bytes and the signature text over them.</summary>
/// <param name="Manifest">The raw bytes of the manifest file, exactly as received (the signature covers them).</param>
/// <param name="Signature">The signature text <c>&lt;keyId&gt;:&lt;base64&gt;</c>.</param>
public sealed record UpdateDocuments(byte[] Manifest, string Signature);

/// <summary>
/// Where release announcements and files come from. The only implementation that ships talks to the release
/// storage of the project; tests use a fake. Implementations never throw for network trouble (no connection,
/// timeout, a server error, an answer that is not what was expected): they return <c>null</c> or <c>false</c>,
/// which means "nothing received". Cancelling throws <see cref="OperationCanceledException"/>.
/// </summary>
public interface IUpdateSource
{
    /// <summary>Fetches the manifest of the latest stable release and its signature, or <c>null</c> if nothing was received.</summary>
    Task<UpdateDocuments?> FetchManifestAsync(CancellationToken ct);

    /// <summary>
    /// Downloads <paramref name="file"/> of release <paramref name="version"/> to <paramref name="destinationPath"/> and
    /// reports the bytes received so far. Returns <c>false</c> if the file was not received completely; a partial file
    /// may then be left at the destination for the caller to delete.
    /// </summary>
    Task<bool> DownloadAsync(
        UpdateVersion version,
        UpdateFile file,
        string destinationPath,
        IProgress<long> bytesReceived,
        CancellationToken ct);
}
