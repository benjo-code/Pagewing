using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Pagewing.App.Measurement;
using Pagewing.App.Platform;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Hosting;
using Pagewing.Core.Pdf;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

internal static partial class Program
{
    /// <summary>How long a later launch tries to hand its files to the running instance.</summary>
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(3);

    /// <summary>How long the new version after an update waits for the old process to end.</summary>
    private static readonly TimeSpan PredecessorTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A measurement whose UI thread hangs is ended this long after its own time limit (exit code 3).</summary>
    private static readonly TimeSpan MeasurementWatchdogGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Startup order (startup path, keep it lean): parse the options; the switches that do something else than viewing
    /// (<c>--register-file-types</c>, and the helper of the updater <c>--finish-update</c>, <see cref="RunUpdateHelper"/>)
    /// branch off here, before the election; elect the single instance before Avalonia is
    /// loaded, and as a later launch hand the files over and exit; otherwise start opening the file of the command
    /// line at once (after an update, with <c>--restore-session</c>, the active file of the first window of the session,
    /// <see cref="LoadSession"/>), so PDFium loads on its own thread while Avalonia builds the window; read the settings (nothing
    /// without a settings file) and choose the UI language (<see cref="ChooseUiCulture"/>); on Windows choose the rendering
    /// mode (<see cref="UseSoftwareRendering"/>); show the window, whose first frame shows the tab with the file name;
    /// accept later launches after the first frame (<see cref="App"/>).
    /// A measurement switch takes the same path, without the election (see <see cref="ElectInstance"/>).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // Native AOT: AppContext must be initialized before any other thread starts (Docs/DECISIONS.md, entry
        // "Startabsturz 0xC0000602: AppContext vor dem PDFium-Thread initialisieren (M3)"). Keep this the first
        // statement; measured at about 0.08 ms.
        _ = AppContext.GetData("System.GC.Server");
        var mainEntered = Stopwatch.GetTimestamp();

        var options = StartupOptions.Parse(args);
        if (OperatingSystem.IsWindows() && (options.RegisterFileTypes is not null || options.UnregisterFileTypes is not null))
        {
            // Installer and manual use; before Avalonia and the single-instance election, nothing else starts.
            return RunFileTypeRegistration(options);
        }

        if (OperatingSystem.IsWindows() && options.FinishUpdate is { } staging)
        {
            // The helper of the updater (a copy of the program, started by the app it replaces): before the single-instance
            // election and without a document, Avalonia shows only its progress window.
            return RunUpdateHelper(args, options, staging);
        }

        if (options.AfterProcess is not null)
        {
            // Started by the old version after an update (AppImage): its process still holds the single-instance lock.
            WaitForPredecessor(options, new SystemProcessLauncher(), PredecessorTimeout);
        }

        if (options.IsMeasurement)
        {
            // Diagnostic phases of a measurement run only (bench/startup/README.md, "Phasen").
            StartupPhases.Enable().Mark("main", mainEntered);
        }

        var (instance, isSecondary) = ElectInstance(options, static () => SingleInstance.TryBecomePrimary(SingleInstance.DefaultName));
        if (isSecondary && HandOver(options.Paths))
        {
            return 0;
        }

        // Primary, a measurement, or an extra instance without the lock when handing over failed (the running one
        // hangs or is closing). Only the restart after an update (--restore-session) reads the session file, once.
        var restore = LoadSession(
            options, static () => new SessionStore(Path.Combine(UserDataPaths.ConfigDirectory(), SessionStore.FileName)), File.Exists);
        var session = new ViewerSession(new PdfEngine());
        if (options.IsMeasurement)
        {
            session.Changed += () =>
            {
                if (session.State == ViewerState.Ready)
                {
                    StartupPhases.Mark("session-ready");
                }
            };
        }

        // A restored session starts like a file of the command line: the active file of its first window opens early.
        var path = options.IsMeasurement ? options.MeasurementPath
            : restore is not null ? restore.Windows[0].Active.Path
            : options.Paths.Count > 0 ? options.Paths[0] : null;
        if (!string.IsNullOrEmpty(path))
        {
            // Sets Loading with the path at once and continues on the PDFium thread; failures end in an error card.
            _ = session.OpenAsync(path);
            StartupPhases.Mark("session-open-started");
        }

        // While PDFium opens the file on its thread. Without a settings file this reads nothing (and parses no JSON).
        var settings = LoadSettings(options, UserSettings.Load);
        if (ChooseUiCulture(options.Language, settings.Current.Language) is { } culture)
        {
            Strings.Culture = culture;
        }

        StartupPhases.Mark("settings-loaded");

        var software = UseSoftwareRendering();
        if (options.IsMeasurement)
        {
            StartMeasurementWatchdog();
        }

        // Reads nothing: the history is read after the first sharp page (or the first frame of the start screen).
        var recentFiles = LoadRecentFiles(options, RecentFilesService.Load);
        var startup = new AppStartup(
            session, instance, settings, options.IsMeasurement ? options : null, restore is null ? options.MorePaths : [], recentFiles, restore);
        return Run(args, startup, software);
    }

    /// <summary>
    /// The new AppImage after an update (<c>--after-process &lt;id&gt;</c>, <see cref="Platform.Linux.AppImageUpdater"/>) was
    /// started by the old program, which still runs: it waits until that process ended (at most <paramref name="timeout"/>;
    /// then it goes on and hands its files over to whatever still runs), so the single-instance election finds the lock free
    /// and the program does not start as an extra instance. Does nothing without the switch, for the helper of the Windows
    /// updater (which waits itself) and for a measurement.
    /// </summary>
    internal static void WaitForPredecessor(StartupOptions options, IProcessLauncher launcher, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(launcher);
        if (options.AfterProcess is not { } process || options.FinishUpdate is not null || options.IsMeasurement)
        {
            return;
        }

        launcher.WaitForExitAsync(process, timeout, CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The session to restore after an update: only with <c>--restore-session</c> (never for a measurement) is the store
    /// made and the file read, then deleted at once, so it is restored only once whatever happens next. Files that
    /// <paramref name="fileExists"/> does not find (moved or deleted) are dropped silently; <see langword="null"/> when
    /// nothing is left, or without the switch. A start without the switch never touches the file.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="store">Makes the store of <c>session.json</c> in the config folder (a temporary folder in tests).</param>
    /// <param name="fileExists"><see cref="File.Exists(string)"/>.</param>
    internal static SessionSnapshot? LoadSession(StartupOptions options, Func<SessionStore> store, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fileExists);
        if (!options.RestoreSession || options.IsMeasurement)
        {
            return null;
        }

        var sessionStore = store();
        var snapshot = sessionStore.Load();
        sessionStore.Delete();
        var existing = snapshot.KeepFiles(fileExists);
        return existing.Windows.Count > 0 ? existing : null;
    }

    /// <summary>
    /// The helper mode <c>--finish-update &lt;staging folder&gt;</c> (Windows, <see cref="Platform.Windows.WindowsUpdateHelper"/>):
    /// reads the settings of the user (theme and language, never written), takes the keys compiled into the program
    /// (<see cref="UpdateTrust.Keys"/>) and the running version as the installed one, and shows the progress window.
    /// </summary>
    [SupportedOSPlatform("windows")]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunUpdateHelper(string[] args, StartupOptions options, string staging)
    {
        var settings = UserSettings.Load();
        if (ChooseUiCulture(options.Language, settings.Current.Language) is { } culture)
        {
            Strings.Culture = culture;
        }

        var config = UserDataPaths.ConfigDirectory();
        var startup = new UpdateHelperStartup(settings, shutdown => new Platform.Windows.WindowsUpdateHelper(
            new Platform.Windows.WindowsUpdateHelperSetup(
                staging, config, UpdateTrust.Keys, PlatformUpdate.RunningVersion(), new SystemProcessLauncher(), shutdown)
            {
                AfterProcess = options.AfterProcess,
                RestoreSession = options.RestoreSession,
                Language = options.Language,
            }));
        var builder = AppBuilder.Configure(() => new App(startup)).UsePlatformDetect();
        if (UseSoftwareRendering())
        {
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }

        return builder.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Handles <c>--register-file-types</c> and <c>--unregister-file-types</c> (Windows): exit code 0, or 1 with
    /// <c>error: …</c> on stderr, e.g. for the machine scope without elevation.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static int RunFileTypeRegistration(StartupOptions options)
    {
        try
        {
            if (options.RegisterFileTypes is { } register)
            {
                var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The path of the program is unknown.");
                Platform.Windows.WindowsFileTypeRegistry.For(register).Register(exe);
            }

            if (options.UnregisterFileTypes is { } unregister)
            {
                Platform.Windows.WindowsFileTypeRegistry.For(unregister).Unregister();
            }

            return 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// The history of recently opened files of this run: <paramref name="load"/> (<see cref="RecentFilesService.Load()"/>,
    /// which reads nothing yet) for a normal launch; for a measurement one in memory only
    /// (<see cref="RecentFilesService.Detached"/>), so a measurement never reads or writes the user's history.
    /// </summary>
    internal static RecentFilesService LoadRecentFiles(StartupOptions options, Func<RecentFilesService> load)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(load);
        return options.IsMeasurement ? RecentFilesService.Detached() : load();
    }

    /// <summary>
    /// The UI language: <c>--lang &lt;code&gt;</c> first, then the setting, then (<see langword="null"/>) the system
    /// language. An unknown code counts as not given. A change of the setting applies at the next start.
    /// </summary>
    internal static CultureInfo? ChooseUiCulture(string? languageOption, LanguageSetting setting)
    {
        if (languageOption is not null)
        {
            try
            {
                return new CultureInfo(languageOption);
            }
            catch (CultureNotFoundException)
            {
                // Unknown language code: as if it was not given.
            }
        }

        return setting switch
        {
            LanguageSetting.German => new CultureInfo("de"),
            LanguageSetting.English => new CultureInfo("en"),
            _ => null,
        };
    }

    /// <summary>
    /// The settings of this run: <paramref name="load"/> (<see cref="UserSettings.Load()"/>) for a normal launch; for a
    /// measurement the defaults in memory (<see cref="UserSettings.Detached"/>), so the settings of the user never change
    /// what is measured and a measurement never writes them. A measurement with <c>--measure-settings &lt;file&gt;</c>
    /// starts with what that file holds, in memory only (<see cref="UserSettings.DetachedFrom"/>: read, never written).
    /// </summary>
    internal static UserSettings LoadSettings(StartupOptions options, Func<UserSettings> load)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(load);
        if (!options.IsMeasurement)
        {
            return load();
        }

        return options.MeasureSettings is { Length: > 0 } file ? UserSettings.DetachedFrom(file) : UserSettings.Detached();
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    // Kept out of Main, so that a later launch that hands over never loads or initializes Avalonia.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args, AppStartup startup, bool softwareRendering)
    {
        StartupPhases.Mark("avalonia-configure");
        var builder = AppBuilder.Configure(() => new App(startup)).UsePlatformDetect();
        StartupPhases.Mark("platform-detected");
        if (softwareRendering)
        {
            // Software as the only mode: Avalonia's Win32 platform then creates no platform graphics at all (no
            // Direct3D device, no ANGLE display) and registers no composition (WinUI, DirectComposition, DXGI).
            builder = builder.With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
        }

        if (StartupPhases.Current is { } phases)
        {
            builder = WithSetupPhases(builder, phases);
        }

        StartupPhases.Mark("desktop-lifetime-start");
        return builder.StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Measurement runs only: records the setup steps of Avalonia as phases (<c>bench/startup/README.md</c>,
    /// "Phasen"). Each subsystem initializer that <see cref="AppBuilderDesktopExtensions.UsePlatformDetect"/> chose
    /// (runtime, text shaping, rendering, windowing; Avalonia runs them in this order) is wrapped in
    /// <c>&lt;name&gt;-init</c> and <c>&lt;name&gt;-ready</c>; the windowing step on Windows includes the graphics
    /// (ANGLE with its Direct3D device, or nothing for software rendering) and the composition. Two callbacks mark
    /// the end of the platform setup (<c>platform-services-setup</c>) and the end of <c>Application.Initialize</c>
    /// (<c>app-setup-done</c>). The chosen subsystems and their order are unchanged.
    /// </summary>
    internal static AppBuilder WithSetupPhases(AppBuilder builder, PhaseRecorder phases)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(phases);
        if (builder.RuntimePlatformServicesInitializer is { } runtime)
        {
            builder = builder.UseRuntimePlatformSubsystem(Marked(runtime, "runtime", phases), builder.RuntimePlatformServicesName ?? string.Empty);
        }

        if (builder.TextShapingSubsystemInitializer is { } textShaping)
        {
            builder = builder.UseTextShapingSubsystem(Marked(textShaping, "text-shaping", phases), builder.TextShapingSubsystemName ?? string.Empty);
        }

        if (builder.RenderingSubsystemInitializer is { } rendering)
        {
            builder = builder.UseRenderingSubsystem(Marked(rendering, "render", phases), builder.RenderingSubsystemName ?? string.Empty);
        }

        if (builder.WindowingSubsystemInitializer is { } windowing)
        {
            builder = builder.UseWindowingSubsystem(Marked(windowing, "windowing", phases), builder.WindowingSubsystemName ?? string.Empty);
        }

        return builder
            .AfterPlatformServicesSetup(_ => phases.Mark("platform-services-setup", Stopwatch.GetTimestamp()))
            .AfterSetup(_ => phases.Mark("app-setup-done", Stopwatch.GetTimestamp()));
    }

    private static Action Marked(Action initializer, string name, PhaseRecorder phases) => () =>
    {
        phases.Mark(name + "-init", Stopwatch.GetTimestamp());
        initializer();
        phases.Mark(name + "-ready", Stopwatch.GetTimestamp());
    };

    /// <summary>
    /// Once per process, before Avalonia starts and never in a later launch that hands over: on Windows software
    /// rendering unless <c>PAGEWING_RENDERING=default</c> asks for Avalonia's default, the GPU path
    /// (<see cref="RenderingModeSelector"/>); elsewhere Avalonia's default. Marked on Windows as the phase
    /// <c>rendering-software</c> or <c>rendering-default</c>.
    /// </summary>
    private static bool UseSoftwareRendering()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var software = RenderingModeSelector.UseSoftwareRendering(
            Environment.GetEnvironmentVariable(RenderingModeSelector.OverrideVariable), isWindows: true);
        StartupPhases.Mark(software ? "rendering-software" : "rendering-default");
        return software;
    }

    /// <summary>
    /// Returns the primary instance, or no instance with <c>IsSecondary</c> set when another one runs. A measurement
    /// never takes part: it must neither hand its file to a running Pagewing nor hold the lock and the pipe of one.
    /// </summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="tryBecomePrimary"><see cref="SingleInstance.TryBecomePrimary"/> with the instance name.</param>
    internal static (SingleInstance? Instance, bool IsSecondary) ElectInstance(StartupOptions options, Func<SingleInstance?> tryBecomePrimary)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tryBecomePrimary);
        if (options.IsMeasurement)
        {
            return (null, false);
        }

        try
        {
            var instance = tryBecomePrimary();
            return (instance, instance is null);
        }
        catch (ArgumentException)
        {
            // The user name makes no valid instance name: start uncoordinated rather than not at all.
            return (null, false);
        }
    }

    /// <summary>
    /// Ends a measurement whose UI thread no longer reacts: its own time limit (<see cref="MeasurementRun"/>) is
    /// posted to that thread and would never run.
    /// </summary>
    private static void StartMeasurementWatchdog() =>
        new Thread(static () =>
        {
            Thread.Sleep(MeasurementRun.DefaultTimeout + MeasurementWatchdogGrace);
            Console.Error.WriteLine("error: the measurement did not end (the UI thread does not respond)");
            Environment.Exit(MeasurementRun.TimedOut);
        })
        { IsBackground = true, Name = "Measurement watchdog" }.Start();

    /// <summary>Hands the full paths (or, without paths, only the request to come to the front) to the running instance.</summary>
    private static bool HandOver(IReadOnlyList<string> paths)
    {
        if (OperatingSystem.IsWindows())
        {
            // This process was started by the user and may take the foreground, the running instance may not:
            // let it, so that it can come to the front. Best effort; nothing depends on the result.
            _ = AllowSetForegroundWindow(AsfwAny);
        }

        try
        {
            return SingleInstance.TrySend(SingleInstance.DefaultName, paths, HandOverTimeout);
        }
        catch (Exception)
        {
            // TrySend may throw in rare cases (for example a socket path that is too long): any failure to hand
            // over means running as an extra instance instead of not starting at all.
            return false;
        }
    }

    private const int AsfwAny = -1;

    [LibraryImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static partial int AllowSetForegroundWindow(int processId);
}
