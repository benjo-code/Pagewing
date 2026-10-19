using System.Threading.Channels;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;
using Xunit;

namespace Pagewing.Core.Tests.Viewing;

/// <summary>How long the scheduler tests wait.</summary>
internal static class SchedulerWait
{
    // How long a test waits for something that must happen.
    public static readonly TimeSpan Due = TimeSpan.FromSeconds(10);

    // How long a test waits to show that something does not happen.
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);
}

// A renderer whose calls the test completes by hand.
internal sealed class FakeRenderer
{
    private readonly Channel<RenderCall> pending = Channel.CreateUnbounded<RenderCall>();
    private readonly List<RenderCall> calls = [];
    private int active;
    private int maxActive;

    // Whether a call ends as cancelled as soon as its token is cancelled.
    public bool HonorsCancellation { get; init; } = true;

    public int MaxActive => Volatile.Read(ref maxActive);

    public int CallCount
    {
        get
        {
            lock (calls)
            {
                return calls.Count;
            }
        }
    }

    public IReadOnlyList<RenderCall> Calls
    {
        get
        {
            lock (calls)
            {
                return [.. calls];
            }
        }
    }

    public Task<RenderedImage> Render(RenderRequest request, RenderOptions options, CancellationToken token)
    {
        // A call counts as active from here until its result is set (before the scheduler can see the result).
        var now = Interlocked.Increment(ref active);
        int seen;
        while (now > (seen = Volatile.Read(ref maxActive)) && Interlocked.CompareExchange(ref maxActive, now, seen) != seen)
        {
        }

        var call = new RenderCall(request, options, HonorsCancellation, () => Interlocked.Decrement(ref active), token);
        lock (calls)
        {
            calls.Add(call);
        }

        pending.Writer.TryWrite(call);
        return call.Task;
    }

    public async Task<RenderCall> NextCallAsync() =>
        await pending.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(SchedulerWait.Due, TestContext.Current.CancellationToken);

    public async Task AssertNoCallAsync()
    {
        await Task.Delay(SchedulerWait.Quiet, TestContext.Current.CancellationToken);
        Assert.False(pending.Reader.TryPeek(out var call), call is null ? null : $"Unexpected render of page {call.Request.Key.Page}.");
    }
}

internal sealed class RenderCall
{
    private readonly TaskCompletionSource<RenderedImage> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action onEnd;
    private int ended;

    public RenderCall(RenderRequest request, RenderOptions options, bool honorsCancellation, Action onEnd, CancellationToken token)
    {
        Request = request;
        Options = options;
        Token = token;
        this.onEnd = onEnd;
        token.Register(() =>
        {
            cancelled.TrySetResult();
            if (honorsCancellation)
            {
                End();
                result.TrySetCanceled(token);
            }
        });
    }

    public RenderRequest Request { get; }

    public RenderOptions Options { get; }

    public CancellationToken Token { get; }

    public Task<RenderedImage> Task => result.Task;

    // Completes once the token was cancelled and the registered callbacks ran.
    public Task Cancelled => cancelled.Task;

    public void Complete(RenderedImage image)
    {
        End();
        result.TrySetResult(image);
    }

    public void Fail(Exception exception)
    {
        End();
        result.TrySetException(exception);
    }

    private void End()
    {
        if (Interlocked.Exchange(ref ended, 1) == 0)
        {
            onEnd();
        }
    }
}

// Records the events of a scheduler.
internal sealed class EventLog : IDisposable
{
    private readonly RenderScheduler scheduler;
    private readonly Channel<TileKey> ready = Channel.CreateUnbounded<TileKey>();
    private readonly Channel<(TileKey Key, Exception Exception)> failed = Channel.CreateUnbounded<(TileKey, Exception)>();
    private readonly List<TileKey> allReady = [];
    private readonly List<(TileKey Key, Exception Exception)> allFailed = [];

    public EventLog(RenderScheduler scheduler)
    {
        this.scheduler = scheduler;
        scheduler.TileReady += OnReady;
        scheduler.TileFailed += OnFailed;
    }

    public IReadOnlyList<TileKey> Ready
    {
        get
        {
            lock (allReady)
            {
                return [.. allReady];
            }
        }
    }

    public IReadOnlyList<(TileKey Key, Exception Exception)> Failed
    {
        get
        {
            lock (allFailed)
            {
                return [.. allFailed];
            }
        }
    }

    public async Task<TileKey> NextReadyAsync() =>
        await ready.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(SchedulerWait.Due, TestContext.Current.CancellationToken);

    public async Task<(TileKey Key, Exception Exception)> NextFailedAsync() =>
        await failed.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(SchedulerWait.Due, TestContext.Current.CancellationToken);

    public async Task AssertNoReadyAsync()
    {
        await Task.Delay(SchedulerWait.Quiet, TestContext.Current.CancellationToken);
        Assert.False(ready.Reader.TryPeek(out var key), $"Unexpected TileReady for page {key.Page}.");
    }

    public void Dispose()
    {
        scheduler.TileReady -= OnReady;
        scheduler.TileFailed -= OnFailed;
    }

    private void OnReady(TileKey key)
    {
        lock (allReady)
        {
            allReady.Add(key);
        }

        ready.Writer.TryWrite(key);
    }

    private void OnFailed(TileKey key, Exception exception)
    {
        lock (allFailed)
        {
            allFailed.Add((key, exception));
        }

        failed.Writer.TryWrite((key, exception));
    }
}
