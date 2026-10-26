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
/// a filter by kind, the summary and the jump to an entry. Read-only outside the mode "Comment" (design system 7.7: the
/// reading view shows no editing tools); in the mode (<see cref="IsEditing"/>) the selected entry can be edited: its
/// comment, its colour, and deleting it (highlights, underlines, strike-outs and drawings that are editable; a drawing of another program that Pagewing cannot redraw keeps its colour and says why). Since milestone 10 the filter has "Drawings", and five or more freehand strokes without a comment on a page are one row that opens and closes (<see cref="GroupThreshold"/>). Created by the tab when the panel first opens (<see cref="TabViewModel.Comments"/>), never in the startup path;
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

    /// <summary>
    /// From this many freehand strokes without a comment on a page, they are one row of the list ("Freehand, 12 strokes")
    /// that opens and closes, so hundreds of strokes do not drown the other entries.
    /// </summary>
    public const int GroupThreshold = 5;

    // The parsed summary formats, by their text: a change of the language brings other texts, which are parsed then.
    private static readonly Dictionary<string, CompositeFormat> Formats = new(StringComparer.Ordinal);

    private readonly TabViewModel _tab;

    // The pages still to read, lowest first; a page changed while it was read is put back (its epoch moved on).
    private readonly SortedSet<int> _pending = [];
    private readonly Dictionary<int, int> _epochs = [];

    // The entries of every page that has some, in the order of the list; Items is the part the filter lets through, with
    // the strokes of a group in its row (_shown: the rows of each page in Items; _groups: the group row of a page, kept with
    // whether it is open while the page has enough strokes).
    private readonly SortedDictionary<int, CommentItemViewModel[]> _byPage = [];
    private readonly Dictionary<int, CommentItemViewModel[]> _shown = [];
    private readonly Dictionary<int, CommentItemViewModel> _groups = [];
    private ObservableCollection<CommentItemViewModel> _items = [];
    private CommentFilter _filter;
    private SettingsChoice<CommentFilter>? _filterChoice;
    private CommentItemViewModel? _current;
    private CommentItemViewModel? _selected;
    private string _commentDraft = string.Empty;
    private string? _commentProblem;

    // The comment last applied for an entry: ApplyDraft does not apply the same text twice before the list shows it.
    private (CommentItemViewModel Item, string Text)? _applied;

    // A markup made by the tab that is to be selected once its page is read (by its name).
    private (int Page, string Name)? _selectNext;

    // An annotation whose comment the document asked to edit (Enter on a selection) and that is not listed yet.
    private PdfAnnotation? _editNext;
    private bool _isCommentFocusRequested;
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
        CommitCommentCommand = new RelayCommand(CommitComment, () => SelectedIsEditable && _tab.CanRunEdit);
        DiscardCommentCommand = new RelayCommand(DiscardComment);
        DeleteCommand = new RelayCommand(Delete, () => SelectedIsEditable && !_tab.IsEditingFrozen);
        SetItemColorCommand = new RelayCommand<MarkupColor>(SetItemColor);
        SetItemDrawColorCommand = new RelayCommand<DrawColor>(SetItemDrawColor);
        FilterChoices =
        [
            new(CommentFilter.All, Strings.CommentFilterAll),
            new(CommentFilter.Highlight, Strings.CommentKindHighlight),
            new(CommentFilter.Underline, Strings.CommentKindUnderline),
            new(CommentFilter.StrikeOut, Strings.CommentKindStrikeOut),
            new(CommentFilter.Drawings, Strings.CommentFilterDrawings),
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
            _shown.Clear();
            var items = new List<CommentItemViewModel>();
            foreach (var (page, entries) in _byPage)
            {
                var rows = Rows(page, entries);
                _shown[page] = rows;
                items.AddRange(rows);
            }

            Items = new ObservableCollection<CommentItemViewModel>(items);
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

            // The comments the filter lets through, not the rows: a group counts its strokes.
            var count = 0;
            var pages = 0;
            var page = 0;
            foreach (var (index, entries) in _byPage)
            {
                var matching = entries.Count(Matches);
                if (matching > 0)
                {
                    page = pages == 0 ? index + 1 : page;
                    pages++;
                    count += matching;
                }
            }

            if (count == 0)
            {
                return Strings.CommentsNone;
            }

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

    /// <summary>Gets whether pages are being read right now (for tests).</summary>
    internal bool IsReading => _reading;

    /// <summary>
    /// Gets whether the entries can be edited here: only in the mode "Comment" of the tab (<see cref="TabViewModel.IsAnnotating"/>);
    /// the panel then shows the edit controls of the selected entry.
    /// </summary>
    public bool IsEditing => _tab.IsAnnotating;

    /// <summary>
    /// Gets or sets the selected entry of the list (the keyboard cursor, a click): the edit controls of the mode act on it.
    /// Choosing another entry drops a comment that was typed and not applied.
    /// </summary>
    public CommentItemViewModel? SelectedItem
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value))
            {
                return;
            }

            CommentDraft = value is { IsGroup: false } entry ? entry.Annotation.Contents ?? string.Empty : string.Empty;
            CommentProblem = null;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedIsEditable));
            OnPropertyChanged(nameof(SelectedIsViewOnly));
            OnPropertyChanged(nameof(SelectedIsDrawing));
            OnPropertyChanged(nameof(SelectedLockReason));
            OnPropertyChanged(nameof(SelectedCommentReadOnly));
            OnPropertyChanged(nameof(SelectedCanChangeColor));
            OnPropertyChanged(nameof(SelectedColor));
            OnPropertyChanged(nameof(SelectedDrawColor));
            CommitCommentCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Gets whether editing is locked for an update (<see cref="TabViewModel.IsEditingFrozen"/>): the comment field is
    /// read-only and keeps what was typed; applying it, colour and "Delete" do nothing.
    /// </summary>
    public bool IsEditingFrozen => _tab.IsEditingFrozen;

    /// <summary>
    /// Gets whether the comment field is read-only: editing is locked for an update, or the entry is a text box of another
    /// program (its comment is the text its own appearance shows, which Pagewing cannot draw anew).
    /// </summary>
    public bool SelectedCommentReadOnly =>
        _tab.IsEditingFrozen || _selected is { IsGroup: false, IsTextBox: true, Annotation.GeometryEditable: false };

    /// <summary>Gets whether an entry is selected (the row of a group is none: it only opens and closes).</summary>
    public bool HasSelection => _selected is { IsGroup: false };

    /// <summary>Gets whether the selected entry can be edited: a highlight, underline, strike-out or drawing without replies.</summary>
    public bool SelectedIsEditable => _selected is { IsGroup: false, Annotation.Editable: true };

    /// <summary>Gets whether the selected entry is shown only ("View only"): any other annotation.</summary>
    public bool SelectedIsViewOnly => _selected is { IsGroup: false, Annotation.Editable: false };

    /// <summary>Gets whether the selected entry is a drawing (its colour comes from the palette of the drawing tools).</summary>
    public bool SelectedIsDrawing => _selected is { IsGroup: false, IsDrawing: true };

    /// <summary>Gets why the selected drawing cannot be moved or changed, or <see langword="null"/> (see <see cref="CommentItemViewModel.LockReason"/>).</summary>
    public string? SelectedLockReason => _selected is { IsGroup: false } entry ? entry.LockReason : null;

    /// <summary>
    /// Gets whether "Change colour" is offered for the selected entry: an editable markup, or a drawing Pagewing can redraw
    /// without loss; a locked drawing of another program offers only its comment and deleting.
    /// </summary>
    public bool SelectedCanChangeColor => SelectedIsEditable && (!_selected!.IsDrawing || _selected.Annotation.GeometryEditable);

    /// <summary>Gets the palette colour of the selected markup, or <see langword="null"/> (none, a drawing, or a colour of another application).</summary>
    public MarkupColor? SelectedColor => _selected is { IsGroup: false, IsDrawing: false, Annotation.Color: { } color } ? AnnotationPalette.Match(color) : null;

    /// <summary>Gets the colour of the selected drawing from the palette of the drawing tools, or <see langword="null"/>.</summary>
    public DrawColor? SelectedDrawColor => _selected is { IsGroup: false, IsDrawing: true, Annotation.Color: { } color } ? DrawPalette.Match(color) : null;

    /// <summary>
    /// Gets or sets the comment typed for the selected entry, not applied yet: Ctrl+Enter applies it
    /// (<see cref="CommitCommentCommand"/>), Esc drops it (<see cref="DiscardCommentCommand"/>).
    /// </summary>
    public string CommentDraft
    {
        get => _commentDraft;
        set
        {
            if (SetProperty(ref _commentDraft, value ?? string.Empty))
            {
                CommentProblem = null;
            }
        }
    }

    /// <summary>
    /// Gets why the typed comment of a text box was not applied (its comment is its text: a character a text box cannot show,
    /// or no text at all), shown under the comment field; <see langword="null"/> otherwise. Typing clears it.
    /// </summary>
    public string? CommentProblem
    {
        get => _commentProblem;
        private set => SetProperty(ref _commentProblem, value);
    }

    /// <summary>Ctrl+Enter in the comment field: the typed comment becomes the comment of the entry; an empty one removes it.</summary>
    public RelayCommand CommitCommentCommand { get; }

    /// <summary>Esc in the comment field: drops what was typed, the entry keeps its comment.</summary>
    public RelayCommand DiscardCommentCommand { get; }

    /// <summary>"Delete": removes the selected markup from the document; undo brings it back.</summary>
    public RelayCommand DeleteCommand { get; }

    /// <summary>A colour of the menu "Colour" (parameter: the <see cref="MarkupColor"/>): the selected markup gets it.</summary>
    public ICommand SetItemColorCommand { get; }

    /// <summary>A colour of the menu "Colour" for a drawing (parameter: the <see cref="DrawColor"/>): the selected drawing gets it, one step.</summary>
    public ICommand SetItemDrawColorCommand { get; }

    /// <summary>
    /// Selects the entry of <paramref name="created"/> once its page is read again (a markup the tab just made), so the
    /// open panel shows it; without a name nothing is selected.
    /// </summary>
    internal void SelectWhenListed(PdfAnnotation created)
    {
        ArgumentNullException.ThrowIfNull(created);
        if (!_disposed && created.Name is { } name)
        {
            _selectNext = (created.Page, name);
        }
    }

    /// <summary>
    /// Gets whether the panel is to give the keys to the comment field of the selected entry (Enter on a selection in the
    /// document, <see cref="BeginEdit"/>); the panel takes the request when it acts on it (<see cref="TakeCommentFocusRequest"/>).
    /// </summary>
    internal bool IsCommentFocusRequested
    {
        get => _isCommentFocusRequested;
        private set => SetProperty(ref _isCommentFocusRequested, value);
    }

    /// <summary>
    /// Selects the entry of <paramref name="annotation"/> (by name, else by index on its page) and asks the panel to put the
    /// keys into its comment field: Enter on a selection in the document. An annotation not listed yet is selected once its
    /// page is read; one the filter hides shows all kinds again.
    /// </summary>
    internal void BeginEdit(PdfAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (_disposed)
        {
            return;
        }

        _editNext = null;
        if (FindEntry(annotation) is not { } item)
        {
            _editNext = annotation;
            return;
        }

        StartEditing(item);
    }

    /// <summary>The panel acted on <see cref="IsCommentFocusRequested"/>.</summary>
    internal void TakeCommentFocusRequest() => IsCommentFocusRequested = false;

    /// <summary>
    /// Applies the typed comment of the selected entry when it differs from its comment (as Ctrl+Enter does), unless that
    /// text was applied for the entry already; only in the mode, and only while editing is possible.
    /// </summary>
    internal void ApplyDraft()
    {
        if (HasPendingDraft)
        {
            CommitComment();
        }
    }

    /// <summary>
    /// Gets whether a comment typed for the selected entry waits to be applied: it differs from the entry's comment and was
    /// not applied yet (<see cref="ApplyDraft"/> applies it once the tab takes edits).
    /// </summary>
    internal bool HasPendingDraft =>
        !_disposed && IsEditing && _selected is { IsGroup: false, Annotation.Editable: true } item
        && _commentDraft != (item.Annotation.Contents ?? string.Empty)
        && !(_applied is { } applied && ReferenceEquals(applied.Item, item) && applied.Text == _commentDraft);

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
        _shown.Clear();
        _groups.Clear();
        _items.Clear();
        _current = null;
        _selected = null;
        _document = null;
    }

    /// <summary>
    /// The session swapped the document for the saved file (the same pages and annotations): the list stays, and what is
    /// still to read (also a page whose read on the closed document failed, before or after this call) is read from
    /// <paramref name="adopted"/>; a reading that stopped on the closed document starts again.
    /// </summary>
    internal void OnDocumentAdopted(PdfDocument adopted)
    {
        ArgumentNullException.ThrowIfNull(adopted);
        if (_disposed || _document is null)
        {
            return;
        }

        _document = adopted;

        // A read that met the closed old document before the tab learned of the new one stopped and left its page to be
        // read: it goes on now (it does nothing while a read runs or nothing is left).
        ReadPending();
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
        CommentFilter.Drawings => item.IsDrawing,
        CommentFilter.Other => item.IsOther,
        _ => true,
    };

    /// <summary>A freehand stroke without a comment: it joins the group of its page when the page has enough of them.</summary>
    private static bool IsGroupable(CommentItemViewModel item) => !item.IsGroup && item.Kind == AnnotationKind.Ink && !item.HasComment;

    /// <summary>
    /// The rows of a page in the list: the entries the filter lets through, with the freehand strokes without a comment in
    /// one group row at the place of the first of them when the page has <see cref="GroupThreshold"/> or more (the strokes
    /// follow in their places while the group is open). The group row of a page is kept with whether it is open.
    /// </summary>
    private CommentItemViewModel[] Rows(int page, CommentItemViewModel[] entries)
    {
        var strokes = 0;
        CommentItemViewModel? first = null;
        foreach (var entry in entries)
        {
            if (IsGroupable(entry))
            {
                first ??= entry;
                strokes++;
            }
        }

        if (strokes < GroupThreshold || first is null)
        {
            _groups.Remove(page);
            return [.. entries.Where(Matches)];
        }

        if (_groups.TryGetValue(page, out var group))
        {
            group.UpdateGroup(first.Annotation, strokes);
        }
        else
        {
            group = CommentItemViewModel.Group(first.Annotation, strokes);
            _groups[page] = group;
        }

        var rows = new List<CommentItemViewModel>(group.IsExpanded ? entries.Length + 1 : entries.Length - strokes + 1);
        foreach (var entry in entries)
        {
            if (!Matches(entry))
            {
                continue;
            }

            if (!IsGroupable(entry))
            {
                rows.Add(entry);
                continue;
            }

            if (ReferenceEquals(entry, first))
            {
                rows.Add(group);
            }

            if (group.IsExpanded)
            {
                rows.Add(entry);
            }
        }

        return [.. rows];
    }

    /// <summary>
    /// Puts <paramref name="rows"/> in the place of the rows of <paramref name="page"/> in <see cref="Items"/>: rows that stay
    /// stay the same objects in place (the list keeps their containers and its scroll position), the others are removed or
    /// inserted. Should the rows that stay come in another order, the part of the page is put in anew.
    /// </summary>
    private void ReplaceRows(int page, CommentItemViewModel[] rows)
    {
        var old = _shown.GetValueOrDefault(page) ?? [];
        if (rows.Length == 0)
        {
            _shown.Remove(page);
        }
        else
        {
            _shown[page] = rows;
        }

        var start = LowerBound(page);
        var keep = new HashSet<CommentItemViewModel>(rows, ReferenceEqualityComparer.Instance);
        var before = new HashSet<CommentItemViewModel>(old, ReferenceEqualityComparer.Instance);
        var inOrder = old.Where(keep.Contains).SequenceEqual(rows.Where(before.Contains), ReferenceEqualityComparer.Instance);
        for (var i = start + old.Length - 1; i >= start; i--)
        {
            if (!inOrder || !keep.Contains(_items[i]))
            {
                _items.RemoveAt(i);
            }
        }

        for (var i = 0; i < rows.Length; i++)
        {
            var at = start + i;
            if (at >= _items.Count || !ReferenceEquals(_items[at], rows[i]))
            {
                _items.Insert(at, rows[i]);
            }
        }
    }

    /// <summary>Opens or closes the group row <paramref name="group"/>; a selected stroke that is hidden then is no longer selected.</summary>
    private void Toggle(CommentItemViewModel group)
    {
        if (!_byPage.TryGetValue(group.PageIndex, out var entries))
        {
            return;
        }

        group.IsExpanded = !group.IsExpanded;
        ReplaceRows(group.PageIndex, Rows(group.PageIndex, entries));
        if (_selected is { } selected && !ReferenceEquals(selected, group) && !_items.Contains(selected))
        {
            SelectedItem = null;
        }
    }

    /// <summary>Makes an entry a row of the list: all kinds again when the filter hides it, its group opened when it is in one.</summary>
    private void EnsureListed(CommentItemViewModel item)
    {
        if (!_items.Contains(item))
        {
            Filter = CommentFilter.All;
        }

        if (!_items.Contains(item) && _groups.TryGetValue(item.PageIndex, out var group) && !group.IsExpanded)
        {
            Toggle(group);
        }
    }

    private int EpochOf(int page) => _epochs.GetValueOrDefault(page);

    private static AnnotationLocator LocatorOf(PdfAnnotation annotation) => new(annotation.Page, annotation.Index, annotation.Name);

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TabViewModel.Document) or nameof(TabViewModel.HasShownSharpPage))
        {
            TryStart();
        }
        else if (e.PropertyName == nameof(TabViewModel.IsAskedAbout))
        {
            // A card asks about the tab: Ctrl+Enter waits for its answer (the text stays).
            CommitCommentCommand.RaiseCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(TabViewModel.IsEditingFrozen))
        {
            // Locked for an update: what was typed stays (read-only), nothing is applied.
            OnPropertyChanged(nameof(IsEditingFrozen));
            OnPropertyChanged(nameof(SelectedCommentReadOnly));
            CommitCommentCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(TabViewModel.IsAnnotating))
        {
            // In and out of the mode: the edit controls come or go; a comment typed and not applied goes with them.
            CommentDraft = _selected?.Annotation.Contents ?? string.Empty;
            foreach (var entries in _byPage.Values)
            {
                foreach (var item in entries)
                {
                    item.IsViewOnlyInMode = IsEditing && !item.Annotation.Editable;
                    item.ShowsLockReason = IsEditing && item.LockReason is not null;
                }
            }

            OnPropertyChanged(nameof(IsEditing));
            CommitCommentCommand.RaiseCanExecuteChanged();
        }
    }

    private void CommitComment()
    {
        if (_selected is not { IsGroup: false, Annotation.Editable: true } item)
        {
            return;
        }

        var text = _commentDraft;
        if (text == (item.Annotation.Contents ?? string.Empty))
        {
            return;
        }

        // The comment of a text box is its text: only Pagewing's own, and only a text it can show (the typed text stays and
        // the field says why).
        if (item.IsTextBox)
        {
            var problem = !item.Annotation.GeometryEditable ? item.LockReason
                : TextBoxSpec.CheckText(text).Problem == TextBoxTextProblem.Empty ? Strings.TextBoxEmpty
                : TabViewModel.TextBoxProblemOf(text);
            if (problem is not null)
            {
                CommentProblem = problem;
                return;
            }
        }

        // Only an edit the tab takes counts as applied: one refused now (a card asks about the tab) keeps the text typed and
        // is applied later (ApplyDraft), never dropped.
        if (!_tab.CanRunEdit)
        {
            return;
        }

        var locator = LocatorOf(item.Annotation);
        _applied = (item, text);
        _ = _tab.RunEditAsync(editor => editor.UpdateAsync(locator, new AnnotationEdit(Contents: text)));
    }

    private void DiscardComment() => CommentDraft = _selected?.Annotation.Contents ?? string.Empty;

    private void Delete()
    {
        if (_selected is not { IsGroup: false, Annotation.Editable: true } item)
        {
            return;
        }

        var locator = LocatorOf(item.Annotation);
        _ = _tab.RunEditAsync(editor => editor.DeleteAsync(locator));
    }

    private void SetItemColor(MarkupColor color)
    {
        if (_selected is not { IsGroup: false, IsDrawing: false, Annotation.Editable: true } item || !Enum.IsDefined(color))
        {
            return;
        }

        var value = AnnotationPalette.ColorOf(color);
        if (item.Annotation.Color == value)
        {
            return;
        }

        var locator = LocatorOf(item.Annotation);
        _ = _tab.RunEditAsync(editor => editor.UpdateAsync(locator, new AnnotationEdit(Color: value)));
    }

    /// <summary>A drawing gets a colour of the drawing palette: it is redrawn (one step); a locked drawing is left alone.</summary>
    private void SetItemDrawColor(DrawColor color)
    {
        if (_selected is not { IsGroup: false, IsDrawing: true, Annotation: { Editable: true, GeometryEditable: true } annotation } || !Enum.IsDefined(color))
        {
            return;
        }

        var value = DrawPalette.ColorOf(color);
        if (annotation.Color == value)
        {
            return;
        }

        var locator = LocatorOf(annotation);
        _ = _tab.RunEditAsync(editor => editor.UpdateAsync(locator, new AnnotationEdit(Color: value)));
    }

    /// <summary>A new entry of the list, with what it shows in the mode.</summary>
    private CommentItemViewModel NewEntry(PdfAnnotation annotation, string? excerpt)
    {
        var entry = new CommentItemViewModel(annotation, excerpt) { IsViewOnlyInMode = IsEditing && !annotation.Editable };
        entry.ShowsLockReason = IsEditing && entry.LockReason is not null;
        return entry;
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
                catch (ObjectDisposedException)
                {
                    // The document was closed under the read: the page stays to be read. After a save the session closes
                    // the old document before the tab learns of the new one, so this may come first; once the tab has
                    // passed the saved document on (OnDocumentAdopted) the reading goes on there. A document that was
                    // really closed takes this list with it.
                    _pending.Add(page);
                    if (!ReferenceEquals(document, _document))
                    {
                        continue;
                    }

                    return;
                }
                catch (Exception)
                {
                    // A damaged page (or one PDFium refuses) lists nothing; the others go on. A page that was read before
                    // keeps its entries: a read that fails once does not make its annotations vanish.
                    if (_byPage.ContainsKey(page))
                    {
                        continue;
                    }

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

            fresh[i] = found >= 0 ? old[found] : NewEntry(annotation, excerpt);
            if (unchanged && !ReferenceEquals(fresh[i], old[i]))
            {
                unchanged = false;
            }
        }

        if (unchanged)
        {
            SelectNew(page, fresh);
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

        // A changed entry is a new object: the selection follows its annotation (by name, else by position), so an edited
        // comment or colour stays selected; an entry that went leaves nothing selected.
        // Taken now: the list drops its selection (and tells it back here) when the old entry leaves it.
        var selectedBefore = _selected;
        CommentItemViewModel? follower = null;
        if (selectedBefore is { IsGroup: false } selected && selected.PageIndex == page && Array.IndexOf(fresh, selected) < 0)
        {
            follower = Array.Find(fresh, item => selected.Annotation.Name is { } name
                ? item.Annotation.Name == name
                : item.Annotation.Index == selected.Annotation.Index && item.Kind == selected.Kind);
        }

        ReplaceRows(page, Rows(page, fresh));
        if (_loaded)
        {
            OnPropertyChanged(nameof(HasNoComments));
            OnPropertyChanged(nameof(Summary));
        }

        if (selectedBefore is { } stale && stale.PageIndex == page && !_items.Contains(stale))
        {
            SelectedItem = follower is not null && _items.Contains(follower) ? follower : null;
        }

        SelectNew(page, fresh);
    }

    /// <summary>The entry of an annotation: by name if it has one, else by index on its page.</summary>
    private CommentItemViewModel? FindEntry(PdfAnnotation annotation) =>
        _byPage.TryGetValue(annotation.Page, out var entries)
            ? Array.Find(entries, item => annotation.Name is { } name ? item.Annotation.Name == name : item.Annotation.Index == annotation.Index)
            : null;

    private void StartEditing(CommentItemViewModel item)
    {
        EnsureListed(item);
        SelectedItem = item;
        IsCommentFocusRequested = true;
    }

    /// <summary>Selects the markup the tab just made once it is listed (<see cref="SelectWhenListed"/>).</summary>
    private void SelectNew(int page, CommentItemViewModel[] fresh)
    {
        if (_editNext is { } edit && edit.Page == page && FindEntry(edit) is { } editing)
        {
            _editNext = null;
            StartEditing(editing);
            return;
        }

        if (_selectNext is not { } next || next.Page != page)
        {
            return;
        }

        var item = Array.Find(fresh, entry => entry.Annotation.Name == next.Name);
        if (item is not null && _items.Contains(item))
        {
            _selectNext = null;
            SelectedItem = item;
        }
        else if (item is not null && IsGroupable(item) && _groups.TryGetValue(page, out var group) && _items.Contains(group))
        {
            // A stroke just drawn into a closed group: its row shows where it went (the group stays closed).
            _selectNext = null;
            SelectedItem = group;
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

        if (item.IsGroup)
        {
            // The row of a group opens and closes; the document stays where it is.
            Toggle(item);
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

        // With the select tool the annotation is selected in the document as well (its frame, its handles, the keys).
        _tab.SelectFromList(item.Annotation);
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
