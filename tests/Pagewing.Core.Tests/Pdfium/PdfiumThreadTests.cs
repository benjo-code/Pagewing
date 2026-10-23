using Pagewing.Pdfium;
using Xunit;

namespace Pagewing.Core.Tests.Pdfium;

public sealed class PdfiumThreadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task InvokeRunsOnTheDedicatedThread()
    {
        using var thread = new PdfiumThread();
        var callerId = Environment.CurrentManagedThreadId;

        var (workerId, insideIsCurrent) = await thread.InvokeAsync(
            () => (Environment.CurrentManagedThreadId, thread.IsCurrent),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotEqual(callerId, workerId);
        Assert.True(insideIsCurrent);
        Assert.False(thread.IsCurrent);
    }

    [Fact]
    public async Task AllWorkRunsOnTheSameThread()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;

        var first = await thread.InvokeAsync(() => Environment.CurrentManagedThreadId, cancellationToken: ct);
        var second = await thread.InvokeAsync(() => Environment.CurrentManagedThreadId, PdfiumPriority.Background, ct);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task VerifyAccessThrowsOnForeignThread()
    {
        using var thread = new PdfiumThread();

        Assert.Throws<PdfiumThreadViolationException>(thread.VerifyAccess);

        // Must not throw inside a work item.
        await thread.InvokeAsync(thread.VerifyAccess, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExceptionInWorkFlowsToTheTaskAndThreadSurvives()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;

        var ex = await Assert.ThrowsAsync<FormatException>(
            () => thread.InvokeAsync<int>(() => throw new FormatException("boom"), cancellationToken: ct));
        Assert.Equal("boom", ex.Message);

        Assert.Equal(42, await thread.InvokeAsync(() => 42, cancellationToken: ct));
    }

    [Fact]
    public async Task InteractiveWorkRunsBetweenBackgroundSteps()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;
        var log = new List<string>();
        using var step1Started = new ManualResetEventSlim();
        using var releaseStep1 = new ManualResetEventSlim();

        var stepped = thread.InvokeSteppedAsync(
            () => new RecordingWork(
                log,
                step =>
                {
                    if (step == 1)
                    {
                        step1Started.Set();
                        releaseStep1.Wait(Timeout, ct);
                    }
                },
                stepCount: 3),
            PdfiumPriority.Background,
            ct);

        Assert.True(step1Started.Wait(Timeout, ct));
        var interactive = thread.InvokeAsync(
            () =>
            {
                lock (log)
                {
                    log.Add("interactive");
                }
            },
            PdfiumPriority.Interactive,
            ct);
        releaseStep1.Set();

        await interactive;
        await stepped;

        Assert.Equal(["step1", "interactive", "step2", "step3"], log);
    }

    [Fact]
    public async Task CancellationStopsSteppedWorkAndDisposesIt()
    {
        using var thread = new PdfiumThread();
        using var cts = new CancellationTokenSource();
        var log = new List<string>();
        RecordingWork? work = null;

        var task = thread.InvokeSteppedAsync(
            () => work = new RecordingWork(log, step =>
            {
                if (step == 1)
                {
                    cts.Cancel();
                }
            }, stepCount: 5),
            PdfiumPriority.Background,
            cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);

        // Flush the thread so the dispose is certainly observed.
        await thread.InvokeAsync(() => { }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(work);
        Assert.Equal(1, work.DisposeCount);
        Assert.Equal(["step1"], log);
    }

    [Fact]
    public async Task AlreadyCanceledTokenNeverStartsWork()
    {
        using var thread = new PdfiumThread();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ran = false;
        var started = false;

        var plain = thread.InvokeAsync(() => ran = true, cancellationToken: cts.Token);
        var stepped = thread.InvokeSteppedAsync<int>(
            () =>
            {
                started = true;
                return new RecordingWork([], _ => { }, stepCount: 1);
            },
            cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => plain);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stepped);
        await thread.InvokeAsync(() => { }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(ran);
        Assert.False(started);
    }

    [Fact]
    public async Task DisposeRunsOnStopOnTheThreadAndRejectsNewWork()
    {
        var stopThreadId = 0;
        var startThreadId = 0;
        var thread = new PdfiumThread(
            onStart: () => startThreadId = Environment.CurrentManagedThreadId,
            onStop: () => stopThreadId = Environment.CurrentManagedThreadId);
        var workerId = await thread.InvokeAsync(
            () => Environment.CurrentManagedThreadId,
            cancellationToken: TestContext.Current.CancellationToken);

        thread.Dispose();

        Assert.Equal(workerId, startThreadId);
        Assert.Equal(workerId, stopThreadId);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => thread.InvokeAsync(() => 1, cancellationToken: TestContext.Current.CancellationToken));
        thread.Post(() => throw new InvalidOperationException("must never run"));
        thread.Dispose();
    }

    [Fact]
    public async Task PostRunsWorkAndSwallowsExceptions()
    {
        using var thread = new PdfiumThread();
        var ct = TestContext.Current.CancellationToken;
        var ran = false;

        thread.Post(() => throw new InvalidOperationException("ignored"));
        thread.Post(() => ran = true);
        await thread.InvokeAsync(() => { }, PdfiumPriority.Background, ct);

        Assert.True(ran);
    }

    private sealed class RecordingWork(List<string> log, Action<int> onStep, int stepCount) : SteppedWork<int>
    {
        private int step;

        public int DisposeCount { get; private set; }

        public override bool Step()
        {
            step++;
            lock (log)
            {
                log.Add($"step{step}");
            }

            onStep(step);
            return step >= stepCount;
        }

        public override int Complete() => step;

        public override void Dispose()
        {
            DisposeCount++;
            base.Dispose();
        }
    }
}
