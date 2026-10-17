using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Pagewing.App.Resources;
using Pagewing.Core.Hosting;
using Pagewing.Core.Pdf;
using Pagewing.Core.Viewing;

namespace Pagewing.App;

internal static partial class Program
{
    /// <summary>How long a later launch tries to hand its files to the running instance.</summary>
    private static readonly TimeSpan HandOverTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Startup order (startup path, keep it lean): parse the options; elect the single instance before Avalonia is
    /// loaded, and as a later launch hand the files over and exit; otherwise start opening the file of the command
    /// line at once, so PDFium loads on its own thread while Avalonia builds the window; show the window, whose first
    /// frame shows the tab with the file name; accept later launches after the first frame (<see cref="App"/>).
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

        var instance = ElectInstance(out var isSecondary);
        if (isSecondary && HandOver(options.Paths))
        {
            return 0;
        }

        // Primary, or an extra instance without the lock when handing over failed (the running one hangs or is closing).
        var session = new ViewerSession(new PdfEngine());
        if (options.Paths.Count > 0)
        {
            // Sets Loading with the path at once and continues on the PDFium thread; failures end in an error card.
            _ = session.OpenAsync(options.Paths[0]);
        }

        return Run(args, new AppStartup(session, instance));
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();

    // Kept out of Main, so that a later launch that hands over never loads or initializes Avalonia.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string[] args, AppStartup startup) =>
        AppBuilder.Configure(() => new App(startup))
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);

    /// <summary>Returns the primary instance, or <see langword="null"/> with <paramref name="isSecondary"/> set when another one runs.</summary>
    private static SingleInstance? ElectInstance(out bool isSecondary)
    {
        isSecondary = false;
        try
        {
            var instance = SingleInstance.TryBecomePrimary(SingleInstance.DefaultName);
            isSecondary = instance is null;
            return instance;
        }
        catch (ArgumentException)
        {
            // The user name makes no valid instance name: start uncoordinated rather than not at all.
            return null;
        }
    }

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
