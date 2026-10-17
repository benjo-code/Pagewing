using System.Runtime.ExceptionServices;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// Renders the wanted pages and tiles of one document into a <see cref="RenderCache"/>, one request at a time,
/// in the order of the latest wish list. Requests that are no longer wanted are dropped from the queue, and the
/// running one is cancelled.
/// </summary>
/// <remarks>
/// <para>
/// The queue lives here, not on the PDFium thread: at most one request of the scheduler that is not cancelled is on
/// the PDFium thread at any time (a cancelled one may still wait there until the PDFium thread drops it), so a new
/// wish list reorders the queue without touching work that PDFium already holds.
/// When the running request is cancelled, the scheduler stops waiting for it at once (a registration on its
/// token) and starts the next one; the PDFium thread drops the cancelled request at its next step without
/// rendering further. Details: <c>Docs/DECISIONS.md</c>, entry "Render-Scheduler und Kachelparameter (M3)".
/// </para>
/// <para>
/// <see cref="Update"/> may be called from any thread, typically the UI thread on every scroll event. It only
/// replaces the queue under a lock and never waits or renders on the calling thread. The events are raised on a
/// thread-pool thread. Their handlers must be short and must not wait for the thread that calls
/// <see cref="Dispose"/>, because <see cref="Dispose"/> waits for a handler that is running.
/// </para>
/// </remarks>
public sealed class RenderScheduler : IDisposable
{
    private static readonly RenderOptions InteractiveOptions = new() { Priority = PdfPriority.Interactive };
    private static readonly RenderOptions BackgroundOptions = new() { Priority = PdfPriority.Background };

    private readonly Func<RenderRequest, CancellationToken, Task<RenderedImage>> render;
    private readonly RenderCache cache;

    // Wakes the worker; released at most once until the worker finds the queue empty again.
    private readonly SemaphoreSlim signal = new(0);

    // Guards everything below except the events.
    private readonly object gate = new();
    private readonly HashSet<TileKey> failed = [];
    private List<RenderRequest> queue = [];
    private int next;
    private Job? current;
    private bool signalled;
    private bool disposed;

    // Serializes raising the events with Dispose, so that no event starts after Dispose has returned.
    private readonly object eventGate = new();
    private bool silenced;

    /// <summary>Creates a scheduler that renders the pages of <paramref name="document"/> into <paramref name="cache"/>.</summary>
    /// <remarks>
    /// Whole-page requests use <see cref="PdfDocument.RenderPageAsync"/>, tiles <see cref="PdfDocument.RenderTileAsync"/>;
    /// background requests run with <see cref="PdfPriority.Background"/>. The scheduler does not own the document
    /// or the cache; dispose the scheduler before the document.
    /// </remarks>
    public RenderScheduler(PdfDocument document, RenderCache cache)
        : this(RenderWith(document), cache)
    {
    }

    /// <summary>Creates a scheduler with its own render function (for tests).</summary>
    internal RenderScheduler(Func<RenderRequest, CancellationToken, Task<RenderedImage>> render, RenderCache cache)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(cache);
        this.render = render;
        this.cache = cache;
        _ = Task.Run(RunWorkerAsync);
    }

    /// <summary>
    /// Occurs on a thread-pool thread when an image has been rendered and stored in the cache. The image may be
    /// evicted again before a handler looks it up.
    /// </summary>
    public event Action<TileKey>? TileReady;

    /// <summary>
    /// Occurs on a thread-pool thread when rendering a key failed, or when its image is larger than the budget of the
    /// cache (it could never be stored). Each key is reported once and not tried again for the lifetime of the
    /// scheduler.
    /// </summary>
    public event Action<TileKey, Exception>? TileFailed;

    /// <summary>Gets the number of wanted requests that are queued or running.</summary>
    public int PendingCount
    {
        get
        {
            lock (gate)
            {
                return queue.Count - next + (current is { IsCancelled: false } ? 1 : 0);
            }
        }
    }

    /// <summary>
    /// Replaces the wish list. The order is the priority, earlier requests first. Keys already in the cache, keys
    /// that failed before and repeated keys are skipped. The running request continues if it is still wanted
    /// (wherever it is in the list) and is cancelled otherwise. After <see cref="Dispose"/> this does nothing.
    /// </summary>
    /// <param name="wanted">The requests to render, most important first, for example from <see cref="ViewportPlanner.Plan"/>.</param>
    public void Update(IReadOnlyList<RenderRequest> wanted)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            var running = current is { IsCancelled: false } job ? job : null;
            var runningWanted = false;
            var seen = new HashSet<TileKey>(wanted.Count);
            var replacement = new List<RenderRequest>(wanted.Count);
            foreach (var request in wanted)
            {
                if (!seen.Add(request.Key))
                {
                    continue;
                }

                if (running is not null && running.Request.Key == request.Key)
                {
                    runningWanted = true;
                    continue;
                }

                if (!failed.Contains(request.Key) && !cache.Contains(request.Key))
                {
                    replacement.Add(request);
                }
            }

            queue = replacement;
            next = 0;
            if (running is not null && !runningWanted)
            {
                running.Cancel();
            }

            Signal();
        }
    }

    /// <summary>
    /// Stops the scheduler: the queue is dropped, the running request is cancelled, and no event is raised after
    /// this returns. Does not wait for the running request; it waits only for an event handler that is running.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                queue = [];
                next = 0;
                current?.Cancel();

                // Wakes the worker so that it ends; it disposes the semaphore.
                signal.Release();
            }
        }

        lock (eventGate)
        {
            silenced = true;
        }
    }

    /// <summary>The render options for a request: the priority follows <see cref="RenderRequest.Background"/>.</summary>
    internal static RenderOptions OptionsFor(RenderRequest request) => request.Background ? BackgroundOptions : InteractiveOptions;

    private static Func<RenderRequest, CancellationToken, Task<RenderedImage>> RenderWith(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (request, cancellationToken) => request.WholePage
            ? document.RenderPageAsync(request.Key.Page, request.Scale, OptionsFor(request), cancellationToken)
            : document.RenderTileAsync(request.Key.Page, request.Scale, request.Rect, OptionsFor(request), cancellationToken);
    }

    // Must run under the lock.
    private void Signal()
    {
        if (!signalled && next < queue.Count)
        {
            signalled = true;
            signal.Release();
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (true)
            {
                await signal.WaitAsync().ConfigureAwait(false);
                while (TryStartNext(out var job))
                {
                    await RenderAsync(job).ConfigureAwait(false);
                }

                lock (gate)
                {
                    if (disposed)
                    {
                        return;
                    }
                }
            }
        }
        finally
        {
            signal.Dispose();
        }
    }

    /// <summary>Takes the next wanted request and makes it the running one; clears the signal when there is none.</summary>
    private bool TryStartNext(out Job job)
    {
        lock (gate)
        {
            while (!disposed && next < queue.Count)
            {
                var request = queue[next++];

                // A key may have been cached or have failed since the list was set.
                if (failed.Contains(request.Key) || cache.Contains(request.Key))
                {
                    continue;
                }

                job = new Job(request);
                current = job;
                return true;
            }

            signalled = false;
            job = null!;
            return false;
        }
    }

    private async Task RenderAsync(Job job)
    {
        Task<RenderedImage> rendering;
        try
        {
            rendering = render(job.Request, job.Token) ?? Task.FromResult<RenderedImage>(null!);
        }
        catch (Exception ex)
        {
            rendering = Task.FromException<RenderedImage>(ex);
        }

        if (!rendering.IsCompleted)
        {
            // Stop waiting as soon as the job is cancelled; the renderer may notice the cancellation much later
            // (the PDFium thread only checks it when the request is next taken from its queue).
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (job.Token.UnsafeRegister(static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled))
            {
                await Task.WhenAny(rendering, cancelled.Task).ConfigureAwait(false);
            }

            if (!rendering.IsCompleted)
            {
                // Abandoned: observe a later failure so that it does not surface as an unobserved task exception.
                _ = rendering.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        Exception? error = null;
        lock (gate)
        {
            current = null;

            // A cancelled job (no longer wanted, or disposed) leaves no trace, even if an image arrived in a race.
            // Its token source is not disposed: the cancellation callbacks may still be running on a pool thread.
            if (job.IsCancelled || !rendering.IsCompleted)
            {
                return;
            }

            if (rendering.IsCompletedSuccessfully && rendering.Result is { } image && RenderCache.CostOf(image) > cache.BudgetBytes)
            {
                // The cache would not store it, so a TileReady would send the handler to an empty cache, and the next
                // Update would render it again, endlessly. Reported as failed instead, once.
                error = new InvalidOperationException(
                    $"The rendered image ({RenderCache.CostOf(image)} bytes) is larger than the cache budget ({cache.BudgetBytes} bytes).");
                failed.Add(job.Request.Key);
            }
            else if (rendering.IsCompletedSuccessfully && rendering.Result is { } cached)
            {
                // Stored under the lock, so that an Update never sees the key as neither running nor cached.
                cache.Add(job.Request.Key, cached);
            }
            else
            {
                error = FailureOf(rendering);
                failed.Add(job.Request.Key);
            }
        }

        // Not cancelled and no longer the running job: nothing can cancel it any more.
        job.Dispose();
        Raise(job.Request.Key, error);
    }

    private static Exception FailureOf(Task<RenderedImage> rendering)
    {
        if (rendering.IsCanceled)
        {
            // The renderer gave up on its own, the scheduler did not cancel it.
            return new TaskCanceledException(rendering);
        }

        if (rendering.Exception is { } aggregate)
        {
            return aggregate.InnerExceptions.Count == 1 ? aggregate.InnerExceptions[0] : aggregate;
        }

        return new InvalidOperationException("The renderer returned no image.");
    }

    private void Raise(TileKey key, Exception? error)
    {
        lock (eventGate)
        {
            if (silenced)
            {
                return;
            }

            try
            {
                if (error is null)
                {
                    TileReady?.Invoke(key);
                }
                else
                {
                    TileFailed?.Invoke(key, error);
                }
            }
            catch (Exception ex)
            {
                // A failing handler must not stop the worker; it surfaces like any unhandled exception on a pool thread.
                var captured = ExceptionDispatchInfo.Capture(ex);
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), captured);
            }
        }
    }

    /// <summary>The running request and the source of its cancellation.</summary>
    private sealed class Job : IDisposable
    {
        private readonly CancellationTokenSource source = new();

        public Job(RenderRequest request)
        {
            Request = request;
            Token = source.Token;
        }

        public RenderRequest Request { get; }

        public CancellationToken Token { get; }

        public bool IsCancelled => source.IsCancellationRequested;

        // Must run under the scheduler's lock while the job is the running one (never after Dispose).
        public void Cancel()
        {
            // The flag is set at once; the registered callbacks run on a pool thread, never on the caller's thread.
            _ = source.CancelAsync();
        }

        public void Dispose() => source.Dispose();
    }
}
