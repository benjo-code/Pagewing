using System.Globalization;
using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// One tab of a window: owns its <see cref="ViewerSession"/> and mirrors it, whose <see cref="ViewerSession.Changed"/>
/// may come from any thread, on the UI thread. Opening a file here replaces the document of this tab. Closing the tab
/// and the file dialog are the window's business (<see cref="CloseRequested"/>, <see cref="OpenOtherRequested"/>).
/// UI thread only.
/// </summary>
public sealed class TabViewModel : ViewModelBase, IDisposable
{
    private readonly ViewerSession _session;
    private readonly RelayCommand _zoomInCommand;
    private readonly RelayCommand _zoomOutCommand;
    private readonly RelayCommand _previousPageCommand;
    private readonly RelayCommand _nextPageCommand;
    private readonly RelayCommand _focusPageFieldCommand;
    private PdfDocument? _document;
    private ViewerState _state;
    private string? _fullPath;
    private PdfOpenError? _error;
    private string _password = string.Empty;
    private bool _checkingPassword;
    private string? _pickerFailure;
    private int _pageCount;
    private int _currentPage;
    private int _currentPageLast;
    private string _pageFieldText = "0";
    private double _zoom = ZoomSteps.Default;
    private ZoomMode _zoomMode = ZoomMode.Free;
    private PageLayoutMode _layoutMode = PageLayoutMode.Continuous;
    private SidePanelKind _sidePanel = SidePanelKind.None;
    private ViewState? _savedView;

    // A new session is active (ViewerSession.SetActive).
    private bool _isActive = true;
    private bool _disposed;

    /// <summary>Creates a tab that owns <paramref name="session"/>: <see cref="Dispose"/> disposes it.</summary>
    public TabViewModel(ViewerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke());
        CancelPasswordCommand = new RelayCommand(() => CloseRequested?.Invoke());
        OpenOtherCommand = new RelayCommand(() => OpenOtherRequested?.Invoke());
        SubmitPasswordCommand = new RelayCommand(SubmitPassword);
        _focusPageFieldCommand = new RelayCommand(() => PageFieldFocusRequested?.Invoke(), () => IsReady);
        FocusPageFieldCommand = _focusPageFieldCommand;

        _zoomInCommand = new RelayCommand(() => Zoom = ZoomSteps.Next(Zoom), () => IsReady && Zoom < ZoomSteps.Max);
        ZoomInCommand = _zoomInCommand;
        _zoomOutCommand = new RelayCommand(() => Zoom = ZoomSteps.Previous(Zoom), () => IsReady && Zoom > ZoomSteps.Min);
        ZoomOutCommand = _zoomOutCommand;
        _previousPageCommand = new RelayCommand(() => GoToPage(CurrentPage - 1), () => IsReady && CurrentPage > 1);
        PreviousPageCommand = _previousPageCommand;
        _nextPageCommand = new RelayCommand(() => GoToPage(LastPageOfRow + 1), () => IsReady && LastPageOfRow < PageCount);
        NextPageCommand = _nextPageCommand;
        CommitPageFieldCommand = new RelayCommand(CommitPageField);
        SetZoomModeCommand = new RelayCommand<ZoomMode>(SelectZoomMode);
        SetLayoutModeCommand = new RelayCommand<PageLayoutMode>(SelectLayoutMode);
        SetZoomCommand = new RelayCommand<double>(zoom =>
        {
            if (IsReady)
            {
                Zoom = zoom;
            }
        });

        _session.Changed += OnSessionChanged;
        Refresh();
    }

    /// <summary>Raised on the UI thread when a password check ended with a wrong password (also a repeated one).</summary>
    public event Action? PasswordRejected;

    /// <summary>
    /// Raised when the document view should show a page (1-based) with its top edge at the margin: the page field,
    /// the arrows of the status bar and Page Up/Page Down.
    /// </summary>
    public event Action<int>? PageRequested;

    /// <summary>Raised when the tab asks to be closed (× on the tab, "Close tab" on the error card, "Cancel" on the password card).</summary>
    public event Action? CloseRequested;

    /// <summary>Raised when the tab asks for the file dialog to replace its document ("Open another file" on the error card).</summary>
    public event Action? OpenOtherRequested;

    /// <summary>Raised when the page field of the status bar should take the keyboard focus.</summary>
    public event Action? PageFieldFocusRequested;

    /// <summary>
    /// Raised after a page was chosen in the side panel (<see cref="ShowPageFromPanel"/>): a narrow window closes the
    /// panel that lies over the document (design system 6, narrow window).
    /// </summary>
    public event Action? PanelPagePicked;

    /// <summary>Gets the session the tab owns and mirrors; the document view draws its document.</summary>
    internal ViewerSession Session => _session;

    /// <summary>Gets the state of the session, as last seen on the UI thread.</summary>
    public ViewerState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsReady));
                OnPropertyChanged(nameof(IsFailed));
                RaiseNavigationCanExecuteChanged();
            }

            // Also when the state stays the same: the password check depends on more than the state.
            OnPropertyChanged(nameof(IsPasswordPrompt));
            OnPropertyChanged(nameof(HasStatusBarRow));
            OnPropertyChanged(nameof(IsPasswordIncorrect));
            OnPropertyChanged(nameof(IsDocumentIcon));
        }
    }

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
    /// <remarks>Without a file (the file dialog failed) the sentence starts with "The file".</remarks>
    public string ErrorCauseText => Error is { } error
        ? FileName is { } name
            ? string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ErrorCause), name, CauseOf(error))
            : string.Format(Strings.Culture, CompositeFormat.Parse(Strings.ErrorCauseNoFile), CauseOf(error))
        : string.Empty;

    /// <summary>
    /// Gets the technical details of the error card: full path and error kind; after a failed file dialog "none"
    /// for the path and the type and message of the exception.
    /// </summary>
    public string ErrorDetailsText => Error is { } error
        ? $"{Strings.ErrorDetailsPath}: {FullPath ?? Strings.ErrorDetailsNoPath}\n{Strings.ErrorDetailsKind}: {error}"
            + (_pickerFailure is { } failure ? $"\n{Strings.ErrorDetailsMessage}: {failure}" : string.Empty)
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
                OnPropertyChanged(nameof(PasswordPromptText));
                OnPropertyChanged(nameof(ErrorCauseText));
                OnPropertyChanged(nameof(ErrorDetailsText));
            }
        }
    }

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
    /// Gets or sets the last page of the row of <see cref="CurrentPage"/> (the same page outside the two-page
    /// layouts), for the page thumbnails. Set by the document view.
    /// </summary>
    public int CurrentPageLast
    {
        get => _currentPageLast;
        set
        {
            if (SetProperty(ref _currentPageLast, value))
            {
                _nextPageCommand.RaiseCanExecuteChanged();
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

    /// <summary>
    /// Gets or sets the zoom (1 = 100 %), limited to <see cref="ZoomSteps.Min"/> to <see cref="ZoomSteps.Max"/>.
    /// Setting it (zoom steps, Ctrl+wheel) switches <see cref="ZoomMode"/> to <see cref="ZoomMode.Free"/>; a fit mode
    /// applies its zoom through <see cref="ApplyFitZoom"/>.
    /// </summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            ZoomMode = ZoomMode.Free;
            SetZoom(value);
        }
    }

    /// <summary>Gets the zoom as shown in the status bar ("100 %"); in a fit mode the zoom the document view computed.</summary>
    public string ZoomText => Zoom.ToString("P0", Strings.Culture);

    /// <summary>Gets or sets how the zoom follows the viewport; <see cref="ZoomMode.Free"/> by default. Kept when the document changes.</summary>
    public ZoomMode ZoomMode
    {
        get => _zoomMode;
        set => SetProperty(ref _zoomMode, value);
    }

    /// <summary>
    /// Gets or sets how the pages are arranged; kept when the document changes. The document view keeps the current
    /// page when it changes (<see cref="SetLayoutModeCommand"/>, layout button of the status bar).
    /// </summary>
    public PageLayoutMode LayoutMode
    {
        get => _layoutMode;
        set => SetProperty(ref _layoutMode, value);
    }

    /// <summary>Gets or sets the side panel of this tab; kept when the document changes.</summary>
    public SidePanelKind SidePanel
    {
        get => _sidePanel;
        set => SetProperty(ref _sidePanel, value);
    }

    /// <summary>
    /// Gets or sets where the document was shown when the document view last left this tab, or
    /// <see langword="null"/>; the view restores it when it shows the tab again. Opening a file clears it.
    /// </summary>
    public ViewState? SavedView
    {
        get => _savedView;
        set => SetProperty(ref _savedView, value);
    }

    /// <summary>
    /// Gets or sets whether this is the active tab of its window. Tells the session (<see cref="ViewerSession.SetActive"/>),
    /// so an inactive tab keeps only a small render cache.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            SetProperty(ref _isActive, value);
            _session.SetActive(value);
        }
    }

    /// <summary>Asks the window to close this tab (× on the tab, "Close tab" on the error card).</summary>
    public ICommand CloseCommand { get; }

    /// <summary>"Cancel" on the password card: asks the window to close this tab.</summary>
    public ICommand CancelPasswordCommand { get; }

    /// <summary>"Open another file" on the error card: asks the window for the file dialog to replace this document.</summary>
    public ICommand OpenOtherCommand { get; }

    /// <summary>Opens the file again with <see cref="Password"/> ("Open" and Enter on the password card).</summary>
    public ICommand SubmitPasswordCommand { get; }

    /// <summary>Gives the keyboard focus to the page field (raises <see cref="PageFieldFocusRequested"/>).</summary>
    public ICommand FocusPageFieldCommand { get; }

    /// <summary>The next zoom step ("+" in the status bar, Ctrl+Plus).</summary>
    public ICommand ZoomInCommand { get; }

    /// <summary>The previous zoom step ("−" in the status bar, Ctrl+Minus).</summary>
    public ICommand ZoomOutCommand { get; }

    /// <summary>
    /// The page before the current one (‹ in the status bar, Page Up). In the two-page layouts the document view shows
    /// the spread of that page, whose first page becomes the current one: the spread before.
    /// </summary>
    public ICommand PreviousPageCommand { get; }

    /// <summary>
    /// The page after the current row (› in the status bar, Page Down): the next page, or in the two-page layouts the
    /// first page of the next spread.
    /// </summary>
    public ICommand NextPageCommand { get; }

    /// <summary>Enter in the page field: jumps to the typed page or resets the field.</summary>
    public ICommand CommitPageFieldCommand { get; }

    /// <summary>
    /// Chooses a <see cref="ZoomMode"/> (parameter: the mode; zoom menu, Ctrl+0, Ctrl+1, Ctrl+2).
    /// The document view computes the zoom of a fit mode; choosing the active mode again fits again.
    /// </summary>
    public ICommand SetZoomModeCommand { get; }

    /// <summary>Sets the zoom to a step of the zoom menu (parameter: the zoom, 1 = 100 %) and leaves a fit mode.</summary>
    public ICommand SetZoomCommand { get; }

    /// <summary>Chooses a <see cref="PageLayoutMode"/> (parameter: the layout; layout menu of the status bar).</summary>
    public ICommand SetLayoutModeCommand { get; }

    /// <summary>Opens a file in this tab and replaces its document.</summary>
    /// <param name="path">Path of the file; an empty path does nothing.</param>
    /// <returns>Completes when the session has a result (see <see cref="ViewerSession.OpenAsync"/>).</returns>
    public Task OpenAsync(string path)
    {
        if (string.IsNullOrEmpty(path) || _disposed)
        {
            return Task.CompletedTask;
        }

        _checkingPassword = false;
        _pickerFailure = null;
        Password = string.Empty;
        SavedView = null;
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
        CurrentPageLast = page;
        ResetPageField();
        PageRequested?.Invoke(page);
    }

    /// <summary>
    /// Shows a page chosen in the side panel (a thumbnail, a bookmark): <see cref="GoToPage"/>, then
    /// <see cref="PanelPagePicked"/>. A page outside the document does nothing.
    /// </summary>
    public void ShowPageFromPanel(int page)
    {
        if (!IsReady || page < 1 || page > PageCount)
        {
            return;
        }

        GoToPage(page);
        PanelPagePicked?.Invoke();
    }

    /// <summary>Puts the current page back into the page field (an edit that was not committed, for example on leaving the field).</summary>
    public void ResetPageField() => PageFieldText = CurrentPage.ToString(CultureInfo.InvariantCulture);

    /// <summary>Releases the session and stops mirroring it. May be called more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.Changed -= OnSessionChanged;
        _checkingPassword = false;
        Password = string.Empty;
        _session.Dispose();
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

    /// <summary>Sets the zoom a fit mode computed, without leaving that mode.</summary>
    internal void ApplyFitZoom(double zoom) => SetZoom(zoom);

    /// <summary>
    /// Takes a document of the session that this tab has not seen yet (<see cref="Refresh"/>) and does nothing
    /// otherwise: the document view calls it before it shows a document, so the tab (zoom and page of a new document)
    /// is up to date whatever order the posted jobs run in, and costs nothing for the later page-size batches.
    /// </summary>
    internal void SyncDocumentFromSession()
    {
        if (_disposed || _session.State != ViewerState.Ready)
        {
            return;
        }

        var document = _session.Document;
        if (document is not null && !ReferenceEquals(document, _document))
        {
            Refresh();
        }
    }

    /// <summary>
    /// Shows the error card with the cause <see cref="PdfOpenError.Unknown"/>, no file and the type and message of
    /// <paramref name="exception"/> in the technical details (no stack trace); closes the document first.
    /// </summary>
    internal void ShowPickerFailure(Exception exception)
    {
        if (_disposed)
        {
            return;
        }

        _checkingPassword = false;
        Password = string.Empty;
        _pickerFailure = $"{exception.GetType().FullName}: {exception.Message}";
        _session.Close();
        Refresh();
        OnPropertyChanged(nameof(ErrorCauseText));
        OnPropertyChanged(nameof(ErrorDetailsText));
    }

    private void SetZoom(double value)
    {
        if (SetProperty(ref _zoom, ZoomSteps.Clamp(value), nameof(Zoom)))
        {
            OnPropertyChanged(nameof(ZoomText));
            _zoomInCommand.RaiseCanExecuteChanged();
            _zoomOutCommand.RaiseCanExecuteChanged();
        }
    }

    private void SelectZoomMode(ZoomMode mode)
    {
        if (!IsReady || !Enum.IsDefined(mode))
        {
            return;
        }

        // The same mode once more still tells the view: after scrolling to a page of another size it fits that one.
        if (!SetProperty(ref _zoomMode, mode, nameof(ZoomMode)))
        {
            OnPropertyChanged(nameof(ZoomMode));
        }
    }

    private void SelectLayoutMode(PageLayoutMode mode)
    {
        if (IsReady && Enum.IsDefined(mode))
        {
            LayoutMode = mode;
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

    /// <summary>The last page of the current row; the current page when the view has not told it yet.</summary>
    private int LastPageOfRow => Math.Max(CurrentPage, CurrentPageLast);

    private void RaiseNavigationCanExecuteChanged()
    {
        _zoomInCommand.RaiseCanExecuteChanged();
        _zoomOutCommand.RaiseCanExecuteChanged();
        _previousPageCommand.RaiseCanExecuteChanged();
        _nextPageCommand.RaiseCanExecuteChanged();
        _focusPageFieldCommand.RaiseCanExecuteChanged();
    }

    private void SubmitPassword()
    {
        // Ignored while a check is running (Enter pressed twice) and outside the password card.
        if (State is not (ViewerState.PasswordRequired or ViewerState.PasswordIncorrect) || FullPath is not { } path || _disposed)
        {
            return;
        }

        _checkingPassword = true;
        _ = _session.OpenAsync(path, Password);
    }

    private void OnSessionChanged() => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Reads the session; runs on the UI thread.</summary>
    private void Refresh()
    {
        // A change posted before the tab was closed: the closed tab keeps what it showed last.
        if (_disposed)
        {
            return;
        }

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

        // A failed file dialog shows the error card until the next open; the session is closed meanwhile.
        if (_pickerFailure is not null)
        {
            state = ViewerState.Failed;
        }

        FullPath = _session.Path;
        Error = _pickerFailure is not null ? PdfOpenError.Unknown : state == ViewerState.Failed ? _session.Error : null;
        PageCount = pageCount;
        _nextPageCommand.RaiseCanExecuteChanged();
        if (isNewDocument)
        {
            // Zoom mode, layout and panel belong to the tab and stay; a saved view brings its own zoom.
            CurrentPage = 1;
            CurrentPageLast = 1;
            if (SavedView is null)
            {
                SetZoom(ZoomSteps.Default);
            }
        }
        else if (document is null)
        {
            CurrentPage = 0;
            CurrentPageLast = 0;
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
