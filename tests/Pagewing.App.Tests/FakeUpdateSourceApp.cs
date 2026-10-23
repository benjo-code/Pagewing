using System.Security.Cryptography;
using System.Text;
using Pagewing.Core.Updates;

namespace Pagewing.App.Tests;

/// <summary>
/// An <see cref="IUpdateSource"/> for the tests of the app: counts the fetches (from any thread), tells what runs on the
/// thread that fetches, can answer with a manifest signed by a throwaway key, hang until cancelled or until released,
/// or throw like a misbehaving source. It never touches the network.
/// </summary>
public sealed class FakeUpdateSourceApp : IUpdateSource, IDisposable
{
    private const string KeyId = "test-key";

    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _fetchCalls;
    private int _fetchThreadId = -1;

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

    /// <summary>Prepares a correctly signed manifest of the given version with one Windows installer entry.</summary>
    public void Offer(string version)
    {
        var json = "{\"format\":1,\"version\":\"" + version + "\",\"files\":[{\"kind\":\"win-x64-setup\",\"name\":\"Pagewing-"
            + version + "-win-x64-setup.exe\",\"size\":1000,\"sha256\":\"" + new string('a', 64) + "\"}]}";
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

    public Task<bool> DownloadAsync(
        UpdateVersion version,
        UpdateFile file,
        string destinationPath,
        IProgress<long> bytesReceived,
        CancellationToken ct) =>
        throw new InvalidOperationException("The tests of the check never download.");

    public void Dispose() => _ecdsa.Dispose();
}
