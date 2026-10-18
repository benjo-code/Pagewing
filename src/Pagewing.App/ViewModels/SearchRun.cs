using System.Diagnostics;
using Avalonia.Threading;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The matches of one page, or of a part of one page, as the search hands them to the UI thread
/// (<see cref="SearchViewModel"/>).
/// </summary>
/// <param name="PageIndex">The 0-based page.</param>
/// <param name="Hits">The matches, in text order.</param>
/// <param name="IsPageStart">Whether this is the first part of the page (each searched page has exactly one).</param>
internal readonly record struct SearchBatchItem(int PageIndex, IReadOnlyList<SearchHit> Hits, bool IsPageStart);

/// <summary>
/// One search of a <see cref="SearchViewModel"/>: runs on pool threads and hands its results to the UI thread in jobs of
/// about <see cref="HitsPerJob"/> matches, so no single job blocks the UI thread for long however many matches come in.
/// A new job is posted at most once per <see cref="BatchInterval"/>, except that the rest of a large batch follows in the
/// next job right away (posted below input and rendering). Everything but <see cref="Flush"/> runs on pool threads.
/// </summary>
internal sealed class SearchRun
{
    /// <summary>The shortest time between two batches of results posted to the UI thread.</summary>
    internal static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>About how many matches one job on the UI thread inserts (a job takes whole parts of at most this size).</summary>
    internal const int HitsPerJob = 250;

    /// <summary>How many pages from the start are checked for any text when a search found nothing (a scanned document).</summary>
    private const int TextCheckPages = 3;

    private readonly object _gate = new();
    private readonly Action<SearchRun, List<SearchBatchItem>, (bool LimitReached, bool NoText)?> _apply;
    private readonly PdfDocument _document;
    private readonly string _query;
    private readonly SearchOptions _options;
    private readonly int _startPage;
    private readonly int _pageCount;
    private readonly PageTextSource? _texts;
    private readonly Func<PdfDocument, string, SearchOptions, int, CancellationToken, IAsyncEnumerable<SearchPageResult>> _searcher;

    // Taken on the UI thread: the source may be disposed there once the run is cancelled; its token stays readable.
    private readonly CancellationToken _token;

    // Guarded by _gate.
    private readonly Queue<SearchBatchItem> _pending = new();
    private (bool LimitReached, bool NoText)? _end;
    private bool _flushScheduled;
    private long _lastFlush;

    /// <param name="apply">Takes a job of results on the UI thread; the end comes with the last one.</param>
    /// <param name="document">The document to search.</param>
    /// <param name="query">The text to search for.</param>
    /// <param name="options">How the text is compared.</param>
    /// <param name="startPage">The page the search starts at.</param>
    /// <param name="pageCount">The number of pages of the document.</param>
    /// <param name="texts">The text of the pages, for the check whether the document has any; <see langword="null"/> skips it.</param>
    /// <param name="searcher">How the document is searched (<see cref="DocumentSearch.SearchAsync"/>).</param>
    public SearchRun(
        Action<SearchRun, List<SearchBatchItem>, (bool LimitReached, bool NoText)?> apply,
        PdfDocument document,
        string query,
        SearchOptions options,
        int startPage,
        int pageCount,
        PageTextSource? texts,
        Func<PdfDocument, string, SearchOptions, int, CancellationToken, IAsyncEnumerable<SearchPageResult>> searcher)
    {
        _apply = apply;
        _document = document;
        _query = query;
        _options = options;
        _startPage = startPage;
        _pageCount = pageCount;
        _texts = texts;
        _searcher = searcher;
        _token = Cancellation.Token;
    }

    /// <summary>Gets the source that cancels the run; cancelled and disposed on the UI thread.</summary>
    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>Searches the document; never throws. A failure ends the search with what it found.</summary>
    public async Task ExecuteAsync()
    {
        try
        {
            var total = 0;
            var limitReached = false;
            await foreach (var result in _searcher(_document, _query, _options, _startPage, _token).ConfigureAwait(false))
            {
                if (_token.IsCancellationRequested)
                {
                    return;
                }

                total += result.Hits.Count;
                limitReached |= result.LimitReached;
                Enqueue(result);
            }

            var noText = total == 0 && !await HasTextAsync().ConfigureAwait(false);
            Finish(limitReached, noText);
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested)
        {
            // Cancelled: a newer search or none replaces it.
        }
        catch (Exception)
        {
            // The document was closed meanwhile or could not be searched: the search ends with what it found.
            if (!_token.IsCancellationRequested)
            {
                Finish(limitReached: false, noText: false);
            }
        }
    }

    /// <summary>
    /// Takes the next job of collected results, about <see cref="HitsPerJob"/> matches, and hands it to the search (UI
    /// thread). What is left follows in another job; the end goes with the last one.
    /// </summary>
    private void Flush()
    {
        var items = new List<SearchBatchItem>();
        (bool LimitReached, bool NoText)? end = null;
        bool more;
        lock (_gate)
        {
            var hits = 0;
            while (_pending.Count > 0 && (items.Count == 0 || hits + _pending.Peek().Hits.Count <= HitsPerJob))
            {
                var item = _pending.Dequeue();
                hits += item.Hits.Count;
                items.Add(item);
            }

            more = _pending.Count > 0;
            if (!more)
            {
                end = _end;
                _end = null;
                _flushScheduled = false;
            }

            _lastFlush = Stopwatch.GetTimestamp();
        }

        _apply(this, items, end);

        // The rest of a large batch: the flag stays set, so nothing else posts meanwhile.
        if (more)
        {
            Post();
        }
    }

    /// <summary>
    /// Whether one of the first <see cref="TextCheckPages"/> pages has any text; a search without matches on a document
    /// without text says so instead of "No matches".
    /// </summary>
    private async Task<bool> HasTextAsync()
    {
        if (_texts is null)
        {
            return true;
        }

        for (var page = 0; page < Math.Min(TextCheckPages, _pageCount); page++)
        {
            try
            {
                var text = await _texts.GetAsync(page, PdfPriority.Background, _token).ConfigureAwait(false);
                if (text.HasText)
                {
                    return true;
                }
            }
            catch (InvalidDataException)
            {
                // A damaged page tells nothing; the next one may.
            }
        }

        return false;
    }

    /// <summary>Queues the result of a page, a page with many matches in parts of at most <see cref="HitsPerJob"/>.</summary>
    private void Enqueue(SearchPageResult result)
    {
        lock (_gate)
        {
            var hits = result.Hits;
            if (hits.Count <= HitsPerJob)
            {
                _pending.Enqueue(new SearchBatchItem(result.PageIndex, hits, IsPageStart: true));
            }
            else
            {
                for (var start = 0; start < hits.Count; start += HitsPerJob)
                {
                    var part = new SearchHit[Math.Min(HitsPerJob, hits.Count - start)];
                    for (var i = 0; i < part.Length; i++)
                    {
                        part[i] = hits[start + i];
                    }

                    _pending.Enqueue(new SearchBatchItem(result.PageIndex, part, IsPageStart: start == 0));
                }
            }
        }

        ScheduleFlush();
    }

    private void Finish(bool limitReached, bool noText)
    {
        lock (_gate)
        {
            _end = (limitReached, noText);
        }

        ScheduleFlush();
    }

    /// <summary>Posts one flush unless one is on its way; at most one per <see cref="BatchInterval"/>.</summary>
    private void ScheduleFlush()
    {
        TimeSpan wait;
        lock (_gate)
        {
            if (_flushScheduled)
            {
                return;
            }

            _flushScheduled = true;
            wait = _lastFlush == 0 ? TimeSpan.Zero : BatchInterval - Stopwatch.GetElapsedTime(_lastFlush);
        }

        if (wait <= TimeSpan.Zero)
        {
            Post();
        }
        else
        {
            _ = PostLaterAsync(wait);
        }
    }

    private async Task PostLaterAsync(TimeSpan wait)
    {
        await Task.Delay(wait).ConfigureAwait(false);
        Post();
    }

    // Any thread: must never throw.
    private void Post()
    {
        try
        {
            // Below input and rendering: typing and scrolling go first.
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nobody shows the results any more.
        }
    }
}
