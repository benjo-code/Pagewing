using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Annotations;
using Pagewing.Core.Ocr;
using Pagewing.Core.Pdf;
using Pagewing.Core.Saving;
using Pagewing.Ocr;

namespace Pagewing.App.ViewModels;

/// <summary>What the card of the text recognition shows.</summary>
public enum OcrCardState
{
    /// <summary>The choices: languages, pages, mode, resolution, and what happens.</summary>
    Options,

    /// <summary>A run goes on: progress, and "Cancel".</summary>
    Running,

    /// <summary>A run was stopped and some pages were done: use them or discard (default).</summary>
    Choose,

    /// <summary>The run is applied (an unsaved change); the report.</summary>
    Report,

    /// <summary>The run could not be done or applied.</summary>
    Failed,
}

/// <summary>Which pages a run looks at.</summary>
public enum OcrPageScope
{
    /// <summary>Every page (those with text are skipped by the mode).</summary>
    All,

    /// <summary>The page in view.</summary>
    Current,

    /// <summary>The pages of <see cref="OcrViewModel.RangeText"/>.</summary>
    Range,
}

/// <summary>A language model of the list of the card: its name, where it is from and whether it is chosen.</summary>
public sealed class OcrLanguageItem : ViewModelBase
{
    private bool _isSelected;

    /// <summary>Creates the item.</summary>
    public OcrLanguageItem(string name, bool imported, bool isSelected)
    {
        Name = name;
        Imported = imported;
        _isSelected = isSelected;
    }

    /// <summary>Gets the language name (<c>deu</c>).</summary>
    public string Name { get; }

    /// <summary>Gets whether the user added the file.</summary>
    public bool Imported { get; }

    /// <summary>Gets the text of the list: the name, with "added by you" for an imported file.</summary>
    public string Display => Imported ? $"{Name} ({Strings.OcrLanguageImported})" : Name;

    /// <summary>Gets or sets whether the language is chosen.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A page of the report that the user can jump to.</summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="Label">The text of the link.</param>
public sealed record OcrPageLink(int Page, string Label);

/// <summary>
/// The card of the text recognition (command "Recognize text (OCR)"): the choices, the run, the question after a stop and the report. The run is
/// owned here and ends with the card (the overlay goes, the tab goes, the window closes): it is cancelled and nothing of it is written. A run that
/// ends is applied to the document at once as one unsaved step that can be undone (<see cref="AnnotationEditor.ApplyOcrAsync"/>); a run that was
/// stopped asks first, with "Discard" as the answer that Esc gives. Nothing of it is made before the first use.
/// </summary>
public sealed class OcrViewModel : ViewModelBase, IDisposable
{
    /// <summary>After this many pages the remaining time is told (before that it would be a guess).</summary>
    internal const int PagesBeforeRemaining = 3;

    private static readonly int[] Resolutions = [0, 150, 200, 300, 400];

    private readonly TabViewModel _tab;
    private readonly TessdataStore _store;
    private readonly UserSettings _settings;
    private readonly Func<string, IOcrEngineFactory> _factoryFor;
    private readonly Func<Task<string?>>? _pickLanguageFile;
    private readonly TimeProvider _clock;
    private readonly string _languageDefault;
    private CancellationTokenSource? _run;
    private OcrRunResult? _partial;
    private long _started;
    private OcrCardState _state;
    private OcrPageScope _scope;
    private string _rangeText = string.Empty;
    private string? _rangeError;
    private int _modeIndex;
    private int _resolutionIndex;
    private bool _importPrompt;
    private string? _importMessage;
    private string _progressText = string.Empty;
    private string _remainingText = string.Empty;
    private string _failedText = string.Empty;
    private string _reportSummary = string.Empty;
    private bool _isCancelling;
    private bool _signed;
    private bool _disposed;
    private IReadOnlyList<string> _reportLines = [];
    private IReadOnlyList<string> _skippedRows = [];
    private IReadOnlyList<OcrPageLink> _reportLinks = [];

    /// <summary>Creates the card for a tab.</summary>
    /// <param name="tab">The tab whose document is recognized.</param>
    /// <param name="store">The language files.</param>
    /// <param name="settings">The settings (the last languages are kept).</param>
    /// <param name="factoryFor">Makes the engine factory for a folder of language files.</param>
    /// <param name="pickLanguageFile">Shows the dialog for a language file, or <see langword="null"/> when there is none.</param>
    /// <param name="clock">The clock of the remaining time.</param>
    public OcrViewModel(
        TabViewModel tab,
        TessdataStore store,
        UserSettings settings,
        Func<string, IOcrEngineFactory> factoryFor,
        Func<Task<string?>>? pickLanguageFile,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(tab);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(factoryFor);
        _tab = tab;
        _store = store;
        _settings = settings;
        _factoryFor = factoryFor;
        _pickLanguageFile = pickLanguageFile;
        _clock = clock ?? TimeProvider.System;
        _languageDefault = OcrLanguages.DefaultFor(Strings.Culture.TwoLetterISOLanguageName, [.. store.List().Select(static l => l.Name)]);
        ReloadLanguages();
        StartCommand = new RelayCommand(() => _ = StartAsync(), () => CanStart);
        CancelCommand = new RelayCommand(Cancel);
        CloseCommand = new RelayCommand(RequestClose);
        UseFinishedCommand = new RelayCommand(() => _ = UseFinishedAsync(), () => _state == OcrCardState.Choose);
        DiscardCommand = new RelayCommand(Discard, () => _state == OcrCardState.Choose);
        AddLanguageCommand = new RelayCommand(() => ImportPrompt = true, () => _state == OcrCardState.Options && _pickLanguageFile is not null);
        CancelImportCommand = new RelayCommand(() => ImportPrompt = false);
        ChooseLanguageFileCommand = new RelayCommand(() => _ = ChooseLanguageFileAsync(), () => _importPrompt);
        GoToPageCommand = new RelayCommand<int>(page => _tab.GoToPage(page + 1));
        _ = LoadSignatureNoteAsync();
    }

    /// <summary>Raised when the card asks to go: the window hides the overlay (which disposes the card).</summary>
    public event Action? Closed;

    /// <summary>Gets the language models to choose from; the chosen ones come first, in the order of their priority.</summary>
    public ObservableCollection<OcrLanguageItem> Languages { get; } = [];

    /// <summary>Gets the texts of the choices of the mode, in the order of <see cref="OcrMode"/>.</summary>
    public IReadOnlyList<string> ModeChoices { get; } = [Strings.OcrModeSkip, Strings.OcrModeRedo, Strings.OcrModeForce];

    /// <summary>Gets the texts of the choices of the resolution: "Automatic (recommended)", 150, 200, 300 and 400 dpi.</summary>
    public IReadOnlyList<string> ResolutionChoices { get; } =
        [Strings.OcrResolutionAuto, .. Resolutions.Skip(1).Select(static dpi => Strings.OcrFormat("OcrResolutionDpi", dpi))];

    /// <summary>Gets the state of the card.</summary>
    public OcrCardState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsOptions));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(IsChoose));
                OnPropertyChanged(nameof(IsReport));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(Title));
                RaiseCanExecute();
            }
        }
    }

    /// <summary>Gets whether the choices are shown.</summary>
    public bool IsOptions => _state == OcrCardState.Options;

    /// <summary>Gets whether a run goes on.</summary>
    public bool IsRunning => _state == OcrCardState.Running;

    /// <summary>Gets whether the question after a stop is shown.</summary>
    public bool IsChoose => _state == OcrCardState.Choose;

    /// <summary>Gets whether the report is shown.</summary>
    public bool IsReport => _state == OcrCardState.Report;

    /// <summary>Gets whether the failure is shown.</summary>
    public bool IsFailed => _state == OcrCardState.Failed;

    /// <summary>Gets whether the card holds the job: a run goes on or is being stopped (no other overlay replaces it).</summary>
    public bool IsBusy => _state == OcrCardState.Running;

    /// <summary>Gets the title of the card for its state.</summary>
    public string Title => _state switch
    {
        OcrCardState.Report => Strings.OcrReportTitle,
        OcrCardState.Choose => Strings.OcrChooseTitle,
        OcrCardState.Failed => Strings.OcrFailedTitle,
        _ => Strings.OcrTitle,
    };

    /// <summary>Gets the notice for a signed document (adding the text is a change), or an empty text.</summary>
    public string SignatureNote => _signed ? Strings.OcrSignatureNote : string.Empty;

    /// <summary>Gets whether the notice for a signed document is shown.</summary>
    public bool ShowsSignatureNote => _signed;

    /// <summary>Gets whether no language is installed.</summary>
    public bool HasNoLanguages => Languages.Count == 0;

    /// <summary>Gets whether more than three languages are chosen (slower, less accurate).</summary>
    public bool ShowsSlowWarning => ChosenNames().Count > OcrLanguages.SlowFrom;

    /// <summary>Gets the notice about many languages, or an empty text.</summary>
    public string SlowWarning => ShowsSlowWarning ? Strings.OcrLanguageSlow : string.Empty;

    /// <summary>Gets or sets which pages the run looks at.</summary>
    public OcrPageScope Scope
    {
        get => _scope;
        set
        {
            if (SetProperty(ref _scope, value))
            {
                OnPropertyChanged(nameof(ScopeIndex));
                OnPropertyChanged(nameof(IsRangeScope));
                ValidateRange();
                RaiseCanExecute();
            }
        }
    }

    /// <summary>Gets or sets the scope as an index (all, current page, range) for the radio buttons.</summary>
    public int ScopeIndex
    {
        get => (int)_scope;
        set => Scope = (OcrPageScope)Math.Clamp(value, 0, 2);
    }

    /// <summary>Gets whether the field of the range is used.</summary>
    public bool IsRangeScope => _scope == OcrPageScope.Range;

    /// <summary>Gets or sets the page range (<c>1-3,5</c>).</summary>
    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (SetProperty(ref _rangeText, value ?? string.Empty))
            {
                ValidateRange();
                RaiseCanExecute();
            }
        }
    }

    /// <summary>Gets the complaint about the range, or <see langword="null"/>.</summary>
    public string? RangeError
    {
        get => _rangeError;
        private set => SetProperty(ref _rangeError, value);
    }

    /// <summary>Gets or sets the mode as an index into <see cref="ModeChoices"/>.</summary>
    public int ModeIndex
    {
        get => _modeIndex;
        set => SetProperty(ref _modeIndex, Math.Clamp(value, 0, 2));
    }

    /// <summary>Gets or sets the resolution as an index into <see cref="ResolutionChoices"/>.</summary>
    public int ResolutionIndex
    {
        get => _resolutionIndex;
        set => SetProperty(ref _resolutionIndex, Math.Clamp(value, 0, Resolutions.Length - 1));
    }

    /// <summary>Gets whether the notice before adding a language file is shown.</summary>
    public bool ImportPrompt
    {
        get => _importPrompt;
        private set
        {
            if (SetProperty(ref _importPrompt, value))
            {
                RaiseCanExecute();
            }
        }
    }

    /// <summary>Gets the result of the last attempt to add a language file, or <see langword="null"/>.</summary>
    public string? ImportMessage
    {
        get => _importMessage;
        private set => SetProperty(ref _importMessage, value);
    }

    /// <summary>Gets the progress text of a run ("3 of 12 pages").</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    /// <summary>Gets the remaining time ("About 40 s left"), told after <see cref="PagesBeforeRemaining"/> pages, otherwise empty.</summary>
    public string RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    /// <summary>Gets the share of the run that is done, 0 to 1.</summary>
    public double ProgressFraction { get; private set; }

    /// <summary>Gets whether the run is being stopped (waiting for the pages in flight).</summary>
    public bool IsCancelling
    {
        get => _isCancelling;
        private set
        {
            if (SetProperty(ref _isCancelling, value))
            {
                OnPropertyChanged(nameof(RunningText));
            }
        }
    }

    /// <summary>Gets the text of a run: "Recognizing text …", or "Stopping …".</summary>
    public string RunningText => _isCancelling ? Strings.OcrCancelling : Strings.OcrRunning;

    /// <summary>Gets the question after a stop ("5 pages were recognized before you stopped. Use them?").</summary>
    public string ChooseText => Strings.OcrFormat("OcrChooseText", _partial?.Pages.Count ?? 0);

    /// <summary>Gets what the failure says.</summary>
    public string FailedText
    {
        get => _failedText;
        private set => SetProperty(ref _failedText, value);
    }

    /// <summary>Gets the summary line of the report.</summary>
    public string ReportSummary
    {
        get => _reportSummary;
        private set => SetProperty(ref _reportSummary, value);
    }

    /// <summary>Gets the lines of the report after the summary (what needs a second look), each a sentence.</summary>
    public IReadOnlyList<string> ReportLines
    {
        get => _reportLines;
        private set => SetProperty(ref _reportLines, value);
    }

    /// <summary>Gets one row for each page that was not recognized: the page and the reason.</summary>
    public IReadOnlyList<string> SkippedRows
    {
        get => _skippedRows;
        private set
        {
            if (SetProperty(ref _skippedRows, value))
            {
                OnPropertyChanged(nameof(HasSkipped));
            }
        }
    }

    /// <summary>Gets whether some page was not recognized.</summary>
    public bool HasSkipped => _skippedRows.Count > 0;

    /// <summary>Gets the pages to check, as links to jump to.</summary>
    public IReadOnlyList<OcrPageLink> ReportLinks
    {
        get => _reportLinks;
        private set => SetProperty(ref _reportLinks, value);
    }

    /// <summary>The command "Start".</summary>
    public ICommand StartCommand { get; }

    /// <summary>The command "Cancel": stops a run; otherwise closes the card.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>The command "Close".</summary>
    public ICommand CloseCommand { get; }

    /// <summary>The command "Use finished pages" after a stop.</summary>
    public ICommand UseFinishedCommand { get; }

    /// <summary>The command "Discard" after a stop.</summary>
    public ICommand DiscardCommand { get; }

    /// <summary>The command "Add language file …".</summary>
    public ICommand AddLanguageCommand { get; }

    /// <summary>The command that goes back from the notice about a language file.</summary>
    public ICommand CancelImportCommand { get; }

    /// <summary>The command "Choose file …" (after the notice).</summary>
    public ICommand ChooseLanguageFileCommand { get; }

    /// <summary>The command that jumps to a page of the report (parameter: the 0-based page).</summary>
    public ICommand GoToPageCommand { get; }

    /// <summary>Gets whether "Start" may run: a language is chosen, the range is valid and the tab allows it.</summary>
    public bool CanStart => _state == OcrCardState.Options && ChosenNames().Count > 0 && _rangeError is null && _tab.CanRunOcr && !_disposed;

    /// <summary>Esc: stops a run, answers the question after a stop with "Discard", otherwise closes the card.</summary>
    /// <returns>Whether the card handled the key.</returns>
    public bool HandleEscape()
    {
        switch (_state)
        {
            case OcrCardState.Running:
                Cancel();
                break;
            case OcrCardState.Choose:
                Discard();
                break;
            default:
                if (_importPrompt)
                {
                    ImportPrompt = false;
                }
                else
                {
                    RequestClose();
                }

                break;
        }

        return true;
    }

    /// <summary>Ends the card: a run is cancelled and nothing of it is written.</summary>
    public void Dispose()
    {
        _disposed = true;
        _run?.Cancel();
    }

    private IReadOnlyList<string> ChosenNames() => [.. Languages.Where(static l => l.IsSelected).Select(static l => l.Name)];

    private void ReloadLanguages()
    {
        foreach (var language in Languages)
        {
            language.PropertyChanged -= OnLanguageChanged;
        }

        Languages.Clear();
        var remembered = OcrLanguages.Normalize(_settings.Current.OcrLanguages) ?? OcrLanguages.Normalize(_languageDefault);
        var chosen = remembered is null ? [] : OcrLanguages.Split(remembered);
        var installed = _store.List();
        var ordered = chosen.Where(name => installed.Any(l => l.Name == name))
            .Concat(installed.Select(static l => l.Name).Where(name => !chosen.Contains(name)))
            .Distinct();
        foreach (var name in ordered)
        {
            var language = installed.First(l => l.Name == name);
            var item = new OcrLanguageItem(name, language.Origin == TessdataOrigin.Imported, chosen.Contains(name));
            item.PropertyChanged += OnLanguageChanged;
            Languages.Add(item);
        }

        OnPropertyChanged(nameof(HasNoLanguages));
        OnPropertyChanged(nameof(ShowsSlowWarning));
        OnPropertyChanged(nameof(SlowWarning));
        RaiseCanExecute();
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OcrLanguageItem.IsSelected))
        {
            OnPropertyChanged(nameof(ShowsSlowWarning));
            OnPropertyChanged(nameof(SlowWarning));
            RaiseCanExecute();
        }
    }

    private void ValidateRange()
    {
        if (_scope != OcrPageScope.Range)
        {
            RangeError = null;
            return;
        }

        try
        {
            PageRange.Parse(_rangeText, _tab.PageCount);
            RangeError = null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            RangeError = Strings.OcrRangeError;
        }
    }

    private void RaiseCanExecute()
    {
        (StartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (UseFinishedCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (DiscardCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AddLanguageCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ChooseLanguageFileCommand as RelayCommand)?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanStart));
    }

    private async Task LoadSignatureNoteAsync()
    {
        try
        {
            if (_tab.Document is { } document)
            {
                var info = await document.GetSignatureInfoAsync().ConfigureAwait(true);
                _signed = SignaturePolicy.Evaluate(info) == SignatureAdvice.WouldInvalidate;
                OnPropertyChanged(nameof(SignatureNote));
                OnPropertyChanged(nameof(ShowsSignatureNote));
            }
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or InvalidDataException)
        {
            // Without the answer the notice is not shown; saving asks again.
        }
    }

    private void RequestClose() => Closed?.Invoke();

    private void Cancel()
    {
        if (_state == OcrCardState.Running)
        {
            IsCancelling = true;
            _run?.Cancel();
        }
        else if (_state == OcrCardState.Choose)
        {
            Discard();
        }
        else
        {
            RequestClose();
        }
    }

    private void Discard()
    {
        _partial = null;
        RequestClose();
    }

    private async Task ChooseLanguageFileAsync()
    {
        if (_pickLanguageFile is null)
        {
            return;
        }

        ImportPrompt = false;
        var path = await _pickLanguageFile().ConfigureAwait(true);
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var result = _store.Import(path);
        ImportMessage = result.Error switch
        {
            TessdataImportError.None => Strings.OcrFormat("OcrLanguageAdded", result.Language!),
            TessdataImportError.InvalidName => Strings.OcrFormat("OcrLanguageAddFailed", Strings.OcrImportInvalidName),
            TessdataImportError.NotAFile => Strings.OcrFormat("OcrLanguageAddFailed", Strings.OcrImportNotAFile),
            TessdataImportError.TooLarge => Strings.OcrFormat("OcrLanguageAddFailed", Strings.OcrImportTooLarge),
            TessdataImportError.Empty => Strings.OcrFormat("OcrLanguageAddFailed", Strings.OcrImportEmpty),
            TessdataImportError.AlreadyImported => Strings.OcrFormat("OcrLanguageAddFailed", Strings.OcrImportAlready),
            _ => Strings.OcrFormat("OcrLanguageAddFailed", Strings.OcrImportFailed),
        };
        if (result.Error == TessdataImportError.None)
        {
            ReloadLanguages();
            if (Languages.FirstOrDefault(l => l.Name == result.Language) is { } added)
            {
                added.IsSelected = true;
            }
        }
    }

    private OcrOptions BuildOptions(PdfDocument document)
    {
        IReadOnlySet<int>? pages = _scope switch
        {
            OcrPageScope.Current => new HashSet<int> { Math.Clamp(_tab.CurrentPage - 1, 0, document.PageCount - 1) },
            OcrPageScope.Range => PageRange.Parse(_rangeText, document.PageCount).ToHashSet(),
            _ => null,
        };
        return new OcrOptions(OcrLanguages.Normalize(string.Join('+', ChosenNames()))!, (OcrMode)_modeIndex, Resolutions[_resolutionIndex], pages);
    }

    private async Task StartAsync()
    {
        var document = _tab.Document;
        if (!CanStart || document is null)
        {
            return;
        }

        OcrOptions options;
        try
        {
            options = BuildOptions(document);
        }
        catch (Exception e) when (e is FormatException or ArgumentException or ArgumentOutOfRangeException)
        {
            RangeError = Strings.OcrRangeError;
            return;
        }

        // The choice of the languages is the next start's default.
        _settings.Update(settings => settings with { OcrLanguages = options.Languages });
        var replace = options.Mode != OcrMode.Skip;
        _run = new CancellationTokenSource();
        _started = _clock.GetTimestamp();
        ProgressFraction = 0;
        ProgressText = Strings.OcrFormat("OcrProgress", 0, 0);
        RemainingText = string.Empty;
        State = OcrCardState.Running;
        _tab.IsOcrRunning = true;
        try
        {
            var folder = _store.ResolveFolder(OcrLanguages.Split(options.Languages));
            var progress = new Progress<OcrProgress>(OnProgress);
            var result = await OcrRunner.RunAsync(document, options, _factoryFor(folder), progress, _run.Token).ConfigureAwait(true);
            await ApplyAsync(document, result, replace).ConfigureAwait(true);
        }
        catch (OcrRunCanceledException canceled)
        {
            _partial = canceled.Partial;
            if (!_disposed && canceled.Partial.Pages.Count > 0)
            {
                IsCancelling = false;
                OnPropertyChanged(nameof(ChooseText));
                State = OcrCardState.Choose;
            }
            else
            {
                _partial = null;
                RequestClose();
            }
        }
        catch (OperationCanceledException)
        {
            RequestClose();
        }
        catch (OcrEngineException e)
        {
            Fail(e.Error is OcrEngineError.LanguageMissing or OcrEngineError.DataCorrupt or OcrEngineError.LanguageInvalid
                ? Strings.OcrFormat("OcrFailedLanguage", e.InstalledLanguages.Count > 0 ? string.Join(", ", e.InstalledLanguages) : options.Languages)
                : e.Error is OcrEngineError.LibraryMissing or OcrEngineError.LibraryUnusable ? Strings.OcrFailedLibrary
                : Strings.OcrFormat("OcrFailedGeneric", e.Message));
        }
        catch (TessdataMissingException e)
        {
            Fail(Strings.OcrFormat("OcrFailedLanguage", string.Join(", ", e.Missing)));
        }
        catch (AnnotationHistoryFullException)
        {
            Fail(Strings.OcrFailedFull);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException or ObjectDisposedException or ArgumentException or InvalidDataException)
        {
            Fail(Strings.OcrFormat("OcrFailedGeneric", e.Message));
        }
        finally
        {
            _tab.IsOcrRunning = false;
            _run?.Dispose();
            _run = null;
            IsCancelling = false;
        }
    }

    private void Fail(string text)
    {
        if (_disposed)
        {
            return;
        }

        FailedText = text;
        State = OcrCardState.Failed;
    }

    private void OnProgress(OcrProgress progress)
    {
        if (_state != OcrCardState.Running)
        {
            return;
        }

        ProgressFraction = progress.PagesTotal == 0 ? 0 : progress.PagesDone / (double)progress.PagesTotal;
        OnPropertyChanged(nameof(ProgressFraction));
        ProgressText = Strings.OcrFormat("OcrProgress", progress.PagesDone, progress.PagesTotal);
        if (progress.PagesDone >= PagesBeforeRemaining && progress.PagesDone < progress.PagesTotal)
        {
            var elapsed = _clock.GetElapsedTime(_started);
            var left = TimeSpan.FromTicks((long)(elapsed.Ticks / (double)progress.PagesDone * (progress.PagesTotal - progress.PagesDone)));
            RemainingText = Strings.OcrFormat("OcrRemaining", left.TotalSeconds < 90
                ? Strings.OcrFormat("OcrRemainingSeconds", Math.Max(5, (int)(Math.Round(left.TotalSeconds / 5) * 5)))
                : Strings.OcrFormat("OcrRemainingMinutes", (int)Math.Ceiling(left.TotalMinutes)));
        }
        else
        {
            RemainingText = string.Empty;
        }

        _tab.OcrProgressText = Strings.OcrFormat("OcrStatusRunning", progress.PagesDone, progress.PagesTotal);
    }

    private async Task UseFinishedAsync()
    {
        if (_partial is not { } partial || _tab.Document is not { } document)
        {
            RequestClose();
            return;
        }

        _partial = null;
        try
        {
            await ApplyAsync(document, partial, replace: (OcrMode)_modeIndex != OcrMode.Skip).ConfigureAwait(true);
        }
        catch (AnnotationHistoryFullException)
        {
            Fail(Strings.OcrFailedFull);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or ArgumentException)
        {
            Fail(Strings.OcrFormat("OcrFailedGeneric", e.Message));
        }
    }

    // The result goes into the document of the tab as one unsaved step (nothing if the document is another one now), then the report.
    private async Task ApplyAsync(PdfDocument document, OcrRunResult result, bool replace)
    {
        if (_disposed || !ReferenceEquals(_tab.Document, document))
        {
            RequestClose();
            return;
        }

        var applied = await _tab.Editor.ApplyOcrAsync(result, replace).ConfigureAwait(true);
        var report = result.Report with { WordsLeftOut = applied?.WordsLeftOut ?? 0 };
        BuildReport(report, applied);
        State = OcrCardState.Report;
    }

    private void BuildReport(OcrReport report, OcrApplyResult? applied)
    {
        ReportSummary = Strings.OcrFormat("OcrReportSummary", report.PagesRecognized, report.Words, Math.Round(report.MeanConfidence));
        var lines = new List<string>();
        if (applied is null)
        {
            lines.Add(Strings.OcrReportNothing);
        }

        if (report.LowConfidencePages.Count > 0)
        {
            lines.Add(Strings.OcrFormat("OcrReportLow", PageList(report.LowConfidencePages)));
        }

        if (report.PagesWithoutWords.Count > 0)
        {
            lines.Add(Strings.OcrFormat("OcrReportNoWords", PageList(report.PagesWithoutWords)));
        }

        if (report.LittleTextPages.Count > 0)
        {
            lines.Add(Strings.OcrFormat("OcrReportLittle", PageList(report.LittleTextPages)));
        }

        if (report.LoweredPages.Count > 0)
        {
            lines.Add(Strings.OcrFormat("OcrReportLowered", PageList(report.LoweredPages)));
        }

        if (applied is { AlreadyRecognized.Count: > 0 })
        {
            lines.Add(Strings.OcrFormat("OcrReportAlready", PageList(applied.AlreadyRecognized)));
        }

        if (report.WordsLeftOut > 0)
        {
            lines.Add(Strings.OcrFormat("OcrReportLeftOut", report.WordsLeftOut));
        }

        if (applied is { Undoable: false })
        {
            lines.Add(Strings.OcrReportNotUndoable);
        }

        ReportLines = lines;
        SkippedRows = [.. report.Skipped.Select(skipped => Strings.OcrFormat("OcrReportSkippedRow", skipped.Page + 1, ReasonOf(skipped.Reason)))];
        ReportLinks = [.. report.LowConfidencePages.Take(12).Select(page => new OcrPageLink(page, Strings.OcrFormat("OcrReportGoTo", page + 1)))];
    }

    private static string PageList(IEnumerable<int> pages) => string.Join(", ", pages.Select(static p => (p + 1).ToString(CultureInfo.CurrentCulture)));

    private static string ReasonOf(OcrSkipReason reason) => reason switch
    {
        OcrSkipReason.HasText => Strings.OcrReasonHasText,
        OcrSkipReason.AlreadyRecognized => Strings.OcrReasonAlready,
        OcrSkipReason.TooLarge => Strings.OcrReasonTooLarge,
        OcrSkipReason.ResolutionTooLow => Strings.OcrReasonResolution,
        OcrSkipReason.Timeout => Strings.OcrReasonTimeout,
        OcrSkipReason.Failed => Strings.OcrReasonFailed,
        OcrSkipReason.NoSupportedCharacters => Strings.OcrReasonNoChars,
        _ => Strings.OcrReasonFailed,
    };
}
