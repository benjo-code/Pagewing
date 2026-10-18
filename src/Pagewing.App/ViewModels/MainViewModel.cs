using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Threading;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The state of a window: its tabs (<see cref="Tabs"/>), each with its own document, and the active one, which the
/// document area, the status bar and the title bar show. Everything except <see cref="OnExternalOpen"/> is used on the
/// UI thread.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>The most files one open (dialog, drop, later launch) opens as tabs; the rest is ignored.</summary>
    public const int MaxFilesPerOpen = 20;

    /// <summary>Default width of the side panel (design system 5.4), also what a double click on its edge restores.</summary>
    public const double DefaultPanelWidth = 264;

    /// <summary>Narrowest the side panel can be dragged (design system 6).</summary>
    public const double MinPanelWidth = 200;

    /// <summary>Widest the side panel can be dragged (design system 6).</summary>
    public const double MaxPanelWidth = 400;

    /// <summary>Below this window width the side panel lies over the document instead of beside it (design system 6).</summary>
    public const double NarrowWindowWidth = 900;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Func<ViewerSession> _sessionFactory;
    private readonly IFilePicker _picker;
    private readonly RelayCommand _closeActiveTabCommand;
    private readonly RelayCommand _confirmLinkCommand;
    private readonly RelayCommand _cancelLinkCommand;

    // What opens a confirmed address; only ConfirmLink reaches it. Made on first use (nothing at startup).
    private ILinkLauncher? _linkLauncher;
    private PendingLink? _pendingLink;

    // What this window listens to on each of its tabs; a tab that moves to another window takes none of it along.
    private readonly Dictionary<TabViewModel, (Action Close, Action OpenOther)> _tabHandlers = [];
    private TabViewModel? _activeTab;
    private int? _tabDropIndex;
    private bool _picking;
    private bool _isWindowsTitleBar = OperatingSystem.IsWindows();
    private double _panelWidth = DefaultPanelWidth;
    private bool _isNarrowWindow;

    // The user opened or closed a panel in this window: the first sharp tile no longer opens "Pages".
    private bool _panelChosenByUser;

    // The first sharp tile of this window was drawn: its one chance to open "Pages" has passed.
    private bool _firstSharpTileSeen;

    /// <param name="sessionFactory">Creates the session of each new tab; the tab owns it.</param>
    /// <param name="picker">The file dialog.</param>
    public MainViewModel(Func<ViewerSession> sessionFactory, IFilePicker picker)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(picker);
        _sessionFactory = sessionFactory;
        _picker = picker;

        Tabs.ActiveChanged += OnActiveTabChanged;
        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasTabs));
        };

        OpenCommand = new RelayCommand(() => _ = PickAndOpenAsync(null));
        _closeActiveTabCommand = new RelayCommand(() => CloseTab(ActiveTab), () => ActiveTab is not null);
        CloseActiveTabCommand = _closeActiveTabCommand;
        CloseTabCommand = new RelayCommand<TabViewModel>(CloseTab);
        ActivateTabCommand = new RelayCommand<TabViewModel>(tab => Tabs.Activate(tab));
        NextTabCommand = new RelayCommand(Tabs.ActivateNext);
        PreviousTabCommand = new RelayCommand(Tabs.ActivatePrevious);
        MoveTabToNewWindowCommand = new RelayCommand<TabViewModel>(tab =>
        {
            if (CanMoveTabToNewWindow && Tabs.IndexOf(tab) >= 0)
            {
                MoveToNewWindowRequested?.Invoke(tab);
            }
        });
        ToggleSidePanelCommand = new RelayCommand<SidePanelKind>(ToggleSidePanel);
        CloseSidePanelCommand = new RelayCommand(() => ToggleSidePanel(SidePanelKind.None));
        OpenSearchCommand = new RelayCommand(OpenSearch);
        _confirmLinkCommand = new RelayCommand(ConfirmLink, () => PendingLink is not null);
        ConfirmLinkCommand = _confirmLinkCommand;
        _cancelLinkCommand = new RelayCommand(() => PendingLink = null, () => PendingLink is not null);
        CancelLinkCommand = _cancelLinkCommand;
    }

    /// <summary>Raised on the UI thread when the window should come to the front (a later launch handed over).</summary>
    public event Action? ActivationRequested;

    /// <summary>Raised when the last tab was closed and <see cref="CloseWindowWhenEmpty"/> is set.</summary>
    public event Action? CloseWindowRequested;

    /// <summary>
    /// Raised by <see cref="MoveTabToNewWindowCommand"/> for a tab of this window that should move into a window of its
    /// own ("Open in new window"); the workspace (<see cref="Services.Workspace"/>) moves it. Without a handler the
    /// window does not offer it.
    /// </summary>
    public event Action<TabViewModel>? MoveToNewWindowRequested;

    /// <summary>
    /// Gets or sets the position at which a tab that is being dragged over this window's strip would go in; the strip
    /// shows its insertion mark there. <see langword="null"/> when no tab is over the strip.
    /// </summary>
    internal int? TabDropIndex
    {
        get => _tabDropIndex;
        set => SetProperty(ref _tabDropIndex, value);
    }

    /// <summary>
    /// Gets or sets what lets a tab of this window be dragged out of the strip (set by the workspace); without one the
    /// tab keeps to the strip.
    /// </summary>
    internal ITabDragHost? TabDragHost { get; set; }

    /// <summary>Gets the tabs of the window, in the order of the tab strip.</summary>
    public TabList<TabViewModel> Tabs { get; } = new();

    /// <summary>Gets the active tab, or <see langword="null"/> without tabs.</summary>
    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasTabs));
                OnPropertyChanged(nameof(WindowTitle));
                RaiseActivePanelChanged();
                _closeActiveTabCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Gets whether the window has no tab: it shows the start screen.</summary>
    public bool IsEmpty => Tabs.Count == 0;

    /// <summary>Gets whether the window has at least one tab: it shows the document area.</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>
    /// Gets or sets whether the last tab closes the window (<see cref="CloseWindowRequested"/>); otherwise the window
    /// returns to the start screen. Off by default.
    /// </summary>
    public bool CloseWindowWhenEmpty { get; set; }

    /// <summary>Gets the window title: "file name – Pagewing" of the active tab, or the app name without a file.</summary>
    public string WindowTitle => ActiveTab?.FileName is { } name ? $"{name} – {Strings.AppName}" : Strings.AppName;

    /// <summary>
    /// Gets or sets whether the window draws its own title bar with the tabs in it (Windows) instead of keeping the
    /// system title bar with a tab row below it (Linux, design system section 2.2). Settable for tests.
    /// </summary>
    public bool IsWindowsTitleBar
    {
        get => _isWindowsTitleBar;
        set => SetProperty(ref _isWindowsTitleBar, value);
    }

    /// <summary>
    /// Gets or sets the width of the side panel of this window (all its tabs share it), limited to
    /// <see cref="MinPanelWidth"/> to <see cref="MaxPanelWidth"/>; <see cref="DefaultPanelWidth"/> at first.
    /// </summary>
    public double PanelWidth
    {
        get => _panelWidth;
        set => SetProperty(ref _panelWidth, double.IsNaN(value) ? DefaultPanelWidth : Math.Clamp(value, MinPanelWidth, MaxPanelWidth));
    }

    /// <summary>
    /// Gets or sets whether the window is narrower than <see cref="NarrowWindowWidth"/>: the side panel then lies over
    /// the document and closes after a page was chosen in it. Set by the window.
    /// </summary>
    public bool IsNarrowWindow
    {
        get => _isNarrowWindow;
        set => SetProperty(ref _isNarrowWindow, value);
    }

    /// <summary>
    /// Gets or sets whether the first sharp tile of the window may open "Pages" (<see cref="OnFirstSharpTileDrawn"/>);
    /// on by default. A measurement run switches it off, so its window keeps the layout and the work of a run without
    /// a panel (comparable with the measurements before the panel existed).
    /// </summary>
    internal bool OpensPagesPanelAfterFirstSharpTile { get; set; } = true;

    /// <summary>Gets the side panel of the active tab (<see cref="SidePanelKind.None"/> without a tab).</summary>
    public SidePanelKind ActivePanel => ActiveTab?.SidePanel ?? SidePanelKind.None;

    /// <summary>Gets whether the active tab shows the page thumbnails ("Pages" is the active entry of the action bar).</summary>
    public bool IsPagesPanelOpen => ActivePanel == SidePanelKind.Pages;

    /// <summary>Gets whether the active tab shows the table of contents ("Contents" is the active entry of the action bar).</summary>
    public bool IsContentsPanelOpen => ActivePanel == SidePanelKind.Contents;

    /// <summary>Gets whether the active tab shows the search ("Search" is the active entry of the action bar).</summary>
    public bool IsSearchPanelOpen => ActivePanel == SidePanelKind.Search;

    /// <summary>
    /// An entry of the action bar (parameter: the <see cref="SidePanelKind"/>): opens its panel in the active tab, or
    /// closes it when it is the open one; another open panel is replaced, so at most one is open.
    /// <see cref="SidePanelKind.None"/> closes the panel.
    /// </summary>
    public ICommand ToggleSidePanelCommand { get; }

    /// <summary>The close button of the side panel: closes the panel of the active tab.</summary>
    public ICommand CloseSidePanelCommand { get; }

    /// <summary>
    /// Ctrl+F: opens "Search" in the active tab with the focus in the search field (or puts the focus back there). Like a
    /// click in the action bar it counts as the user choosing a panel, so "Pages" no longer opens by itself.
    /// </summary>
    public ICommand OpenSearchCommand { get; }

    /// <summary>
    /// Gets the address a link of the active tab asks to open, waiting for the user (the window shows it on a card over
    /// the document), or <see langword="null"/>. At most one at a time: while one waits, further links are ignored. It is
    /// dropped (never opened) when the active tab or its document changes. Nothing is ever remembered: every address is
    /// asked for anew.
    /// </summary>
    public PendingLink? PendingLink
    {
        get => _pendingLink;
        private set
        {
            if (SetProperty(ref _pendingLink, value))
            {
                _confirmLinkCommand.RaiseCanExecuteChanged();
                _cancelLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>"Open" on the link card: drops <see cref="PendingLink"/> and opens its address. The only way an address is opened.</summary>
    public ICommand ConfirmLinkCommand { get; }

    /// <summary>"Cancel" on the link card, also Esc: drops <see cref="PendingLink"/> without opening it.</summary>
    public ICommand CancelLinkCommand { get; }

    /// <summary>
    /// Sets what opens a confirmed address (<see cref="SystemLinkLauncher"/> unless set; tests set a fake that only
    /// records). There is no getter: only <see cref="ConfirmLinkCommand"/> uses it.
    /// </summary>
    internal ILinkLauncher LinkLauncher
    {
        set => _linkLauncher = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Shows the file dialog and opens the chosen file in a new tab (Ctrl+O, "Choose file", "Open file").</summary>
    public ICommand OpenCommand { get; }

    /// <summary>Closes the active tab (Ctrl+W).</summary>
    public ICommand CloseActiveTabCommand { get; }

    /// <summary>Closes the tab given as the parameter.</summary>
    public ICommand CloseTabCommand { get; }

    /// <summary>Activates the tab given as the parameter.</summary>
    public ICommand ActivateTabCommand { get; }

    /// <summary>Activates the tab to the right of the active one, wrapping around.</summary>
    public ICommand NextTabCommand { get; }

    /// <summary>Activates the tab to the left of the active one, wrapping around.</summary>
    public ICommand PreviousTabCommand { get; }

    /// <summary>
    /// "Open in new window" for the tab given as the parameter: raises <see cref="MoveToNewWindowRequested"/> while
    /// <see cref="CanMoveTabToNewWindow"/> holds.
    /// </summary>
    public ICommand MoveTabToNewWindowCommand { get; }

    /// <summary>
    /// Gets whether a tab can move into a new window: the window has at least two tabs (moving the only one would only
    /// make an equal window) and something takes the tab (<see cref="MoveToNewWindowRequested"/>).
    /// </summary>
    public bool CanMoveTabToNewWindow => Tabs.Count >= 2 && MoveToNewWindowRequested is not null;

    /// <summary>
    /// Opens a file: a file that is already open in a tab activates that tab; a tab that shows the error card for it
    /// (the file may be readable now) is activated and tries again; otherwise a new tab to the right of the active one
    /// opens it.
    /// </summary>
    /// <param name="path">Path of the file; an empty path does nothing.</param>
    /// <returns>Completes when the session of the tab has a result (see <see cref="ViewerSession.OpenAsync"/>).</returns>
    public Task OpenAsync(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return Task.CompletedTask;
        }

        if (FindOpenTab(path) is { } open)
        {
            Tabs.Activate(open);

            // A password prompt keeps its card (and what was typed); only a failed open is worth another try.
            return open.IsFailed ? open.OpenAsync(path) : Task.CompletedTask;
        }

        return AddTab(new TabViewModel(_sessionFactory())).OpenAsync(path);
    }

    /// <summary>Opens up to <see cref="MaxFilesPerOpen"/> files in their order (see <see cref="OpenAsync"/>); the last one ends up active.</summary>
    /// <returns>Completes when every session has a result.</returns>
    public Task OpenManyAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var opens = new List<Task>();
        foreach (var path in paths.Take(MaxFilesPerOpen))
        {
            opens.Add(OpenAsync(path));
        }

        return Task.WhenAll(opens);
    }

    /// <summary>
    /// Opens up to <see cref="MaxFilesPerOpen"/> files as tabs behind the active one, in their order, without ever
    /// changing the active tab (so the document view of the window is not touched): the new tabs are appended at the end
    /// of the tab strip (<see cref="TabList{T}.AddInBackground"/>), and a file that is already open is skipped, not
    /// activated. Only a window without a tab has to activate one: the first file's tab. The files of a first launch with
    /// several files open this way.
    /// </summary>
    /// <returns>Completes when every session has a result.</returns>
    public Task OpenInBackgroundAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var opens = new List<Task>();
        foreach (var path in paths.Take(MaxFilesPerOpen))
        {
            if (!string.IsNullOrEmpty(path) && FindOpenTab(path) is null)
            {
                opens.Add(AddTab(new TabViewModel(_sessionFactory()), background: true).OpenAsync(path));
            }
        }

        return Task.WhenAll(opens);
    }

    /// <summary>
    /// Adds a tab for a session that is already opening (the file of the command line, which <see cref="Program.Main"/>
    /// starts opening before the window exists) and activates it.
    /// </summary>
    public TabViewModel AdoptSession(ViewerSession session) => AddTab(new TabViewModel(session));

    /// <summary>
    /// Handles the files a later launch handed over (single instance). May be called on any thread: opens them on the
    /// UI thread (<see cref="OpenManyAsync"/>) and brings the window to the front; an empty list only brings it to the
    /// front.
    /// </summary>
    public void OnExternalOpen(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var copy = paths.ToArray();
        Dispatcher.UIThread.Post(() => OpenExternal(copy));
    }

    /// <summary>
    /// <see cref="OnExternalOpen"/> on the UI thread: opens the files (<see cref="OpenManyAsync"/>) and brings the
    /// window to the front.
    /// </summary>
    internal void OpenExternal(IReadOnlyList<string> paths)
    {
        _ = OpenManyAsync(paths);
        ActivationRequested?.Invoke();
    }

    /// <summary>
    /// Adds a tab that comes from another window, with its session and its state, and activates it; the tab is not
    /// opened again.
    /// </summary>
    internal void AttachTab(TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (Tabs.IndexOf(tab) < 0)
        {
            AddTab(tab);
        }
    }

    /// <summary>
    /// Adds a tab that comes from another window (a tab dropped on the strip) at <paramref name="index"/> (limited to
    /// the tabs there are), with its session and its state, and activates it; the tab is not opened again. A tab that
    /// is in this window already changes nothing.
    /// </summary>
    internal void AttachTab(TabViewModel tab, int index)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (Tabs.IndexOf(tab) < 0)
        {
            AddTab(tab, index: index);
        }
    }

    /// <summary>
    /// Takes a tab out of this window without releasing it (it moves to another window); the window stops listening to
    /// it. Unlike closing, it never closes the window.
    /// </summary>
    /// <returns><see langword="true"/> when the tab was in this window.</returns>
    internal bool DetachTab(TabViewModel tab)
    {
        if (Tabs.IndexOf(tab) < 0)
        {
            return false;
        }

        // The document view leaves the tab (with the change of the active tab) and saves where it showed it.
        Tabs.Close(tab);
        Unhook(tab);

        // The window it goes to has not drawn it yet: its thumbnails wait for the first sharp page there, which sets the
        // flag again.
        tab.HasShownSharpPage = false;
        return true;
    }

    /// <summary>Sets <see cref="PanelWidth"/> back to <see cref="DefaultPanelWidth"/> (double click on the edge of the panel).</summary>
    public void ResetPanelWidth() => PanelWidth = DefaultPanelWidth;

    /// <summary>
    /// The first sharp tile of this window was drawn (the document view tells the window once per document): the first
    /// time, "Pages" opens in the active tab when it shows no panel, the window is at least
    /// <see cref="NarrowWindowWidth"/> wide, the user has not opened or closed a panel in this window yet (design
    /// system 7.3; remembering the choice comes in M6) and <see cref="OpensPagesPanelAfterFirstSharpTile"/> holds.
    /// The panel opens in a job of <see cref="DispatcherPriority.Background"/>, after every handler of the first sharp
    /// tile (a measurement takes its time there) and after rendering. Later calls do nothing.
    /// </summary>
    internal void OnFirstSharpTileDrawn()
    {
        if (_firstSharpTileSeen)
        {
            return;
        }

        _firstSharpTileSeen = true;
        if (OpensPagesPanelAfterFirstSharpTile)
        {
            Dispatcher.UIThread.Post(OpenPagesPanelAfterFirstSharpTile, DispatcherPriority.Background);
        }
    }

    /// <summary>Releases every tab and its session. May be called more than once.</summary>
    public void Dispose()
    {
        foreach (var tab in Tabs.ToArray())
        {
            tab.Dispose();
        }
    }

    private static string FullPathOrAsGiven(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return path;
        }
    }

    private TabViewModel? FindOpenTab(string path)
    {
        var fullPath = FullPathOrAsGiven(path);
        foreach (var tab in Tabs)
        {
            if (string.Equals(tab.Session.Path, fullPath, PathComparison))
            {
                return tab;
            }
        }

        return null;
    }

    private TabViewModel AddTab(TabViewModel tab, bool background = false, int? index = null)
    {
        var handlers = (Close: (Action)(() => CloseTab(tab)), OpenOther: (Action)(() => _ = PickAndOpenAsync(tab)));
        tab.CloseRequested += handlers.Close;
        tab.OpenOtherRequested += handlers.OpenOther;
        _tabHandlers[tab] = handlers;
        if (background)
        {
            if (Tabs.Count > 0)
            {
                // A new tab starts active; one that is added behind the active tab never becomes it, so it keeps only the
                // small render cache of an inactive tab (activating a tab sets it again).
                tab.IsActive = false;
            }

            Tabs.AddInBackground(tab);
        }
        else if (index is { } position)
        {
            Tabs.Insert(tab, position);
        }
        else
        {
            Tabs.Add(tab);
        }

        return tab;
    }

    private void Unhook(TabViewModel tab)
    {
        if (_tabHandlers.Remove(tab, out var handlers))
        {
            tab.CloseRequested -= handlers.Close;
            tab.OpenOtherRequested -= handlers.OpenOther;
        }
    }

    private void CloseTab(TabViewModel? tab)
    {
        if (tab is null || Tabs.IndexOf(tab) < 0)
        {
            return;
        }

        // The document view leaves the tab (with the change of the active tab) before its session is released; a
        // tab that is still loading is released the same way, the session cancels its request.
        Tabs.Close(tab);
        Unhook(tab);
        tab.Dispose();
        if (Tabs.Count == 0 && CloseWindowWhenEmpty)
        {
            CloseWindowRequested?.Invoke();
        }
    }

    private void OnActiveTabChanged()
    {
        var old = _activeTab;
        var next = Tabs.Active;
        if (old is not null)
        {
            old.PropertyChanged -= OnActiveTabPropertyChanged;
            old.PanelPagePicked -= OnPanelPagePicked;
            old.ExternalLinkRequested -= OnExternalLinkRequested;
            old.IsActive = false;
        }

        if (next is not null)
        {
            next.IsActive = true;
            next.PropertyChanged += OnActiveTabPropertyChanged;
            next.PanelPagePicked += OnPanelPagePicked;
            next.ExternalLinkRequested += OnExternalLinkRequested;
        }

        // The address belongs to the tab the user clicked it in.
        PendingLink = null;
        ActiveTab = next;
    }

    private void OnActiveTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.FileName))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
        else if (e.PropertyName == nameof(TabViewModel.Document))
        {
            // Another document (or none): the address came from the one that went.
            PendingLink = null;
        }
        else if (e.PropertyName == nameof(TabViewModel.SidePanel))
        {
            RaiseActivePanelChanged();
        }
    }

    private void RaiseActivePanelChanged()
    {
        OnPropertyChanged(nameof(ActivePanel));
        OnPropertyChanged(nameof(IsPagesPanelOpen));
        OnPropertyChanged(nameof(IsContentsPanelOpen));
        OnPropertyChanged(nameof(IsSearchPanelOpen));
    }

    // The conditions are checked again: the user may have chosen a panel, or the window may have narrowed meanwhile.
    private void OpenPagesPanelAfterFirstSharpTile()
    {
        if (OpensPagesPanelAfterFirstSharpTile && !_panelChosenByUser && !IsNarrowWindow && ActiveTab is { SidePanel: SidePanelKind.None } tab)
        {
            tab.SidePanel = SidePanelKind.Pages;
        }
    }

    private void ToggleSidePanel(SidePanelKind kind)
    {
        if (ActiveTab is not { } tab || !Enum.IsDefined(kind))
        {
            return;
        }

        _panelChosenByUser = true;
        tab.SidePanel = tab.SidePanel == kind ? SidePanelKind.None : kind;
    }

    private void OpenSearch()
    {
        if (ActiveTab is { } tab)
        {
            _panelChosenByUser = true;
            tab.OpenSearch();
        }
    }

    // A link of the active tab asks to open an address (already accepted by ExternalLink.TryParse): ask the user. While
    // one address waits, another is ignored, so the card never changes under the user's pointer.
    private void OnExternalLinkRequested(ExternalLink link)
    {
        if (PendingLink is null)
        {
            PendingLink = new PendingLink(link);
        }
    }

    /// <summary>The user confirmed the address on the card: the only call of the launcher.</summary>
    private void ConfirmLink()
    {
        if (PendingLink is not { } pending)
        {
            return;
        }

        // Dropped first, so a second confirmation (Enter pressed twice) opens nothing.
        PendingLink = null;
        _ = LaunchAsync(_linkLauncher ??= new SystemLinkLauncher(), pending.Link);
    }

    private static async Task LaunchAsync(ILinkLauncher launcher, ExternalLink link)
    {
        try
        {
            await launcher.LaunchAsync(link);
        }
        catch (Exception)
        {
            // A launcher must not throw; if one does, the address simply does not open and the window stays.
        }
    }

    // Over the document (narrow window) the panel gets out of the way once a page was chosen in it.
    private void OnPanelPagePicked()
    {
        if (IsNarrowWindow && ActiveTab is { } tab)
        {
            tab.SidePanel = SidePanelKind.None;
        }
    }

    /// <summary>
    /// Shows the file dialog. The chosen file opens in a new tab, or, for "Open another file" of a tab
    /// (<paramref name="target"/>), replaces the document of that tab. A cancelled dialog changes nothing; a failing
    /// one shows the error card in the target tab, or in a new tab.
    /// </summary>
    private async Task PickAndOpenAsync(TabViewModel? target)
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
        catch (OperationCanceledException)
        {
            // Treated like a cancelled dialog: nothing changes.
            return;
        }
        catch (Exception ex)
        {
            // The dialog itself failed (for example no file chooser portal on Linux): the command must not end
            // silently in an unobserved task. Like any failed open, it shows the error card in a tab.
            if (target is null || Tabs.IndexOf(target) < 0)
            {
                target = AddTab(new TabViewModel(_sessionFactory()));
            }

            target.ShowPickerFailure(ex);
            return;
        }
        finally
        {
            _picking = false;
        }

        if (path is null)
        {
            return;
        }

        if (target is not null && Tabs.IndexOf(target) >= 0)
        {
            await target.OpenAsync(path);
        }
        else
        {
            await OpenAsync(path);
        }
    }
}
