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

    public int StatusCalls { get; private set; }

    public int MakeCalls { get; private set; }

    public int StatusThreadId { get; private set; }

    public bool IsSupported => Supported;

    public async Task<DefaultAppStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        StatusCalls++;
        StatusThreadId = Environment.CurrentManagedThreadId;
        OnStatus?.Invoke();
        if (StatusGate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return StatusError is { } error ? throw error : Status;
    }

    public Task<DefaultAppResult> MakeDefaultAsync(CancellationToken cancellationToken)
    {
        MakeCalls++;
        return Task.FromResult(Result);
    }
}
