using Pagewing.Core.Distribution;

namespace Pagewing.App.Tests;

/// <summary>A default app service that asks no system: the tests of the card and the command drive it.</summary>
public sealed class FakeDefaultAppService : IDefaultAppService
{
    public bool Supported { get; set; } = true;

    public DefaultAppStatus Status { get; set; } = DefaultAppStatus.NotDefault;

    /// <summary>Gets what the status check throws, or <see langword="null"/> to answer <see cref="Status"/>.</summary>
    public Exception? StatusError { get; set; }

    public DefaultAppResult Result { get; set; } = DefaultAppResult.Done;

    /// <summary>Gets what the status check waits for before it answers, or <see langword="null"/> to answer at once.</summary>
    public TaskCompletionSource? StatusGate { get; set; }

    /// <summary>Gets what runs on the thread that asks for the status, before it answers.</summary>
    public Action? OnStatus { get; set; }

    private int _statusCalls;
    private int _makeCalls;
    private int _statusThreadId = -1;

    /// <summary>Gets how many status checks reached their end of setup; counted last, so a test that sees it also sees the effects of the hooks.</summary>
    public int StatusCalls => Volatile.Read(ref _statusCalls);

    public int MakeCalls => Volatile.Read(ref _makeCalls);

    /// <summary>Gets the thread that asked for the status, or -1 while no check has run.</summary>
    public int StatusThreadId => Volatile.Read(ref _statusThreadId);

    public bool IsSupported => Supported;

    public async Task<DefaultAppStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref _statusThreadId, Environment.CurrentManagedThreadId);
        OnStatus?.Invoke();
        Interlocked.Increment(ref _statusCalls);
        if (StatusGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return StatusError is { } error ? throw error : Status;
    }

    public Task<DefaultAppResult> MakeDefaultAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _makeCalls);
        return Task.FromResult(Result);
    }
}
