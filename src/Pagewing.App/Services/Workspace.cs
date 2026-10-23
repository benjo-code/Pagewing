using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Distribution;
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
                window.Close();
            }
        }

        // A window whose close did not get through (the app is ending) still releases its tabs.
        foreach (var viewModel in _windows.ToArray())
        {
            Forget(viewModel);
        }

        _updateCoordinator?.Dispose();
    }

    /// <summary>The coordinator all windows share, made by the factory the first time a window needs it.</summary>
    private UpdateCoordinator? GetUpdateCoordinator()
    {
        if (!_updateCoordinatorMade)
        {
            _updateCoordinator = _updateFactory?.Invoke();
            _updateCoordinatorMade = true;
        }

        return _updateCoordinator;
    }

    private MainViewModel Open(
        TabViewModel? firstTab,
        Window? source,
        Action<Window, MainViewModel>? beforeShow,
        PixelPoint? position = null,
        bool showActivated = true,
        ViewerSession? firstSession = null)
    {
        var window = _createWindow();
        var viewModel = new MainViewModel(_sessionFactory, _createPicker(window), _settings, _recentFiles) { AppTheme = _theme };
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
            viewModel.AdoptSession(firstSession);
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
