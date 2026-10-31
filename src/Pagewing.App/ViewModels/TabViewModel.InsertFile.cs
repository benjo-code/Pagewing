using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The files inserted into the page plan of the tab (milestone 13, "Insert pages from a file"; spec 5.3 and 5.4). The tab owns
/// them: each is an open document with the stamp of its file, numbered by <see cref="AllocatePageSource"/> (the numbers are never
/// reused in this tab, also not after every page of a file was deleted or undone), and they live as long as the plan: they are
/// closed when the plan ends (applied, discarded, another document), and when the tab closes. They travel with the tab into
/// another window. Nothing of it exists before a file is inserted.
/// </summary>
public sealed partial class TabViewModel
{
    private readonly Dictionary<PageSourceId, InsertedSource> _insertedSources = [];
    private RelayCommand? _insertFileCommand;

    /// <summary>
    /// Gets the files inserted into the plan, by their number in it (the sources the slots of <see cref="PagePlan"/> refer to).
    /// Changes are announced as a change of this property, also when more page sizes of a file are known.
    /// </summary>
    internal IReadOnlyDictionary<PageSourceId, InsertedSource> InsertedSources => _insertedSources;

    /// <summary>
    /// The command "Insert pages from a file" (catalog <c>pages.insertFile</c>): asks the window to pick a file and show its
    /// dialog (<see cref="InsertFileRequested"/>). Runs only in the mode, like the other page commands.
    /// </summary>
    public ICommand InsertFileCommand => _insertFileCommand ??= new RelayCommand(() => InsertFileRequested?.Invoke(), () => CanEditPages);

    /// <summary>Raised by <see cref="InsertFileCommand"/>: the user wants pages from another file.</summary>
    internal event Action? InsertFileRequested;

    /// <summary>Gets whether one more file may be inserted (a plan takes pages from <see cref="PageEditLimits.MaxInsertedFiles"/> files at most).</summary>
    internal bool CanInsertAnotherFile => _insertedSources.Count < PageEditLimits.MaxInsertedFiles;

    /// <summary>Tells the user that no more files can be inserted (the notice of the pages bar); nothing was opened.</summary>
    internal void RefuseAnotherFile() => PageEditNotice = Strings.PageEditsFileLimit;

    /// <summary>Tells the user that the file dialog failed.</summary>
    internal void ReportInsertPickFailure() => PageEditNotice = Strings.InsertFilePickFailed;

    /// <summary>Gets the file of each inserted source with its stamp, for the rewrite (the apply checks each against it).</summary>
    internal IReadOnlyDictionary<PageSourceId, PageSourceFile> InsertedFiles() =>
        _insertedSources.ToDictionary(pair => pair.Key, pair => pair.Value.File);

    /// <summary>Gets the open document of each inserted source, for the report of what the plan loses.</summary>
    internal IReadOnlyDictionary<PageSourceId, PdfDocument> InsertedDocuments() =>
        _insertedSources.ToDictionary(pair => pair.Key, pair => pair.Value.Document);

    /// <summary>Gets the names of the inserted files by the number of their source, for the sentences of the report.</summary>
    internal IReadOnlyDictionary<int, string> InsertedNames() =>
        _insertedSources.ToDictionary(pair => pair.Key.Value, pair => pair.Value.Name);

    /// <summary>
    /// Tells where the pages of a file would go: after the last selected page of the plan, else at the end, as the gap of the
    /// plan (0 before the first page).
    /// </summary>
    internal int InsertGap => _pageHistory is not { } history ? 0
        : _selectedPages.Count > 0 ? _selectedPages.Max() + 1
        : history.Current.Slots.Count;

    /// <summary>Gets the words that tell where inserted pages would go ("The pages are inserted after page 3.", "… at the end.").</summary>
    internal string InsertPositionText => _pageHistory is not { } history || _selectedPages.Count == 0 || InsertGap >= history.Current.Slots.Count
        ? Strings.InsertFilePositionEnd
        : string.Format(Strings.Culture, CompositeFormat.Parse(Strings.InsertFilePositionAfter), InsertGap.ToString("N0", Strings.Culture));

    /// <summary>
    /// Puts pages of a file into the plan: <paramref name="source"/> becomes a source of the plan (numbered now), the pages
    /// <paramref name="pages"/> (0-based, in the order they get) go in after the last selected page, else at the end, and are
    /// selected. A refusal (the plan would hold too many pages) leaves the plan and the tab's files as they were.
    /// </summary>
    /// <param name="source">The open file; the tab owns it from now on when this works.</param>
    /// <param name="pages">The pages of the file.</param>
    /// <returns><see langword="null"/> when the pages went in, else why they did not.</returns>
    internal string? TryInsertFile(InsertedSource source, IReadOnlyList<int> pages)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(pages);
        if (!CanEditPages)
        {
            return Strings.PageEditsRefused;
        }

        if (!CanInsertAnotherFile)
        {
            return Strings.PageEditsFileLimit;
        }

        var gap = InsertGap;
        var id = AllocatePageSource();
        source.Id = id;
        _insertedSources[id] = source;
        if (!Stage(plan => plan.InsertFrom(id, pages, gap), Enumerable.Range(gap, pages.Count)))
        {
            // Refused (Stage set the notice) or nothing to insert: the file is not part of the plan.
            _insertedSources.Remove(id);
            var reason = PageEditNotice ?? Strings.PageEditsRefused;
            PageEditNotice = null;
            return reason;
        }

        source.SizesChanged += OnInsertedSizesChanged;
        OnPropertyChanged(nameof(InsertedSources));
        _ = source.ReadSizesAsync();
        return null;
    }

    private void OnInsertedSizesChanged() => OnPropertyChanged(nameof(InsertedSources));

    /// <summary>Closes the inserted files: their thumbnails first, then the documents. The plan that used them is gone or about to go.</summary>
    private void ReleaseInsertedSources()
    {
        if (_insertedSources.Count == 0)
        {
            return;
        }

        var sources = _insertedSources.Values.ToArray();
        _insertedSources.Clear();
        foreach (var source in sources)
        {
            source.SizesChanged -= OnInsertedSizesChanged;
            source.Dispose();
        }

        OnPropertyChanged(nameof(InsertedSources));
    }

    private void ReleaseInsertedThumbnails()
    {
        foreach (var source in _insertedSources.Values)
        {
            source.ReleaseThumbnails();
        }
    }
}
