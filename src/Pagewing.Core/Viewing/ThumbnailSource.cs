using Pagewing.Core.Pdf;

namespace Pagewing.Core.Viewing;

/// <summary>
/// The page thumbnails of one document: an own <see cref="RenderCache"/> and an own <see cref="RenderScheduler"/>
/// on the same <see cref="PdfDocument"/>, apart from the cache and the scheduler of the main view. Created by
/// <see cref="ViewerSession.EnsureThumbnails"/>, which also disposes it together with the document.
/// </summary>
/// <remarks>
/// The images are stored under <c>new TileKey(page, TileKey.PreviewScaleKey, 0, 0)</c>. That key is also the one of
/// the coarse previews of the main view, but the caches are separate, so the two never clash. All requests are
/// background requests (see <see cref="ThumbnailPlanner"/>), so they yield to the main view on the PDFium thread.
/// </remarks>
public sealed class ThumbnailSource : IDisposable
{
    /// <summary>The memory budget of the thumbnail cache: 48 MiB.</summary>
    public const long BudgetBytes = 48L * 1024 * 1024;

    private int disposed;

    /// <summary>
    /// Creates the source for <paramref name="document"/>, rendering in <paramref name="pageTheme"/>. The source does not
    /// own the document; dispose it first.
    /// </summary>
    internal ThumbnailSource(PdfDocument document, PageTheme pageTheme = PageTheme.Light)
    {
        ArgumentNullException.ThrowIfNull(document);
        Cache = new RenderCache(BudgetBytes);
        Scheduler = new RenderScheduler(document, Cache, pageTheme);
    }

    /// <summary>Gets the cache that holds the thumbnails.</summary>
    public RenderCache Cache { get; }

    /// <summary>Gets the scheduler that renders the thumbnails into <see cref="Cache"/>.</summary>
    public RenderScheduler Scheduler { get; }

    /// <summary>Gets whether <see cref="Dispose"/> has been called (for tests).</summary>
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    /// <summary>Gets the theme the thumbnails are rendered in.</summary>
    public PageTheme PageTheme => Scheduler.PageTheme;

    /// <summary>
    /// Renders the thumbnails in <paramref name="theme"/> from now on: a change empties <see cref="Cache"/> and drops
    /// the work of the old theme (<see cref="RenderScheduler.SetPageTheme"/>); plan again afterwards. The budget stays.
    /// Does nothing after <see cref="Dispose"/>.
    /// </summary>
    public void SetPageTheme(PageTheme theme)
    {
        if (!IsDisposed)
        {
            // A Dispose that runs meanwhile disposes the scheduler, which then ignores the call.
            Scheduler.SetPageTheme(theme);
        }
    }

    /// <summary>Stops the scheduler and empties the cache. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Scheduler.Dispose();
        Cache.Clear();
    }
}
