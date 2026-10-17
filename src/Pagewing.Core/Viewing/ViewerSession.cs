using System.Runtime.ExceptionServices;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>Where a <see cref="ViewerSession"/> stands.</summary>
public enum ViewerState
{
    /// <summary>No file requested.</summary>
    Empty,

    /// <summary>A file is being opened.</summary>
    Loading,

    /// <summary>The file is encrypted; open it again with a password.</summary>
    PasswordRequired,

    /// <summary>The password was wrong; open the file again with another one.</summary>
    PasswordIncorrect,

    /// <summary>The file could not be opened; <see cref="ViewerSession.Error"/> says why.</summary>
    Failed,

    /// <summary>The document is open and its first page sizes are known.</summary>
    Ready,
}

/// <summary>
/// The open, password and error state of the viewer without any UI: owns the current document, its
/// <see cref="RenderCache"/> and its <see cref="RenderScheduler"/>, and tells the UI about every change.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OpenAsync"/> may be called from any thread. The latest call wins: an older request that is still
/// running ends without an exception and without a <see cref="Changed"/> event, and closes the document it may
/// still have opened. The previous document stays open (and is still returned by <see cref="Document"/>, with
/// <see cref="State"/> being <see cref="ViewerState.Loading"/>) until the new request has a result; then it is
/// closed, whatever the result is. <see cref="Document"/>, <see cref="Cache"/>, <see cref="Scheduler"/> and
/// <see cref="LoadedSizes"/> of a new document appear together with <see cref="ViewerState.Ready"/>; after a
/// failure or a password prompt they are <see langword="null"/> and empty.
/// </para>
/// <para>
/// <see cref="OpenAsync"/> completes when the request has reached its result, that is
/// <see cref="ViewerState.Ready"/> (the first 32 page sizes are loaded), a password prompt or a failure. The
/// remaining page sizes follow in background batches of 256, each batch raising <see cref="Changed"/>.
/// </para>
/// <para>
/// <see cref="Changed"/> is raised on whatever thread made the change (the caller of <see cref="OpenAsync"/> or a
/// pool thread), never while the session holds a lock, and never for a request that was superseded or after
/// <see cref="Dispose"/>. <see cref="Dispose"/> waits for a handler that is running on another thread, so a
/// handler must not wait for the thread that disposes the session; a handler may call
/// <see cref="Close"/> and <see cref="Dispose"/> itself. The session does not subscribe to the events of the
/// scheduler, but it disposes the scheduler, so it must not be closed or disposed from a scheduler handler.
/// </para>
/// </remarks>
public sealed class ViewerSession : IDisposable
{
    /// <summary>The number of page sizes that must be known when the session becomes ready.</summary>
    internal const int FirstBatchSize = 32;

    /// <summary>The number of page sizes per background batch after that.</summary>
    internal const int BackgroundBatchSize = 256;

    // Counts the Changed handlers of this thread, so that Dispose called from one does not wait for itself.
    [ThreadStatic]
    private static int handlerDepth;

    private readonly Func<string, string?, CancellationToken, Task<PdfDocument>> open;

    // Guards everything below up to "raising". Never held while an event is raised or anything is disposed.
    private readonly object gate = new();
    private ViewerState state;
    private string? path;
    private PdfOpenError? error;
    private PdfDocument? document;
    private RenderCache? cache;
    private RenderScheduler? scheduler;
    private IReadOnlyList<PdfPageSize> loadedSizes = [];
    private CancellationTokenSource? request;
    private long generation;
    private volatile bool disposed;

    // The number of Changed invocations that are running or about to; Dispose waits for it to drop to zero.
    private int raising;

    /// <summary>Creates an empty session that opens documents with <paramref name="engine"/>.</summary>
    public ViewerSession(PdfEngine engine)
        : this(OpenWith(engine))
    {
    }

    /// <summary>Creates a session with its own open function (for tests).</summary>
    internal ViewerSession(Func<string, string?, CancellationToken, Task<PdfDocument>> open)
    {
        ArgumentNullException.ThrowIfNull(open);
        this.open = open;
    }

    /// <summary>
    /// Occurs on any thread after every change of the state and after every batch of page sizes. Read the
    /// properties of the session to see what changed; marshal to the UI thread if needed.
    /// </summary>
    public event Action? Changed;

    /// <summary>Gets the current state.</summary>
    public ViewerState State
    {
        get
        {
            lock (gate)
            {
                return state;
            }
        }
    }

    /// <summary>
    /// Gets the full path of the file that was requested last, or <see langword="null"/> when there is none. It is
    /// kept for <see cref="ViewerState.PasswordRequired"/> and <see cref="ViewerState.PasswordIncorrect"/>, so the
    /// file can be opened again with a password, and for <see cref="ViewerState.Failed"/>.
    /// </summary>
    public string? Path
    {
        get
        {
            lock (gate)
            {
                return path;
            }
        }
    }

    /// <summary>Gets why the last request did not reach <see cref="ViewerState.Ready"/>, or <see langword="null"/>.</summary>
    public PdfOpenError? Error
    {
        get
        {
            lock (gate)
            {
                return error;
            }
        }
    }

    /// <summary>Gets the open document; see the remarks of the class for <see cref="ViewerState.Loading"/>.</summary>
    public PdfDocument? Document
    {
        get
        {
            lock (gate)
            {
                return document;
            }
        }
    }

    /// <summary>Gets the cache of the open document.</summary>
    public RenderCache? Cache
    {
        get
        {
            lock (gate)
            {
                return cache;
            }
        }
    }

    /// <summary>Gets the scheduler that renders the open document into <see cref="Cache"/>.</summary>
    public RenderScheduler? Scheduler
    {
        get
        {
            lock (gate)
            {
                return scheduler;
            }
        }
    }

    /// <summary>
    /// Gets the sizes of the first pages of the open document: a prefix of all pages that only grows while the
    /// batches arrive. Each read returns an immutable snapshot, so it is safe to use from any thread.
    /// </summary>
    public IReadOnlyList<PdfPageSize> LoadedSizes
    {
        get
        {
            lock (gate)
            {
                return loadedSizes;
            }
        }
    }

    /// <summary>
    /// Opens a file; a request that is still running is superseded. Failures do not throw but end in
    /// <see cref="ViewerState.Failed"/>, <see cref="ViewerState.PasswordRequired"/> or
    /// <see cref="ViewerState.PasswordIncorrect"/>. After <see cref="Dispose"/> this does nothing.
    /// </summary>
    /// <param name="path">Path of the file; a relative path is resolved against the current directory now.</param>
    /// <param name="password">User or owner password of an encrypted file, or <see langword="null"/>.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is <see langword="null"/> or empty.</exception>
    public async Task OpenAsync(string path, string? password = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // PdfEngine does not translate the exceptions of GetFullPath; an invalid path is a failed open.
        var fullPath = path;
        PdfOpenError? invalidPath = null;
        try
        {
            fullPath = System.IO.Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            invalidPath = PdfOpenError.CannotRead;
        }

        CancellationTokenSource source;
        CancellationTokenSource? superseded;
        long mine;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            mine = Interlocked.Increment(ref generation);
            superseded = request;
            source = request = new CancellationTokenSource();
            state = ViewerState.Loading;
            this.path = fullPath;
            error = null;
        }

        Cancel(superseded);
        Raise(mine);

        PdfDocument? opened = null;
        PdfPageSize[]? sizes = null;
        var firstCount = 0;
        PdfOpenError? failure = invalidPath;
        if (failure is null)
        {
            try
            {
                opened = await open(fullPath, password, source.Token).ConfigureAwait(false);
                sizes = new PdfPageSize[opened.PageCount];
                var first = await opened.GetPageSizesAsync(0, Math.Min(FirstBatchSize, sizes.Length), PdfPriority.Interactive, source.Token).ConfigureAwait(false);
                firstCount = first.Count;
                for (var i = 0; i < firstCount; i++)
                {
                    sizes[i] = first[i];
                }
            }
            catch (Exception ex)
            {
                opened?.Dispose();
                opened = null;
                failure = ex is PdfOpenException openFailure ? openFailure.Error : PdfOpenError.Unknown;
            }
        }

        // Publish the result, unless a newer request or Close/Dispose has overtaken this one.
        PdfDocument? oldDocument;
        RenderScheduler? oldScheduler;
        bool publish;
        lock (gate)
        {
            if (disposed || Volatile.Read(ref generation) != mine)
            {
                oldDocument = null;
                oldScheduler = null;
                publish = false;
            }
            else
            {
                publish = true;
                oldDocument = document;
                oldScheduler = scheduler;
                if (opened is not null)
                {
                    document = opened;
                    cache = new RenderCache();
                    scheduler = new RenderScheduler(opened, cache);
                    loadedSizes = new ArraySegment<PdfPageSize>(sizes!, 0, firstCount);
                    state = ViewerState.Ready;
                    error = null;
                }
                else
                {
                    document = null;
                    cache = null;
                    scheduler = null;
                    loadedSizes = [];
                    state = StateOf(failure!.Value);
                    error = failure;
                }
            }
        }

        if (!publish)
        {
            opened?.Dispose();
            return;
        }

        oldScheduler?.Dispose();
        oldDocument?.Dispose();
        Raise(mine);

        if (opened is not null && firstCount < sizes!.Length)
        {
            _ = LoadRemainingSizesAsync(opened, mine, sizes, firstCount, source.Token);
        }
    }

    /// <summary>
    /// Returns to <see cref="ViewerState.Empty"/>: supersedes a request that is still running and releases the
    /// document, the cache and the scheduler. Does nothing (and raises nothing) on an empty session.
    /// </summary>
    public void Close()
    {
        RenderScheduler? oldScheduler;
        PdfDocument? oldDocument;
        CancellationTokenSource? superseded;
        long mine;
        lock (gate)
        {
            if (disposed || (state == ViewerState.Empty && request is null))
            {
                return;
            }

            mine = Interlocked.Increment(ref generation);
            (oldScheduler, oldDocument, superseded) = Reset();
        }

        Cancel(superseded);
        oldScheduler?.Dispose();
        oldDocument?.Dispose();
        Raise(mine);
    }

    /// <summary>
    /// Releases everything; no <see cref="Changed"/> event starts after this returns (it waits for a handler that
    /// is running on another thread). The document is closed in the background.
    /// </summary>
    public void Dispose()
    {
        RenderScheduler? oldScheduler;
        PdfDocument? oldDocument;
        CancellationTokenSource? superseded;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Interlocked.Increment(ref generation);
            (oldScheduler, oldDocument, superseded) = Reset();
        }

        Cancel(superseded);
        oldScheduler?.Dispose();
        oldDocument?.Dispose();

        // A Raise that started before disposed was set may still be calling a handler. Not when this runs inside a
        // handler: it would wait for itself.
        if (handlerDepth == 0)
        {
            var wait = new SpinWait();
            while (Volatile.Read(ref raising) != 0)
            {
                wait.SpinOnce();
            }
        }
    }

    private static Func<string, string?, CancellationToken, Task<PdfDocument>> OpenWith(PdfEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.OpenAsync;
    }

    private static ViewerState StateOf(PdfOpenError error) => error switch
    {
        PdfOpenError.PasswordRequired => ViewerState.PasswordRequired,
        PdfOpenError.PasswordIncorrect => ViewerState.PasswordIncorrect,
        _ => ViewerState.Failed,
    };

    // The flag is set at once; the callbacks run on a pool thread, never on the caller's thread.
    private static void Cancel(CancellationTokenSource? source) => _ = source?.CancelAsync();

    // Must run under the lock. Returns what the caller has to release after leaving the lock.
    private (RenderScheduler? Scheduler, PdfDocument? Document, CancellationTokenSource? Request) Reset()
    {
        var result = (scheduler, document, request);
        state = ViewerState.Empty;
        path = null;
        error = null;
        document = null;
        cache = null;
        scheduler = null;
        loadedSizes = [];
        request = null;
        return result;
    }

    /// <summary>
    /// Reads the remaining page sizes in background batches and publishes the grown prefix after each. Ends
    /// quietly when the request is superseded or the document is closed meanwhile.
    /// </summary>
    private async Task LoadRemainingSizesAsync(PdfDocument loading, long mine, PdfPageSize[] sizes, int loaded, CancellationToken cancellationToken)
    {
        try
        {
            while (loaded < sizes.Length)
            {
                var count = Math.Min(BackgroundBatchSize, sizes.Length - loaded);
                var batch = await loading.GetPageSizesAsync(loaded, count, PdfPriority.Background, cancellationToken).ConfigureAwait(false);

                // Only the part beyond the published prefix is written, so a reader of a snapshot never sees it change.
                for (var i = 0; i < count; i++)
                {
                    sizes[loaded + i] = batch[i];
                }

                loaded += count;
                lock (gate)
                {
                    if (disposed || Volatile.Read(ref generation) != mine)
                    {
                        return;
                    }

                    loadedSizes = new ArraySegment<PdfPageSize>(sizes, 0, loaded);
                }

                Raise(mine);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Superseded, closed or the PDFium thread is gone: nothing left to load. A page whose size cannot be
            // read is already A4 (see PdfDocument.GetPageSizesAsync), so this is not a failure of the document.
        }
    }

    /// <summary>Raises <see cref="Changed"/> unless the session is disposed or request <paramref name="mine"/> was superseded.</summary>
    private void Raise(long mine)
    {
        Interlocked.Increment(ref raising);
        try
        {
            if (disposed || Volatile.Read(ref generation) != mine)
            {
                return;
            }

            var handler = Changed;
            if (handler is null)
            {
                return;
            }

            handlerDepth++;
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                // A failing handler must not break the state machine; it surfaces like any unhandled exception on a pool thread.
                var captured = ExceptionDispatchInfo.Capture(ex);
                ThreadPool.UnsafeQueueUserWorkItem(static state => ((ExceptionDispatchInfo)state!).Throw(), captured);
            }
            finally
            {
                handlerDepth--;
            }
        }
        finally
        {
            Interlocked.Decrement(ref raising);
        }
    }
}
