using System.Text;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The state of the main window: one document per window (Milestone 3). Mirrors the <see cref="ViewerSession"/>,
/// whose <see cref="ViewerSession.Changed"/> may come from any thread, on the UI thread.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly ViewerSession _session;
    private readonly RelayCommand _closeCommand;
    private PdfDocument? _document;
    private ViewerState _state;
    private string? _fullPath;
    private int _pageCount;
    private int _currentPage;
    private double _zoom = ZoomSteps.Default;
    private bool _isWindowsTitleBar = OperatingSystem.IsWindows();

    public MainViewModel(ViewerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;

        // Scaffolding: the file picker arrives in Task 10, page and zoom navigation in Task 11.
        OpenCommand = new RelayCommand(static () => { });
        _closeCommand = new RelayCommand(_session.Close, () => State != ViewerState.Empty);
        CloseCommand = _closeCommand;
        ZoomInCommand = new RelayCommand(static () => { }, static () => false);
        ZoomOutCommand = new RelayCommand(static () => { }, static () => false);
        PreviousPageCommand = new RelayCommand(static () => { }, static () => false);
        NextPageCommand = new RelayCommand(static () => { }, static () => false);

        _session.Changed += OnSessionChanged;
        Refresh();
    }

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
                _closeCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether no file is requested: the window shows the start screen and no tab.</summary>
    public bool IsEmpty => State == ViewerState.Empty;

    /// <summary>Gets whether a file is requested (loading, open, password prompt or failure): the window shows its tab.</summary>
    public bool HasDocument => State != ViewerState.Empty;

    /// <summary>Gets whether the document is open: the status bar shows page and zoom.</summary>
    public bool IsReady => State == ViewerState.Ready;

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

    /// <summary>Gets or sets the current page, 1-based (0 when no document is open).</summary>
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
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

    public ICommand OpenCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand PreviousPageCommand { get; }

    public ICommand NextPageCommand { get; }

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
        PageCount = pageCount;
        if (isNewDocument)
        {
            CurrentPage = 1;
            Zoom = ZoomSteps.Default;
        }
        else if (document is null)
        {
            CurrentPage = 0;
        }

        State = state;
    }
}
