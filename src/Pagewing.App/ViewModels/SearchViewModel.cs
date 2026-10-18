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
/// small jobs (<see cref="SearchRun"/>), so typing and scrolling stay smooth while thousands of matches come in. Created
/// by the tab when it is first needed (<see cref="TabViewModel.Search"/>), never in the startup path; the tab disposes it.
/// UI thread only.
/// </summary>
public sealed class SearchViewModel : ViewModelBase, IDisposable
{
    /// <summary>How long a new query waits before it searches, so typing a word does not start a search per letter.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromMilliseconds(200);

    // The parsed summary formats, by their text: a change of the language brings other texts, which are parsed then.
    private static readonly Dictionary<string, CompositeFormat> Formats = new(StringComparer.Ordinal);

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
    /// search ended. The document view redraws its marks.
    /// </summary>
    public event Action? HighlightsChanged;

    /// <summary>Raised on the UI thread for every page the running search has gone through, in the order it searched them (for tests).</summary>
    internal event Action<int>? PageSearched;

    /// <summary>Raised on the UI thread after each job that applied results, with its number of matches and how long it took (for tests).</summary>
    internal event Action<int, TimeSpan>? BatchApplied;

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
    /// The tab has a document now that it did not have before (the first one, or the one after a password): a query typed
    /// while it loaded is searched now.
    /// </summary>
    internal void OnDocumentReady()
    {
        if (!_disposed && _query.Length > 0 && _run is null)
        {
            Restart();
        }
    }

    /// <summary>
    /// Takes a job of results of <paramref name="run"/> (UI thread): inserts the matches in order, makes the first one
    /// the current match unless the user chose one, and ends the search with the last job. A job of a run that was
    /// cancelled meanwhile is dropped.
    /// </summary>
    private void Apply(SearchRun run, List<SearchBatchItem> items, (bool LimitReached, bool NoText)? end)
    {
        if (_disposed || !ReferenceEquals(run, _run))
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        ApplyCore(run, items, end);
        if (BatchApplied is { } handler)
        {
            var hits = 0;
            foreach (var item in items)
            {
                hits += item.Hits.Count;
            }

            handler(hits, Stopwatch.GetElapsedTime(started));
        }
    }

    private void ApplyCore(SearchRun run, List<SearchBatchItem> items, (bool LimitReached, bool NoText)? end)
    {
        var hadHits = _hits.Count > 0;
        SearchHitViewModel? first = null;
        foreach (var item in items)
        {
            if (item.IsPageStart)
            {
                PageSearched?.Invoke(item.PageIndex);
            }

            if (item.Hits.Count == 0)
            {
                continue;
            }

            // The pages come in search order (from the start page to the end, then from the first page), each once, a
            // page with many matches in parts in text order.
            if (item.IsPageStart)
            {
                _pagesWithHits++;
            }

            var index = LowerBound(item.PageIndex, item.Hits[0].FirstChar);
            foreach (var hit in item.Hits)
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
            // Grouped as the language writes numbers: "10,000" in English, "10.000" in German.
            return string.Format(Strings.Culture, FormatOf(Strings.SearchLimit), DocumentSearch.MaxHits.ToString("N0", Strings.Culture));
        }

        var count = _hits.Count;
        if (count == 0)
        {
            return _noText ? Strings.SearchNoText : Strings.SearchNone;
        }

        var page = _hits[0].Hit.PageIndex + 1;
        if (count == 1)
        {
            return string.Format(Strings.Culture, FormatOf(Strings.SearchSummaryOne), page);
        }

        return _pagesWithHits == 1
            ? string.Format(Strings.Culture, FormatOf(Strings.SearchSummaryOnePage), count, page)
            : string.Format(Strings.Culture, FormatOf(Strings.SearchSummaryPages), count, _pagesWithHits);
    }

    /// <summary>The parsed format of a summary text, parsed once per text (UI thread only).</summary>
    private static CompositeFormat FormatOf(string text)
    {
        if (!Formats.TryGetValue(text, out var format))
        {
            format = CompositeFormat.Parse(text);
            Formats[text] = format;
        }

        return format;
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
            // No document yet (it loads, or waits for its password): the query stays and is searched once the document is
            // there (OnDocumentReady).
            State = SearchState.Idle;
            return;
        }

        var startPage = pageCount == 0 ? 0 : Math.Clamp(_tab.CurrentPage - 1, 0, pageCount - 1);
        var run = new SearchRun(Apply, document, _query, new SearchOptions(_matchCase, _wholeWord), startPage, pageCount, _tab.Session.EnsurePageTexts(), Searcher);
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
            // No current match yet: forwards the first one on or after the current page, backwards the last one on or
            // before it; wrapping round when there is none.
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
}
