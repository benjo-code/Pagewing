using Pagewing.Core.Updates;

namespace Pagewing.App.Tests;

/// <summary>
/// An <see cref="IUpdateApplier"/> that installs nothing: it records what it was handed (and whether the staged file was
/// there at that moment), can wait at a gate, and answers with <see cref="Result"/>.
/// </summary>
public sealed class FakeUpdateApplier : IUpdateApplier
{
    private int _calls;

    /// <summary>Gets how often <see cref="ApplyAsync"/> was called.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Gets the update of the last call.</summary>
    public StagedUpdate? Staged { get; private set; }

    /// <summary>Gets the bytes of the staged file at the moment of the last call.</summary>
    public byte[]? StagedBytes { get; private set; }

    /// <summary>Gets or sets what runs at the start of a call (to look at the files before anything else happens).</summary>
    public Action<StagedUpdate>? OnApply { get; set; }

    /// <summary>Gets or sets what a call waits for before it answers.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Gets or sets the answer.</summary>
    public ApplyResult Result { get; set; } = ApplyResult.Restarting;

    /// <summary>Gets or sets what a call throws instead of answering.</summary>
    public Exception? Throws { get; set; }

    public async Task<ApplyResult> ApplyAsync(StagedUpdate staged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        Interlocked.Increment(ref _calls);
        Staged = staged;
        var file = Path.Combine(staged.Directory, staged.File.Name);
        StagedBytes = File.Exists(file) ? await File.ReadAllBytesAsync(file, ct) : null;
        OnApply?.Invoke(staged);
        if (Throws is { } error)
        {
            throw error;
        }

        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return Result;
    }
}
