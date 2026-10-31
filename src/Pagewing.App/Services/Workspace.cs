using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Distribution;
using Pagewing.Core.Settings;
using Pagewing.Core.Signatures;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;

namespace Pagewing.App.Services;

/// <summary>
/// The windows of the app: opens them, knows the one activated last (where files of a later launch open) and moves a
/// tab into a window of its own with its session, without opening the file again. The first window returns to the
/// start screen when its last tab closes; every other window closes with its last tab (<see
/// cref="MainViewModel.CloseWindowWhenEmpty"/>), and when the first one is closed, the next one takes its part. A closed
/// window releases its tabs. Everything except <see cref="OnExternalOpen"/> is used on the UI thread.
/// </summary>
internal sealed class Workspace : IDisposable, ITabDragHost
{
    /// <summary>How far a new window is offset to the right and down from its source window, in device-independent pixels.</summary>
    public const double NewWindowOffset = 32;

    private readonly Func<ViewerSession> _sessionFactory;
    private readonly Func<Window> _createWindow;
    private readonly Func<Window, IFilePicker> _createPicker;
    private readonly UserSettings _settings;
    private readonly RecentFilesService? _recentFiles;
    private readonly IEffectiveTheme? _theme;
    private readonly Func<IDefaultAppService?>? _defaultAppServiceFactory;
    private readonly DefaultAppPromptGate _defaultAppPromptGate = new();
    private readonly Func<UpdateCoordinator?>? _updateFactory;
    private readonly UpdateStartGate _updateStartGate = new();
    private UpdateCoordinator? _updateCoordinator;
    private bool _updateCoordinatorMade;

    // The answers of the questions of the update about unsaved changes, from "Install" to the installer; the window that
    // asks now; whether editing is locked because the installer has the update.
    private Dictionary<TabViewModel, int>? _updateAnswers;
    private MainViewModel? _askingWindow;
    private bool _editingFrozen;
    private readonly List<MainViewModel> _windows = [];
    private readonly Dictionary<MainViewModel, Window> _views = [];
    private readonly List<MainViewModel> _activationOrder = [];
    private TabDrag? _activeDrag;

    /// <param name="sessionFactory">Creates the session of each new tab, in every window.</param>
    /// <param name="createWindow">Creates a window (a <see cref="Views.MainWindow"/> in the app).</param>
    /// <param name="createPicker">Creates the file dialog of a window.</param>
    /// <param name="settings">The settings every window shares; <see cref="UserSettings.Detached"/> when omitted.</param>
    /// <param name="theme">The theme in effect (<see cref="Appearance"/>), which every window follows; none counts as light.</param>
    /// <param name="recentFiles">
    /// The history of recently opened files every window shares and writes when it closes; none keeps no history.
    /// </param>
    /// <param name="defaultAppServiceFactory">
    /// Makes the service behind the card "Set as the default for PDFs?" (<see cref="PlatformDefaultAppServices.FactoryFor"/>);
    /// none (a measurement run, tests) means no card. The question is asked once for all windows.
    /// </param>
    /// <param name="updateFactory">
    /// Makes the coordinator of the update check (<see cref="PlatformUpdate.FactoryFor"/>); none (a measurement run,
    /// tests) means no check and no command. It is called at the first need, after the first sharp page of any window
    /// or on the command, and once: all windows share the coordinator and its check.
    /// </param>
    public Workspace(
        Func<ViewerSession> sessionFactory,
        Func<Window> createWindow,
        Func<Window, IFilePicker> createPicker,
        UserSettings? settings = null,
        IEffectiveTheme? theme = null,
        RecentFilesService? recentFiles = null,
        Func<IDefaultAppService?>? defaultAppServiceFactory = null,
        Func<UpdateCoordinator?>? updateFactory = null)
    {
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(createWindow);
        ArgumentNullException.ThrowIfNull(createPicker);
        _sessionFactory = sessionFactory;
        _createWindow = createWindow;
        _createPicker = createPicker;
        _settings = settings ?? UserSettings.Detached();
        _theme = theme;
        _recentFiles = recentFiles;
        _defaultAppServiceFactory = defaultAppServiceFactory;
        _updateFactory = updateFactory;
    }

    /// <summary>Gets the view models of the open windows, the oldest first.</summary>
    public IReadOnlyList<MainViewModel> Windows => _windows;

    /// <summary>
    /// Gets the window that was activated last (<see cref="WindowBase.Activated"/>), or <see langword="null"/> when none
    /// was activated yet or it has been closed since.
    /// </summary>
    public MainViewModel? ActiveWindow { get; private set; }

    /// <summary>
    /// Opens a window: creates it and its view model, adds <paramref name="firstTab"/> as its tab, sets the
    /// <c>DataContext</c> and shows it. The caller makes the first window <c>desktop.MainWindow</c>. A window opened
    /// while others are open lies next to the active one (see <see cref="MoveToNewWindow"/>).
    /// </summary>
    /// <param name="firstTab">The first tab of the window, for example the file of the command line; none for the start screen.</param>
    /// <param name="beforeShow">
    /// Called with the window and its view model right before the window is shown, for what has to see the window open
    /// (the first frame of a measurement, for example).
    /// </param>
    public MainViewModel OpenWindow(TabViewModel? firstTab = null, Action<Window, MainViewModel>? beforeShow = null)
    {
        var source = ActiveWindow ?? (_windows.Count > 0 ? _windows[^1] : null);
        return Open(firstTab, source is null ? null : _views[source], beforeShow);
    }

    /// <summary>
    /// Opens a window (see <see cref="OpenWindow(TabViewModel?, Action{Window, MainViewModel}?)"/>) whose first tab is a
    /// new one for <paramref name="firstSession"/>, a session that is already opening (the file of the command line). The
    /// window's view model makes the tab (<see cref="MainViewModel.AdoptSession"/>), so it starts with the defaults of
    /// the settings like every other new tab; the tab owns the session from then on.
    /// </summary>
    public MainViewModel OpenWindowFor(ViewerSession firstSession, Action<Window, MainViewModel>? beforeShow = null)
    {
        ArgumentNullException.ThrowIfNull(firstSession);
        var source = ActiveWindow ?? (_windows.Count > 0 ? _windows[^1] : null);
        return Open(null, source is null ? null : _views[source], beforeShow, firstSession: firstSession);
    }

    /// <summary>
    /// The windows and tabs to bring back after an update (<see cref="SessionStore"/>): for every window with a tab, the
    /// oldest first, its tabs from left to right with their file and where it is read
    /// (<see cref="MainViewModel.RememberedViewOf"/>; the active tab where it is shown now) and the active one. A tab
    /// without an open document (still loading, asking for its password, failed) keeps its file without a view; a tab
    /// without a file and a window on the start screen are left out.
    /// </summary>
    public SessionSnapshot CaptureSession()
    {
        var windows = new List<SessionWindow>();
        foreach (var viewModel in _windows)
        {
            if (_views[viewModel] is MainWindow window)
            {
                window.DocumentView.SaveView();
            }

            var tabs = new List<SessionTab>();
            var active = 0;
            foreach (var tab in viewModel.Tabs)
            {
                if (ReferenceEquals(tab, viewModel.ActiveTab))
                {
                    active = tabs.Count;
                }

                if (tab.FullPath is { Length: > 0 } path)
                {
                    tabs.Add(new SessionTab(path, MainViewModel.RememberedViewOf(tab)?.View));
                }
            }

            if (tabs.Count > 0)
            {
                windows.Add(new SessionWindow(tabs, Math.Min(active, tabs.Count - 1)));
            }
        }

        return new SessionSnapshot(windows);
    }

    /// <summary>
    /// Brings back the windows and tabs of <paramref name="snapshot"/> after an update, as they were, without the card
    /// "Set as the default for PDFs?" (the files were open before). The first window opens like a start with files on the
    /// command line: its active tab first, for <paramref name="firstSession"/> (already opening its file since
    /// <see cref="Program.Main"/>), its other tabs at their places after its first sharp tile
    /// (<see cref="App.AfterFirstSharpTile"/>, <see cref="MainViewModel.RestoreInBackgroundAsync"/>). Then the other
    /// windows open the same way, without taking the focus. Every tab starts where its file was read
    /// (<see cref="TabViewModel.InitialView"/>). A protected file asks for its password as at any other time.
    /// </summary>
    /// <param name="snapshot">The session, without missing files (<see cref="Program.LoadSession"/>).</param>
    /// <param name="firstSession">
    /// The session that opens the active file of the first window; without one the workspace starts opening it.
    /// </param>
    /// <param name="beforeShow">Called for the first window right before it is shown (see <see cref="OpenWindow"/>).</param>
    /// <returns>The first window, or <see langword="null"/> for a session without windows.</returns>
    public MainViewModel? RestoreSession(SessionSnapshot snapshot, ViewerSession? firstSession = null, Action<Window, MainViewModel>? beforeShow = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Windows.Count == 0)
        {
            return null;
        }

        // The files were open before the update: no first-open card for them.
        _defaultAppPromptGate.Started = true;
        return OpenRestoredWindow(
            snapshot.Windows[0],
            firstSession,
            beforeShow,
            showActivated: true,
            afterFirstSharpTile: () =>
            {
                foreach (var window in snapshot.Windows.Skip(1))
                {
                    OpenRestoredWindow(window, null, null, showActivated: false, afterFirstSharpTile: null);
                }
            });
    }

    /// <summary>
    /// Opens a window of a restored session with its active tab, and its other tabs (then <paramref name="afterFirstSharpTile"/>)
    /// once the active one has its first sharp tile.
    /// </summary>
    private MainViewModel OpenRestoredWindow(
        SessionWindow saved, ViewerSession? session, Action<Window, MainViewModel>? beforeShow, bool showActivated, Action? afterFirstSharpTile)
    {
        var active = saved.Active;
        if (session is null)
        {
            session = _sessionFactory();
            _ = session.OpenAsync(active.Path);
        }

        var source = ActiveWindow ?? (_windows.Count > 0 ? _windows[^1] : null);
        var viewModel = Open(
            null, source is null ? null : _views[source], beforeShow, showActivated: showActivated, firstSession: session, firstView: active.View);
        var before = saved.Tabs.Take(saved.ActiveIndex).ToList();
        var after = saved.Tabs.Skip(saved.ActiveIndex + 1).ToList();
        void OpenTheRest()
        {
            _ = viewModel.RestoreInBackgroundAsync(before, after);
            afterFirstSharpTile?.Invoke();
        }

        if (_views[viewModel] is MainWindow window && viewModel.ActiveTab is { } first)
        {
            App.AfterFirstSharpTile(window, viewModel, first, OpenTheRest, App.MoreFilesFallbackDelay);
        }
        else
        {
            OpenTheRest();
        }

        return viewModel;
    }

    /// <summary>Gets the window of an open window view model.</summary>
    public Window WindowOf(MainViewModel viewModel) =>
        _views.TryGetValue(viewModel, out var window) ? window : throw new ArgumentException("Not an open window of this workspace.", nameof(viewModel));

    /// <summary>
    /// "Open in new window": takes <paramref name="tab"/> out of <paramref name="source"/> without releasing it and
    /// opens a window with it as its only, active tab, with the same session, document, page and zoom. The new window
    /// has the size of the source window and lies <see cref="NewWindowOffset"/> to the right and down from it, on the
    /// screen. Does nothing when <paramref name="source"/> has only this tab (it would make an equal window) or the
    /// tab is not one of its tabs.
    /// </summary>
    public void MoveToNewWindow(MainViewModel source, TabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tab);
        if (!_views.TryGetValue(source, out var sourceWindow) || source.Tabs.Count < 2 || source.Tabs.IndexOf(tab) < 0)
        {
            return;
        }

        OpenDetached(source, sourceWindow, tab, position: null, showActivated: true);
    }

    /// <summary>
    /// Starts the drag of a tab out of its strip (<see cref="ITabDragHost.BeginTabDrag"/>). With two or more tabs the
    /// tab goes into a new window of the size of the source window, which is placed so that the pointer holds it where
    /// it held the tab, and is shown without taking the focus (the strip that leads the drag keeps hearing the
    /// keyboard, Esc cancels). With only this tab the window itself follows the pointer.
    /// </summary>
    public TabDrag? BeginTabDrag(MainViewModel source, TabViewModel tab, PixelPoint pointer, PixelPoint grip)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(tab);
        if (_activeDrag is not null || !_views.TryGetValue(source, out var sourceWindow) || source.Tabs.IndexOf(tab) < 0)
        {
            return null;
        }

        TabDrag drag;
        if (source.Tabs.Count < 2)
        {
            // A maximized or full-screen window cannot be moved as it is; the title bar does that.
            if (sourceWindow.WindowState != WindowState.Normal)
            {
                return null;
            }

            var position = sourceWindow.Position;
            var grab = new PixelPoint(pointer.X - position.X, pointer.Y - position.Y);
            drag = new TabDrag(this, source, tab, 0, source, grab, movesWholeWindow: true, position);
        }
        else
        {
            // The position of a window is its frame; the grip is measured from its content.
            var content = sourceWindow.PointToScreen(default);
            var grab = new PixelPoint(content.X - sourceWindow.Position.X + grip.X, content.Y - sourceWindow.Position.Y + grip.Y);
            var index = source.Tabs.IndexOf(tab);
            var floating = OpenDetached(source, sourceWindow, tab, new PixelPoint(pointer.X - grab.X, pointer.Y - grab.Y), showActivated: false);
            drag = new TabDrag(this, source, tab, index, floating, grab, movesWholeWindow: false, default);
        }

        _activeDrag = drag;
        return drag;
    }

    /// <summary>
    /// Gets the window whose tab strip lies under <paramref name="pointer"/> (a point on the screen) and the position a
    /// dropped tab takes there, or <see langword="null"/>. <paramref name="floating"/>, the window of the drag, is no
    /// target. Where strips overlap, the window activated last wins, then the newest.
    /// </summary>
    internal (MainViewModel Target, int Index)? FindDropTarget(PixelPoint pointer, MainViewModel floating)
    {
        // Front to back as far as it is known: the windows in the order they were activated, then the ones never activated, newest first.
        var candidates = new List<MainViewModel>(_activationOrder);
        for (var i = _windows.Count - 1; i >= 0; i--)
        {
            if (!candidates.Contains(_windows[i]))
            {
                candidates.Add(_windows[i]);
            }
        }

        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, floating) || _views[candidate] is not MainWindow { IsVisible: true, WindowState: not WindowState.Minimized } window)
            {
                continue;
            }

            if (window.TabDropIndexAt(pointer) is { } index)
            {
                return (candidate, index);
            }

            // A window in front covers the strips behind it: the tab must not land in one the user cannot see.
            if (new PixelRect(window.Position, PixelSize.FromSize(window.ClientSize, window.DesktopScaling)).Contains(pointer))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A drag has ended: the workspace is free for the next one.</summary>
    internal void EndDrag(TabDrag drag)
    {
        if (ReferenceEquals(_activeDrag, drag))
        {
            _activeDrag = null;
        }
    }

    /// <summary>
    /// Takes <paramref name="tab"/> out of <paramref name="source"/> without releasing it and opens a window with it
    /// as its only tab. The document view of the source window saves where it showed the tab when it leaves it.
    /// </summary>
    private MainViewModel OpenDetached(MainViewModel source, Window sourceWindow, TabViewModel tab, PixelPoint? position, bool showActivated)
    {
        source.DetachTab(tab);

        // The tab brings its own choice of panel: the window must not open "Pages" on its own for the first sharp page.
        return Open(tab, sourceWindow, static (_, viewModel) => viewModel.OpensPagesPanelAfterFirstSharpTile = false, position, showActivated);
    }

    /// <summary>
    /// Handles the files a later launch handed over (single instance). May be called on any thread: on the UI thread
    /// they open as tabs of the window activated last, or of the first window when none was activated, which comes to
    /// the front (<see cref="MainViewModel.OpenExternal"/>).
    /// </summary>
    public void OnExternalOpen(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var copy = paths.ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            var target = ActiveWindow ?? (_windows.Count > 0 ? _windows[0] : null);
            target?.OpenExternal(copy);
        });
    }

    /// <summary>
    /// Where a window of the size of <paramref name="source"/> goes next to it: <see cref="NewWindowOffset"/> (times
    /// <paramref name="scaling"/>) to the right and down, moved back into <paramref name="workingArea"/> where it would
    /// stick out; a window larger than the area starts at its top left. All in physical pixels.
    /// </summary>
    public static PixelPoint PlaceNextTo(PixelRect source, PixelRect? workingArea, double scaling)
    {
        var offset = (int)Math.Round(NewWindowOffset * scaling);
        var x = source.X + offset;
        var y = source.Y + offset;
        if (workingArea is { } area)
        {
            x = Math.Max(area.X, Math.Min(x, area.Right - source.Width));
            y = Math.Max(area.Y, Math.Min(y, area.Bottom - source.Height));
        }

        return new PixelPoint(x, y);
    }

    /// <summary>Closes every open window, which releases its tabs.</summary>
    public void Dispose()
    {
        foreach (var viewModel in _windows.ToArray())
        {
            if (_views.TryGetValue(viewModel, out var window))
            {
                // The app ends: nothing can be asked any more.
                if (window is MainWindow main)
                {
                    main.CloseWithoutAsking();
                }
                else
                {
                    window.Close();
                }
            }
        }

        // A window whose close did not get through (the app is ending) still releases its tabs.
        foreach (var viewModel in _windows.ToArray())
        {
            Forget(viewModel);
        }

        _updateCoordinator?.Dispose();
    }

    /// <summary>
    /// Gets the coordinator all windows share, or <see langword="null"/> without an updater. Reading it makes it (tests ask
    /// for it before a window needs it) and lets the open windows follow it.
    /// </summary>
    internal UpdateCoordinator? UpdateCoordinator
    {
        get
        {
            var coordinator = GetUpdateCoordinator();
            if (coordinator is not null)
            {
                _updateStartGate.Publish(coordinator);
            }

            return coordinator;
        }
    }

    /// <summary>
    /// "Install" of the update, before anything of it starts (<see cref="UpdateCoordinator.BeforeInstall"/>): asks every
    /// window with unsaved changes in turn (<see cref="ResolveUnsavedChangesAsync"/>). The answers count for the question
    /// before the installer of the same installation too.
    /// </summary>
    internal Task<bool> BeforeUpdateInstallAsync()
    {
        _updateAnswers = [];
        return ResolveUnsavedChangesAsync(_updateAnswers);
    }

    /// <summary>
    /// Right before the installer of the update takes over and ends the app (<see cref="UpdateCoordinator.BeforeApply"/>):
    /// asks again about what changed since the answers of "Install" (a tab edited during the download). With the last
    /// answer editing is locked in every window (<see cref="EditingFrozen"/>), in the same step as the last look at the
    /// windows, so no change can come between the answers and the end of the app.
    /// </summary>
    internal Task<bool> BeforeUpdateApplyAsync() => ResolveUnsavedChangesAsync(_updateAnswers ??= [], freezeWhenAnswered: true);

    /// <summary>
    /// Gets whether editing is locked in every window (<see cref="MainViewModel.IsEditingFrozen"/>): from the last answer
    /// before the installer until the installation ends without a restart (a failure, or no installer). The installer copies
    /// the program (Windows) or the AppImage and checks it again before it ends the app, which takes seconds, during which
    /// the windows would otherwise take changes that the end of the app drops without a question.
    /// </summary>
    internal bool EditingFrozen
    {
        get => _editingFrozen;
        private set
        {
            if (_editingFrozen == value)
            {
                return;
            }

            _editingFrozen = value;
            foreach (var window in _windows)
            {
                window.IsEditingFrozen = value;
            }
        }
    }

    /// <summary>
    /// Asks every window that has unsaved changes, one after another, with the card of closing
    /// (<see cref="MainViewModel.ResolveUnsavedChangesAsync(Dictionary{TabViewModel, int})"/>): the window that was active
    /// first, then the others from the oldest; each comes to the front while it asks, and a window without changes is not
    /// asked. Then the windows are looked at again until none is left to ask about (the user may change a tab of another
    /// window while one asks). A window closed meanwhile is skipped. A window that asks already (a tab or the window is to
    /// close) is waited for: its question is answered first, then what is left is asked. Before each look a comment typed in
    /// a "Comments" panel and not applied is applied (<see cref="MainViewModel.ApplyCommentDrafts"/>), so it is asked about
    /// like any other change. Completes at once when there is nothing to ask.
    /// </summary>
    /// <param name="answers">The "Don't save" answers so far (tab and version of its history), extended here.</param>
    /// <param name="freezeWhenAnswered">Locks editing (<see cref="EditingFrozen"/>) when nothing is left to ask.</param>
    /// <returns><see langword="false"/> as soon as a window answers "Cancel" (or its save fails).</returns>
    internal async Task<bool> ResolveUnsavedChangesAsync(Dictionary<TabViewModel, int> answers, bool freezeWhenAnswered = false)
    {
        ArgumentNullException.ThrowIfNull(answers);
        var first = ActiveWindow;
        try
        {
            while (true)
            {
                foreach (var each in _windows.ToArray())
                {
                    each.ApplyCommentDrafts();
                }

                if (NextWindowToAsk(first, answers) is not { } window)
                {
                    if (freezeWhenAnswered)
                    {
                        EditingFrozen = true;
                    }

                    return true;
                }

                _askingWindow = window;
                window.BringToFront();
                if (window.IsResolving)
                {
                    // The window asks before a tab or itself closes: the user answers that first. Then the window may be
                    // gone or its tab closed; what is left is asked about here.
                    await window.WhenResolveEndsAsync().ConfigureAwait(true);
                    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                    continue;
                }

                if (!await window.ResolveUnsavedChangesAsync(answers).ConfigureAwait(true) && !window.IsDisposed)
                {
                    return false;
                }

                // A typed comment that waited for a card is applied in the next round; the dispatcher gets its turn first.
                if (window.HasUnansweredChanges(answers))
                {
                    await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
                }
            }
        }
        finally
        {
            _askingWindow = null;
        }
    }

    // Raised on the UI thread by the coordinator; must not throw. The lock ends when the installation ends without a restart.
    private void OnUpdateChanged()
    {
        if (_editingFrozen && _updateCoordinator is { State.Stage: not (UpdateStage.Ready or UpdateStage.Installing or UpdateStage.Restarting) })
        {
            EditingFrozen = false;
        }
    }

    /// <summary>The next window with something to ask about: <paramref name="first"/>, then the open windows from the oldest.</summary>
    private MainViewModel? NextWindowToAsk(MainViewModel? first, Dictionary<TabViewModel, int> answers)
    {
        var order = first is not null && _windows.Contains(first) ? _windows.Where(window => !ReferenceEquals(window, first)).Prepend(first) : _windows;
        return order.FirstOrDefault(window => window.HasUnansweredChanges(answers));
    }

    /// <summary>The coordinator all windows share, made by the factory the first time a window needs it.</summary>
    private SignatureStore? _signatureStore;

    /// <summary>The library of saved signatures of this process, opened on first use; every window shares it.</summary>
    private SignatureStore GetSignatureStore() => _signatureStore ??= SignatureStore.Load(UserDataPaths.SignaturesDirectory());

    private UpdateCoordinator? GetUpdateCoordinator()
    {
        if (!_updateCoordinatorMade)
        {
            _updateCoordinator = _updateFactory?.Invoke();
            _updateCoordinatorMade = true;
            if (_updateCoordinator is { } coordinator)
            {
                // Pagewing ends after the installer took over: nothing unsaved may go without the user's answer. The button
                // "Update" brings the window that asks to the front; the lock of editing ends with the installation.
                coordinator.BeforeInstall = BeforeUpdateInstallAsync;
                coordinator.BeforeApply = BeforeUpdateApplyAsync;
                coordinator.QuestionShown = () => _askingWindow?.BringToFront();
                coordinator.Changed += OnUpdateChanged;
            }
        }

        return _updateCoordinator;
    }

    private MainViewModel Open(
        TabViewModel? firstTab,
        Window? source,
        Action<Window, MainViewModel>? beforeShow,
        PixelPoint? position = null,
        bool showActivated = true,
        ViewerSession? firstSession = null,
        RememberedView? firstView = null)
    {
        var window = _createWindow();
        var viewModel = new MainViewModel(_sessionFactory, _createPicker(window), _settings, _recentFiles) { AppTheme = _theme };

        // One library of signatures for all windows (read when a menu or the dialog first needs it).
        viewModel.SignatureStoreProvider = GetSignatureStore;
        if (_defaultAppServiceFactory is { } defaultAppFactory)
        {
            viewModel.DefaultAppServiceFactory = defaultAppFactory;
            viewModel.DefaultAppPromptGate = _defaultAppPromptGate;
        }

        if (_updateFactory is not null)
        {
            viewModel.UpdateCoordinatorFactory = GetUpdateCoordinator;
            viewModel.UpdateStartGate = _updateStartGate;
        }

        if (source is not null)
        {
            PlaceNextTo(window, source, position);
        }

        if (!showActivated)
        {
            window.ShowActivated = false;
        }

        viewModel.TabDragHost = this;
        viewModel.IsEditingFrozen = _editingFrozen;
        viewModel.MoveToNewWindowRequested += tab => MoveToNewWindow(viewModel, tab);
        window.Activated += (_, _) =>
        {
            if (_views.ContainsKey(viewModel))
            {
                ActiveWindow = viewModel;
                _activationOrder.Remove(viewModel);
                _activationOrder.Insert(0, viewModel);
            }
        };
        window.Closed += (_, _) => Forget(viewModel);

        _windows.Add(viewModel);
        _views.Add(viewModel, window);
        UpdateCloseWindowWhenEmpty();
        if (firstTab is not null)
        {
            // Before the DataContext, so the first frame of the window shows the tab. A tab from another window keeps
            // its own layout and zoom.
            viewModel.AttachTab(firstTab);
        }
        else if (firstSession is not null)
        {
            // A new tab, so it gets the defaults of the settings (MainViewModel.NewTab); also before the DataContext.
            viewModel.AdoptSession(firstSession, firstView);
        }

        window.DataContext = viewModel;
        beforeShow?.Invoke(window, viewModel);
        window.Show();
        return viewModel;
    }

    /// <summary>
    /// Gives <paramref name="window"/> the size of <paramref name="source"/> and puts it at <paramref name="position"/>,
    /// or next to the source window, on its screen, without one.
    /// </summary>
    private static void PlaceNextTo(Window window, Window source, PixelPoint? position)
    {
        var size = source.ClientSize;
        var scaling = source.DesktopScaling;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = size.Width;
        window.Height = size.Height;
        if (position is { } given)
        {
            window.Position = given;
            return;
        }

        var frame = new PixelRect(source.Position, PixelSize.FromSize(size, scaling));
        var area = source.Screens.ScreenFromWindow(source)?.WorkingArea;
        window.Position = PlaceNextTo(frame, area, scaling);
    }

    /// <summary>A window closed: it leaves the list and releases its tabs.</summary>
    private void Forget(MainViewModel viewModel)
    {
        if (!_views.Remove(viewModel, out var window))
        {
            return;
        }

        _windows.Remove(viewModel);
        _activationOrder.Remove(viewModel);
        if (ReferenceEquals(ActiveWindow, viewModel))
        {
            ActiveWindow = null;
        }

        // A drag whose window is gone has nothing left to lead.
        if (_activeDrag is not null && ReferenceEquals(_activeDrag.Floating, viewModel))
        {
            _activeDrag = null;
        }

        UpdateCloseWindowWhenEmpty();

        // The views let go of the tabs before their sessions are released.
        window.DataContext = null;
        viewModel.Dispose();
    }

    /// <summary>The oldest window shows the start screen without tabs; every other one closes with its last tab.</summary>
    private void UpdateCloseWindowWhenEmpty()
    {
        for (var i = 0; i < _windows.Count; i++)
        {
            _windows[i].CloseWindowWhenEmpty = i > 0;
        }
    }
}
