namespace Pagewing.Core.Updates;

/// <summary>The two documents of a release announcement: the manifest bytes and the signature text over them.</summary>
/// <param name="Manifest">The raw bytes of the manifest file, exactly as received (the signature covers them).</param>
/// <param name="Signature">The signature text <c>&lt;keyId&gt;:&lt;base64&gt;</c>.</param>
public sealed record UpdateDocuments(byte[] Manifest, string Signature);

/// <summary>
/// Where release announcements and files come from. The only implementation that ships talks to the release
/// storage of the project; tests use a fake. Implementations never throw for network trouble (no connection, a
/// connection that breaks off, timeout, a server error, an answer that is not what was expected): they return
/// <c>null</c> or <c>false</c>, which means "nothing received". Cancelling throws <see cref="OperationCanceledException"/>,
/// and a download throws the exception of the file system when writing the file fails (a full disk, no access).
/// </summary>
public interface IUpdateSource
{
    /// <summary>Fetches the manifest of the latest stable release and its signature, or <c>null</c> if nothing was received.</summary>
    Task<UpdateDocuments?> FetchManifestAsync(CancellationToken ct);

    /// <summary>
    /// Downloads <paramref name="file"/> of release <paramref name="version"/> to <paramref name="destinationPath"/> and
    /// reports the bytes received so far. The bytes go to <c>&lt;destinationPath&gt;.part</c> first, which becomes the
    /// destination only when the file is complete. Returns <c>false</c> if the file was not received completely (network
    /// trouble, see above); throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> if the file
    /// could not be written, and <see cref="OperationCanceledException"/> when cancelled. In every case but success the
    /// part file is deleted before the call returns or throws, and the destination is not created.
    /// </summary>
    Task<bool> DownloadAsync(
        UpdateVersion version,
        UpdateFile file,
        string destinationPath,
        IProgress<long> bytesReceived,
        CancellationToken ct);
}
