using System.Globalization;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The card "Split document" and the card "Extract selected pages" (milestone 13, spec 5.3 and 5.4). Splitting cuts the open
/// document every <i>N</i> pages, by page ranges or by the bookmarks of the top level (the choice is open only when the document
/// has such bookmarks) into new files in a folder; extracting writes the selected pages as one new file at a place the save
/// dialog chose. Before anything is written the card shows the files, what the new files lose (the sentences of the apply card)
/// and which files are in the way. The new files are written one after the other through the checked full rewrite; the
/// original is never touched, and a file that exists is replaced only on the explicit choice (the checkbox of the card, or the
/// confirmation of the save dialog of an extract). UI thread only.
/// </summary>
public sealed class SplitViewModel : PageOutputViewModel
{
    // More notes than this end in a sentence that counts the rest; a card with hundreds of lines helps nobody.
    private const int MaxNotes = 12;

    // More files than this in the list end in a count of the rest.
    private const int MaxListedFiles = 8;

    private readonly bool _isExtract;
    private readonly Func<string, string?, Task<string?>> _pickSave;
    private readonly RelayCommand _reviewCommand;
    private readonly RelayCommand _backCommand;
    private readonly RelayCommand _startCommand;
    private string _everyText = "1";
    private string _rangesText = string.Empty;
    private SplitWay _way = SplitWay.Every;
    private string? _inputError;
    private string? _bookmarksReason = Strings.SplitBookmarksReading;
    private IReadOnlyList<PdfOutlineItem> _outline = [];
    private IReadOnlyList<SplitPart> _parts = [];
    private IReadOnlyList<string> _fileNames = [];
    private IReadOnlyList<string> _allConflicts = [];
    private IReadOnlyList<string> _conflicts = [];
    private List<string> _notes = [];
    private string _summary = string.Empty;
    private int _moreFiles;
    private bool _picking;

    /// <summary>Creates the card; <see cref="BeginAsync"/> starts it.</summary>
    /// <param name="source">The open document, its file and the selection of the list.</param>
    /// <param name="extract">Whether the card extracts the selection (else it splits).</param>
    /// <param name="pickFolder">Shows the folder dialog.</param>
    /// <param name="pickSave">Shows the save dialog with a suggested name and start folder.</param>
    internal SplitViewModel(PageOutputSource source, bool extract, Func<string?, Task<string?>> pickFolder, Func<string, string?, Task<string?>> pickSave)
        : base(source, pickFolder, extract ? PageOutputState.Checking : PageOutputState.Setup)
    {
        ArgumentNullException.ThrowIfNull(pickSave);
        if (extract && source.Selection.Count == 0)
        {
            throw new ArgumentException("There is nothing selected to extract.", nameof(source));
        }

        _isExtract = extract;
        _pickSave = pickSave;
        _reviewCommand = new RelayCommand(() => _ = ReviewAsync(), () => IsSetup);
        _backCommand = new RelayCommand(Back, () => IsReview && !_isExtract);
        _startCommand = new RelayCommand(() => _ = StartAsync(), CanStart);
        if (extract)
        {
            _summary = SummaryOfExtract(source.Selection);
        }
        else
        {
            _bookmarksReason = Strings.SplitBookmarksReading;
        }
    }

    private enum SplitWay
    {
        Every,
        Ranges,
        Bookmarks,
    }

    /// <summary>Gets whether the card extracts the selection (else it splits the document).</summary>
    public bool IsExtract => _isExtract;

    /// <summary>Gets the title of the card.</summary>
    public string Title => _isExtract ? Strings.ExtractTitle : Strings.SplitTitle;

    /// <summary>Gets the text of the primary button of the review: "Save as …" for an extract (the dialog asks where), "Create files" for a split.</summary>
    public string StartText => _isExtract ? Strings.ExtractSaveAs : Strings.SplitStart;

    /// <summary>Gets whether "Back" is shown (the review of a split; an extract has no options to go back to).</summary>
    public bool ShowsBack => IsReview && !_isExtract;

    /// <summary>Gets whether the way is "every N pages".</summary>
    public bool IsEvery
    {
        get => _way == SplitWay.Every;
        set => ChooseWay(SplitWay.Every, value);
    }

    /// <summary>Gets whether the way is "by page ranges".</summary>
    public bool IsRanges
    {
        get => _way == SplitWay.Ranges;
        set => ChooseWay(SplitWay.Ranges, value);
    }

    /// <summary>Gets or sets whether the way is "by bookmarks"; only possible while <see cref="BookmarksAvailable"/>.</summary>
    public bool IsBookmarks
    {
        get => _way == SplitWay.Bookmarks;
        set => ChooseWay(SplitWay.Bookmarks, value);
    }

    /// <summary>Gets or sets the number of pages per file for "every N pages".</summary>
    public string EveryText
    {
        get => _everyText;
        set
        {
            if (SetProperty(ref _everyText, value ?? string.Empty))
            {
                InputError = null;
            }
        }
    }

    /// <summary>Gets or sets the ranges for "by page ranges" (the syntax of printing, one file per range).</summary>
    public string RangesText
    {
        get => _rangesText;
        set
        {
            if (SetProperty(ref _rangesText, value ?? string.Empty))
            {
                InputError = null;
            }
        }
    }

    /// <summary>Gets whether the document has top-level bookmarks that point to a page, so that "by bookmarks" can be chosen (known once they are read).</summary>
    public bool BookmarksAvailable => _bookmarksReason is null;

    /// <summary>Gets why "by bookmarks" cannot be chosen (still reading, or the document has none), or <see langword="null"/> when it can.</summary>
    public string? BookmarksReason
    {
        get => _bookmarksReason;
        private set
        {
            if (SetProperty(ref _bookmarksReason, value))
            {
                OnPropertyChanged(nameof(BookmarksAvailable));
            }
        }
    }

    /// <summary>Gets why the options cannot be taken (a wrong number or range, a missing folder), or <see langword="null"/>.</summary>
    public string? InputError
    {
        get => _inputError;
        private set => SetProperty(ref _inputError, value);
    }

    /// <summary>Gets the sentence that says what the card will do (for an extract: which pages become a new PDF).</summary>
    public string Summary => _summary;

    /// <summary>Gets the names of the files that will be created, up to a few (the review).</summary>
    public IReadOnlyList<string> FileNames => _fileNames;

    /// <summary>Gets the heading of the list of files ("Files to create (5):").</summary>
    public string FilesHeading => Format(Strings.SplitFilesHeading, _parts.Count.ToString("N0", Strings.Culture));

    /// <summary>Gets the sentence for the files that are not in the list ("… and 12 more"), or <see langword="null"/>.</summary>
    public string? MoreFilesText => _moreFiles > 0 ? Format(Strings.SplitMoreFiles, _moreFiles.ToString("N0", Strings.Culture)) : null;

    /// <summary>Gets what the new files lose or change, one sentence each (the sentences of the apply card), without repeats.</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>Gets whether the review has notes to show.</summary>
    public bool HasNotes => _notes.Count > 0;

    /// <summary>Gets the names of files that already exist at a target and will not be replaced (empty with <see cref="PageOutputViewModel.ReplaceExisting"/>).</summary>
    public IReadOnlyList<string> Conflicts => _conflicts;

    /// <summary>Gets whether files are in the way.</summary>
    public bool HasConflicts => _conflicts.Count > 0;

    /// <summary>Gets the names of the files in the way as one list for the card.</summary>
    public string ConflictsText => NamesOf(_conflicts);

    /// <summary>"Continue": checks the options and what the new files lose, then shows the review.</summary>
    public ICommand ReviewCommand => _reviewCommand;

    /// <summary>"Back": from the review to the options.</summary>
    public ICommand BackCommand => _backCommand;

    /// <summary>"Create files" (split) or "Save as …" (extract): writes the files. Does nothing while files are in the way and not to be replaced.</summary>
    public ICommand StartCommand => _startCommand;

    /// <summary>Starts the card: an extract checks its one file, a split reads the bookmarks of the document.</summary>
    internal async Task BeginAsync()
    {
        if (_isExtract)
        {
            await CheckAsync([new SplitPart(Source.Stem, Source.Selection)]);
            return;
        }

        try
        {
            var outline = await Source.Document.GetOutlineAsync(Token);
            if (IsDisposed)
            {
                return;
            }

            _outline = outline;
            try
            {
                DocumentSplitter.Plan(Source.PageCount, SplitMode.Bookmarks(outline));
                BookmarksReason = null;
            }
            catch (DocumentSplitException ex) when (ex.Error == DocumentSplitError.NoBookmarks)
            {
                BookmarksReason = Strings.SplitNoBookmarks;
            }
            catch (PageEditException)
            {
                BookmarksReason = Strings.SplitNoBookmarks;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The card or the document went meanwhile.
        }
        catch (Exception)
        {
            // The bookmarks cannot be read: the other ways stay open.
            if (!IsDisposed)
            {
                BookmarksReason = Strings.SplitNoBookmarks;
            }
        }
    }

    private protected override void OnStateChanged()
    {
        OnPropertyChanged(nameof(ShowsBack));
        _reviewCommand.RaiseCanExecuteChanged();
        _backCommand.RaiseCanExecuteChanged();
        _startCommand.RaiseCanExecuteChanged();
    }

    private protected override void OnFolderChanged() => InputError = null;

    private protected override void OnReplaceExistingChanged()
    {
        SetConflicts();
        _startCommand.RaiseCanExecuteChanged();
    }

    private protected override string SuccessText(IReadOnlyList<string> paths) =>
        paths.Count == 1
            ? Format(Strings.SplitDoneOne, paths[0])
            : Format(Strings.SplitDoneMany, paths.Count.ToString("N0", Strings.Culture), Path.GetDirectoryName(paths[0]) ?? Folder);

    private protected override string Describe(Exception exception)
    {
        // A part that failed wraps the real cause: the words follow the cause.
        if (exception is DocumentSplitException split)
        {
            switch (split.Error)
            {
                case DocumentSplitError.NoBookmarks:
                    return Strings.SplitNoBookmarks;
                case DocumentSplitError.DirectoryMissing:
                    return Strings.PageOutputErrorFolderMissing;
                case DocumentSplitError.TargetExists:
                    return Format(Strings.PageOutputErrorTargetExists, NamesOf(split.Paths));
                case DocumentSplitError.TargetIsSource:
                    return Strings.PageOutputErrorTargetIsSource;
                case DocumentSplitError.UnsafeName:
                    return Strings.PageOutputErrorUnsafeName;
                default:
                    var cause = split.InnerException is { } inner ? DescribeCommon(inner) ?? Strings.PageOutputErrorUnknown : Strings.PageOutputErrorUnknown;

                    // The file is not the one that was opened: that says it all, for every part.
                    return cause == Strings.PageOutputErrorSourceChanged
                        ? cause
                        : Format(Strings.PageOutputErrorPart, (split.PartIndex + 1).ToString("N0", Strings.Culture), cause);
            }
        }

        return DescribeCommon(exception) ?? Strings.PageOutputErrorUnknown;
    }

    private static string SummaryOfExtract(IReadOnlyList<int> selection)
    {
        var list = PageListText(selection);
        return selection.Count == 1
            ? Format(Strings.ExtractSummaryOne, list)
            : Format(Strings.ExtractSummaryMany, list, Format(Strings.PageCounterSame, selection.Count.ToString("N0", Strings.Culture)));
    }

    /// <summary>"3, 5", "2–4, 7" for 0-based pages: the page numbers in groups, a few groups at most.</summary>
    private static string PageListText(IReadOnlyList<int> pages)
    {
        var sorted = pages.Distinct().Order().ToArray();
        var groups = new List<string>();
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            groups.Add(end == i ? Number(sorted[i] + 1) : end == i + 1 ? Number(sorted[i] + 1) + ", " + Number(sorted[end] + 1) : Number(sorted[i] + 1) + "–" + Number(sorted[end] + 1));
            i = end + 1;
        }

        return groups.Count > 10 ? string.Join(", ", groups.Take(10)) + ", …" : string.Join(", ", groups);
    }

    private static string Number(int value) => value.ToString("N0", Strings.Culture);

    private void ChooseWay(SplitWay way, bool chosen)
    {
        if (!chosen || _way == way || !IsSetup || (way == SplitWay.Bookmarks && !BookmarksAvailable))
        {
            return;
        }

        _way = way;
        InputError = null;
        OnPropertyChanged(nameof(IsEvery));
        OnPropertyChanged(nameof(IsRanges));
        OnPropertyChanged(nameof(IsBookmarks));
    }

    private bool CanStart() => !_picking && IsReview && (_isExtract || ReplaceExisting || _allConflicts.Count == 0);

    private void Back()
    {
        if (IsReview)
        {
            State = PageOutputState.Setup;
        }
    }

    // Takes the options; a mistake stays on the card in Setup, with its reason on the field, and nothing is read or written.
    private async Task ReviewAsync()
    {
        if (!IsSetup)
        {
            return;
        }

        InputError = null;
        FolderError = null;
        var folder = Folder.Trim();
        if (folder.Length == 0 || !Directory.Exists(folder))
        {
            FolderError = Strings.PageOutputErrorFolderMissing;
            return;
        }

        var parts = PlanParts(out var error);
        if (parts is null)
        {
            InputError = error;
            return;
        }

        await CheckAsync(parts);
    }

    // The parts the options describe, or null with the words for a wrong option.
    private IReadOnlyList<SplitPart>? PlanParts(out string? error)
    {
        error = null;
        var name = Source.Stem;
        try
        {
            switch (_way)
            {
                case SplitWay.Every:
                    if (!int.TryParse(_everyText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var every) || every < 1)
                    {
                        error = Strings.SplitEveryInvalid;
                        return null;
                    }

                    return DocumentSplitter.Plan(Source.PageCount, SplitMode.Every(every), name);
                case SplitWay.Ranges:
                    return DocumentSplitter.Plan(Source.PageCount, SplitMode.Ranges(_rangesText), name);
                default:
                    return DocumentSplitter.Plan(Source.PageCount, SplitMode.Bookmarks(_outline), name);
            }
        }
        catch (FormatException)
        {
            error = Strings.SplitRangesInvalid;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            var size = Format(Source.PageCount == 1 ? Strings.PageCounterSameOne : Strings.PageCounterSame, Number(Source.PageCount));
            var page = ex.ActualValue is int number ? Number(number) : _rangesText.Trim();
            error = Format(Strings.InsertFileRangeOutside, size, page);
        }
        catch (DocumentSplitException ex) when (ex.Error == DocumentSplitError.NoBookmarks)
        {
            error = Strings.SplitNoBookmarks;
        }
        catch (PageEditException)
        {
            error = Strings.PageOutputErrorNotPossible;
        }

        return null;
    }

    // Reads what the new files lose (before anything is written) and what is in the way, then shows the review.
    private async Task CheckAsync(IReadOnlyList<SplitPart> parts)
    {
        State = PageOutputState.Checking;
        var folder = _isExtract ? Source.DocumentFolder : Path.GetFullPath(Folder.Trim());
        var token = Token;
        try
        {
            var result = await Task.Run(
                async () =>
                {
                    var reports = await DocumentSplitter.AnalyzeAsync(Source.Document, parts, token).ConfigureAwait(false);
                    var targets = DocumentSplitter.TargetPaths(parts, folder);
                    var inTheWay = targets.Where(static target => File.Exists(target) || Directory.Exists(target)).ToArray();
                    return new Checked(reports, targets, inTheWay);
                },
                CancellationToken.None);
            if (IsDisposed)
            {
                return;
            }

            if (result.Reports.Any(static report => report.IsRefused))
            {
                Fail(Strings.PageOutputErrorRefused);
                return;
            }

            _parts = parts;
            _allConflicts = [.. result.InTheWay.Select(static path => Path.GetFileName(path))];
            _notes = NotesOf(result.Reports);
            _fileNames = [.. result.Targets.Take(MaxListedFiles).Select(static path => Path.GetFileName(path))];
            _moreFiles = Math.Max(0, result.Targets.Count - MaxListedFiles);
            if (!_isExtract)
            {
                _summary = string.Empty;
            }

            SetConflicts();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(FileNames));
            OnPropertyChanged(nameof(FilesHeading));
            OnPropertyChanged(nameof(MoreFilesText));
            OnPropertyChanged(nameof(Notes));
            OnPropertyChanged(nameof(HasNotes));
            State = PageOutputState.Review;
        }
        catch (Exception ex) when (token.IsCancellationRequested || ex is OperationCanceledException)
        {
            // The card went meanwhile.
        }
        catch (DocumentSplitException ex) when (ex.Error == DocumentSplitError.UnsafeName)
        {
            if (!IsDisposed)
            {
                InputError = Strings.PageOutputErrorUnsafeName;
                State = PageOutputState.Setup;
            }
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
            {
                Fail(Describe(ex));
            }
        }
    }

    private sealed record Checked(IReadOnlyList<PageEditReport> Reports, IReadOnlyList<string> Targets, IReadOnlyList<string> InTheWay);

    private static List<string> NotesOf(IReadOnlyList<PageEditReport> reports)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var more = 0;
        foreach (var loss in reports.SelectMany(static report => report.Losses))
        {
            var line = ApplyPagesInfo.LineOf(loss);
            if (!seen.Add(line))
            {
                continue;
            }

            if (lines.Count < MaxNotes)
            {
                lines.Add(line);
            }
            else
            {
                more++;
            }
        }

        if (more > 0)
        {
            lines.Add(Format(Strings.PageOutputMoreNotes, Number(more)));
        }

        return lines;
    }

    private void SetConflicts()
    {
        _conflicts = ReplaceExisting || _isExtract ? [] : _allConflicts;
        OnPropertyChanged(nameof(Conflicts));
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(ConflictsText));
    }

    private async Task StartAsync()
    {
        if (!CanStart())
        {
            return;
        }

        if (!_isExtract)
        {
            var parts = _parts;
            var folder = Path.GetFullPath(Folder.Trim());
            var overwrite = ReplaceExisting;
            await RunAsync((progress, written, token) =>
                DocumentSplitter.WriteAsync(Source.Engine, Source.Document, Source.File, parts, folder, overwrite, progress, token, written));
            return;
        }

        // An extract goes where the save dialog says; a file that is there was confirmed by the dialog, which asks before it
        // replaces one. The open document itself is never the target (the splitter refuses it).
        _picking = true;
        _startCommand.RaiseCanExecuteChanged();
        string? chosen;
        try
        {
            chosen = await _pickSave($"{Source.Stem}-extract.pdf", Source.DocumentFolder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsDisposed)
            {
                Fail(Strings.PageOutputPickFailed);
            }

            return;
        }
        finally
        {
            _picking = false;
            _startCommand.RaiseCanExecuteChanged();
        }

        if (string.IsNullOrEmpty(chosen) || IsDisposed || !IsReview)
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(chosen);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return;
        }

        var directory = Path.GetDirectoryName(full)!;
        var stem = string.Equals(Path.GetExtension(full), ".pdf", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(full);
        var part = new SplitPart(stem, Source.Selection);
        var overwriteExisting = false;
        try
        {
            // The name is made safe like every generated name; when that changes what the user typed, the file is a different one
            // and the confirmation of the dialog does not cover it.
            var target = DocumentSplitter.TargetPaths([part], directory)[0];
            overwriteExisting = string.Equals(target, full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && File.Exists(full);
        }
        catch (DocumentSplitException)
        {
            Fail(Strings.PageOutputErrorUnsafeName);
            return;
        }

        await RunAsync((progress, written, token) =>
            DocumentSplitter.WriteAsync(Source.Engine, Source.Document, Source.File, [part], directory, overwriteExisting, progress, token, written));
    }
}
