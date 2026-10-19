using System.ComponentModel;
using System.Windows.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Pagewing.App.Commands;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Text;
using Pagewing.Core.Viewing;

namespace Pagewing.App.ViewModels;

/// <summary>
/// The state of a window: its tabs (<see cref="Tabs"/>), each with its own document, and the active one, which the
/// document area, the status bar and the title bar show. Everything except <see cref="OnExternalOpen"/> is used on the
/// UI thread.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    /// <summary>The most files one open (dialog, drop, later launch) opens as tabs; the rest is ignored.</summary>
    public const int MaxFilesPerOpen = 20;

    /// <summary>
    /// Default width of the side panel in the density "Normal" (design system 5.4), also what a double click on its edge
    /// restores there; see <see cref="DensityDefaultPanelWidth"/>.
    /// </summary>
    public const double DefaultPanelWidth = AppSettings.DefaultPanelWidth;

    /// <summary>Default width of the side panel in the density "Kompakt" (design system 5.4).</summary>
    public const double CompactDefaultPanelWidth = 236;

    /// <summary>Narrowest the side panel can be dragged (design system 6).</summary>
    public const double MinPanelWidth = AppSettings.MinPanelWidth;

    /// <summary>Widest the side panel can be dragged (design system 6).</summary>
    public const double MaxPanelWidth = AppSettings.MaxPanelWidth;

    /// <summary>Below this window width the side panel lies over the document instead of beside it (design system 6).</summary>
    public const double NarrowWindowWidth = 900;

    /// <summary>How many recently opened files the start screen lists.</summary>
    public const int MaxStartRecentFiles = 5;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly Func<ViewerSession> _sessionFactory;
    private readonly IFilePicker _picker;

    // The history of recently opened files, or none (a window made without one keeps no history).
    private readonly RecentFilesService? _recent;
    private IReadOnlyList<RecentEntry> _startRecentFiles = [];
    private bool _historyLoadPosted;
    private readonly RelayCommand _closeActiveTabCommand;
    private readonly RelayCommand _confirmLinkCommand;
    private readonly RelayCommand _cancelLinkCommand;

    // What opens a confirmed address; only ConfirmLink reaches it. Made on first use (nothing at startup).
    private ILinkLauncher? _linkLauncher;
    private PendingLink? _pendingLink;
    private CommandCatalog? _commands;
    private OverlayKind _overlay;
    private PaletteViewModel? _palette;
    private SettingsViewModel? _settingsCard;

    // Since when the pending address is locked (Clock timestamp), for the arming delay of "Open": from its arrival, and
    // again from when its card has been laid out (OnLinkCardShown), which is the later and the one that counts.
    private long _pendingSince;

    // Ends the lock of "Open" visibly (enables the button); made when a card is shown, never at startup.
    private ITimer? _armingTimer;

    // What this window listens to on each of its tabs; a tab that moves to another window takes none of it along.
    private readonly Dictionary<TabViewModel, (Action Close, Action OpenOther, PropertyChangedEventHandler Changed)> _tabHandlers = [];
    private TabViewModel? _activeTab;
    private int? _tabDropIndex;
    private bool _picking;
    private bool _isWindowsTitleBar = OperatingSystem.IsWindows();
    private double _panelWidth;
    private DensitySetting _density;
    private bool _isNarrowWindow;

    // The user opened or closed a panel in this window: the first sharp tile no longer opens the start panel.
    private bool _panelChosenByUser;

    // The first sharp page of this window was drawn: its one chance to open the start panel has passed.
    private bool _firstSharpPageSeen;

    // The theme in effect, for the page theme of the tabs (AppPageTheme); none: the theme counts as light.
    private IEffectiveTheme? _appTheme;
    private bool _disposed;

    /// <param name="sessionFactory">Creates the session of each new tab; the tab owns it.</param>
    /// <param name="picker">The file dialog.</param>
    /// <param name="settings">The settings of the app; <see cref="UserSettings.Detached"/> (in memory only) when omitted.</param>
    /// <param name="recentFiles">
    /// The history of recently opened files the app shares (<see cref="RecentFiles"/>); without one the window keeps no
    /// history: it records nothing, restores nothing and lists nothing.
    /// </param>
    public MainViewModel(Func<ViewerSession> sessionFactory, IFilePicker picker, UserSettings? settings = null, RecentFilesService? recentFiles = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(picker);
        _sessionFactory = sessionFactory;
        _picker = picker;
        Settings = settings ?? UserSettings.Detached();
        _density = Settings.Current.Density;
        _panelWidth = ShownPanelWidth(Settings.Current);
        Settings.Changed += OnSettingsChanged;
        _recent = recentFiles;
        if (recentFiles is not null)
        {
            ClearRecentFiles = recentFiles.Clear;
            RecentFilesSource = () => recentFiles.Files.Items;
            recentFiles.Changed += OnRecentFilesChanged;
        }

        OpenRecentCommand = new RelayCommand<RecentEntry>(entry => _ = OpenAsync(entry.Path));
        RemoveRecentCommand = new RelayCommand<RecentEntry>(entry => _recent?.Remove(entry.Path));

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
        _confirmLinkCommand = new RelayCommand(ConfirmLink, () => IsLinkArmed);
        ConfirmLinkCommand = _confirmLinkCommand;
        _cancelLinkCommand = new RelayCommand(() => PendingLink = null, () => PendingLink is not null);
        CancelLinkCommand = _cancelLinkCommand;
        ShowPaletteCommand = new RelayCommand(() => ToggleOverlay(OverlayKind.Palette));
        ShowSettingsCommand = new RelayCommand(() => ToggleOverlay(OverlayKind.Settings));
        ShowHelpCommand = new RelayCommand(() => ToggleOverlay(OverlayKind.Help));
        HideOverlayCommand = new RelayCommand(HideOverlay);
    }

    /// <summary>
    /// Gets the commands of this window with their shortcuts (made on first use: the window binds its keys from it when it
    /// gets this view model).
    /// </summary>
    public CommandCatalog Commands => _commands ??= new CommandCatalog(this);

    /// <summary>
    /// Gets what lies over the window: the command palette, the settings or the help card, or nothing. Only one at a
    /// time; the window makes each of them when it is first shown.
    /// </summary>
    public OverlayKind Overlay
    {
        get => _overlay;
        private set
        {
            if (_overlay == value)
            {
                return;
            }

            // The palette's entries are ready before the window shows it.
            var old = _overlay;
            _overlay = value;
            if (old == OverlayKind.Palette)
            {
                _palette?.OnClosed();
            }
            else if (old == OverlayKind.Settings)
            {
                _settingsCard?.OnClosed();
            }
            else if (old == OverlayKind.Print)
            {
                _print?.OnClosed();
            }

            if (value == OverlayKind.Palette)
            {
                (_palette ??= new PaletteViewModel(this)).OnOpened();
            }
            else if (value == OverlayKind.Settings)
            {
                (_settingsCard ??= new SettingsViewModel(Settings, ClearRecentFiles)).OnOpened();
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the view model of the settings card of the window, or <see langword="null"/> before the card was first shown
    /// (it is made then, never in the startup path).
    /// </summary>
    public SettingsViewModel? SettingsCard => _settingsCard;

    /// <summary>
    /// Gets the command palette of the window, or <see langword="null"/> before it was first shown (it is made then,
    /// never in the startup path).
    /// </summary>
    public PaletteViewModel? Palette => _palette;

    /// <summary>Ctrl+K and "Search or command" in the title bar: shows the command palette, or hides it while it is shown.</summary>
    public ICommand ShowPaletteCommand { get; }

    /// <summary>Ctrl+Comma: shows the settings, or hides them while they are shown.</summary>
    public ICommand ShowSettingsCommand { get; }

    /// <summary>F1: shows the card with the keyboard shortcuts, or hides it while it is shown.</summary>
    public ICommand ShowHelpCommand { get; }

    /// <summary>Esc or the close button of an overlay: hides it.</summary>
    public ICommand HideOverlayCommand { get; }

    /// <summary>
    /// Gets or sets what clears the history of recently opened files (list and positions,
    /// <see cref="RecentFilesService.Clear"/>), or <see langword="null"/> for a window without a history; the command
    /// <c>recent.clear</c> of the catalog and the button of the settings card run only with one.
    /// </summary>
    internal Action? ClearRecentFiles { get; set; }

    /// <summary>
    /// Gets or sets where the command palette reads the recently opened files from, newest first: the history
    /// (<see cref="RecentFilesService.Files"/>, which the first use reads), or an empty list without one. Read each time
    /// the palette filters; it never asks the disk about the files themselves.
    /// </summary>
    internal Func<IReadOnlyList<RecentFile>> RecentFilesSource { get; set; } = static () => [];

    /// <summary>Gets the history of recently opened files of the app, or <see langword="null"/> for a window without one.</summary>
    internal RecentFilesService? RecentFiles => _recent;

    /// <summary>
    /// Gets the newest <see cref="MaxStartRecentFiles"/> files of the history for the start screen ("Recently opened"):
    /// empty until the history has been read (<see cref="OnStartScreenShown"/>), so the first frame never waits for it, and
    /// kept up to date only while the window shows the start screen (a window with tabs does no work for it).
    /// </summary>
    public IReadOnlyList<RecentEntry> StartRecentFiles
    {
        get => _startRecentFiles;
        private set
        {
            if (SetProperty(ref _startRecentFiles, value))
            {
                OnPropertyChanged(nameof(HasStartRecentFiles));
            }
        }
    }

    /// <summary>Gets whether the start screen shows "Recently opened" (the section is absent without entries).</summary>
    public bool HasStartRecentFiles => _startRecentFiles.Count > 0;

    /// <summary>Click or Enter on an entry of "Recently opened" (parameter: the <see cref="RecentEntry"/>): opens it (<see cref="OpenAsync"/>).</summary>
    public ICommand OpenRecentCommand { get; }

    /// <summary>Delete on an entry of "Recently opened", or its remove button: takes it out of the history.</summary>
    public ICommand RemoveRecentCommand { get; }

    /// <summary>
    /// The start screen is shown: the history is read in a job of <see cref="DispatcherPriority.Background"/>, after the
    /// frame, and its entries appear then. Nothing happens when it is read already or the window keeps no history.
    /// </summary>
    internal void OnStartScreenShown()
    {
        if (!IsEmpty)
        {
            return;
        }

        if (_recent is { IsLoaded: false })
        {
            PostHistoryLoad();
        }
        else
        {
            UpdateStartRecentFiles();
        }
    }

    /// <summary>
    /// The window closes (<see cref="Views.MainWindow"/>, after the document view saved where it shows the active tab):
    /// every tab with an open document and a known position goes into the history with it, in one write; the active one
    /// last, so it is the newest.
    /// </summary>
    internal void RememberOpenTabs()
    {
        if (_recent is null)
        {
            return;
        }

        var closed = new List<(string Path, RememberedView View)>();
        foreach (var tab in Tabs.Where(tab => !ReferenceEquals(tab, ActiveTab)).Append(ActiveTab))
        {
            if (RememberedViewOf(tab) is { } entry)
            {
                closed.Add(entry);
            }
        }

        if (closed.Count > 0)
        {
            _recent.Closed(closed);
        }

        // Files opened since the last write (a tab that never showed a sharp page, for example) are not lost.
        _recent.Flush();
    }

    /// <summary>Shows <paramref name="kind"/> over the window in place of the overlay shown so far; <see cref="OverlayKind.None"/> hides it.</summary>
    public void ShowOverlay(OverlayKind kind)
    {
        if (Enum.IsDefined(kind))
        {
            Overlay = kind;
        }
    }

    /// <summary>Hides the overlay of the window.</summary>
    public void HideOverlay() => Overlay = OverlayKind.None;

    /// <summary>The key of an overlay: shows it, or hides it when it is the one shown.</summary>
    private void ToggleOverlay(OverlayKind kind) => Overlay = Overlay == kind ? OverlayKind.None : kind;

    /// <summary>
    /// Gets the settings of the app: the window starts with their panel width and start panel and writes the user's
    /// choices of both back.
    /// </summary>
    public UserSettings Settings { get; }

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

    /// <summary>
    /// Gets or sets the theme in effect (the <see cref="Appearance"/> of the app, set by the workspace; tests set their
    /// own), or <see langword="null"/>, which counts as light. With <see cref="PageAppearanceSetting.FollowTheme"/> a
    /// dark theme makes the pages of every tab dark (<see cref="TabViewModel.AppPageTheme"/>); the window follows its
    /// changes until it is disposed.
    /// </summary>
    internal IEffectiveTheme? AppTheme
    {
        get => _appTheme;
        set
        {
            if (ReferenceEquals(value, _appTheme))
            {
                return;
            }

            if (_appTheme is not null)
            {
                _appTheme.EffectiveThemeChanged -= UpdatePageThemes;
            }

            _appTheme = value;
            if (value is not null && !_disposed)
            {
                value.EffectiveThemeChanged += UpdatePageThemes;
            }

            UpdatePageThemes();
        }
    }

    /// <summary>
    /// Gets the page theme the settings give the tabs of this window now: dark when the pages follow the theme and the
    /// theme in effect is dark (<see cref="AppTheme"/>), else light.
    /// </summary>
    internal PageTheme AppPageTheme =>
        Settings.Current.PageAppearance == PageAppearanceSetting.FollowTheme && Equals(_appTheme?.EffectiveTheme, ThemeVariant.Dark)
            ? PageTheme.Dark
            : PageTheme.Light;

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
    /// <see cref="MinPanelWidth"/> to <see cref="MaxPanelWidth"/>; the stored width (<see cref="AppSettings.PanelWidth"/>)
    /// at first (see <see cref="DensityDefaultPanelWidth"/>). Setting it does not store it (it changes on every pixel of a
    /// drag); <see cref="SavePanelWidth"/> does.
    /// </summary>
    public double PanelWidth
    {
        get => _panelWidth;
        set => SetProperty(ref _panelWidth, double.IsNaN(value) ? DensityDefaultPanelWidth : Math.Clamp(value, MinPanelWidth, MaxPanelWidth));
    }

    /// <summary>
    /// Gets the default width of the side panel in the density of the settings: <see cref="DefaultPanelWidth"/> (264) or,
    /// in "Kompakt", <see cref="CompactDefaultPanelWidth"/> (236). A width the user chose wins over it.
    /// </summary>
    /// <remarks>
    /// The settings store one width for both densities, and the stored <see cref="DefaultPanelWidth"/> stands for "the
    /// default of the density": "Kompakt" shows it as 236, and the default of "Kompakt" is stored as 264. So a user who
    /// never dragged the edge (or reset it with a double click) gets the default of each density, and one who dragged it
    /// keeps that width in both. Dragging it to exactly the default of the density counts as the default.
    /// </remarks>
    public double DensityDefaultPanelWidth => DefaultFor(_density);

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
    /// Gets or sets whether the first sharp page of the window may open the start panel (<see cref="OnFirstSharpPageDrawn"/>);
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
            if (value is not null)
            {
                _pendingSince = Clock.GetTimestamp();
            }

            if (SetProperty(ref _pendingLink, value))
            {
                StopArmingTimer();
                _confirmLinkCommand.RaiseCanExecuteChanged();
                _cancelLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// "Open" on the link card: drops <see cref="PendingLink"/> and opens its address. The only way an address is opened.
    /// Disabled (and ignored) until <see cref="LinkArmingDelay"/> has passed since the card was laid out
    /// (<see cref="IsLinkArmed"/>), by pointer and by keyboard alike: the second click of a double click on the document
    /// must not land on "Open" of the card that its first click brought up.
    /// </summary>
    public ICommand ConfirmLinkCommand { get; }

    /// <summary>How long after its card is shown "Open" stays without effect; "Cancel" and Esc act at once.</summary>
    internal static TimeSpan LinkArmingDelay { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Gets or sets the clock (and timer) of <see cref="LinkArmingDelay"/> (the system clock; tests set a manual one).</summary>
    internal TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>
    /// Gets whether an address waits and <see cref="LinkArmingDelay"/> has passed since its card was laid out
    /// (<see cref="OnLinkCardShown"/>; before that, since it arrived, which is earlier and only matters when no card shows
    /// it, so that "Open" cannot be reached anyway).
    /// </summary>
    internal bool IsLinkArmed => _pendingLink is not null && Clock.GetElapsedTime(_pendingSince) >= LinkArmingDelay;

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
            return open.IsFailed ? OpenInTab(open, path) : Task.CompletedTask;
        }

        return OpenInTab(AddTab(NewTab(_sessionFactory())), path);
    }

    /// <summary>
    /// Opens <paramref name="path"/> in <paramref name="tab"/> where the history says it was last read (page, zoom and
    /// layout win over the defaults of a new tab); a file the history does not know opens as before.
    /// </summary>
    private Task OpenInTab(TabViewModel tab, string path)
    {
        tab.InitialView = _recent?.Files.TryGet(FullPathOrAsGiven(path))?.View;
        return tab.OpenAsync(path);
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
                opens.Add(OpenInTab(AddTab(NewTab(_sessionFactory()), background: true), path));
            }
        }

        return Task.WhenAll(opens);
    }

    /// <summary>
    /// Adds a tab for a session that is already opening (the file of the command line, which <see cref="Program.Main"/>
    /// starts opening before the window exists; <see cref="Workspace.OpenWindowFor"/>) and activates it. Like every
    /// new tab it starts with the default layout and zoom of the settings.
    /// </summary>
    public TabViewModel AdoptSession(ViewerSession session) => AddTab(NewTab(session));

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
        CancelPrintingOf(tab);

        // The window it goes to has not drawn it yet: its thumbnails wait for the first sharp page there, which sets the
        // flag again.
        tab.HasShownSharpPage = false;
        return true;
    }

    /// <summary>
    /// Sets <see cref="PanelWidth"/> back to <see cref="DensityDefaultPanelWidth"/> (double click on the edge of the
    /// panel) and stores it.
    /// </summary>
    public void ResetPanelWidth()
    {
        PanelWidth = DensityDefaultPanelWidth;
        SavePanelWidth();
    }

    /// <summary>
    /// Stores <see cref="PanelWidth"/> in the settings (the edge of the panel was let go); the next window starts with it.
    /// The default of the density is stored as <see cref="DefaultPanelWidth"/> (see <see cref="DensityDefaultPanelWidth"/>).
    /// </summary>
    public void SavePanelWidth()
    {
        var width = PanelWidth == DensityDefaultPanelWidth ? DefaultPanelWidth : PanelWidth;
        Settings.Update(current => current with { PanelWidth = width });
    }

    /// <summary>
    /// The first sharp page of this window was drawn, whichever page it is (the document view tells the window once per
    /// document, <see cref="Views.DocumentView.FirstSharpPageDrawn"/>; a document reopened at its remembered position may
    /// never draw page 1, and the panel must not come later, in the middle of reading): the first time, the start panel
    /// of the settings (<see cref="AppSettings.StartPanel"/>: "Pages", "Contents" or none) opens in the active tab when
    /// it shows no panel, the window is at least <see cref="NarrowWindowWidth"/> wide, the user
    /// has not opened or closed a panel in this window yet (design system 7.3) and
    /// <see cref="OpensPagesPanelAfterFirstSharpTile"/> holds. The panel opens in a job of
    /// <see cref="DispatcherPriority.Background"/>, after every handler of that frame (a measurement takes its time
    /// there) and after rendering. Later calls do nothing.
    /// </summary>
    internal void OnFirstSharpPageDrawn()
    {
        if (_firstSharpPageSeen)
        {
            return;
        }

        _firstSharpPageSeen = true;
        if (OpensPagesPanelAfterFirstSharpTile)
        {
            Dispatcher.UIThread.Post(OpenPagesPanelAfterFirstSharpTile, DispatcherPriority.Background);
        }
    }

    /// <summary>Releases every tab and its session and stops following the settings and the theme. May be called more than once.</summary>
    public void Dispose()
    {
        _disposed = true;
        _palette?.OnClosed();
        _settingsCard?.OnClosed();
        DisposePrinting();
        Settings.Changed -= OnSettingsChanged;
        if (_recent is not null)
        {
            _recent.Changed -= OnRecentFilesChanged;
        }

        if (_appTheme is not null)
        {
            _appTheme.EffectiveThemeChanged -= UpdatePageThemes;
        }

        StopArmingTimer();
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

    private void OnSettingsChanged(AppSettings settings)
    {
        if (settings.Density != _density)
        {
            // Another density: a panel at the default width of the old one takes the default of the new one.
            var atDefault = _panelWidth == DensityDefaultPanelWidth;
            _density = settings.Density;
            OnPropertyChanged(nameof(DensityDefaultPanelWidth));
            if (atDefault)
            {
                PanelWidth = DensityDefaultPanelWidth;
            }
        }

        UpdatePageThemes();
    }

    private static double DefaultFor(DensitySetting density) =>
        density == DensitySetting.Compact ? CompactDefaultPanelWidth : DefaultPanelWidth;

    /// <summary>The width a window shows for the stored one: the stored default stands for the default of the density.</summary>
    private static double ShownPanelWidth(AppSettings settings) =>
        settings.PanelWidth == DefaultPanelWidth ? DefaultFor(settings.Density) : settings.PanelWidth;

    /// <summary>Gives every tab of the window the page theme of the settings (<see cref="AppPageTheme"/>); the override of a tab stays.</summary>
    private void UpdatePageThemes()
    {
        if (_disposed)
        {
            return;
        }

        var theme = AppPageTheme;
        foreach (var tab in Tabs)
        {
            tab.AppPageTheme = theme;
        }
    }

    /// <summary>
    /// A new tab for <paramref name="session"/> with the defaults of the settings: the layout
    /// (<see cref="AppSettings.DefaultLayout"/>) and, for "page width" and "whole page", the zoom mode
    /// (<see cref="AppSettings.DefaultZoomMode"/>), which the document view applies when it shows the document.
    /// "Actual size" keeps the free zoom of 100 % a tab always started with (the same zoom, and a resize never changes
    /// it). Read when the tab is made: a tab that is open keeps its own, and so does one that moves to another window.
    /// </summary>
    private TabViewModel NewTab(ViewerSession session)
    {
        var settings = Settings.Current;
        var tab = new TabViewModel(session) { LayoutMode = settings.DefaultLayout };
        if (settings.DefaultZoomMode is ZoomMode.FitWidth or ZoomMode.FitPage)
        {
            tab.ZoomMode = settings.DefaultZoomMode;
        }

        return tab;
    }

    private TabViewModel AddTab(TabViewModel tab, bool background = false, int? index = null)
    {
        // Before the tab is shown: a tab from another window keeps its override and takes the page theme of this one.
        tab.AppPageTheme = AppPageTheme;
        var handlers = (
            Close: (Action)(() => CloseTab(tab)),
            OpenOther: (Action)(() => _ = PickAndOpenAsync(tab)),
            Changed: (PropertyChangedEventHandler)((_, e) => OnTabPropertyChanged(tab, e)));
        tab.CloseRequested += handlers.Close;
        tab.OpenOtherRequested += handlers.OpenOther;
        tab.PropertyChanged += handlers.Changed;
        _tabHandlers[tab] = handlers;

        // The file of the command line may be open already when its tab comes; a tab from another window was reported there.
        ReportOpened(tab);
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
            tab.PropertyChanged -= handlers.Changed;
        }
    }

    /// <summary>
    /// A tab of this window changed: a document it opened goes into the history (its first Ready), and after its first
    /// sharp page the history is read and what changed is written (in the background, never before: neither the startup
    /// path nor the render of a later open waits for the disk).
    /// </summary>
    private void OnTabPropertyChanged(TabViewModel tab, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Document))
        {
            ReportOpened(tab);
        }
        else if (e.PropertyName == nameof(TabViewModel.HasShownSharpPage) && tab.HasShownSharpPage)
        {
            PostHistoryLoad();
            _recent?.SaveLater();
        }
    }

    /// <summary>
    /// Tells the history that <paramref name="tab"/> opened its document, once per document: not for a file that failed
    /// or still asks for its password (it has no document yet), and not again for a tab that moved here.
    /// </summary>
    private void ReportOpened(TabViewModel tab)
    {
        if (_recent is null || tab.Document is not { } document || ReferenceEquals(document, tab.ReportedDocument) || tab.FullPath is not { } path)
        {
            return;
        }

        tab.ReportedDocument = document;
        _recent.Opened(path);
    }

    /// <summary>
    /// Where <paramref name="tab"/> shows its document, for the history: the position the document view saved
    /// (<see cref="TabViewModel.SavedView"/>), zoom, zoom mode and layout; <see langword="null"/> without an open document
    /// or a saved position.
    /// </summary>
    private static (string Path, RememberedView View)? RememberedViewOf(TabViewModel? tab) =>
        tab is { IsReady: true, FullPath: { } path, SavedView: { } view }
            ? (path, new RememberedView(view, tab.Zoom, tab.ZoomMode, tab.LayoutMode))
            : null;

    /// <summary>The history changed (or was read): the start screen shows its newest entries.</summary>
    private void OnRecentFilesChanged()
    {
        if (!_disposed)
        {
            UpdateStartRecentFiles();
        }
    }

    private void UpdateStartRecentFiles()
    {
        if (_recent is not { IsLoaded: true } recent || !IsEmpty)
        {
            return;
        }

        var items = recent.Files.Items;
        var entries = new List<RecentEntry>(Math.Min(items.Count, MaxStartRecentFiles));
        foreach (var item in items.Take(MaxStartRecentFiles))
        {
            entries.Add(new RecentEntry(item.Path));
        }

        if (!entries.SequenceEqual(_startRecentFiles))
        {
            StartRecentFiles = entries;
        }
    }

    /// <summary>
    /// Reads the history in a job of <see cref="DispatcherPriority.Background"/> (after rendering), once: when the job
    /// runs, the window must show the start screen or a sharp page, so the startup path with a file never reads it.
    /// </summary>
    private void PostHistoryLoad()
    {
        if (_historyLoadPosted || _recent is not { IsLoaded: false } recent)
        {
            return;
        }

        _historyLoadPosted = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _historyLoadPosted = false;
                if (!_disposed && (IsEmpty || Tabs.Any(tab => tab.HasShownSharpPage)))
                {
                    recent.EnsureLoaded();
                    UpdateStartRecentFiles();
                    recent.SaveLater();
                }
            },
            DispatcherPriority.Background);
    }

    private void CloseTab(TabViewModel? tab)
    {
        if (tab is null || Tabs.IndexOf(tab) < 0)
        {
            return;
        }

        // The document view leaves the tab (with the change of the active tab) before its session is released; a
        // tab that is still loading is released the same way, the session cancels its request. Leaving it, the view
        // saved where it showed it: that position goes into the history.
        Tabs.Close(tab);
        Unhook(tab);
        CancelPrintingOf(tab);
        if (_recent is not null && RememberedViewOf(tab) is { } closed)
        {
            _recent.Closed([closed]);
        }

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
        _printCommand?.RaiseCanExecuteChanged();
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

        if (e.PropertyName is nameof(TabViewModel.IsReady) or nameof(TabViewModel.Document))
        {
            // The print button of the action bar is enabled once the document of the active tab is open.
            _printCommand?.RaiseCanExecuteChanged();
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
        var kind = Settings.Current.StartPanel switch
        {
            StartPanelSetting.Pages => SidePanelKind.Pages,
            StartPanelSetting.Contents => SidePanelKind.Contents,
            _ => SidePanelKind.None,
        };

        if (kind != SidePanelKind.None && OpensPagesPanelAfterFirstSharpTile && !_panelChosenByUser && !IsNarrowWindow
            && ActiveTab is { SidePanel: SidePanelKind.None } tab)
        {
            tab.SidePanel = kind;
        }
    }

    private void ToggleSidePanel(SidePanelKind kind)
    {
        if (ActiveTab is not { } tab || !Enum.IsDefined(kind))
        {
            return;
        }

        _panelChosenByUser = true;
        var old = tab.SidePanel;
        var next = old == kind ? SidePanelKind.None : kind;
        tab.SidePanel = next;
        RememberStartPanel(old, next);
    }

    /// <summary>
    /// The user opened or closed a panel: opening "Pages" or "Contents" makes it the start panel of the next window,
    /// closing it makes that none. "Search" never changes it, neither by opening nor by closing.
    /// </summary>
    private void RememberStartPanel(SidePanelKind old, SidePanelKind next)
    {
        StartPanelSetting? start = next switch
        {
            SidePanelKind.Pages => StartPanelSetting.Pages,
            SidePanelKind.Contents => StartPanelSetting.Contents,
            SidePanelKind.None when old is SidePanelKind.Pages or SidePanelKind.Contents => StartPanelSetting.None,
            _ => null,
        };

        if (start is { } value)
        {
            Settings.Update(current => current with { StartPanel = value });
        }
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
        if (PendingLink is not { } pending || !IsLinkArmed)
        {
            return;
        }

        // Dropped first, so a second confirmation (Enter pressed twice) opens nothing.
        PendingLink = null;
        _ = LaunchAsync(_linkLauncher ??= new SystemLinkLauncher(), pending.Link);
    }

    /// <summary>
    /// The card of <paramref name="link"/> has been laid out (UI thread): the lock of "Open" starts now, so a stall
    /// between the arrival of the address and the first frame of the card does not shorten it, and a one-shot timer
    /// enables "Open" when it ends. A card of an address that is no longer pending does nothing.
    /// </summary>
    internal void OnLinkCardShown(PendingLink link)
    {
        if (!ReferenceEquals(link, _pendingLink))
        {
            return;
        }

        _pendingSince = Clock.GetTimestamp();
        _confirmLinkCommand.RaiseCanExecuteChanged();
        StartArmingTimer(link);
    }

    /// <summary>Starts the timer that enables "Open" when the lock of <paramref name="link"/> ends.</summary>
    private void StartArmingTimer(PendingLink link)
    {
        StopArmingTimer();
        var remaining = LinkArmingDelay - Clock.GetElapsedTime(_pendingSince);
        if (remaining <= TimeSpan.Zero)
        {
            _confirmLinkCommand.RaiseCanExecuteChanged();
            return;
        }

        _armingTimer = Clock.CreateTimer(_ => PostArmed(link), null, remaining, Timeout.InfiniteTimeSpan);
    }

    // The timer's thread (any): must never throw.
    private void PostArmed(PendingLink link)
    {
        try
        {
            Dispatcher.UIThread.Post(() => OnArmed(link));
        }
        catch (Exception)
        {
            // The dispatcher is shutting down: nobody sees the button any more.
        }
    }

    private void OnArmed(PendingLink link)
    {
        if (!ReferenceEquals(link, _pendingLink))
        {
            return;
        }

        if (!IsLinkArmed)
        {
            // The timer fired a little before the clock says the lock is over: wait for the rest.
            StartArmingTimer(link);
            return;
        }

        StopArmingTimer();
        _confirmLinkCommand.RaiseCanExecuteChanged();
    }

    private void StopArmingTimer()
    {
        _armingTimer?.Dispose();
        _armingTimer = null;
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
                target = AddTab(NewTab(_sessionFactory()));
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
            await OpenInTab(target, path);
        }
        else
        {
            await OpenAsync(path);
        }
    }
}
