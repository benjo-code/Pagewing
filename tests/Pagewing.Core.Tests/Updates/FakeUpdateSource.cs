using System.Security.Cryptography;
using System.Text;
using Pagewing.Core.Updates;

namespace Pagewing.Core.Tests.Updates;

/// <summary>A clock that moves only when told to; its timers fire (on the calling thread) when it moves past them.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _now.UtcTicks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        _now += by;
        foreach (var timer in _timers.ToArray())
        {
            timer.FireIfDue(_now);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            clock._timers.Remove(this);
            _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            if (_due is not null)
            {
                clock._timers.Add(this);
            }

            return true;
        }

        public void FireIfDue(DateTimeOffset now)
        {
            if (_due is { } due && due <= now)
            {
                Dispose();
                callback(state);
            }
        }

        public void Dispose()
        {
            _due = null;
            clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An <see cref="IUpdateSource"/> for tests: counts the calls, answers with prepared documents signed by a throwaway
/// key, can hang until cancelled, and writes prepared bytes for a download.
/// </summary>
internal sealed class FakeUpdateSource : IUpdateSource, IDisposable
{
    public const string KeyId = "test-key";

    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public FakeUpdateSource() => Key = new UpdateKey(KeyId, _ecdsa.ExportSubjectPublicKeyInfo());

    /// <summary>Gets the public key that verifies what this source signs.</summary>
    public UpdateKey Key { get; }

    /// <summary>Gets the keys a checker needs to trust this source.</summary>
    public IReadOnlyList<UpdateKey> Keys => [Key];

    /// <summary>Gets or sets the documents the next fetch returns; <c>null</c> means nothing was received.</summary>
    public UpdateDocuments? Documents { get; set; }

    /// <summary>Gets or sets whether a fetch waits until it is cancelled instead of answering.</summary>
    public bool Hang { get; set; }

    /// <summary>Gets or sets the bytes a download writes; <c>null</c> means nothing was received.</summary>
    public byte[]? DownloadBytes { get; set; }

    public int FetchCalls { get; private set; }

    public int DownloadCalls { get; private set; }

    public UpdateVersion? LastDownloadVersion { get; private set; }

    public UpdateFile? LastDownloadFile { get; private set; }

    /// <summary>Prepares a correctly signed manifest of the given version with one Windows installer entry.</summary>
    public void Offer(string version)
    {
        var json = "{\"format\":1,\"version\":\"" + version + "\",\"files\":[{\"kind\":\"win-x64-setup\",\"name\":\"Pagewing-"
            + version + "-win-x64-setup.exe\",\"size\":1000,\"sha256\":\"" + new string('a', 64) + "\"}]}";
        Documents = Sign(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Signs raw manifest bytes with this source's key.</summary>
    public UpdateDocuments Sign(byte[] manifest)
    {
        var signature = _ecdsa.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new UpdateDocuments(manifest, KeyId + ":" + Convert.ToBase64String(signature));
    }

    public async Task<UpdateDocuments?> FetchManifestAsync(CancellationToken ct)
    {
        FetchCalls++;
        if (Hang)
        {
            await Task.Delay(Timeout.Infinite, ct);
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
        DownloadCalls++;
        LastDownloadVersion = version;
        LastDownloadFile = file;
        ct.ThrowIfCancellationRequested();
        if (DownloadBytes is not { } bytes)
        {
            return false;
        }

        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
        bytesReceived.Report(bytes.Length);
        return true;
    }

    public void Dispose() => _ecdsa.Dispose();
}
