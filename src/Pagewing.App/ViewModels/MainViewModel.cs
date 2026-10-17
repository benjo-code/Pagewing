using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The state of the main window: one document per window (Milestone 3), so every open replaces the current
/// document. Mirrors the <see cref="ViewerSession"/>, whose <see cref="ViewerSession.Changed"/> may come from any
/// thread, on the UI thread. Everything except <see cref="OnExternalOpen"/> is used on the UI thread.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly ViewerSession _session;
    private readonly IFilePicker _picker;
    private readonly RelayCommand _closeCommand;
    private readonly RelayCommand _zoomInCommand;
    private readonly RelayCommand _zoomOutCommand;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private PdfDocument? _document;
    private ViewerState _state;
    private string? _fullPath;
    private PdfOpenError? _error;
    private string _password = string.Empty;
    private bool _checkingPassword;
    private bool _picking;
    private int _pageCount;
    private int _currentPage;
    private string _pageFieldText = "0";
    private double _zoom = ZoomSteps.Default;
    private bool _isWindowsTitleBar = OperatingSystem.IsWindows();

    public MainViewModel(ViewerSession session, IFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(picker);
        _session = session;
        _picker = picker;

        OpenCommand = new RelayCommand(() => _ = PickAndOpenAsync());
        _closeCommand = new RelayCommand(Close, () => State != ViewerState.Empty);
        CloseCommand = _closeCommand;
        SubmitPasswordCommand = new RelayCommand(SubmitPassword);
        CancelPasswordCommand = new RelayCommand(Close);

        _zoomInCommand = new RelayCommand(() => Zoom = ZoomSteps.Next(Zoom), () => IsReady && Zoom < ZoomSteps.Max);
        ZoomInCommand = _zoomInCommand;
        _zoomOutCommand = new RelayCommand(() => Zoom = ZoomSteps.Previous(Zoom), () => IsReady && Zoom > ZoomSteps.Min);
        ZoomOutCommand = _zoomOutCommand;
        _previousPageCommand = new RelayCommand(() => GoToPage(CurrentPage - 1), () => IsReady && CurrentPage > 1);
        PreviousPageCommand = _previousPageCommand;
        _nextPageCommand = new RelayCommand(() => GoToPage(CurrentPage + 1), () => IsReady && CurrentPage < PageCount);
        NextPageCommand = _nextPageCommand;
        CommitPageFieldCommand = new RelayCommand(CommitPageField);

        _session.Changed += OnSessionChanged;
        Refresh();
    }

    /// <summary>Raised on the UI thread when the window should come to the front (a later launch handed over).</summary>
    public event Action? ActivationRequested;

    /// <summary>Raised on the UI thread when a password check ended with a wrong password (also a repeated one).</summary>
    public event Action? PasswordRejected;

    /// <summary>
    /// Raised when the document view should show a page (1-based) with its top edge at the margin: the page field,
    /// the arrows of the status bar and Page Up/Page Down.
    /// </summary>
    public event Action<int>? PageRequested;

    /// <summary>Gets the session the window mirrors; the document view draws its document.</summary>
    internal ViewerSession Session => _session;

    /// <summary>Gets the state of the session, as last seen on the UI thread.</summary>
    public ViewerState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasDocument));
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(IsFailed));
                _closeCommand.RaiseCanExecuteChanged();
                RaiseNavigationCanExecuteChanged();
            }

            // Also when the state stays the same: the password check depends on more than the state.
            OnPropertyChanged(nameof(IsPasswordPrompt));
            OnPropertyChanged(nameof(HasStatusBarRow));
            OnPropertyChanged(nameof(IsPasswordIncorrect));
            OnPropertyChanged(nameof(IsDocumentIcon));
        }
    }

    /// <summary>Gets whether no file is requested: the window shows the start screen and no tab.</summary>
    public bool IsEmpty => State == ViewerState.Empty;

    /// <summary>Gets whether a file is requested (loading, open, password prompt or failure): the window shows its tab.</summary>
    public bool HasDocument => State != ViewerState.Empty;

    /// <summary>Gets whether the document is open: the status bar shows page and zoom.</summary>
    public bool IsReady => State == ViewerState.Ready;

    /// <summary>
    /// Gets whether the row of the status bar is there: while a file loads (so the viewport keeps its height when the
    /// document is ready) and once it is open; not under the password card or the error card.
    /// </summary>
    public bool HasStatusBarRow => State is ViewerState.Loading or ViewerState.Ready && !IsPasswordPrompt;

    /// <summary>
    /// Gets whether the document area shows the password card: the file needs a password, or the entered one is
    /// being checked (the card and the focus in its field stay while the file is opened again).
    /// </summary>
    public bool IsPasswordPrompt =>
        State is ViewerState.PasswordRequired or ViewerState.PasswordIncorrect
        || (State == ViewerState.Loading && _checkingPassword);

    /// <summary>Gets whether the last password was wrong: the field gets a danger border and the message shows.</summary>
    public bool IsPasswordIncorrect => State == ViewerState.PasswordIncorrect;

    /// <summary>Gets whether the document area shows the error card.</summary>
    public bool IsFailed => State == ViewerState.Failed;

    /// <summary>Gets whether the tab shows the document symbol, not the lock or the warning triangle (design system 7.2).</summary>
    public bool IsDocumentIcon => !IsPasswordPrompt && !IsFailed;

    /// <summary>Gets or sets the password typed into the password card. Cleared as soon as it is no longer needed.</summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value ?? string.Empty);
    }

    /// <summary>Gets the text of the password card: "Enter the password to open “file name”."</summary>
    public string PasswordPromptText =>
        string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PasswordPrompt), FileName);

    /// <summary>Gets why the last open failed (only in <see cref="ViewerState.Failed"/>), or <see langword="null"/>.</summary>
    public PdfOpenError? Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(ErrorCauseText));
                OnPropertyChanged(nameof(ErrorDetailsText));
            }
        }
    }

    /// <summary>Gets the cause on the error card in everyday words, for example "“file name” was not found."</summary>
    public string ErrorCauseText => Error is { } error
        ? string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ErrorCause), FileName, CauseOf(error))
        : string.Empty;

    /// <summary>Gets the technical details of the error card: full path and error kind.</summary>
    public string ErrorDetailsText => Error is { } error
        ? $"{Strings.ErrorDetailsPath}: {FullPath}\n{Strings.ErrorDetailsKind}: {error}"
        : string.Empty;

    /// <summary>Gets the file name of the requested file, or <see langword="null"/>.</summary>
    public string? FileName => FullPath is { } path ? Path.GetFileName(path) : null;

    /// <summary>Gets the full path of the requested file, or <see langword="null"/>.</summary>
    public string? FullPath
    {
        get => _fullPath;
        private set
        {
            if (SetProperty(ref _fullPath, value))
            {
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(WindowTitle));
                OnPropertyChanged(nameof(PasswordPromptText));
                OnPropertyChanged(nameof(ErrorCauseText));
                OnPropertyChanged(nameof(ErrorDetailsText));
            }
        }
    }

    /// <summary>Gets the window title: "file name – Pagewing", or the app name without a file.</summary>
    public string WindowTitle => FileName is { } name ? $"{name} – {Strings.AppName}" : Strings.AppName;

    /// <summary>Gets the number of pages of the open document (0 when none is open).</summary>
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

    /// <summary>Gets the page count as shown after the page field ("of 24").</summary>
    public string PageCountText => string.Format(Strings.Culture, CompositeFormat.Parse(Strings.PageOf), PageCount);

    /// <summary>
    /// Gets or sets the current page, 1-based (0 when no document is open). The document view sets it to the page
    /// that fills most of the viewport; setting it does not scroll (see <see cref="GoToPage"/>).
    /// </summary>
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                RaiseNavigationCanExecuteChanged();
                ResetPageField();
            }
        }
    }

    /// <summary>
    /// Gets or sets the text of the page field. Typing changes only the text; Enter (<see cref="CommitPageFieldCommand"/>)
    /// jumps, and anything that is not a page of the document puts the current page back without a message (design
    /// system 7.5).
    /// </summary>
    public string PageFieldText
    {
        get => _pageFieldText;
        set => SetProperty(ref _pageFieldText, value ?? string.Empty);
    }

    /// <summary>Gets or sets the zoom (1 = 100 %), limited to <see cref="ZoomSteps.Min"/> to <see cref="ZoomSteps.Max"/>.</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetProperty(ref _zoom, ZoomSteps.Clamp(value)))
            {
                OnPropertyChanged(nameof(ZoomText));
                _zoomInCommand.RaiseCanExecuteChanged();
                _zoomOutCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets the zoom as shown in the status bar ("100 %").</summary>
    public string ZoomText => Zoom.ToString("P0", Strings.Culture);

    /// <summary>
    /// Gets or sets whether the window draws its own title bar with the tab in it (Windows) instead of keeping the
    /// system title bar with a tab row below it (Linux, design system section 2.2). Settable for tests.
    /// </summary>
    public bool IsWindowsTitleBar
    {
        get => _isWindowsTitleBar;
        set => SetProperty(ref _isWindowsTitleBar, value);
    }

    /// <summary>Shows the file dialog and opens the chosen file (Ctrl+O, "Choose file", "Open another file").</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Closes the document and returns to the start screen (Ctrl+W, × on the tab, "Close tab").</summary>
    public ICommand CloseCommand { get; }

    /// <summary>Opens the file again with <see cref="Password"/> ("Open" and Enter on the password card).</summary>
    public ICommand SubmitPasswordCommand { get; }

    /// <summary>"Cancel" on the password card: closes the tab.</summary>
    public ICommand CancelPasswordCommand { get; }

    /// <summary>The next zoom step ("+" in the status bar, Ctrl+Plus).</summary>
    public ICommand ZoomInCommand { get; }

    /// <summary>The previous zoom step ("−" in the status bar, Ctrl+Minus).</summary>
    public ICommand ZoomOutCommand { get; }

    /// <summary>The page before the current one (‹ in the status bar, Page Up).</summary>
    public ICommand PreviousPageCommand { get; }

    /// <summary>The page after the current one (› in the status bar, Page Down).</summary>
    public ICommand NextPageCommand { get; }

    /// <summary>Enter in the page field: jumps to the typed page or resets the field.</summary>
    public ICommand CommitPageFieldCommand { get; }

    /// <summary>Opens a file and replaces the current document (one document per window).</summary>
    /// <param name="path">Path of the file; an empty path does nothing.</param>
    /// <returns>Completes when the session has a result (see <see cref="ViewerSession.OpenAsync"/>).</returns>
    public Task OpenAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Task.CompletedTask;
        }

        _checkingPassword = false;
        Password = string.Empty;
        return _session.OpenAsync(path);
    }

    /// <summary>Asks the document view to show <paramref name="page"/> (1-based); a page outside the document does nothing.</summary>
    public void GoToPage(int page)
    {
        if (!IsReady || page < 1 || page > PageCount)
        {
            return;
        }

        CurrentPage = page;
        ResetPageField();
        PageRequested?.Invoke(page);
    }

    /// <summary>Puts the current page back into the page field (an edit that was not committed, for example on leaving the field).</summary>
    public void ResetPageField() => PageFieldText = CurrentPage.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Handles the files a later launch handed over (single instance). May be called on any thread: opens the
    /// first path on the UI thread and brings the window to the front; an empty list only brings it to the front.
    /// </summary>
    public void OnExternalOpen(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var first = paths.Count > 0 ? paths[0] : null;
        Dispatcher.UIThread.Post(() =>
        {
            if (!string.IsNullOrEmpty(first))
            {
                _ = OpenAsync(first);
            }

            ActivationRequested?.Invoke();
        });
    }

    /// <summary>The cause on the error card for a failure (design system 7.8); password kinds never reach the card.</summary>
    internal static string CauseOf(PdfOpenError error) => error switch
    {
        PdfOpenError.NotAPdf => Strings.ErrorNotAPdf,
        PdfOpenError.FileNotFound => Strings.ErrorFileNotFound,
        PdfOpenError.CannotRead => Strings.ErrorCannotRead,
        PdfOpenError.UnsupportedEncryption => Strings.ErrorUnsupportedEncryption,
        _ => Strings.ErrorUnknown,
    };

    private async Task PickAndOpenAsync()
    {
        // Ctrl+O while the dialog is open (where the platform lets it through) does not open a second dialog.
        if (_picking)
        {
            return;
        }

        _picking = true;
        string? path;
        try
        {
            path = await _picker.PickPdfAsync();
        }
        finally
        {
            _picking = false;
        }

        if (path is not null)
        {
            await OpenAsync(path);
        }
    }

    private void CommitPageField()
    {
        if (int.TryParse(PageFieldText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page) && IsReady && page >= 1 && page <= PageCount)
        {
            GoToPage(page);
        }
        else
        {
            ResetPageField();
        }
    }

    private void RaiseNavigationCanExecuteChanged()
    {
        _zoomInCommand.RaiseCanExecuteChanged();
        _zoomOutCommand.RaiseCanExecuteChanged();
        _previousPageCommand.RaiseCanExecuteChanged();
        _nextPageCommand.RaiseCanExecuteChanged();
    }

    private void SubmitPassword()
    {
        // Ignored while a check is running (Enter pressed twice) and outside the password card.
        if (State is not (ViewerState.PasswordRequired or ViewerState.PasswordIncorrect) || FullPath is not { } path)
        {
            return;
        }

        _checkingPassword = true;
        _ = _session.OpenAsync(path, Password);
    }

    private void Close()
    {
        _checkingPassword = false;
        Password = string.Empty;
        _session.Close();
    }

    private void OnSessionChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Reads the session; runs on the UI thread.</summary>
    private void Refresh()
    {
        var state = _session.State;
        var document = state == ViewerState.Ready ? _session.Document : null;
        var pageCount = 0;
        if (document is not null)
        {
            try
            {
                pageCount = document.PageCount;
            }
            catch (ObjectDisposedException)
            {
                // Replaced or closed meanwhile; the event of that change follows.
                document = null;
            }
        }

        var isNewDocument = document is not null && !ReferenceEquals(document, _document);
        _document = document;

        FullPath = _session.Path;
        Error = state == ViewerState.Failed ? _session.Error : null;
        PageCount = pageCount;
        _nextPageCommand.RaiseCanExecuteChanged();
        if (isNewDocument)
        {
            CurrentPage = 1;
            Zoom = ZoomSteps.Default;
        }
        else if (document is null)
        {
            CurrentPage = 0;
        }

        // The password check is over once the session leaves Loading. The password stays only for another try.
        var passwordRejected = _checkingPassword && state == ViewerState.PasswordIncorrect;
        if (state != ViewerState.Loading)
        {
            _checkingPassword = false;
        }

        if (state is ViewerState.Ready or ViewerState.Failed or ViewerState.Empty)
        {
            Password = string.Empty;
        }

        State = state;
        if (passwordRejected)
        {
            PasswordRejected?.Invoke();
        }
    }
}
