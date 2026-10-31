using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// A file inserted into the page plan of a tab (milestone 13, "Insert file"): the open document (for the thumbnails and the
/// report of what the plan loses), the file it came from with the stamp taken when it was opened (the apply checks the file
/// against it), and the thumbnails of its pages, which are made when a page of the file is first shown. The tab owns it and
/// closes it when the plan ends or the tab goes: <see cref="Dispose"/> stops the thumbnails first and closes the document
/// after them. UI thread only, except <see cref="Document"/> and <see cref="File"/>, which are read by the apply.
/// </summary>
internal sealed class InsertedSource : IDisposable
{
    // Page sizes are read in short background jobs of this many pages.
    private const int SizeBatch = 128;

    private readonly CancellationTokenSource _cancellation = new();
    private readonly List<PdfPageSize> _sizes = [];
    private ThumbnailSource? _thumbnails;
    private bool _disposed;

    /// <summary>Creates the source for a document that was just opened from <paramref name="file"/>.</summary>
    /// <param name="document">The open document; the source owns it.</param>
    /// <param name="file">Its file, path and password, with the stamp taken when it was opened (<see cref="PageSourceFile.Of"/>).</param>
    public InsertedSource(PdfDocument document, PageSourceFile file)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(file);
        Document = document;
        File = file;
        PageCount = document.PageCount;
    }

    /// <summary>Gets the number of the source in the plan (assigned by the tab when the file is inserted; never reused in the tab).</summary>
    public PageSourceId Id { get; internal set; }

    /// <summary>Gets the open document.</summary>
    public PdfDocument Document { get; }

    /// <summary>Gets the file with the stamp it had when it was inserted.</summary>
    public PageSourceFile File { get; }

    /// <summary>Gets the number of pages of the file.</summary>
    public int PageCount { get; }

    /// <summary>Gets the name of the file without its folder.</summary>
    public string Name => Path.GetFileName(File.Path);

    /// <summary>Gets the sizes of the pages read so far (the first ones; more arrive in batches, see <see cref="SizesChanged"/>).</summary>
    public IReadOnlyList<PdfPageSize> Sizes => _sizes;

    /// <summary>Gets the thumbnails once a page of the file was first shown, else <see langword="null"/>.</summary>
    public ThumbnailSource? Thumbnails => _thumbnails;

    /// <summary>Raised on the UI thread when more page sizes are known.</summary>
    public event Action? SizesChanged;

    /// <summary>
    /// Reads the page sizes in the background, a batch at a time (<see cref="PdfPriority.Background"/>, so the rendering of the
    /// tabs goes on between two batches), from where the last call stopped. The dialog reads the first batch before it offers
    /// the pages (so the first pages have a size when they go in); the tab reads on once the file is inserted. UI thread.
    /// </summary>
    /// <param name="firstBatchOnly">Whether to stop after one batch.</param>
    public async Task ReadSizesAsync(bool firstBatchOnly = false)
    {
        var token = _cancellation.Token;
        var batches = 0;
        try
        {
            while (_sizes.Count < PageCount && !token.IsCancellationRequested && !(firstBatchOnly && batches > 0))
            {
                batches++;
                var count = Math.Min(SizeBatch, PageCount - _sizes.Count);
                var batch = await Document.GetPageSizesAsync(_sizes.Count, count, PdfPriority.Background, token);
                if (_disposed)
                {
                    return;
                }

                _sizes.AddRange(batch);
                SizesChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The source went: nobody needs the sizes any more.
        }
    }

    /// <summary>Gets the thumbnails of the file, making them on the first call; <see langword="null"/> after <see cref="Dispose"/>.</summary>
    /// <param name="theme">The theme the thumbnails are rendered in.</param>
    public ThumbnailSource? EnsureThumbnails(PageTheme theme)
    {
        if (_disposed)
        {
            return null;
        }

        return _thumbnails ??= ThumbnailSource.Create(Document, theme);
    }

    /// <summary>Renders the thumbnails in <paramref name="theme"/> from now on (they may not exist yet).</summary>
    public void SetPageTheme(PageTheme theme) => _thumbnails?.SetPageTheme(theme);

    /// <summary>Stops the work of the thumbnails and empties their cache (the tab is no longer in view).</summary>
    public void ReleaseThumbnails()
    {
        if (_thumbnails is { } thumbnails)
        {
            thumbnails.Scheduler.Update([]);
            thumbnails.Cache.Clear();
        }
    }

    /// <summary>Stops the thumbnails, then closes the document. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();

        // The thumbnails first: their scheduler renders from the document.
        _thumbnails?.Dispose();
        _thumbnails = null;
        Document.Dispose();
        _cancellation.Dispose();
    }
}
