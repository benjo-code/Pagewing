using System.Diagnostics.CodeAnalysis;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Text;

/// <summary>
/// The <see cref="PageText"/> of the pages of one document, read on demand and kept for the most recently used
/// <see cref="MaxCachedPages"/> pages. Created by <see cref="Viewing.ViewerSession.EnsurePageTexts"/>, which also
/// disposes it together with the document. All members may be called from any thread.
/// </summary>
/// <remarks>
/// <para>
/// A page is read once at a time: a call for a page whose load is running joins that load and gets its result. The
/// load runs without the token of any caller; each caller only stops waiting when its own token is canceled, so
/// one canceled call never cancels another and a finished load is cached even if everybody gave up.
/// A load joins with the priority it started with; a later call with another priority does not change it.
/// </para>
/// <para>
/// A failed load is neither cached nor kept: the next call reads the page again.
/// </para>
/// </remarks>
public sealed class PageTextSource : IDisposable
{
    /// <summary>The number of pages whose text is kept.</summary>
    public const int MaxCachedPages = 32;

    private readonly Func<int, PdfPriority, Task<PageText>> read;

    // Guards everything below. Never held while the document is called back into or an event is raised.
    private readonly object gate = new();
    private readonly Dictionary<int, LinkedListNode<PageText>> cached = [];
    private readonly LinkedList<PageText> recent = new(); // most recently used first
    private readonly Dictionary<int, Task<PageText>> loading = [];
    private bool disposed;

    /// <summary>Creates the source for <paramref name="document"/>. The source does not own the document; dispose it first.</summary>
    internal PageTextSource(PdfDocument document)
        : this(document, null)
    {
    }

    /// <summary>Creates the source with its own way to read a page (for tests); <see langword="null"/> reads from <paramref name="document"/>.</summary>
    internal PageTextSource(PdfDocument document, Func<int, PdfPriority, Task<PageText>>? read)
    {
        ArgumentNullException.ThrowIfNull(document);
        this.read = read ?? ((page, priority) => document.GetPageTextAsync(page, priority, CancellationToken.None));
    }

    /// <summary>
    /// Occurs on a pool thread after the text of a page was read and cached, with the 0-based page index, and before the
    /// calls that wait for that page return (for tests). Handlers should be quick. Not raised for a page that is read
    /// after <see cref="Dispose"/>. A handler must not throw; if it does, the exception is ignored.
    /// </summary>
    internal event Action<int>? Loaded;

    /// <summary>Gets whether <see cref="Dispose"/> has been called (for tests).</summary>
    internal bool IsDisposed
    {
        get
        {
            lock (gate)
            {
                return disposed;
            }
        }
    }

    /// <summary>Gets the number of pages whose text is kept (for tests).</summary>
    internal int Count
    {
        get
        {
            lock (gate)
            {
                return cached.Count;
            }
        }
    }

    /// <summary>Gets the text of a page if it is kept, and marks it as recently used.</summary>
    /// <param name="pageIndex">The 0-based page index.</param>
    /// <param name="text">The text, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the text of the page is kept.</returns>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    public bool TryGet(int pageIndex, [NotNullWhen(true)] out PageText? text)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cached.TryGetValue(pageIndex, out var node))
            {
                recent.Remove(node);
                recent.AddFirst(node);
                text = node.Value;
                return true;
            }
        }

        text = null;
        return false;
    }

    /// <summary>
    /// Gets the text of a page: from the cache, from a load that is running, or by starting one.
    /// </summary>
    /// <param name="pageIndex">The 0-based page index.</param>
    /// <param name="priority">The priority of the load if this call starts it.</param>
    /// <param name="cancellationToken">Stops this call waiting; the load itself goes on and is cached.</param>
    /// <returns>The text of the page.</returns>
    /// <exception cref="ObjectDisposedException">The source is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">(in the task) <paramref name="pageIndex"/> is not a page of the document.</exception>
    /// <exception cref="OperationCanceledException">(in the task) <paramref name="cancellationToken"/> was canceled.</exception>
    /// <exception cref="InvalidDataException">(in the task) The page is damaged and cannot be loaded.</exception>
    public Task<PageText> GetAsync(int pageIndex, PdfPriority priority = PdfPriority.Interactive, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<PageText>(cancellationToken);
        }

        Task<PageText> task;
        TaskCompletionSource<PageText>? start = null;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (cached.TryGetValue(pageIndex, out var node))
            {
                recent.Remove(node);
                recent.AddFirst(node);
                return Task.FromResult(node.Value);
            }

            if (!loading.TryGetValue(pageIndex, out var running))
            {
                start = new TaskCompletionSource<PageText>(TaskCreationOptions.RunContinuationsAsynchronously);
                running = loading[pageIndex] = start.Task;
            }

            task = running;
        }

        // Started outside the lock and after the load is registered, so even a load that fails at once is removed again.
        if (start is not null)
        {
            _ = LoadAsync(pageIndex, priority, start);
        }

        return task.WaitAsync(cancellationToken);
    }

    /// <summary>Forgets the text of all pages; loads that are running still deliver their result (for tests).</summary>
    internal void Clear()
    {
        lock (gate)
        {
            cached.Clear();
            recent.Clear();
        }
    }

    /// <summary>
    /// Forgets the text of the given pages, because their content changed; the other pages stay cached with their
    /// order of use. A load of such a page that is running is forgotten too: callers that already wait for it still
    /// get its (old) text, but it is not cached, and a later call reads the page again instead of joining it. An empty
    /// set, and any call after <see cref="Dispose"/>, do nothing.
    /// </summary>
    /// <param name="pages">The zero-based indices of the pages to forget.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pages"/> is <see langword="null"/>.</exception>
    internal void RemovePages(IReadOnlySet<int> pages)
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
                if (cached.Remove(page, out var node))
                {
                    recent.Remove(node);
                }

                // The running load keeps its completion; it sees below that it is no longer the registered one.
                loading.Remove(page);
            }
        }
    }

    /// <summary>
    /// Empties the cache and makes every later call throw <see cref="ObjectDisposedException"/>. Loads that are
    /// running cannot be stopped; callers that wait for them still get their result, which is not cached.
    /// Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            cached.Clear();
            recent.Clear();
        }
    }

    private async Task LoadAsync(int pageIndex, PdfPriority priority, TaskCompletionSource<PageText> completion)
    {
        PageText text;
        try
        {
            // Without the token of a caller: the load is shared. The continuation runs on a pool thread because
            // the PDFium thread completes its tasks asynchronously.
            text = await read(pageIndex, priority).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                ForgetLoad(pageIndex, completion);
            }

            completion.SetException(ex);
            return;
        }

        var keep = false;
        lock (gate)
        {
            // A load that RemovePages forgot read the page before its change; its text is not cached.
            if (ForgetLoad(pageIndex, completion) && !disposed)
            {
                keep = true;
                if (cached.Remove(pageIndex, out var stale))
                {
                    recent.Remove(stale);
                }

                cached[pageIndex] = recent.AddFirst(text);
                while (cached.Count > MaxCachedPages)
                {
                    var oldest = recent.Last!;
                    recent.RemoveLast();
                    cached.Remove(oldest.Value.PageIndex);
                }
            }
        }

        // Before the callers are released, so a caller that returns from its await sees the effect of every handler.
        if (keep)
        {
            RaiseLoaded(pageIndex);
        }

        completion.SetResult(text);
    }

    // Must run under the lock. Removes the load of the page if it is still the registered one (RemovePages may have
    // forgotten it, and a newer load may have taken its place) and says whether it was.
    private bool ForgetLoad(int pageIndex, TaskCompletionSource<PageText> completion)
    {
        if (loading.TryGetValue(pageIndex, out var registered) && registered == completion.Task)
        {
            loading.Remove(pageIndex);
            return true;
        }

        return false;
    }

    private void RaiseLoaded(int pageIndex)
    {
        var handlers = Loaded;
        if (handlers is null)
        {
            return;
        }

        // One by one, so a handler that throws neither stops the others nor reaches the pool, where nobody could catch it.
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<int>)handler)(pageIndex);
            }
            catch (Exception)
            {
                // Ignored on purpose: see above.
            }
        }
    }
}
