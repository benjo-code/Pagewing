using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Redaction;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>How far the search of the panel "Redact" is.</summary>
public enum RedactionSearchState
{
    /// <summary>No search runs and no result is shown (nothing was searched, the query changed, or the search was stopped).</summary>
    Idle,

    /// <summary>A search runs; its hits come in.</summary>
    Running,

    /// <summary>The search went through the document (or ended at the limit of hits); <see cref="RedactionViewModel.RequiresAcknowledgement"/> tells whether it left something out.</summary>
    Finished,

    /// <summary>The search stopped on a failure (for example the document was closed); the hits found so far are shown and count as incomplete.</summary>
    Failed,
}

/// <summary>
/// The panel "Redact" of one tab (milestone 14, task 12b; design spec 5.6): the marks of the plan listed by page, and "find and
/// redact", the search of the page text, the annotations and the form values with a list of hits that become marks. The search
/// runs on a pool thread through the PDF thread, one page at a time, and hands its hits to the UI thread in small jobs; it ends when
/// the query or an option changes, when it is stopped, when the panel closes or when the mode ends. A search that was stopped or that
/// left something out is never shown as "no hits": the summary says what happened, and marking from an incomplete search waits for the
/// user's word (<see cref="IncompleteAcknowledged"/>), as <c>--allow-unsearched</c> does on the command line. Made by the tab when the
/// panel first opens (<see cref="TabViewModel.RedactionPanel"/>), never in the startup path; the tab disposes it. UI thread only.
/// </summary>
public sealed class RedactionViewModel : ViewModelBase, IDisposable
{
    /// <summary>About how many hits one job on the UI thread inserts.</summary>
    internal const int HitsPerJob = 250;

    /// <summary>The shortest time between two batches of hits posted to the UI thread.</summary>
    internal static readonly TimeSpan BatchInterval = TimeSpan.FromMilliseconds(50);

    private const int PagesListed = 10;
    private const int DiffLimit = 300;

    private readonly TabViewModel _tab;
    private readonly BulkCollection<RedactionHitViewModel> _hits = [];
    private readonly BulkCollection<RedactionMarkRowViewModel> _rows = [];
    private readonly Dictionary<Guid, RedactionMarkRowViewModel> _markRows = [];
    private readonly Dictionary<int, RedactionMarkRowViewModel> _headerRows = [];
    private readonly Dictionary<int, RedactionMarkRowViewModel> _rasterRows = [];
    private readonly RelayCommand _searchCommand;
    private readonly RelayCommand _stopCommand;
    private readonly RelayCommand _markAllCommand;
    private readonly RelayCommand _markCheckedCommand;
    private readonly RelayCommand _checkAllCommand;
    private readonly RelayCommand _uncheckAllCommand;
    private readonly RelayCommand _toggleNotSearchedCommand;

    private string _query = string.Empty;
    private bool _matchCase;
    private bool _wholeWord;
    private RedactionSearchState _state;
    private FindRun? _run;
    private string _summary = string.Empty;
    private string? _incompleteNote;
    private string? _limitNote;
    private string? _markedNote;
    private bool _requiresAcknowledgement;
    private bool _acknowledged;
    private bool _hitsConsumed;
    private bool _showNotSearched;
    private bool _bulkChecking;
    private bool _disposed;

    // The query and the options the hits on show belong to: a change of either ends the search, so they cannot differ from the fields.
    private string _hitQuery = string.Empty;
    private SearchOptions _hitOptions;

    internal RedactionViewModel(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
        Hits = _hits;
        MarkRows = _rows;
        _searchCommand = new RelayCommand(StartSearch, () => CanSearch);
        _stopCommand = new RelayCommand(Stop, () => _state == RedactionSearchState.Running);
        _markAllCommand = new RelayCommand(MarkAll, () => CanMark && _hits.Any(static hit => hit.CanCheck));
        _markCheckedCommand = new RelayCommand(MarkChecked, () => CanMark && CheckedCount > 0);
        _checkAllCommand = new RelayCommand(() => SetAllChecked(true), () => _hits.Any(static hit => hit.CanCheck));
        _uncheckAllCommand = new RelayCommand(() => SetAllChecked(false), () => CheckedCount > 0);
        _toggleNotSearchedCommand = new RelayCommand(() => ShowNotSearched = !ShowNotSearched);
        _tab.PropertyChanged += OnTabPropertyChanged;
        RebuildMarkRows();
    }

    /// <summary>
    /// Gets or sets how the document is searched: <see cref="RedactionSearch.FindAsync(PdfDocument, string, SearchOptions, RedactionSearchReport?, PageTextSource?, CancellationToken)"/>
    /// (replaceable for tests). It runs on a pool thread and fills the report while it enumerates.
    /// </summary>
    internal Func<PdfDocument, string, SearchOptions, RedactionSearchReport, PageTextSource?, CancellationToken, IAsyncEnumerable<RedactionHit>> Searcher { get; set; } =
        static (document, term, options, report, texts, cancellationToken) => RedactionSearch.FindAsync(document, term, options, report, texts, cancellationToken);

    /// <summary>Gets or sets the text to find and redact. A change ends a search and empties its hits: they belong to the old text.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value ?? string.Empty))
            {
                ResetSearch();
                _searchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets or sets whether upper and lower case must match; a change ends a search like a change of the text.</summary>
    public bool MatchCase
    {
        get => _matchCase;
        set
        {
            if (SetProperty(ref _matchCase, value))
            {
                ResetSearch();
            }
        }
    }

    /// <summary>Gets or sets whether a hit must be a whole word. Marks of a whole-word search are never "everywhere" (the check counts substrings).</summary>
    public bool WholeWord
    {
        get => _wholeWord;
        set
        {
            if (SetProperty(ref _wholeWord, value))
            {
                ResetSearch();
            }
        }
    }

    /// <summary>Gets how far the search is.</summary>
    public RedactionSearchState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(HasResults));
                OnPropertyChanged(nameof(NoHitsVisible));
                RaiseMarkStateChanged();
                _stopCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether a search runs.</summary>
    public bool IsRunning => _state == RedactionSearchState.Running;

    /// <summary>Gets whether the area of the hits is shown: a search ran, runs or ended.</summary>
    public bool HasResults => _state != RedactionSearchState.Idle || _summary.Length > 0;

    /// <summary>Gets the hits found so far, in page order (page text, then comments, then form fields within a page).</summary>
    public ObservableCollection<RedactionHitViewModel> Hits { get; }

    /// <summary>Gets the rows of the list of marks: the header of each page that has marks, and the marks of that page under it.</summary>
    public ObservableCollection<RedactionMarkRowViewModel> MarkRows { get; }

    /// <summary>
    /// Gets what to say about the search in one line: the number of hits, that it runs, was stopped or failed, or "No hits" when a
    /// search went through everything and found none. Empty before a search.
    /// </summary>
    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(HasResults));
            }
        }
    }

    /// <summary>
    /// Gets whether the line "No hits." (or "No hits on the pages that were searched.") is on show: a search ended and its list is
    /// empty because nothing was found, not because it was stopped, failed or its hits were marked.
    /// </summary>
    public bool NoHitsVisible => _state == RedactionSearchState.Finished && _hits.Count == 0 && !_hitsConsumed;

    /// <summary>Gets the note that names the pages that were not searched completely, or <see langword="null"/>.</summary>
    public string? IncompleteNote
    {
        get => _incompleteNote;
        private set => SetProperty(ref _incompleteNote, value);
    }

    /// <summary>Gets the note that the search stopped at its limit of hits, or <see langword="null"/>.</summary>
    public string? LimitNote
    {
        get => _limitNote;
        private set => SetProperty(ref _limitNote, value);
    }

    /// <summary>Gets what the last marking did ("Marked 4 places."), or <see langword="null"/>; ends with the next search.</summary>
    public string? MarkedNote
    {
        get => _markedNote;
        private set
        {
            if (SetProperty(ref _markedNote, value))
            {
                OnPropertyChanged(nameof(HasMarkedNote));
            }
        }
    }

    /// <summary>Gets whether <see cref="MarkedNote"/> is on show.</summary>
    public bool HasMarkedNote => _markedNote is not null;

    /// <summary>
    /// Gets whether the search left something out (pages not searched completely, the limit of hits, or a failure) so that marking
    /// waits for <see cref="IncompleteAcknowledged"/>.
    /// </summary>
    public bool RequiresAcknowledgement
    {
        get => _requiresAcknowledgement;
        private set => SetProperty(ref _requiresAcknowledgement, value);
    }

    /// <summary>
    /// Gets or sets whether the user accepted that the search of this query was incomplete (like <c>--allow-unsearched</c>). It belongs to
    /// one search: the next one asks again.
    /// </summary>
    public bool IncompleteAcknowledged
    {
        get => _acknowledged;
        set
        {
            if (SetProperty(ref _acknowledged, value))
            {
                RaiseMarkStateChanged();
            }
        }
    }

    /// <summary>Gets whether the places the search never looks at are listed (<see cref="NotSearchedLines"/>).</summary>
    public bool ShowNotSearched
    {
        get => _showNotSearched;
        private set => SetProperty(ref _showNotSearched, value);
    }

    /// <summary>Gets whether hits may be marked now: a search ended, the user accepted what it left out, and the plan takes changes.</summary>
    public bool CanMark =>
        !_disposed && (_state is RedactionSearchState.Finished or RedactionSearchState.Failed)
        && (!_requiresAcknowledgement || _acknowledged) && _tab.CanMarkOnPage;

    /// <summary>Gets how many hits are checked (they can all be marked).</summary>
    public int CheckedCount => _hits.Count(static hit => hit.IsChecked);

    /// <summary>Gets whether a search can start: the document shows a sharp page, and the text is not empty or only white space.</summary>
    public bool CanSearch =>
        !_disposed && _tab.IsRedacting && _tab.HasShownSharpPage && _tab.Document is not null
        && !string.IsNullOrWhiteSpace(_query) && _query.Length <= RedactionLimits.MaxTermLength;

    /// <summary>Gets the counter of the marks of the plan, "4 marks on 2 pages".</summary>
    public string MarksSummary => _tab.RedactionCounterText;

    /// <summary>Gets whether the plan has no mark yet (the list says how to make one).</summary>
    public bool HasNoMarks => _tab.RedactionPlan is not { IsEmpty: false };

    /// <summary>Starts the search (the button "Find", Enter in the field).</summary>
    public ICommand SearchCommand => _searchCommand;

    /// <summary>Stops the running search and drops what it found: a stopped search shows no result.</summary>
    public ICommand StopCommand => _stopCommand;

    /// <summary>Marks every hit that can become a mark, each text to be gone from the whole document (not with whole words).</summary>
    public ICommand MarkAllCommand => _markAllCommand;

    /// <summary>Marks the checked hits, not "everywhere".</summary>
    public ICommand MarkCheckedCommand => _markCheckedCommand;

    /// <summary>Checks every hit that can be marked.</summary>
    public ICommand CheckAllCommand => _checkAllCommand;

    /// <summary>Removes the check of every hit.</summary>
    public ICommand UncheckAllCommand => _uncheckAllCommand;

    /// <summary>Shows or hides the list of the places the search never looks at.</summary>
    public ICommand ToggleNotSearchedCommand => _toggleNotSearchedCommand;

    /// <summary>Removes the selected marks from the plan (the command of the tab).</summary>
    public ICommand DeleteMarksCommand => _tab.DeleteMarksCommand;

    /// <summary>Turns the page of the selected marks, else the page in view, into an image, or back (the command of the tab).</summary>
    public ICommand ToggleRasterPageCommand => _tab.ToggleRasterPageCommand;

    /// <summary>Gets whether the pages the button "Page as image" concerns all become images (its state).</summary>
    public bool RasterTargetsAreImages => _tab.RasterTargetsAreImages;

    /// <summary>Gets whether the plan turns a page into an image (the choice of the resolution shows then).</summary>
    public bool HasRasterPages => _tab.HasRasterPages;

    /// <summary>Gets or sets whether the pages that become images get 150 dpi (a step of the plan).</summary>
    public bool IsRasterDpi150
    {
        get => _tab.IsRasterDpi150;
        set => _tab.IsRasterDpi150 = value;
    }

    /// <summary>Gets or sets whether the pages that become images get 200 dpi.</summary>
    public bool IsRasterDpi200
    {
        get => _tab.IsRasterDpi200;
        set => _tab.IsRasterDpi200 = value;
    }

    /// <summary>Gets or sets whether the pages that become images get 300 dpi.</summary>
    public bool IsRasterDpi300
    {
        get => _tab.IsRasterDpi300;
        set => _tab.IsRasterDpi300 = value;
    }

    /// <summary>
    /// A click, Enter or Space on a row of the list of marks: selects the mark and shows its place on the page (the view scrolls to it
    /// unless it is in view, and its page becomes the current one). The header of a page only shows the page.
    /// </summary>
    /// <param name="row">The row.</param>
    public void ChooseMark(RedactionMarkRowViewModel row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_disposed)
        {
            return;
        }

        if (row.Mark is not { } mark)
        {
            _tab.GoToPage(row.Page + 1);
            return;
        }

        // The mark of the plan as it is now, by id: the row may hold an older state.
        var current = _tab.RedactionPlan?.Marks.FirstOrDefault(candidate => candidate.Id == mark.Id);
        if (current is null)
        {
            return;
        }

        _tab.SelectMarks([current.Id]);
        _tab.RevealArea(current.Page, Union(current.Areas), fromPanel: true);
    }

    /// <summary>A click or Enter on a row of the list of hits: shows the hit on its page; the check stays as it is.</summary>
    /// <param name="hit">The hit.</param>
    public void ChooseHit(RedactionHitViewModel hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        if (!_disposed)
        {
            _tab.RevealArea(hit.Hit.Page, Union(hit.Hit.Areas), fromPanel: true);
        }
    }

    /// <summary>The panel is not the shown one any more: a running search stops and shows nothing (nobody would look at it).</summary>
    internal void OnPanelHidden()
    {
        if (_state == RedactionSearchState.Running)
        {
            Stop();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tab.PropertyChanged -= OnTabPropertyChanged;

        // A search that still runs stops, and what it found is not kept.
        CancelRun();
        ClearHits();
        State = RedactionSearchState.Idle;
    }

    private static PageRect? Union(IReadOnlyList<PageRect> areas)
    {
        if (areas.Count == 0)
        {
            return null;
        }

        var union = areas[0];
        for (var i = 1; i < areas.Count; i++)
        {
            union = union.Union(areas[i]);
        }

        return union;
    }

    // The search -------------------------------------------------------------------------------------------------------------------

    private void StartSearch()
    {
        if (!CanSearch || _tab.Document is not { } document)
        {
            return;
        }

        CancelRun();
        ClearHits();
        ResetNotes();
        _hitQuery = _query;
        _hitOptions = new SearchOptions(_matchCase, _wholeWord);
        var run = new FindRun(document, _hitQuery, _hitOptions, _tab.PageTexts);
        _run = run;
        State = RedactionSearchState.Running;
        UpdateSummary();
        _ = RunAsync(run);
    }

    private void Stop()
    {
        CancelRun();
        ClearHits();
        ResetNotes();
        State = RedactionSearchState.Idle;
        Summary = Strings.RedactStopped;
    }

    // A change of the query or of an option: what was found belongs to the old text.
    private void ResetSearch()
    {
        CancelRun();
        ClearHits();
        ResetNotes();
        State = RedactionSearchState.Idle;
        Summary = string.Empty;
    }

    private void CancelRun()
    {
        var run = _run;
        _run = null;
        run?.Cancel();
    }

    private void ClearHits()
    {
        foreach (var hit in _hits)
        {
            hit.CheckedChanged -= OnHitCheckedChanged;
        }

        _hits.ResetTo([]);
        _hitsConsumed = false;
        OnHitsChanged();
    }

    private void ResetNotes()
    {
        IncompleteNote = null;
        LimitNote = null;
        MarkedNote = null;
        RequiresAcknowledgement = false;
        IncompleteAcknowledged = false;
        _hitsConsumed = false;
    }

    private async Task RunAsync(FindRun run)
    {
        try
        {
            await Task.Run(
                async () =>
                {
                    var batch = new List<RedactionHit>();
                    var last = Stopwatch.GetTimestamp();
                    var hits = Searcher(run.Document, run.Query, run.Options, run.Report, run.Texts, run.Token);
                    await foreach (var hit in hits.WithCancellation(run.Token).ConfigureAwait(false))
                    {
                        batch.Add(hit);
                        if (batch.Count >= HitsPerJob || Stopwatch.GetElapsedTime(last) >= BatchInterval)
                        {
                            Post(run, batch, null);
                            batch = [];
                            last = Stopwatch.GetTimestamp();
                        }
                    }

                    // An enumeration that ends because it was stopped is not a search that went through the document.
                    run.Token.ThrowIfCancellationRequested();
                    Post(run, batch, new SearchEnd([.. run.Report.IncompletePages], run.Report.LimitReached, Failed: false));
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (run.Token.IsCancellationRequested)
        {
            // Stopped: a newer search or none replaces it.
        }
        catch (Exception)
        {
            // The document was closed meanwhile or could not be searched: the search ends with what it found, as incomplete.
            if (!run.Token.IsCancellationRequested)
            {
                Post(run, [], new SearchEnd([], LimitReached: false, Failed: true));
            }
        }
        finally
        {
            run.Dispose();
        }
    }

    // Any thread: must never throw.
    private void Post(FindRun run, List<RedactionHit> hits, SearchEnd? end)
    {
        try
        {
            // Below input and rendering: typing and scrolling go first.
            Dispatcher.UIThread.Post(() => Apply(run, hits, end), DispatcherPriority.Background);
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nobody shows the results any more.
        }
    }

    private void Apply(FindRun run, List<RedactionHit> hits, SearchEnd? end)
    {
        // The results of a run that was stopped, replaced or disposed change nothing.
        if (_disposed || !ReferenceEquals(_run, run) || run.IsCancelled)
        {
            return;
        }

        foreach (var hit in hits)
        {
            var row = new RedactionHitViewModel(hit);
            row.CheckedChanged += OnHitCheckedChanged;
            _hits.Add(row);
        }

        if (end is null)
        {
            if (hits.Count > 0)
            {
                OnHitsChanged();
                UpdateSummary();
            }

            return;
        }

        FinishRun(end);
    }

    private void FinishRun(SearchEnd end)
    {
        if (end.IncompletePages.Count > 0)
        {
            IncompleteNote = Strings.RedactIncompletePagesOf(PagesText(end.IncompletePages));
        }

        if (end.LimitReached)
        {
            LimitNote = Strings.RedactLimitReachedOf(RedactionSearch.MaxHits);
        }

        RequiresAcknowledgement = end.Failed || end.IncompletePages.Count > 0 || end.LimitReached;
        State = end.Failed ? RedactionSearchState.Failed : RedactionSearchState.Finished;
        OnHitsChanged();
        UpdateSummary();
    }

    private static string PagesText(IReadOnlyList<int> pages)
    {
        var listed = string.Join(", ", pages.Take(PagesListed).Select(static page => (page + 1).ToString(CultureInfo.InvariantCulture)));
        return pages.Count > PagesListed
            ? Strings.RedactMorePagesOf(listed, pages.Count - PagesListed)
            : listed;
    }

    private void UpdateSummary()
    {
        Summary = _state switch
        {
            RedactionSearchState.Running => Strings.RedactSearching(_hits.Count),
            RedactionSearchState.Failed => Strings.RedactFailed,
            RedactionSearchState.Finished when _hitsConsumed && _hits.Count == 0 => _markedNote ?? string.Empty,
            RedactionSearchState.Finished when _hits.Count == 0 => RequiresAcknowledgement ? Strings.RedactNoHitsSearched : Strings.RedactNoHits,
            RedactionSearchState.Finished => Strings.RedactHitsCount(_hits.Count),
            _ => Summary,
        };
    }

    // Marking ----------------------------------------------------------------------------------------------------------------------

    private void MarkAll() => MarkHits([.. _hits.Where(static hit => hit.CanCheck)], everywhere: true);

    private void MarkChecked() => MarkHits([.. _hits.Where(static hit => hit.IsChecked)], everywhere: false);

    private void MarkHits(List<RedactionHitViewModel> chosen, bool everywhere)
    {
        if (!CanMark || chosen.Count == 0)
        {
            return;
        }

        var marks = RedactionSearch.ToMarks(chosen.Select(static hit => hit.Hit), _hitQuery, everywhere, _hitOptions, out _);
        if (marks.Count == 0 || !_tab.AddRedactionMarks(marks))
        {
            // The plan refused (it is full): the hits stay, and the tab's notice says why.
            return;
        }

        var used = new HashSet<RedactionHitViewModel>(chosen);
        foreach (var hit in used)
        {
            hit.CheckedChanged -= OnHitCheckedChanged;
        }

        _hits.ResetTo([.. _hits.Where(hit => !used.Contains(hit))]);
        _hitsConsumed = true;
        var note = Strings.RedactMarkedCount(marks.Count);

        // "Mark all" leaves the hits that cannot become a mark in the list, each with its reason; the note counts them.
        var notMarkable = everywhere ? _hits.Count(static hit => !hit.CanCheck) : 0;
        MarkedNote = notMarkable > 0 ? $"{note} {Strings.RedactSkippedCount(notMarkable)}" : note;
        OnHitsChanged();
        UpdateSummary();
    }

    private void SetAllChecked(bool value)
    {
        _bulkChecking = true;
        try
        {
            foreach (var hit in _hits)
            {
                hit.IsChecked = value;
            }
        }
        finally
        {
            _bulkChecking = false;
        }

        RaiseMarkStateChanged();
    }

    private void OnHitCheckedChanged(RedactionHitViewModel hit)
    {
        if (!_bulkChecking)
        {
            RaiseMarkStateChanged();
        }
    }

    private void OnHitsChanged()
    {
        OnPropertyChanged(nameof(NoHitsVisible));
        RaiseMarkStateChanged();
    }

    private void RaiseMarkStateChanged()
    {
        OnPropertyChanged(nameof(CanMark));
        OnPropertyChanged(nameof(CheckedCount));
        _markAllCommand.RaiseCanExecuteChanged();
        _markCheckedCommand.RaiseCanExecuteChanged();
        _checkAllCommand.RaiseCanExecuteChanged();
        _uncheckAllCommand.RaiseCanExecuteChanged();
    }

    // The list of marks ------------------------------------------------------------------------------------------------------------

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.RedactionPlan):
                RebuildMarkRows();
                OnPropertyChanged(nameof(MarksSummary));
                OnPropertyChanged(nameof(HasNoMarks));
                RaiseMarkStateChanged();
                break;
            case nameof(TabViewModel.SelectedMarks):
                SyncSelection();
                break;
            case nameof(TabViewModel.RasterTargetsAreImages):
                OnPropertyChanged(nameof(RasterTargetsAreImages));
                break;
            case nameof(TabViewModel.HasRasterPages):
                OnPropertyChanged(nameof(HasRasterPages));
                break;
            case nameof(TabViewModel.IsRasterDpi150):
                OnPropertyChanged(nameof(IsRasterDpi150));
                break;
            case nameof(TabViewModel.IsRasterDpi200):
                OnPropertyChanged(nameof(IsRasterDpi200));
                break;
            case nameof(TabViewModel.IsRasterDpi300):
                OnPropertyChanged(nameof(IsRasterDpi300));
                break;
            case nameof(TabViewModel.IsRedacting) or nameof(TabViewModel.HasShownSharpPage) or nameof(TabViewModel.Document):
                _searchCommand.RaiseCanExecuteChanged();
                RaiseMarkStateChanged();
                break;
        }
    }

    // A row holds the mark it shows; a mark that was changed (moved, resized, undone) is another object of the plan, so its row is made
    // again and never shows or jumps to an old rectangle.
    // Brings the rows in line with the plan: the marks grouped by page, the pages ascending, the marks of a page in the order they were
    // made. Rows are kept for the marks that stay (their position in the list and its scroll offset stay with them), so a delete or an
    // added mark touches a few rows; many changes at once are a reset.
    private void RebuildMarkRows()
    {
        var marks = _tab.RedactionPlan?.Marks ?? [];
        var byPage = new SortedDictionary<int, List<RedactionMark>>();
        foreach (var mark in marks)
        {
            if (!byPage.TryGetValue(mark.Page, out var list))
            {
                byPage[mark.Page] = list = [];
            }

            list.Add(mark);
        }

        // A page that becomes an image is listed too, even without a mark: its row says so and with which resolution.
        var plan = _tab.RedactionPlan;
        var rasterPages = plan?.RasterPages ?? (IReadOnlySet<int>)new HashSet<int>();
        var pages = new SortedSet<int>(byPage.Keys);
        pages.UnionWith(rasterPages);
        var wanted = new List<RedactionMarkRowViewModel>(marks.Count + pages.Count + rasterPages.Count);
        var markRows = new Dictionary<Guid, RedactionMarkRowViewModel>(marks.Count);
        var headerRows = new Dictionary<int, RedactionMarkRowViewModel>(pages.Count);
        var rasterRows = new Dictionary<int, RedactionMarkRowViewModel>(rasterPages.Count);
        foreach (var page in pages)
        {
            if (!_headerRows.TryGetValue(page, out var header))
            {
                header = RedactionMarkRowViewModel.Header(page);
            }

            headerRows[page] = header;
            wanted.Add(header);
            if (byPage.TryGetValue(page, out var list))
            {
                foreach (var mark in list)
                {
                    if (!_markRows.TryGetValue(mark.Id, out var row) || !ReferenceEquals(row.Mark, mark))
                    {
                        row = RedactionMarkRowViewModel.Of(mark);
                    }

                    markRows[mark.Id] = row;
                    wanted.Add(row);
                }
            }

            if (rasterPages.Contains(page) && plan is not null)
            {
                if (!_rasterRows.TryGetValue(page, out var raster) || raster.RasterDpi != plan.RasterDpi)
                {
                    raster = RedactionMarkRowViewModel.Raster(page, plan.RasterDpi);
                }

                rasterRows[page] = raster;
                wanted.Add(raster);
            }
        }

        _markRows.Clear();
        foreach (var pair in markRows)
        {
            _markRows[pair.Key] = pair.Value;
        }

        _headerRows.Clear();
        foreach (var pair in headerRows)
        {
            _headerRows[pair.Key] = pair.Value;
        }

        _rasterRows.Clear();
        foreach (var pair in rasterRows)
        {
            _rasterRows[pair.Key] = pair.Value;
        }

        _rows.Apply(wanted, DiffLimit);
        SyncSelection();
    }

    private void SyncSelection()
    {
        var selected = _tab.SelectedMarks;
        foreach (var row in _rows)
        {
            if (row.Mark is { } mark)
            {
                row.IsSelected = selected.Contains(mark.Id);
            }
        }
    }

    // The state of one search; its token ends it. The source is disposed when the run has ended; the token stays readable.
    private sealed class FindRun : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private bool _cancelled;

        public FindRun(PdfDocument document, string query, SearchOptions options, PageTextSource? texts)
        {
            Document = document;
            Query = query;
            Options = options;
            Texts = texts;
            Token = _cancellation.Token;
        }

        public PdfDocument Document { get; }

        public string Query { get; }

        public SearchOptions Options { get; }

        public PageTextSource? Texts { get; }

        public RedactionSearchReport Report { get; } = new();

        public CancellationToken Token { get; }

        public bool IsCancelled => Volatile.Read(ref _cancelled);

        public void Cancel()
        {
            Volatile.Write(ref _cancelled, true);
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run has ended already.
            }
        }

        public void Dispose() => _cancellation.Dispose();
    }

    private sealed record SearchEnd(IReadOnlyList<int> IncompletePages, bool LimitReached, bool Failed);

    /// <summary>An observable list that can be reset or brought in line with another list in few notifications.</summary>
    private sealed class BulkCollection<T> : ObservableCollection<T>
    {
        public void ResetTo(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            Reset();
        }

        /// <summary>
        /// Makes the list equal to <paramref name="wanted"/>, in which the items that stay keep their relative order: removes the others,
        /// inserts the new ones. More than <paramref name="limit"/> changes are one reset.
        /// </summary>
        public void Apply(List<T> wanted, int limit)
        {
            var keep = new HashSet<T>(wanted);
            var removals = 0;
            foreach (var item in Items)
            {
                if (!keep.Contains(item))
                {
                    removals++;
                }
            }

            var present = new HashSet<T>(Items);
            var insertions = wanted.Count(item => !present.Contains(item));
            if (removals + insertions > limit)
            {
                ResetTo(wanted);
                return;
            }

            for (var i = Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(Items[i]))
                {
                    RemoveAt(i);
                }
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                if (i < Count && EqualityComparer<T>.Default.Equals(Items[i], wanted[i]))
                {
                    continue;
                }

                Insert(i, wanted[i]);
            }
        }

        private void Reset()
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }
}
