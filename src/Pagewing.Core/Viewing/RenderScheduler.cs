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
/// <see cref="Dispose"/>, <see cref="SetPageTheme"/> or <see cref="SetFormHighlight"/>, because all three wait for a handler
/// that is running.
/// </para>
/// <para>
/// Every request renders in the <see cref="PageTheme"/> that was current when it started. <see cref="SetPageTheme"/>
/// treats everything of the old theme like a request that is no longer wanted: the running request is cancelled
/// and leaves no trace, and the queue, the cache and the failed keys are emptied. <see cref="SetFormHighlight"/> does the
/// same for the highlight of the form fields; theme and highlight share one epoch.
/// </para>
/// <para>
/// In the same way every request renders one epoch of its page. <see cref="InvalidatePages"/> starts a new epoch for
/// the pages whose content changed (an annotation was added, changed or removed): their images and failed keys are
/// dropped, a running request of such a page is cancelled, and a result of an older epoch of the page is never stored
/// or reported. Unlike <see cref="SetPageTheme"/> it waits for nothing and leaves the other pages and the queue alone.
/// </para>
/// </remarks>
public sealed class RenderScheduler : IDisposable
{
    private static readonly RenderOptions InteractiveOptions = new() { Priority = PdfPriority.Interactive };
    private static readonly RenderOptions BackgroundOptions = new() { Priority = PdfPriority.Background };
    private static readonly RenderOptions DarkInteractiveOptions = InteractiveOptions with { PageTheme = PageTheme.Dark };
    private static readonly RenderOptions DarkBackgroundOptions = BackgroundOptions with { PageTheme = PageTheme.Dark };
    private static readonly RenderOptions HighlightedInteractiveOptions = InteractiveOptions with { FormHighlight = true };
    private static readonly RenderOptions HighlightedBackgroundOptions = BackgroundOptions with { FormHighlight = true };
    private static readonly RenderOptions HighlightedDarkInteractiveOptions = DarkInteractiveOptions with { FormHighlight = true };
    private static readonly RenderOptions HighlightedDarkBackgroundOptions = DarkBackgroundOptions with { FormHighlight = true };

    private readonly Func<RenderRequest, RenderOptions, CancellationToken, Task<RenderedImage>> render;
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

    // Serializes raising the events with Dispose, SetPageTheme and SetFormHighlight, so that no event starts after Dispose
    // has returned and no event of an old theme or highlight starts after the setter has returned. Taken before gate, never
    // the other way round.
    private readonly object eventGate = new();
    private bool silenced;

    // Changed only under eventGate and gate together, so either of them is enough to read them. The epoch counts the
    // changes of theme and highlight: a job of an earlier epoch belongs to old settings, even after a switch back.
    private PageTheme theme;
    private bool highlight;
    private long settingsEpoch;

    // The epoch of each page that InvalidatePages has dropped at least once (a page without an entry is at 0). Changed
    // and read only under gate: InvalidatePages must not wait for eventGate, so Raise takes gate to read it. A job of
    // an earlier epoch of its page shows content the page no longer has.
    private readonly Dictionary<int, long> pageEpochs = [];

    /// <summary>Creates a scheduler that renders the pages of <paramref name="document"/> into <paramref name="cache"/>.</summary>
    /// <remarks>
    /// Whole-page requests use <see cref="PdfDocument.RenderPageAsync"/>, tiles <see cref="PdfDocument.RenderTileAsync"/>;
    /// background requests run with <see cref="PdfPriority.Background"/>. The scheduler does not own the document
    /// or the cache; dispose the scheduler before the document.
    /// </remarks>
    /// <param name="document">The document to render.</param>
    /// <param name="cache">The cache the images are stored in.</param>
    /// <param name="pageTheme">The theme the pages are rendered in until <see cref="SetPageTheme"/> changes it.</param>
    /// <param name="formHighlight">Whether the form fields are highlighted until <see cref="SetFormHighlight"/> changes it.</param>
    public RenderScheduler(PdfDocument document, RenderCache cache, PageTheme pageTheme = PageTheme.Light, bool formHighlight = false)
        : this(RenderWith(document), cache, pageTheme, formHighlight)
    {
    }

    /// <summary>Creates a scheduler with its own render function, which gets the options of each request (for tests).</summary>
    internal RenderScheduler(
        Func<RenderRequest, RenderOptions, CancellationToken, Task<RenderedImage>> render,
        RenderCache cache,
        PageTheme pageTheme = PageTheme.Light,
        bool formHighlight = false)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(cache);
        if (!Enum.IsDefined(pageTheme))
        {
            throw new ArgumentOutOfRangeException(nameof(pageTheme), pageTheme, "Unknown page theme.");
        }

        this.render = render;
        this.cache = cache;
        theme = pageTheme;
        highlight = formHighlight;
        _ = Task.Run(RunWorkerAsync);
    }

    /// <summary>
    /// Occurs on a thread-pool thread when an image has been rendered and stored in the cache. The image may be
    /// evicted again before a handler looks it up.
    /// </summary>
    public event Action<TileKey>? TileReady;

    /// <summary>
    /// Occurs on a thread-pool thread when rendering a key failed, or when its image is larger than the budget of the
    /// cache (it could never be stored). Each key is reported once and not tried again until the scheduler is
    /// disposed, <see cref="SetPageTheme"/> changes the theme, <see cref="SetFormHighlight"/> the highlight or
    /// <see cref="InvalidatePages"/> drops its page.
    /// </summary>
    public event Action<TileKey, Exception>? TileFailed;

    /// <summary>Gets the theme new requests are rendered in. Default: <see cref="PageTheme.Light"/>.</summary>
    public PageTheme PageTheme
    {
        get
        {
            lock (gate)
            {
                return theme;
            }
        }
    }

    /// <summary>Gets whether new requests highlight the form fields (<see cref="RenderOptions.FormHighlight"/>). Default: off.</summary>
    public bool FormHighlight
    {
        get
        {
            lock (gate)
            {
                return highlight;
            }
        }
    }

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
    /// Renders from now on in <paramref name="pageTheme"/>. A change cancels the running request, empties the queue,
    /// the cache and the failed keys, and drops every result of the old theme that is still on its way: it is neither
    /// stored nor reported (no <see cref="TileReady"/>, no <see cref="TileFailed"/>, not marked as failed). Plan again
    /// with <see cref="Update"/> afterwards. Waits only for an event handler that is running, so no event of the old
    /// theme starts after this returns. The same theme, and any call after <see cref="Dispose"/>, do nothing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageTheme"/> is not a defined value.</exception>
    public void SetPageTheme(PageTheme pageTheme)
    {
        if (!Enum.IsDefined(pageTheme))
        {
            throw new ArgumentOutOfRangeException(nameof(pageTheme), pageTheme, "Unknown page theme.");
        }

        lock (eventGate)
        {
            lock (gate)
            {
                if (disposed || pageTheme == theme)
                {
                    return;
                }

                theme = pageTheme;
                settingsEpoch++;
                queue = [];
                next = 0;
                current?.Cancel();
                failed.Clear();

                // The cache has its own lock and calls nothing back; images are stored under this lock too, so none of
                // the old theme can be added after this.
                cache.Clear();
            }
        }
    }

    /// <summary>
    /// Highlights the form fields from now on, or stops (<see cref="RenderOptions.FormHighlight"/>). A change works exactly
    /// like a change of <see cref="SetPageTheme"/>: the running request is cancelled, the queue, the cache and the failed keys
    /// are emptied, and no result of the old setting is stored or reported. Plan again with <see cref="Update"/> afterwards.
    /// Waits only for an event handler that is running, so it must not be called from one. The same value, and any call
    /// after <see cref="Dispose"/>, do nothing.
    /// </summary>
    public void SetFormHighlight(bool formHighlight)
    {
        lock (eventGate)
        {
            lock (gate)
            {
                if (disposed || formHighlight == highlight)
                {
                    return;
                }

                highlight = formHighlight;
                settingsEpoch++;
                queue = [];
                next = 0;
                current?.Cancel();
                failed.Clear();
                cache.Clear();
            }
        }
    }

    /// <summary>
    /// Drops what the scheduler has of <paramref name="pages"/>, because their content changed: starts a new epoch for
    /// each of them, removes their images from the cache (<see cref="RenderCache.RemovePages"/>) and forgets their
    /// failed keys, and cancels the running request if it belongs to one of them. A result of an older epoch of such a
    /// page that is still on its way is neither stored nor reported (no <see cref="TileReady"/>, no
    /// <see cref="TileFailed"/>, not marked as failed). Queued requests stay queued and render the new epoch. Plan
    /// again with <see cref="Update"/> afterwards; the cancelled request is rendered again only if it is wanted then.
    /// Images and requests of other pages are not touched, and the budget of the cache stays as it is. An empty set,
    /// and any call after <see cref="Dispose"/>, do nothing.
    /// </summary>
    /// <remarks>
    /// Takes only the lock of the queue, never the one that serializes the events, so it does not wait for an event
    /// handler and may be called from any thread. Do not call it from a handler of <see cref="TileReady"/> or
    /// <see cref="TileFailed"/>: a handler that drops the page it was told about would make the scheduler render that
    /// page again and again. An event that has already started when this is called may still name a key of the old
    /// epoch; its image is no longer in the cache then, as with any evicted image.
    /// </remarks>
    /// <param name="pages">The zero-based indices of the pages whose content changed.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pages"/> is <see langword="null"/>.</exception>
    public void InvalidatePages(IReadOnlySet<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0)
        {
            return;
        }

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            foreach (var page in pages)
            {
                pageEpochs[page] = PageEpochOf(page) + 1;
            }

            if (current is { } running && pages.Contains(running.Request.Key.Page))
            {
                running.Cancel();
            }

            failed.RemoveWhere(key => pages.Contains(key.Page));

            // Under this lock, like the images that are stored, so none of an old epoch can be added after this.
            cache.RemovePages(pages);
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

    /// <summary>
    /// The render options for a request: the priority follows <see cref="RenderRequest.Background"/>, the page theme is
    /// <paramref name="theme"/>, the highlight of the form fields <paramref name="formHighlight"/>.
    /// </summary>
    internal static RenderOptions OptionsFor(RenderRequest request, PageTheme theme = PageTheme.Light, bool formHighlight = false) => (theme, formHighlight) switch
    {
        (PageTheme.Light, false) => request.Background ? BackgroundOptions : InteractiveOptions,
        (PageTheme.Dark, false) => request.Background ? DarkBackgroundOptions : DarkInteractiveOptions,
        (PageTheme.Light, true) => request.Background ? HighlightedBackgroundOptions : HighlightedInteractiveOptions,
        (PageTheme.Dark, true) => request.Background ? HighlightedDarkBackgroundOptions : HighlightedDarkInteractiveOptions,
        _ => (request.Background ? BackgroundOptions : InteractiveOptions) with { PageTheme = theme, FormHighlight = formHighlight },
    };

    private static Func<RenderRequest, RenderOptions, CancellationToken, Task<RenderedImage>> RenderWith(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return (request, options, cancellationToken) => request.WholePage
            ? document.RenderPageAsync(request.Key.Page, request.Scale, options, cancellationToken)
            : document.RenderTileAsync(request.Key.Page, request.Scale, request.Rect, options, cancellationToken);
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

                job = new Job(request, theme, highlight, settingsEpoch, PageEpochOf(request.Key.Page));
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
            rendering = render(job.Request, OptionsFor(job.Request, job.Theme, job.FormHighlight), job.Token) ?? Task.FromResult<RenderedImage>(null!);
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

            // A cancelled job (no longer wanted, of an old page theme, highlight or page epoch, or disposed) leaves no
            // trace, even if an image arrived in a race. SetPageTheme, SetFormHighlight and InvalidatePages cancel the
            // running job, so the epoch checks only state the rule. Its token source is not disposed: the cancellation callbacks may still be running on
            // a pool thread.
            if (job.IsCancelled || !rendering.IsCompleted || job.SettingsEpoch != settingsEpoch || IsOutdatedPage(job))
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
        Raise(job, error);
    }

    // Must run under the lock.
    private long PageEpochOf(int page) => pageEpochs.GetValueOrDefault(page);

    // Must run under the lock. Whether InvalidatePages has dropped the page of the job since the job started.
    private bool IsOutdatedPage(Job job) => job.PageEpoch != PageEpochOf(job.Request.Key.Page);

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

    private void Raise(Job job, Exception? error)
    {
        var key = job.Request.Key;
        lock (eventGate)
        {
            // The theme or the highlight may have changed between storing the result and here; then the result is already gone from the
            // cache (and from the failed keys), and it is not reported either.
            if (silenced || job.SettingsEpoch != settingsEpoch)
            {
                return;
            }

            // The same for the epoch of the page. It is only guarded by gate, which may be taken inside eventGate (never
            // the other way round). InvalidatePages may still come right after this check; see its remarks.
            lock (gate)
            {
                if (IsOutdatedPage(job))
                {
                    return;
                }
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

    /// <summary>The running request, its page theme and highlight, the epoch of its page and the source of its cancellation.</summary>
    private sealed class Job : IDisposable
    {
        private readonly CancellationTokenSource source = new();

        public Job(RenderRequest request, PageTheme theme, bool formHighlight, long settingsEpoch, long pageEpoch)
        {
            Request = request;
            Theme = theme;
            FormHighlight = formHighlight;
            SettingsEpoch = settingsEpoch;
            PageEpoch = pageEpoch;
            Token = source.Token;
        }

        public RenderRequest Request { get; }

        // The page theme and highlight the job renders in: the scheduler's settings when it started, and their epoch.
        public PageTheme Theme { get; }

        public bool FormHighlight { get; }

        public long SettingsEpoch { get; }

        // The epoch of the request's page when the job started (see InvalidatePages).
        public long PageEpoch { get; }

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
