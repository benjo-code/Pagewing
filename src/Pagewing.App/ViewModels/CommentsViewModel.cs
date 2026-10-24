using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Annotations;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The "Comments" panel of one tab (milestone 9): the annotations of the document as a list sorted by page and position,
/// a filter by kind, the summary and the jump to an entry. Read-only (design system 7.7: the reading view shows no editing
/// tools). Created by the tab when the panel first opens (<see cref="TabViewModel.Comments"/>), never in the startup path;
/// it reads nothing before the first sharp page of the document (<see cref="TabViewModel.HasShownSharpPage"/>). Then it
/// reads every page once, one after the other in the background (<see cref="PdfDocument.GetAnnotationsAsync"/> runs with
/// <see cref="PdfPriority.Background"/>), and the list fills page by page. A page whose content the editor changed
/// (<see cref="Pagewing.Core.Viewing.ViewerSession.PagesInvalidated"/>) is read again, only that page; entries that stay as
/// they were stay the same objects, so the list keeps their containers, its scroll position and the current entry. A saved
/// document that the session adopted keeps the list. UI thread only.
/// </summary>
public sealed class CommentsViewModel : ViewModelBase, IDisposable
{
    /// <summary>The longest text under an annotation an entry shows, the ellipsis included.</summary>
    public const int ExcerptLength = 120;

    // The parsed summary formats, by their text: a change of the language brings other texts, which are parsed then.
    private static readonly Dictionary<string, CompositeFormat> Formats = new(StringComparer.Ordinal);

    private readonly TabViewModel _tab;

    // The pages still to read, lowest first; a page changed while it was read is put back (its epoch moved on).
    private readonly SortedSet<int> _pending = [];
    private readonly Dictionary<int, int> _epochs = [];

    // The entries of every page that has some, in the order of the list; Items is the part the filter lets through.
    private readonly SortedDictionary<int, CommentItemViewModel[]> _byPage = [];
    private ObservableCollection<CommentItemViewModel> _items = [];
    private CommentFilter _filter;
    private SettingsChoice<CommentFilter>? _filterChoice;
    private CommentItemViewModel? _current;
    private PdfDocument? _document;
    private CancellationTokenSource? _cancellation;
    private int _pageCount;
    private int _total;
    private bool _reading;
    private bool _loaded;
    private bool _disposed;

    /// <summary>Creates the list of <paramref name="tab"/>, which owns it; it starts reading once the tab has shown a sharp page.</summary>
    internal CommentsViewModel(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _tab = tab;
        JumpCommand = new RelayCommand<CommentItemViewModel>(Jump);
        FilterChoices =
        [
            new(CommentFilter.All, Strings.CommentFilterAll),
            new(CommentFilter.Highlight, Strings.CommentKindHighlight),
            new(CommentFilter.Underline, Strings.CommentKindUnderline),
            new(CommentFilter.StrikeOut, Strings.CommentKindStrikeOut),
            new(CommentFilter.Other, Strings.CommentKindOther),
        ];
        _filterChoice = FilterChoices[0];
        tab.PropertyChanged += OnTabPropertyChanged;
        tab.Session.PagesInvalidated += OnPagesInvalidated;
        TryStart();
    }

    /// <summary>Gets the entries the filter lets through, sorted by page, then from top to bottom and left to right; filled while the pages are read.</summary>
    /// <remarks>A change of <see cref="Filter"/> replaces the collection (and raises <see cref="ViewModelBase.PropertyChanged"/>).</remarks>
    public ObservableCollection<CommentItemViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>Gets or sets which kinds the list shows; <see cref="CommentFilter.All"/> at first.</summary>
    public CommentFilter Filter
    {
        get => _filter;
        set
        {
            if (!Enum.IsDefined(value) || !SetProperty(ref _filter, value))
            {
                return;
            }

            _filterChoice = FilterChoices.FirstOrDefault(choice => choice.Value == value);
            OnPropertyChanged(nameof(SelectedFilterChoice));
            Items = new ObservableCollection<CommentItemViewModel>(_byPage.Values.SelectMany(entries => entries).Where(Matches));
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>Gets the choices of the filter list, in the order of <see cref="CommentFilter"/>.</summary>
    public IReadOnlyList<SettingsChoice<CommentFilter>> FilterChoices { get; }

    /// <summary>Gets or sets the chosen entry of <see cref="FilterChoices"/> (the panel's list).</summary>
    public SettingsChoice<CommentFilter>? SelectedFilterChoice
    {
        get => _filterChoice;
        set
        {
            if (value is not null)
            {
                Filter = value.Value;
            }
        }
    }

    /// <summary>Gets whether the pages are still read for the first time (the summary says "Loading …").</summary>
    public bool IsLoading => !_loaded;

    /// <summary>Gets whether every page is read and the document has no comments at all: the panel shows its hint instead of the list.</summary>
    public bool HasNoComments => _loaded && _total == 0;

    /// <summary>
    /// Gets the summary of the panel (design system 7.4 as the model): "Loading …" until every page is read, then the
    /// number of entries the filter lets through and their pages ("3 comments on 2 pages", "2 comments, all on page 1",
    /// "1 comment on page 2"), or "No comments".
    /// </summary>
    public string Summary
    {
        get
        {
            if (!_loaded)
            {
                return Strings.CommentsLoading;
            }

            var count = _items.Count;
            if (count == 0)
            {
                return Strings.CommentsNone;
            }

            var pages = 0;
            var last = -1;
            foreach (var item in _items)
            {
                if (item.PageIndex != last)
                {
                    pages++;
                    last = item.PageIndex;
                }
            }

            var page = _items[0].PageIndex + 1;
            if (count == 1)
            {
                return string.Format(Strings.Culture, FormatOf(Strings.CommentsSummaryOne), page);
            }

            return pages == 1
                ? string.Format(Strings.Culture, FormatOf(Strings.CommentsSummaryOnePage), count, page)
                : string.Format(Strings.Culture, FormatOf(Strings.CommentsSummaryPages), count, pages);
        }
    }

    /// <summary>
    /// Shows an entry (parameter: the <see cref="CommentItemViewModel"/>; a click, Enter or Space in the list): by the rules
    /// of a search match (design system 7.4), the view scrolls to its area only when that is not fully visible, its page
    /// becomes the current one, and a narrow window closes the panel. The entry becomes the current one.
    /// </summary>
    public ICommand JumpCommand { get; }

    /// <summary>Gets whether the entries can be edited here: never outside the mode "Comment" (milestone 9, task 16).</summary>
    public bool IsEditing { get; }

    /// <summary>Stops reading and lets go of the tab and the session. May be called more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tab.PropertyChanged -= OnTabPropertyChanged;
        _tab.Session.PagesInvalidated -= OnPagesInvalidated;
        CancelReading();
        _pending.Clear();
        _byPage.Clear();
        _items.Clear();
        _current = null;
        _document = null;
    }

    /// <summary>
    /// The session swapped the document for the saved file (the same pages and annotations): the list stays, and what is
    /// still to read (also a page whose read on the closed document fails) is read from <paramref name="adopted"/>.
    /// </summary>
    internal void OnDocumentAdopted(PdfDocument adopted)
    {
        ArgumentNullException.ThrowIfNull(adopted);
        if (!_disposed && _document is not null)
        {
            _document = adopted;
        }
    }

    /// <summary>Reads <paramref name="pages"/> again (their content changed); before the first read of every page it does nothing.</summary>
    internal void Invalidate(IReadOnlySet<int> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);
        if (_disposed || _document is null)
        {
            // Nothing is read yet: the first read reads them anyway.
            return;
        }

        foreach (var page in pages)
        {
            if (page >= 0 && page < _pageCount)
            {
                _epochs[page] = EpochOf(page) + 1;
                _pending.Add(page);
            }
        }

        ReadPending();
    }

    private static bool IsListed(PdfAnnotation annotation) =>

        // Links and form fields are part of the page, not comments; popups and replies never come from the document.
        annotation.Subtype is not ("Link" or "Widget" or "Popup");

    private static bool IsMarkup(PdfAnnotation annotation) =>
        annotation.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut;

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

    /// <summary>
    /// Reads the annotations of a page and, for the markup among them, the text under it (on a pool thread; PDFium runs on
    /// its own thread). The text comes from the tab's cache when it has the page, otherwise straight from the document, so
    /// reading every page does not push the pages of the view out of that cache. Sorted from top to bottom and left to
    /// right, then in the order of the page.
    /// </summary>
    private static async Task<(PdfAnnotation Annotation, string? Excerpt)[]> ReadPageAsync(
        Func<PdfDocument, int, CancellationToken, Task<IReadOnlyList<PdfAnnotation>>> reader,
        PdfDocument document,
        PageTextSource? texts,
        int page,
        CancellationToken cancellationToken)
    {
        var annotations = await reader(document, page, cancellationToken).ConfigureAwait(false);
        var listed = annotations.Where(IsListed).ToArray();
        if (listed.Length == 0)
        {
            return [];
        }

        PageText? text = null;
        if (texts is not null && listed.Any(IsMarkup))
        {
            try
            {
                if (!texts.TryGet(page, out text))
                {
                    text = await document.GetPageTextAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (InvalidDataException)
            {
                // The text of the page cannot be read: the entries come without it.
            }
        }

        var entries = new (PdfAnnotation Annotation, string? Excerpt)[listed.Length];
        for (var i = 0; i < listed.Length; i++)
        {
            var annotation = listed[i];
            entries[i] = (annotation, text is not null && IsMarkup(annotation) ? AnnotationExcerpt.For(annotation, text, ExcerptLength) : null);
        }

        Array.Sort(entries, static (a, b) =>
        {
            var top = a.Annotation.Bounds.Top.CompareTo(b.Annotation.Bounds.Top);
            if (top != 0)
            {
                return top;
            }

            var left = a.Annotation.Bounds.Left.CompareTo(b.Annotation.Bounds.Left);
            return left != 0 ? left : a.Annotation.Index.CompareTo(b.Annotation.Index);
        });
        return entries;
    }

    private bool Matches(CommentItemViewModel item) => _filter switch
    {
        CommentFilter.Highlight => item.Kind == AnnotationKind.Highlight,
        CommentFilter.Underline => item.Kind == AnnotationKind.Underline,
        CommentFilter.StrikeOut => item.Kind == AnnotationKind.StrikeOut,
        CommentFilter.Other => item.Kind == AnnotationKind.Other,
        _ => true,
    };

    private int EpochOf(int page) => _epochs.GetValueOrDefault(page);

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Document) or nameof(TabViewModel.HasShownSharpPage))
        {
            TryStart();
        }
    }

    // Any thread (the editor's): must not wait and must never throw.
    private void OnPagesInvalidated(IReadOnlySet<int> pages)
    {
        try
        {
            Dispatcher.UIThread.Post(() => Invalidate(pages));
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nothing left to show.
        }
    }

    /// <summary>Starts the first read of every page once the tab has its document and has shown a sharp page of it.</summary>
    private void TryStart()
    {
        if (_disposed || _document is not null || !_tab.HasShownSharpPage || _tab.Document is not { } document)
        {
            return;
        }

        int pageCount;
        try
        {
            pageCount = document.PageCount;
        }
        catch (ObjectDisposedException)
        {
            // Replaced or closed meanwhile; the tab drops this list with that change.
            return;
        }

        _document = document;
        _pageCount = pageCount;
        _cancellation = new CancellationTokenSource();
        for (var page = 0; page < pageCount; page++)
        {
            _pending.Add(page);
        }

        ReadPending();
    }

    /// <summary>Reads the pending pages unless that runs already; marks the first read as done when nothing is left.</summary>
    private void ReadPending()
    {
        if (_disposed || _reading || _cancellation is not { } cancellation)
        {
            return;
        }

        if (_pending.Count == 0)
        {
            MarkLoaded();
            return;
        }

        _ = ReadAsync(cancellation.Token);
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        _reading = true;
        try
        {
            while (!_disposed && !cancellationToken.IsCancellationRequested && _pending.Count > 0 && _document is { } document)
            {
                var page = _pending.Min;
                _pending.Remove(page);
                var epoch = EpochOf(page);
                var texts = _tab.PageTexts;
                var reader = _tab.AnnotationReader;
                (PdfAnnotation Annotation, string? Excerpt)[] entries;
                try
                {
                    entries = await Task.Run(() => ReadPageAsync(reader, document, texts, page, cancellationToken), CancellationToken.None);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException) when (!ReferenceEquals(document, _document))
                {
                    // The saved file replaced the document meanwhile: read the page from it.
                    _pending.Add(page);
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    // Closed: the tab drops this list with the document.
                    return;
                }
                catch (Exception)
                {
                    // A damaged page (or one PDFium refuses) lists nothing; the others go on.
                    entries = [];
                }

                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (!ReferenceEquals(document, _document) || epoch != EpochOf(page))
                {
                    // The page changed, or the document was swapped, while it was read: read it again.
                    _pending.Add(page);
                    continue;
                }

                Apply(page, entries);
            }
        }
        finally
        {
            _reading = false;
        }

        if (!_disposed && !cancellationToken.IsCancellationRequested && _pending.Count == 0)
        {
            MarkLoaded();
        }
    }

    private void MarkLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasNoComments));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Puts the entries of a page into the list: an entry that shows the same annotation as before stays the same object
    /// (and keeps its container and whether it is current), the others are replaced. Only the part of <see cref="Items"/>
    /// that belongs to the page changes.
    /// </summary>
    private void Apply(int page, (PdfAnnotation Annotation, string? Excerpt)[] entries)
    {
        var old = _byPage.GetValueOrDefault(page) ?? [];
        var fresh = new CommentItemViewModel[entries.Length];
        var unchanged = old.Length == entries.Length;
        var next = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var (annotation, excerpt) = entries[i];

            // Mostly the entry after the last one found (a page with one annotation more or less); else any old one.
            var found = next < old.Length && old[next].Shows(annotation, excerpt)
                ? next
                : Array.FindIndex(old, item => item.Shows(annotation, excerpt));
            if (found >= 0)
            {
                next = found + 1;
            }

            fresh[i] = found >= 0 ? old[found] : new CommentItemViewModel(annotation, excerpt);
            if (unchanged && !ReferenceEquals(fresh[i], old[i]))
            {
                unchanged = false;
            }
        }

        if (unchanged)
        {
            return;
        }

        if (fresh.Length == 0)
        {
            _byPage.Remove(page);
        }
        else
        {
            _byPage[page] = fresh;
        }

        _total += fresh.Length - old.Length;
        if (_current is { } current && current.PageIndex == page && Array.IndexOf(fresh, current) < 0)
        {
            // The current entry went with its annotation.
            current.IsCurrent = false;
            _current = null;
        }

        var start = LowerBound(page);
        var shownOld = old.Count(Matches);
        var shown = fresh.Where(Matches).ToArray();
        var keep = new HashSet<CommentItemViewModel>(shown, ReferenceEqualityComparer.Instance);
        for (var i = start + shownOld - 1; i >= start; i--)
        {
            if (!keep.Contains(_items[i]))
            {
                _items.RemoveAt(i);
            }
        }

        for (var i = 0; i < shown.Length; i++)
        {
            var at = start + i;
            if (at >= _items.Count || !ReferenceEquals(_items[at], shown[i]))
            {
                _items.Insert(at, shown[i]);
            }
        }

        if (_loaded)
        {
            OnPropertyChanged(nameof(HasNoComments));
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>The first position in <see cref="Items"/> whose entry is not on a page before <paramref name="page"/>.</summary>
    private int LowerBound(int page)
    {
        var low = 0;
        var high = _items.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_items[middle].PageIndex < page)
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

    private void Jump(CommentItemViewModel item)
    {
        if (_disposed || !_items.Contains(item))
        {
            return;
        }

        if (!ReferenceEquals(_current, item))
        {
            if (_current is not null)
            {
                _current.IsCurrent = false;
            }

            _current = item;
            item.IsCurrent = true;
        }

        _tab.RevealArea(item.PageIndex, item.Target, fromPanel: true);
    }

    private void CancelReading()
    {
        var cancellation = _cancellation;
        _cancellation = null;
        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }
}
