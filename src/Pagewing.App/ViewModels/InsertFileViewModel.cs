using System.Globalization;
using System.Text;
using System.Windows.Input;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;
using Pagewing.Core.Pdf;

namespace Pagewing.App.ViewModels;

/// <summary>Where the dialog "Insert pages from a file" is with the file the user picked.</summary>
public enum InsertFileState
{
    /// <summary>The file is being opened.</summary>
    Opening,

    /// <summary>The file is password-protected: the dialog asks for the password (also after a wrong one).</summary>
    NeedsPassword,

    /// <summary>The file is open: the user chooses the pages.</summary>
    Ready,

    /// <summary>The file cannot be inserted (not a PDF, damaged, gone, unreadable); <see cref="InsertFileViewModel.ErrorText"/> says why.</summary>
    Failed,
}

/// <summary>
/// The dialog "Insert pages from a file" (milestone 13, spec 5.3): the file the user picked, its password if it needs one, the
/// pages to take (the syntax of printing, <see cref="PageRange"/>; default all) and where they go (after the selection of the
/// list, else at the end). It opens the file itself, so a wrong password or a file that is no PDF is told in the dialog and adds
/// nothing. The file stays open for the dialog; <see cref="Dispose"/> closes it unless it went into the plan of the tab.
/// Merging two PDFs is inserting all pages of one into the other. UI thread only.
/// </summary>
public sealed class InsertFileViewModel : ViewModelBase, IDisposable
{
    private readonly string _path;
    private readonly PdfEngine _engine;
    private readonly Func<InsertedSource, IReadOnlyList<int>, string?> _insert;
    private readonly Func<string> _position;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly RelayCommand _insertCommand;
    private readonly RelayCommand _submitPasswordCommand;
    private InsertFileState _state = InsertFileState.Opening;
    private InsertedSource? _source;
    private string _rangeText = "all";
    private string? _rangeError;
    private string _password = string.Empty;
    private bool _isPasswordIncorrect;
    private string? _errorText;
    private string _positionText;
    private int _pageCount;
    private bool _disposed;
    private bool _opening;

    /// <summary>Creates the dialog for the file at <paramref name="path"/>; <see cref="OpenAsync"/> opens it.</summary>
    /// <param name="path">The full path of the file the user picked.</param>
    /// <param name="engine">Opens the file.</param>
    /// <param name="insert">Puts the pages into the plan; returns <see langword="null"/> when it worked, else the reason it was refused. Owns the source from then on.</param>
    /// <param name="position">Tells where the pages would go, as words, read each time the selection changes.</param>
    internal InsertFileViewModel(string path, PdfEngine engine, Func<InsertedSource, IReadOnlyList<int>, string?> insert, Func<string> position)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(insert);
        ArgumentNullException.ThrowIfNull(position);
        _path = path;
        _engine = engine;
        _insert = insert;
        _position = position;
        _positionText = position();
        _insertCommand = new RelayCommand(Insert, () => _state == InsertFileState.Ready);
        _submitPasswordCommand = new RelayCommand(() => _ = OpenAsync(_password), () => _state == InsertFileState.NeedsPassword && !_opening);
        ChooseOtherCommand = new RelayCommand(() => ChooseOtherRequested?.Invoke());
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke());
    }

    /// <summary>Raised when the user closes the dialog without inserting.</summary>
    internal event Action? Cancelled;

    /// <summary>Raised when the user wants another file.</summary>
    internal event Action? ChooseOtherRequested;

    /// <summary>Raised after the pages went into the plan.</summary>
    internal event Action? Inserted;

    /// <summary>Gets where the dialog is.</summary>
    public InsertFileState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsOpening));
                OnPropertyChanged(nameof(NeedsPassword));
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(IsFailed));
                _insertCommand.RaiseCanExecuteChanged();
                _submitPasswordCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether the file is being opened.</summary>
    public bool IsOpening => _state == InsertFileState.Opening;

    /// <summary>Gets whether the dialog asks for the password.</summary>
    public bool NeedsPassword => _state == InsertFileState.NeedsPassword;

    /// <summary>Gets whether the user chooses pages.</summary>
    public bool IsReady => _state == InsertFileState.Ready;

    /// <summary>Gets whether the file cannot be inserted.</summary>
    public bool IsFailed => _state == InsertFileState.Failed;

    /// <summary>Gets the name of the picked file without its folder.</summary>
    public string FileName => Path.GetFileName(_path);

    /// <summary>Gets the number of pages of the file once it is open, else 0.</summary>
    public int PageCount
    {
        get => _pageCount;
        private set
        {
            if (SetProperty(ref _pageCount, value))
            {
                OnPropertyChanged(nameof(PageCountText));
            }
        }
    }

    /// <summary>Gets the number of pages as words ("6 pages").</summary>
    public string PageCountText => string.Format(
        Strings.Culture,
        CompositeFormat.Parse(_pageCount == 1 ? Strings.PageCounterSameOne : Strings.PageCounterSame),
        _pageCount.ToString("N0", Strings.Culture));

    /// <summary>Gets or sets the pages to take, in the syntax of printing (<c>1-3,5</c>, <c>all</c>); starts as <c>all</c>.</summary>
    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (SetProperty(ref _rangeText, value ?? string.Empty))
            {
                // A new text is a new try: the old complaint is about the old one.
                RangeError = null;
            }
        }
    }

    /// <summary>Gets why <see cref="RangeText"/> was refused, or <see langword="null"/>.</summary>
    public string? RangeError
    {
        get => _rangeError;
        private set => SetProperty(ref _rangeError, value);
    }

    /// <summary>Gets or sets the password typed for the file.</summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value ?? string.Empty);
    }

    /// <summary>Gets whether the last password was wrong.</summary>
    public bool IsPasswordIncorrect
    {
        get => _isPasswordIncorrect;
        private set => SetProperty(ref _isPasswordIncorrect, value);
    }

    /// <summary>Gets the sentence that asks for the password, naming the file.</summary>
    public string PasswordPrompt => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.InsertFilePasswordPrompt), FileName);

    /// <summary>Gets why the file cannot be inserted, in the sentence of the card of a file that cannot be opened ("“x.pdf” was not found."), or <see langword="null"/>.</summary>
    public string? ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    /// <summary>Gets where the pages go, as words ("after page 3", "at the end").</summary>
    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    /// <summary>Gets the open file while the dialog holds it, or <see langword="null"/>.</summary>
    internal InsertedSource? Source => _source;

    /// <summary>Inserts the pages of <see cref="RangeText"/>; a mistake in the text stays in the dialog (<see cref="RangeError"/>).</summary>
    public ICommand InsertCommand => _insertCommand;

    /// <summary>Opens the file again with <see cref="Password"/> (Enter in the password field).</summary>
    public ICommand SubmitPasswordCommand => _submitPasswordCommand;

    /// <summary>Asks for another file (the window shows its file dialog; the old file is closed).</summary>
    public ICommand ChooseOtherCommand { get; }

    /// <summary>Closes the dialog (Esc).</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Makes the position text again from the selection of the list.</summary>
    internal void RefreshPosition() => PositionText = _position();

    /// <summary>Opens the file; the state tells the outcome (never throws for a file that cannot be opened).</summary>
    /// <param name="password">The password to try, or <see langword="null"/>.</param>
    internal async Task OpenAsync(string? password = null)
    {
        if (_disposed || _opening)
        {
            return;
        }

        _opening = true;
        State = InsertFileState.Opening;
        IsPasswordIncorrect = false;
        _submitPasswordCommand.RaiseCanExecuteChanged();
        try
        {
            var document = await _engine.OpenAsync(_path, string.IsNullOrEmpty(password) ? null : password, _cancellation.Token);
            if (_disposed)
            {
                await document.DisposeAsync();
                return;
            }

            // The stamp is taken from the open file: the apply checks the file against it.
            _source = new InsertedSource(document, PageSourceFile.Of(_path, string.IsNullOrEmpty(password) ? null : password, document));
            PageCount = document.PageCount;
            await _source.ReadSizesAsync(firstBatchOnly: true);
            if (_disposed)
            {
                return;
            }

            RefreshPosition();
            State = InsertFileState.Ready;
        }
        catch (PdfOpenException ex) when (ex.Error is PdfOpenError.PasswordRequired or PdfOpenError.PasswordIncorrect)
        {
            IsPasswordIncorrect = ex.Error == PdfOpenError.PasswordIncorrect;
            State = InsertFileState.NeedsPassword;
        }
        catch (PdfOpenException ex)
        {
            // The sentence of the card of a file that cannot be opened: "“x.pdf” is damaged or not a PDF file."
            ErrorText = string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ErrorCause), FileName, TabViewModel.CauseOf(ex.Error));
            State = InsertFileState.Failed;
        }
        catch (OperationCanceledException)
        {
            // The dialog was closed meanwhile.
        }
        catch (ObjectDisposedException)
        {
            // The window went meanwhile.
        }
        finally
        {
            _opening = false;
            _submitPasswordCommand.RaiseCanExecuteChanged();
        }
    }

    private void Insert()
    {
        if (_state != InsertFileState.Ready || _source is not { } source)
        {
            return;
        }

        // Bounds are checked against the real page count before anything is allocated (PageRange).
        IReadOnlyList<int> pages;
        try
        {
            pages = PageRange.Parse(_rangeText, _pageCount);
        }
        catch (FormatException)
        {
            RangeError = Strings.InsertFileRangeInvalid;
            return;
        }
        catch (ArgumentOutOfRangeException ex)
        {
            var size = PageCountText;
            var page = ex.ActualValue is int number ? number.ToString("N0", Strings.Culture) : _rangeText.Trim();
            RangeError = string.Format(Strings.Culture, CompositeFormat.Parse(Strings.InsertFileRangeOutside), size, page);
            return;
        }

        if (_insert(source, pages) is { } refused)
        {
            // The plan refused (it would hold too many pages): the dialog stays with the reason and the file.
            RangeError = refused;
            return;
        }

        // The tab owns the file now; the dialog lets go of it without closing it.
        _source = null;
        Inserted?.Invoke();
    }

    /// <summary>Stops opening and closes the file unless it went into the plan.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _source?.Dispose();
        _source = null;
        _cancellation.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Insert {FileName} ({_state})");
}
