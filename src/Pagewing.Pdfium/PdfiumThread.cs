using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>
/// The single dedicated thread that runs every PDFium call. PDFium is not thread-safe, not even across
/// documents, so all access is serialized here. Interactive work runs before background work, and stepped
/// work yields between steps.
/// </summary>
internal sealed class PdfiumThread : IDisposable
{
    private static readonly Lazy<PdfiumThread> SharedThread =
        new(() => new PdfiumThread(Fpdf.FPDF_InitLibrary, Fpdf.FPDF_DestroyLibrary));

    private readonly object gate = new();
    private readonly LinkedList<WorkItem> interactive = new();
    private readonly LinkedList<WorkItem> background = new();
    private readonly Action? onStart;
    private readonly Action? onStop;
    private readonly Thread thread;
    private bool disposed;

    public PdfiumThread(Action? onStart = null, Action? onStop = null, string name = "Pagewing.Pdfium")
    {
        this.onStart = onStart;
        this.onStop = onStop;
        thread = new Thread(Run) { IsBackground = true, Name = name };
        thread.Start();
    }

    /// <summary>
    /// Gets the process-wide PDFium thread, created on first use. It initializes PDFium on start and is never
    /// disposed in production, so <c>FPDF_DestroyLibrary</c> does not run at process exit.
    /// </summary>
    public static PdfiumThread Shared => SharedThread.Value;

    /// <summary>Gets a value indicating whether the calling thread is the PDFium thread.</summary>
    public bool IsCurrent => Environment.CurrentManagedThreadId == thread.ManagedThreadId;

    /// <summary>Throws when the calling thread is not the PDFium thread.</summary>
    public void VerifyAccess()
    {
        if (!IsCurrent)
        {
            throw new PdfiumThreadViolationException();
        }
    }

    public Task<T> InvokeAsync<T>(Func<T> work, PdfiumPriority priority = PdfiumPriority.Interactive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new FuncItem<T>(work, cancellationToken);
        Enqueue(item, priority);
        return item.Task;
    }

    public Task InvokeAsync(Action work, PdfiumPriority priority = PdfiumPriority.Interactive, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return InvokeAsync(
            () =>
            {
                work();
                return 0;
            },
            priority,
            cancellationToken);
    }

    /// <summary>
    /// Runs stepped work. <paramref name="start"/> and every <see cref="SteppedWork{T}.Step"/> run on the
    /// PDFium thread; between steps other queued work may run.
    /// </summary>
    public Task<T> InvokeSteppedAsync<T>(Func<SteppedWork<T>> start, PdfiumPriority priority = PdfiumPriority.Background, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);
        var item = new SteppedItem<T>(start, cancellationToken);
        Enqueue(item, priority);
        return item.Task;
    }

    /// <summary>Queues fire-and-forget work. Exceptions are swallowed; after disposal this does nothing.</summary>
    public void Post(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        var item = new PostItem(work);
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            interactive.AddLast(item);
            Monitor.Pulse(gate);
        }
    }

    /// <summary>Runs <c>onStop</c> on the PDFium thread and waits for the thread to end.</summary>
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Monitor.Pulse(gate);
        }

        // Disposing from within the thread itself would deadlock on Join; the thread ends on its own then.
        if (!IsCurrent)
        {
            thread.Join();
        }
    }

    private void Enqueue(WorkItem item, PdfiumPriority priority)
    {
        lock (gate)
        {
            if (!disposed)
            {
                (priority == PdfiumPriority.Interactive ? interactive : background).AddLast(item);
                Monitor.Pulse(gate);
                return;
            }
        }

        item.Reject(new ObjectDisposedException(nameof(PdfiumThread)));
    }

    private void Run()
    {
        Exception? startFailure = null;
        try
        {
            onStart?.Invoke();
        }
        catch (Exception ex)
        {
            startFailure = ex;
        }

        while (TryTakeNext(out var item, out var priority, out var stopping))
        {
            bool again;
            if (stopping)
            {
                // Disposal drops work that has not finished yet.
                item.Reject(new ObjectDisposedException(nameof(PdfiumThread)));
                again = false;
            }
            else if (startFailure is not null)
            {
                item.Reject(startFailure);
                again = false;
            }
            else
            {
                again = item.Run();
            }

            if (again)
            {
                lock (gate)
                {
                    // A stepped item continues before other work of its class.
                    (priority == PdfiumPriority.Interactive ? interactive : background).AddFirst(item);
                }
            }
        }

        try
        {
            onStop?.Invoke();
        }
        catch (Exception)
        {
            // Nothing sensible left to do during shutdown.
        }
    }

    /// <summary>
    /// Takes the next item (interactive first). Returns <see langword="false"/> once the thread is disposed
    /// and the queues are empty. <paramref name="stopping"/> tells that the thread is disposed, so the item
    /// must be rejected instead of run.
    /// </summary>
    private bool TryTakeNext(out WorkItem item, out PdfiumPriority priority, out bool stopping)
    {
        lock (gate)
        {
            stopping = disposed;
            while (true)
            {
                if (interactive.First is { } first)
                {
                    interactive.RemoveFirst();
                    item = first.Value;
                    priority = PdfiumPriority.Interactive;
                    return true;
                }

                if (background.First is { } next)
                {
                    background.RemoveFirst();
                    item = next.Value;
                    priority = PdfiumPriority.Background;
                    return true;
                }

                if (disposed)
                {
                    item = null!;
                    priority = default;
                    return false;
                }

                Monitor.Wait(gate);
            }
        }
    }

    private abstract class WorkItem
    {
        /// <summary>Runs the item on the PDFium thread.</summary>
        /// <returns><see langword="true"/> when the item wants to run another step later.</returns>
        public abstract bool Run();

        /// <summary>Fails the item without running it (and releases anything it holds).</summary>
        public abstract void Reject(Exception exception);
    }

    private sealed class PostItem(Action work) : WorkItem
    {
        public override bool Run()
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                // Fire-and-forget by contract.
            }

            return false;
        }

        public override void Reject(Exception exception)
        {
        }
    }

    private sealed class FuncItem<T>(Func<T> work, CancellationToken cancellationToken) : WorkItem
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => completion.Task;

        public override bool Run()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
                return false;
            }

            try
            {
                completion.TrySetResult(work());
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            return false;
        }

        public override void Reject(Exception exception) => completion.TrySetException(exception);
    }

    private sealed class SteppedItem<T>(Func<SteppedWork<T>> start, CancellationToken cancellationToken) : WorkItem
    {
        private readonly TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private SteppedWork<T>? work;

        public Task<T> Task => completion.Task;

        public override bool Run()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Reject(new OperationCanceledException(cancellationToken));
                return false;
            }

            try
            {
                work ??= start();
                if (!work.Step())
                {
                    return true;
                }

                var result = work.Complete();
                Finish();
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                Reject(ex);
            }

            return false;
        }

        public override void Reject(Exception exception)
        {
            try
            {
                Finish();
            }
            catch (Exception)
            {
                // The original failure is the one worth reporting.
            }

            if (exception is OperationCanceledException canceled)
            {
                completion.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                completion.TrySetException(exception);
            }
        }

        private void Finish()
        {
            var finished = work;
            work = null;
            finished?.Dispose();
        }
    }
}
