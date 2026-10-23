using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Pagewing.App.Measurement;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Hosting;
using Pagewing.Core.Pdf;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

/// <summary>What <see cref="Program.Main"/> prepared before Avalonia started.</summary>
/// <param name="Session">
/// The session, possibly already opening the file of the command line; the first tab of the window when it does.
/// </param>
/// <param name="Instance">The primary single instance, or <see langword="null"/> for an extra instance or a measurement.</param>
/// <param name="Settings">The settings read before Avalonia started (<see cref="UserSettings.Load()"/>).</param>
/// <param name="Measurement">The options of a measurement switch, or <see langword="null"/> for a normal launch.</param>
/// <param name="MorePaths">
/// The further files of the command line (<see cref="StartupOptions.MorePaths"/>): they open as tabs after the first
/// frame, behind the first one. Empty for a measurement.
/// </param>
/// <param name="RecentFiles">
/// The history of recently opened files (<see cref="RecentFilesService.Load()"/>, not read yet; in memory only for a
/// measurement), or <see langword="null"/> for one in memory only.
/// </param>
/// <param name="Restore">
/// The session to restore after an update (<see cref="Program.LoadSession"/>), whose first window's active file
/// <paramref name="Session"/> is opening; <see langword="null"/> for every other start.
/// </param>
internal sealed record AppStartup(
    ViewerSession Session,
    SingleInstance? Instance,
    UserSettings Settings,
    StartupOptions? Measurement = null,
    IReadOnlyList<string>? MorePaths = null,
    RecentFilesService? RecentFiles = null,
    SessionSnapshot? Restore = null)
{
    /// <summary>Gets the further files of the command line, none by default.</summary>
    public IReadOnlyList<string> MorePaths { get; } = MorePaths ?? [];

    /// <summary>Gets the history of recently opened files; one in memory only by default.</summary>
    public RecentFilesService RecentFiles { get; } = RecentFiles ?? RecentFilesService.Detached();
}

/// <summary>What <see cref="Program.Main"/> prepared for the helper of the Windows updater (<c>--finish-update</c>).</summary>
/// <param name="Settings">The settings of the user (theme, language), only read.</param>
/// <param name="CreateHelper">Makes the helper over the way to end the app.</param>
internal sealed record UpdateHelperStartup(UserSettings Settings, Func<IAppShutdown, Platform.Windows.WindowsUpdateHelper> CreateHelper);

public sealed class App : Application
{
    private readonly AppStartup? _startup;
    private readonly UpdateHelperStartup? _helper;
    private bool _firstWindowCreated;
    private WindowIcon? _windowIcon;

    /// <summary>
    /// Creates the app without startup state (tests, designer): its window starts without a tab, and its settings live
    /// in memory only (<see cref="UserSettings.Detached"/>), so nothing is read from or written to the user's folder.
    /// </summary>
    public App()
    {
        Settings = UserSettings.Detached();
        RecentFiles = RecentFilesService.Detached();
    }

    internal App(AppStartup startup)
    {
        // Avalonia creates the app after the platform (windowing, graphics) is set up.
        StartupPhases.Mark("app-created");
        ArgumentNullException.ThrowIfNull(startup);
        _startup = startup;
        Settings = startup.Settings;
        RecentFiles = startup.RecentFiles;
    }

    /// <summary>
    /// Creates the app of the helper of the Windows updater: one window, the progress of the installation, and nothing of
    /// the viewer (no document, no history, no single instance).
    /// </summary>
    internal App(UpdateHelperStartup helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        _helper = helper;
        Settings = helper.Settings;
        RecentFiles = RecentFilesService.Detached();
    }

    /// <summary>Gets the settings of the app, which every window shares.</summary>
    internal UserSettings Settings { get; }

    /// <summary>
    /// Gets the history of recently opened files, which every window shares (read after the first sharp page or the
    /// first frame of the start screen, never in the startup path).
    /// </summary>
    internal RecentFilesService RecentFiles { get; }

    /// <summary>Gets what puts the appearance settings into effect; made before the first window.</summary>
    internal Appearance? Appearance { get; private set; }

    public override void RegisterServices()
    {
        StartupPhases.Mark("app-register-services");
        base.RegisterServices();
    }

    public override void Initialize()
    {
        // The platform (windowing, graphics) is initialized before this.
        StartupPhases.Mark("app-initialize");
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        StartupPhases.Mark("app-initialized");

        // Theme and motion before the first window, so it never shows another theme first.
        Appearance = new Appearance(this, Settings);
        if (_helper is { } helper && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime helperDesktop)
        {
            StartUpdateHelper(helperDesktop, helper);
        }
        else if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Creating an engine does not start PDFium; that happens with the first document.
            var session = _startup?.Session;
            var instance = _startup?.Instance;

            // The process ends with its last window, whichever it is (the default of the classic lifetime, made explicit).
            desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;

            // Creates no window yet; every window gets its own file dialog. An installer of an update takes the session of
            // all windows and ends the app (made only when an update is installed).
            Workspace? created = null;
            var updateHost = new UpdateAppHost(() => created?.CaptureSession() ?? SessionSnapshot.Empty, new DesktopShutdown(desktop));
            var workspace = new Workspace(
                () => new ViewerSession(new PdfEngine()), CreateWindow, static window => new AvaloniaFilePicker(window), Settings, Appearance, RecentFiles,
                PlatformDefaultAppServices.FactoryFor(_startup?.Measurement is not null),
                PlatformUpdate.FactoryFor(_startup?.Measurement is not null, Settings, updateHost));
            created = workspace;

            void BeforeShow(Window window, MainViewModel viewModel)
            {
                desktop.MainWindow = window;
                ConfigureFirstWindow(desktop, (MainWindow)window, viewModel, workspace, instance);
            }

            // The file of the command line, already opening since Program.Main: the first tab, in the first frame. The
            // window makes the tab, so it gets the default layout and zoom of the settings like every new tab.
            var adopted = session is not null && session.State != ViewerState.Empty;
            if (adopted && _startup?.Restore is { } restore)
            {
                // The restart after an update: the windows and tabs of the session, the first one the same way.
                workspace.RestoreSession(restore, session!, BeforeShow);
            }
            else if (adopted)
            {
                workspace.OpenWindowFor(session!, BeforeShow);
            }
            else
            {
                workspace.OpenWindow(null, BeforeShow);
            }

            desktop.Exit += (_, _) =>
            {
                instance?.Dispose();
                workspace.Dispose();
                if (!adopted)
                {
                    // Unused (no file on the command line); a tab owns and releases an adopted one.
                    session?.Dispose();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// The helper of the Windows updater: its window takes the place of the progress window of the app, the installation
    /// starts once it is shown, and the process ends with the window (or when the helper ends it).
    /// </summary>
    private static void StartUpdateHelper(IClassicDesktopStyleApplicationLifetime desktop, UpdateHelperStartup startup)
    {
        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        var helper = startup.CreateHelper(new DesktopShutdown(desktop));
        var window = UpdateWindow.ShowHelper(helper);
        desktop.MainWindow = window;
        desktop.Exit += (_, _) => helper.Dispose();
        Dispatcher.UIThread.Post(() => _ = helper.RunAsync(), DispatcherPriority.Background);
    }

    /// <summary>
    /// What only the first window does, before it is shown: accept later launches after its first frame, carry the
    /// icon outside Windows and run a measurement switch on its view model.
    /// </summary>
    private void ConfigureFirstWindow(
        IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainViewModel viewModel, Workspace workspace, SingleInstance? instance)
    {
        if (instance is not null)
        {
            // Later launches are accepted only after the first frame (startup path); Background runs after rendering.
            // Their files open in the window activated last.
            window.Opened += (_, _) => Dispatcher.UIThread.Post(
                () => StartListening(instance, workspace),
                DispatcherPriority.Background);
        }

        if (_startup?.MorePaths is { Count: > 0 } more && viewModel.ActiveTab is { } first)
        {
            OpenMoreFilesAfterFirstSharpTile(window, viewModel, first, more, MoreFilesFallbackDelay);
        }

        if (!OperatingSystem.IsWindows())
        {
            // Windows takes the icon of the executable (ApplicationIcon); elsewhere the window carries it. Decoding
            // it is not part of the startup path. Later windows get it when they are created (CreateWindow).
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() => window.Icon = _windowIcon ??= LoadWindowIcon(), DispatcherPriority.Background);
        }

        if (_startup?.Measurement is { } measure)
        {
            // The first frame of the window: the first tick of the render loop after it opened.
            window.Opened += (_, _) =>
            {
                StartupPhases.Mark("window-opened");
                window.RequestAnimationFrame(_ => StartupPhases.Mark("first-frame"));
            };

            // Started before the window opens, so no sharp image can come before the measurement listens.
            var output = new MeasurementOutput(
                Console.Out, Console.Error, code => Dispatcher.UIThread.Post(() => desktop.Shutdown(code)), StartupPhases.Current);
            MeasurementRun.Create(measure, viewModel, window.DocumentView, output)?.Start();
        }
    }

    /// <summary>
    /// How long the further files of the command line wait, from the moment the first file is
    /// <see cref="ViewerState.Ready"/>, for its first sharp page before they open anyway (see
    /// <see cref="OpenMoreFilesAfterFirstSharpTile"/>).
    /// </summary>
    internal static readonly TimeSpan MoreFilesFallbackDelay = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The further files of the command line (a first launch with several files; the first file opened early, in
    /// <see cref="Program.Main"/>) open as tabs behind the first one, so the startup path and the first document stay
    /// as they are. They start once the first file has its first sharp tile of page 1
    /// (<see cref="DocumentView.FirstSharpTileDrawn"/>) or of any other page (<see cref="TabViewModel.HasShownSharpPage"/>,
    /// when the user left page 1 before it was drawn); at once when it will not get one (it failed or asks for a
    /// password, or its tab left the window); and at the latest <paramref name="fallbackDelay"/> after it became
    /// <see cref="ViewerState.Ready"/> (a page that fails to render). Then they open in a job of
    /// <see cref="DispatcherPriority.Background"/> (<see cref="MainViewModel.OpenInBackgroundAsync"/>), which never
    /// changes the active tab, so the document view is not touched. A window closed before that opens nothing.
    /// </summary>
    /// <param name="fallbackDelay">
    /// <see cref="MoreFilesFallbackDelay"/>, shorter in tests; <see cref="Timeout.InfiniteTimeSpan"/> waits without a limit.
    /// </param>
    internal static void OpenMoreFilesAfterFirstSharpTile(
        MainWindow window, MainViewModel viewModel, TabViewModel first, IReadOnlyList<string> paths, TimeSpan fallbackDelay) =>
        AfterFirstSharpTile(window, viewModel, first, () => _ = viewModel.OpenInBackgroundAsync(paths), fallbackDelay);

    /// <summary>
    /// Runs <paramref name="open"/> once, in a job of <see cref="DispatcherPriority.Background"/>, when
    /// <paramref name="first"/> has its first sharp tile, with the rules of <see cref="OpenMoreFilesAfterFirstSharpTile"/>
    /// (also the restart after an update, <see cref="Workspace.RestoreSession"/>). A window closed before that runs nothing.
    /// </summary>
    internal static void AfterFirstSharpTile(MainWindow window, MainViewModel viewModel, TabViewModel first, Action open, TimeSpan fallbackDelay)
    {
        var done = false;
        IDisposable? fallback = null;

        void Unhook()
        {
            done = true;
            fallback?.Dispose();
            fallback = null;
            window.DocumentView.FirstSharpTileDrawn -= Start;
            first.PropertyChanged -= OnFirstChanged;
            viewModel.Tabs.CollectionChanged -= OnTabsChanged;
            window.Closed -= OnWindowClosed;
        }

        void Start()
        {
            if (done)
            {
                return;
            }

            Unhook();
            Dispatcher.UIThread.Post(
                () =>
                {
                    if (ReferenceEquals(window.DataContext, viewModel))
                    {
                        open();
                    }
                },
                DispatcherPriority.Background);
        }

        void Check()
        {
            if (done)
            {
                return;
            }

            if (first.State is not (ViewerState.Loading or ViewerState.Ready) || first.HasShownSharpPage)
            {
                // No sharp tile will come (the error card or the password prompt is what the first frame shows), or a
                // sharp page of another page came first.
                Start();
            }
            else if (first.State == ViewerState.Ready && fallback is null && fallbackDelay != Timeout.InfiniteTimeSpan)
            {
                // The document is open, so a sharp page should come soon; if none does (it fails to render), the files
                // open after the delay anyway.
                fallback = DispatcherTimer.RunOnce(Start, fallbackDelay, DispatcherPriority.Background);
            }
        }

        void OnFirstChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TabViewModel.State) or nameof(TabViewModel.HasShownSharpPage))
            {
                Check();
            }
        }

        void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (viewModel.Tabs.IndexOf(first) < 0)
            {
                Start();
            }
        }

        void OnWindowClosed(object? sender, EventArgs e) => Unhook();

        window.DocumentView.FirstSharpTileDrawn += Start;
        first.PropertyChanged += OnFirstChanged;
        viewModel.Tabs.CollectionChanged += OnTabsChanged;
        window.Closed += OnWindowClosed;
        Check();
    }

    /// <summary>
    /// Creates a window of the workspace. The first one is part of the startup path (marked as a phase, icon after its
    /// first frame); later ones carry the icon outside Windows from the start.
    /// </summary>
    private MainWindow CreateWindow()
    {
        var window = new MainWindow();
        if (!_firstWindowCreated)
        {
            _firstWindowCreated = true;
            StartupPhases.Mark("window-created");
        }
        else if (!OperatingSystem.IsWindows())
        {
            window.Icon = _windowIcon ??= LoadWindowIcon();
        }

        return window;
    }

    internal static WindowIcon LoadWindowIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Pagewing/Assets/pagewing-128.png"));
        return new WindowIcon(stream);
    }

    private static void StartListening(SingleInstance instance, Workspace workspace)
    {
        try
        {
            instance.StartListening(workspace.OnExternalOpen);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // Closed before the first frame, or the window opened again: nothing (more) to listen for.
        }
    }
}
