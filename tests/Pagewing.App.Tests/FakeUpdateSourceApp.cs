using System.Security.Cryptography;
using System.Text;
using Pagewing.Core.Updates;

namespace Pagewing.App.Tests;

/// <summary>
/// An <see cref="IUpdateSource"/> for the tests of the app: counts the fetches (from any thread), tells what runs on the
/// thread that fetches, can answer with a manifest signed by a throwaway key, hang until cancelled or until released,
/// or throw like a misbehaving source. Downloads write <see cref="Content"/> (or what a test asks for) the way the real
/// source does: into <c>&lt;destination&gt;.part</c> first, then to the destination. It never touches the network.
/// </summary>
public sealed class FakeUpdateSourceApp : IUpdateSource, IDisposable
{
    private const string KeyId = "test-key";

    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _fetchCalls;
    private int _fetchThreadId = -1;
    private int _downloadCalls;

    public FakeUpdateSourceApp() => Key = new UpdateKey(KeyId, _ecdsa.ExportSubjectPublicKeyInfo());

    /// <summary>Gets the public key that verifies what this source signs.</summary>
    public UpdateKey Key { get; }

    /// <summary>Gets the keys a checker needs to trust this source.</summary>
    public IReadOnlyList<UpdateKey> Keys => [Key];

    /// <summary>Gets or sets the documents the next fetch returns; <see langword="null"/> means nothing was received.</summary>
    public UpdateDocuments? Documents { get; set; }

    /// <summary>Gets or sets what a fetch throws instead of answering (a misbehaving source).</summary>
    public Exception? Throws { get; set; }

    /// <summary>Gets or sets whether a fetch waits until it is cancelled instead of answering.</summary>
    public bool Hang { get; set; }

    /// <summary>Gets or sets what a fetch waits for before it answers (and honours cancellation while it waits).</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Gets or sets what runs on the fetching thread when a fetch starts, before it is counted.</summary>
    public Action? OnFetch { get; set; }

    /// <summary>Gets how many fetches started; counted after <see cref="OnFetch"/>, so a test that sees it sees its effects.</summary>
    public int FetchCalls => Volatile.Read(ref _fetchCalls);

    /// <summary>Gets the thread of the last fetch, or -1 while there was none.</summary>
    public int FetchThreadId => Volatile.Read(ref _fetchThreadId);

    /// <summary>Gets or sets the bytes of the release file that <see cref="Offer"/> announces and a download delivers.</summary>
    public byte[] Content { get; set; } = [.. Enumerable.Range(0, 1000).Select(i => (byte)(i * 7))];

    /// <summary>Gets or sets whether a download delivers the content with one byte changed (its checksum does not match).</summary>
    public bool Corrupt { get; set; }

    /// <summary>
    /// Gets or sets what a download waits for after it wrote half of the part file and reported <see cref="ReportBytes"/>
    /// (and honours cancellation while it waits).
    /// </summary>
    public TaskCompletionSource? DownloadGate { get; set; }

    /// <summary>Gets or sets how many bytes a download reports before it waits at <see cref="DownloadGate"/>; half the content without a value.</summary>
    public long? ReportBytes { get; set; }

    /// <summary>Gets or sets what a download throws after it wrote half of the part file (a full disk, for example).</summary>
    public Exception? DownloadThrows { get; set; }

    /// <summary>Gets or sets whether a download stops after half of the file and returns <see langword="false"/>, leaving its part file.</summary>
    public bool DownloadBreaks { get; set; }

    /// <summary>Gets how many downloads started.</summary>
    public int DownloadCalls => Volatile.Read(ref _downloadCalls);

    /// <summary>Gets the destination of the last download.</summary>
    public string? LastDestination { get; private set; }

    /// <summary>Gets the part file of the last download (<c>&lt;destination&gt;.part</c>).</summary>
    public string? LastPart => LastDestination is null ? null : LastDestination + ".part";

    /// <summary>
    /// Prepares a correctly signed manifest of the given version with a Windows installer and an AppImage entry, both
    /// with the size and SHA-256 of <see cref="Content"/>, or with <paramref name="size"/> and a checksum no download
    /// matches (for a download that never ends).
    /// </summary>
    public void Offer(string version, long? size = null)
    {
        var length = size ?? Content.Length;
        var sha = size is null ? Convert.ToHexStringLower(SHA256.HashData(Content)) : new string('a', 64);
        var json = "{\"format\":1,\"version\":\"" + version + "\",\"files\":["
            + Entry(UpdateFileKinds.WindowsSetup, "Pagewing-" + version + "-win-x64-setup.exe", length, sha) + ","
            + Entry(UpdateFileKinds.LinuxAppImage, "Pagewing-" + version + "-x86_64.AppImage", length, sha) + "]}";
        var manifest = Encoding.UTF8.GetBytes(json);
        var signature = _ecdsa.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Documents = new UpdateDocuments(manifest, KeyId + ":" + Convert.ToBase64String(signature));
    }

    public async Task<UpdateDocuments?> FetchManifestAsync(CancellationToken ct)
    {
        Volatile.Write(ref _fetchThreadId, Environment.CurrentManagedThreadId);
        OnFetch?.Invoke();
        Interlocked.Increment(ref _fetchCalls);
        if (Throws is { } error)
        {
            throw error;
        }

        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        return Documents;
    }

    public async Task<bool> DownloadAsync(
        UpdateVersion version,
        UpdateFile file,
        string destinationPath,
        IProgress<long> bytesReceived,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _downloadCalls);
        LastDestination = destinationPath;
        var part = destinationPath + ".part";
        var bytes = Content.ToArray();
        if (Corrupt)
        {
            bytes[^1] ^= 0xFF;
        }

        var half = bytes.Length / 2;
        await File.WriteAllBytesAsync(part, bytes.AsMemory(0, half).ToArray(), ct).ConfigureAwait(false);
        bytesReceived.Report(ReportBytes ?? half);

        if (DownloadThrows is { } error)
        {
            throw error;
        }

        if (DownloadBreaks)
        {
            return false;
        }

        if (DownloadGate is { } gate)
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(part, bytes, ct).ConfigureAwait(false);
        bytesReceived.Report(bytes.Length);
        File.Move(part, destinationPath, overwrite: true);
        return true;
    }

    public void Dispose() => _ecdsa.Dispose();

    private static string Entry(string kind, string name, long size, string sha) =>
        "{\"kind\":\"" + kind + "\",\"name\":\"" + name + "\",\"size\":" + size + ",\"sha256\":\"" + sha + "\"}";
}
