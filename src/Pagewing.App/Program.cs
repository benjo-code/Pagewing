using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Pagewing.App.Measurement;
using Pagewing.App.Resources;
using Pagewing.Core.Hosting;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

internal static partial class Program
{
    /// <summary>How long a later launch tries to hand its files to the running instance.</summary>
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(3);

    /// <summary>A measurement whose UI thread hangs is ended this long after its own time limit (exit code 3).</summary>
    private static readonly TimeSpan MeasurementWatchdogGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Startup order (startup path, keep it lean): parse the options; elect the single instance before Avalonia is
    /// loaded, and as a later launch hand the files over and exit; otherwise start opening the file of the command
    /// line at once, so PDFium loads on its own thread while Avalonia builds the window; show the window, whose first
    /// frame shows the tab with the file name; accept later launches after the first frame (<see cref="App"/>).
    /// A measurement switch takes the same path, without the election (see <see cref="ElectInstance"/>).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var options = StartupOptions.Parse(args);
        if (options.Language is { } language)
        {
            try
            {
                Strings.Culture = new CultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                // Unknown language code: keep the system language.
            }
        }

        var (instance, isSecondary) = ElectInstance(options, static () => SingleInstance.TryBecomePrimary(SingleInstance.DefaultName));
        if (isSecondary && HandOver(options.Paths))
        {
            return 0;
        }

        // Primary, a measurement, or an extra instance without the lock when handing over failed (the running one
        // hangs or is closing).
        var session = new ViewerSession(new PdfEngine());
        var path = options.IsMeasurement ? options.MeasurementPath : options.Paths.Count > 0 ? options.Paths[0] : null;
        if (!string.IsNullOrEmpty(path))
        {
            // Sets Loading with the path at once and continues on the PDFium thread; failures end in an error card.
            _ = session.OpenAsync(path);
        }

        if (options.IsMeasurement)
        {
            StartMeasurementWatchdog();
        }

        return Run(args, new AppStartup(session, instance, options.IsMeasurement ? options : null));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    // Kept out of Main, so that a later launch that hands over never loads or initializes Avalonia.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args, AppStartup startup) =>
        AppBuilder.Configure(() => new App(startup))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);

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
