using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The full-text search of one tab (design system 7.4): the query and its options, the matches found so far, the current
/// match and the summary. A new query or option cancels the running search, empties the list and starts again after
/// <see cref="StartDelay"/>, from the current page of the tab. The search itself runs on pool threads
/// (<see cref="DocumentSearch.SearchAsync"/>, which goes through the PDFium thread); its results reach the UI thread in
/// batches, at most one posted job per <see cref="BatchInterval"/>, so typing and scrolling stay smooth while thousands of
/// matches come in. Created by the tab when it is first needed (<see cref="TabViewModel.Search"/>), never in the startup
/// path; the tab disposes it. UI thread only, except where noted.
/// </summary>
public sealed class SearchViewModel : ViewModelBase, IDisposable
{
    /// <summary>How long a new query waits before it searches, so typing a word does not start a search per letter.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>The shortest time between two batches of results posted to the UI thread.</summary>
    internal static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>How many pages from the start are checked for any text when a search found nothing (a scanned document).</summary>
    private const int TextCheckPages = 3;

    private readonly TabViewModel _tab;
    private readonly ObservableCollection<SearchHitViewModel> _hits = [];
    private readonly RelayCommand _nextCommand;
    private readonly RelayCommand _previousCommand;
    private string _query = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private SearchState _state;
    private SearchHitViewModel? _current;
    private int _currentIndex = -1;
    private DispatcherTimer? _startTimer;
    private SearchRun? _run;

    // What the summary of a finished search says besides the number of matches.
    private int _pagesWithHits;
    private bool _limitReached;
    private bool _noText;

    // The user chose a match of this search (Enter, F3, a click): the first match no longer becomes the current one by itself.
    private bool _userChose;

    // The first match of this search became the current one and was revealed; that happens once per search.
    private bool _revealed;
    private IReadOnlyList<(int Page, PageRect Rect, bool IsCurrent)>? _highlights;
    private bool _disposed;

    /// <summary>Creates the search of <paramref name="tab"/>, which owns it.</summary>
    internal SearchViewModel(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
        Hits = new ReadOnlyObservableCollection<SearchHitViewModel>(_hits);
        _nextCommand = new RelayCommand(() => Step(1), () => _hits.Count > 0);
        NextCommand = _nextCommand;
        _previousCommand = new RelayCommand(() => Step(-1), () => _hits.Count > 0);
        PreviousCommand = _previousCommand;
        EndCommand = new RelayCommand(End);
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="Highlights"/> changed: matches came in, the current match changed or the
    /// search ended. The document view redraws its marks (Task 13).
    /// </summary>
    public event Action? HighlightsChanged;

    /// <summary>Raised on the UI thread for every page the running search has gone through, in the order it searched them (for tests).</summary>
    internal event Action<int>? PageSearched;

    /// <summary>
    /// Gets or sets the text to search for. A change cancels the running search, empties the list and searches again
    /// after <see cref="StartDelay"/>; an empty query ends the search.
    /// </summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                Restart();
            }
        }
    }

    /// <summary>Gets or sets whether upper and lower case must match; a change searches again.</summary>
    public bool MatchCase
    {
        get => _matchCase;
        set
        {
            if (SetProperty(ref _matchCase, value))
            {
                Restart();
            }
        }
    }

    /// <summary>Gets or sets whether a match must be a whole word; a change searches again.</summary>
    public bool WholeWord
    {
        get => _wholeWord;
        set
        {
            if (SetProperty(ref _wholeWord, value))
            {
                Restart();
            }
        }
    }

    /// <summary>Gets how far the search is.</summary>
    public SearchState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsActive));
            }

            // Also when the state stays: the counts behind the summary may have changed.
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>Gets the matches found so far, sorted by page and position on the page; filled while the search runs.</summary>
    public ReadOnlyObservableCollection<SearchHitViewModel> Hits { get; }

    /// <summary>Gets the position of <see cref="Current"/> in <see cref="Hits"/>, or -1 without a current match.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Gets the current match (on the page marked with <c>search.current</c>), or <see langword="null"/>.</summary>
    public SearchHitViewModel? Current => _current;

    /// <summary>
    /// Gets the summary below the options (design system 7.4): "Searching …" while it runs; at the end the number of
    /// matches and pages, "No matches", the hint for a document without text, or that the search stopped at
    /// <see cref="DocumentSearch.MaxHits"/>. Empty without a search.
    /// </summary>
    public string Summary => State switch
    {
        SearchState.Searching => Strings.SearchRunning,
        SearchState.Done => DoneSummary(),
        _ => string.Empty,
    };

    /// <summary>
    /// Gets whether a search runs or has matches to show: the document marks them, and F3 steps through them, also with
    /// the panel closed.
    /// </summary>
    public bool IsActive => State == SearchState.Searching || _hits.Count > 0;

    /// <summary>The next match, after the last one the first (Enter in the field, F3).</summary>
    public ICommand NextCommand { get; }

    /// <summary>The previous match, before the first one the last (Shift+Enter in the field, Shift+F3).</summary>
    public ICommand PreviousCommand { get; }

    /// <summary>Ends the search (Esc in the field): clears the query, the matches and the marks; the tab shows the panel it showed before.</summary>
    public ICommand EndCommand { get; }

    /// <summary>
    /// Gets the marks of all matches for the document view: one entry per rectangle of a match (one per line), in page
    /// points from the top left of the page, with the rectangles of the current match flagged.
    /// </summary>
    public IReadOnlyList<(int Page, PageRect Rect, bool IsCurrent)> Highlights => _highlights ??= BuildHighlights();

    /// <summary>Gets or sets how a search runs: <see cref="DocumentSearch.SearchAsync"/> (replaceable for tests).</summary>
    internal Func<PdfDocument, string, SearchOptions, int, CancellationToken, IAsyncEnumerable<SearchPageResult>> Searcher { get; set; } =
        static (document, query, options, startPage, cancellationToken) => DocumentSearch.SearchAsync(document, query, options, startPage, cancellationToken);

    /// <summary>Makes <paramref name="hit"/> the current match and shows it (a click in the list); a narrow window then closes the panel.</summary>
    public void Choose(SearchHitViewModel hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (IndexOf(hit) >= 0)
        {
            Go(hit, fromPanel: true);
        }
    }

    /// <summary>Cancels the search and clears everything, the query included. May be called more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Reset();
        _disposed = true;
        _startTimer = null;
    }

    /// <summary>
    /// Cancels the search and clears the query, the matches and the marks, without touching the panel: the tab calls it
    /// when its document is replaced or closed.
    /// </summary>
    internal void Reset()
    {
        if (_disposed)
        {
            return;
        }

        _startTimer?.Stop();
        CancelRun();
        SetProperty(ref _query, string.Empty, nameof(Query));
        ClearResults();
        State = SearchState.Idle;
    }

    /// <summary>
    /// Takes a batch of results of <paramref name="run"/> (UI thread): inserts the matches in order, makes the first one
    /// the current match unless the user chose one, and ends the search after the last batch. A batch of a run that was
    /// cancelled meanwhile is dropped.
    /// </summary>
    private void Apply(SearchRun run, List<SearchPageResult> results, (bool LimitReached, bool NoText)? end)
    {
        if (_disposed || !ReferenceEquals(run, _run))
        {
            return;
        }

        var hadHits = _hits.Count > 0;
        SearchHitViewModel? first = null;
        foreach (var result in results)
        {
            PageSearched?.Invoke(result.PageIndex);
            if (result.Hits.Count == 0)
            {
                continue;
            }

            // The pages come in search order (from the start page to the end, then from the first page), each once.
            _pagesWithHits++;
            var index = LowerBound(result.Hits[0].PageIndex, int.MinValue);
            foreach (var hit in result.Hits)
            {
                var entry = new SearchHitViewModel(hit);
                first ??= entry;
                _hits.Insert(index++, entry);
            }
        }

        if (first is not null)
        {
            if (!hadHits)
            {
                _nextCommand.RaiseCanExecuteChanged();
                _previousCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsActive));
            }

            if (_current is null && !_userChose && !_revealed)
            {
                // The first match from the start page on (the order of the search): the one at or after the view.
                _revealed = true;
                SetCurrent(first);
                _tab.RevealSearchHit(first.Hit, fromPanel: false);
            }
            else
            {
                // Matches before the current one moved it down the list.
                UpdateCurrentIndex();
                InvalidateHighlights();
            }
        }

        if (end is { } ending)
        {
            _run = null;
            run.Cancellation.Dispose();
            _limitReached = ending.LimitReached;
            _noText = ending.NoText;
            State = SearchState.Done;
        }
    }

    private string DoneSummary()
    {
        if (_limitReached)
        {
            return string.Format(Strings.Culture, CompositeFormat.Parse(Strings.SearchLimit), DocumentSearch.MaxHits);
        }

        var count = _hits.Count;
        if (count == 0)
        {
            return _noText ? Strings.SearchNoText : Strings.SearchNone;
        }

        var page = _hits[0].Hit.PageIndex + 1;
        if (count == 1)
        {
            return string.Format(Strings.Culture, CompositeFormat.Parse(Strings.SearchSummaryOne), page);
        }

        return _pagesWithHits == 1
            ? string.Format(Strings.Culture, CompositeFormat.Parse(Strings.SearchSummaryOnePage), count, page)
            : string.Format(Strings.Culture, CompositeFormat.Parse(Strings.SearchSummaryPages), count, _pagesWithHits);
    }

    /// <summary>Cancels what runs and starts again after <see cref="StartDelay"/>; an empty query only ends the search.</summary>
    private void Restart()
    {
        if (_disposed)
        {
            return;
        }

        CancelRun();
        ClearResults();
        if (_query.Length == 0)
        {
            _startTimer?.Stop();
            State = SearchState.Idle;
            return;
        }

        State = SearchState.Searching;
        if (_startTimer is null)
        {
            _startTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = StartDelay };
            _startTimer.Tick += OnStartTimer;
        }

        // Restarted: the delay counts from the last change.
        _startTimer.Stop();
        _startTimer.Start();
    }

    private void OnStartTimer(object? sender, EventArgs e)
    {
        _startTimer?.Stop();
        Start();
    }

    /// <summary>Starts the search of <see cref="Query"/> in the open document, from the current page.</summary>
    private void Start()
    {
        if (_disposed || _query.Length == 0)
        {
            return;
        }

        int pageCount;
        try
        {
            pageCount = _tab.Document?.PageCount ?? -1;
        }
        catch (ObjectDisposedException)
        {
            // Replaced or closed meanwhile; the tab resets the search with that change.
            pageCount = -1;
        }

        if (pageCount < 0 || _tab.Document is not { } document)
        {
            State = SearchState.Idle;
            return;
        }

        var startPage = pageCount == 0 ? 0 : Math.Clamp(_tab.CurrentPage - 1, 0, pageCount - 1);
        var run = new SearchRun(this, document, _query, new SearchOptions(_matchCase, _wholeWord), startPage, pageCount, _tab.Session.EnsurePageTexts(), Searcher);
        _run = run;
        _ = Task.Run(run.ExecuteAsync);
    }

    private void End()
    {
        Reset();
        _tab.EndSearchPanel();
    }

    /// <summary>The next (<paramref name="direction"/> 1) or previous (-1) match, wrapping around.</summary>
    private void Step(int direction)
    {
        var count = _hits.Count;
        if (count == 0)
        {
            return;
        }

        int index;
        if (_currentIndex < 0)
        {
            // No current match yet: the first one at or after the current page, or the last one before it.
            var page = Math.Max(_tab.CurrentPage - 1, 0);
            index = direction > 0 ? LowerBound(page, int.MinValue) : LowerBound(page + 1, int.MinValue) - 1;
            index = index >= count ? 0 : index < 0 ? count - 1 : index;
        }
        else
        {
            index = (_currentIndex + direction + count) % count;
        }

        Go(_hits[index], fromPanel: false);
    }

    private void Go(SearchHitViewModel hit, bool fromPanel)
    {
        _userChose = true;
        SetCurrent(hit);
        _tab.RevealSearchHit(hit.Hit, fromPanel);
    }

    private void SetCurrent(SearchHitViewModel? hit)
    {
        if (_current is not null)
        {
            _current.IsCurrent = false;
        }

        _current = hit;
        if (hit is not null)
        {
            hit.IsCurrent = true;
        }

        OnPropertyChanged(nameof(Current));
        UpdateCurrentIndex();
        InvalidateHighlights();
    }

    private void UpdateCurrentIndex()
    {
        var index = _current is null ? -1 : IndexOf(_current);
        if (index != _currentIndex)
        {
            _currentIndex = index;
            OnPropertyChanged(nameof(CurrentIndex));
        }
    }

    /// <summary>Clears the matches, the current match and the counts of the summary.</summary>
    private void ClearResults()
    {
        var hadHits = _hits.Count > 0;
        var hadCurrent = _current is not null;
        if (hadCurrent)
        {
            _current!.IsCurrent = false;
            _current = null;
            OnPropertyChanged(nameof(Current));
        }

        _hits.Clear();
        UpdateCurrentIndex();
        _pagesWithHits = 0;
        _limitReached = false;
        _noText = false;
        _userChose = false;
        _revealed = false;
        if (hadHits)
        {
            _nextCommand.RaiseCanExecuteChanged();
            _previousCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsActive));
        }

        if (hadHits || hadCurrent)
        {
            InvalidateHighlights();
        }
    }

    /// <summary>Cancels the running search; its later results are dropped (<see cref="Apply"/>).</summary>
    private void CancelRun()
    {
        // Cleared before cancelling: whatever the cancellation runs sees that the run is stale.
        var run = _run;
        _run = null;
        if (run is not null)
        {
            run.Cancellation.Cancel();
            run.Cancellation.Dispose();
        }
    }

    private void InvalidateHighlights()
    {
        _highlights = null;
        HighlightsChanged?.Invoke();
    }

    private (int Page, PageRect Rect, bool IsCurrent)[] BuildHighlights()
    {
        var highlights = new List<(int Page, PageRect Rect, bool IsCurrent)>(_hits.Count);
        foreach (var entry in _hits)
        {
            foreach (var rect in entry.Hit.Rects)
            {
                highlights.Add((entry.Hit.PageIndex, rect, entry.IsCurrent));
            }
        }

        return [.. highlights];
    }

    /// <summary>The position of <paramref name="hit"/> in the sorted list, or -1.</summary>
    private int IndexOf(SearchHitViewModel hit)
    {
        var index = LowerBound(hit.Hit.PageIndex, hit.Hit.FirstChar);
        return index < _hits.Count && ReferenceEquals(_hits[index], hit) ? index : -1;
    }

    /// <summary>The first position in the sorted list whose match is not before (<paramref name="page"/>, <paramref name="firstChar"/>).</summary>
    private int LowerBound(int page, int firstChar)
    {
        var low = 0;
        var high = _hits.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var hit = _hits[middle].Hit;
            if (hit.PageIndex < page || (hit.PageIndex == page && hit.FirstChar < firstChar))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>
    /// One search: runs on pool threads and hands its results to the UI thread in batches, at most one posted job per
    /// <see cref="BatchInterval"/>. Everything but <see cref="Flush"/> runs on pool threads.
    /// </summary>
    private sealed class SearchRun
    {
        private readonly object _gate = new();
        private readonly SearchViewModel _owner;
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
        private List<SearchPageResult> _pending = [];
        private (bool LimitReached, bool NoText)? _end;
        private bool _flushScheduled;
        private long _lastFlush;

        public SearchRun(
            SearchViewModel owner,
            PdfDocument document,
            string query,
            SearchOptions options,
            int startPage,
            int pageCount,
            PageTextSource? texts,
            Func<PdfDocument, string, SearchOptions, int, CancellationToken, IAsyncEnumerable<SearchPageResult>> searcher)
        {
            _owner = owner;
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

        /// <summary>Takes the results collected so far and hands them to the search (UI thread).</summary>
        private void Flush()
        {
            List<SearchPageResult> results;
            (bool LimitReached, bool NoText)? end;
            lock (_gate)
            {
                results = _pending;
                _pending = [];
                end = _end;
                _end = null;
                _flushScheduled = false;
                _lastFlush = Stopwatch.GetTimestamp();
            }

            _owner.Apply(this, results, end);
        }

        /// <summary>
        /// Whether one of the first <see cref="TextCheckPages"/> pages has any text; a search without matches on a
        /// document without text says so instead of "No matches".
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

        private void Enqueue(SearchPageResult result)
        {
            lock (_gate)
            {
                _pending.Add(result);
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

        // Pool thread: must never throw.
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
}
