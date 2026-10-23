using System.ComponentModel;
using System.Diagnostics;
using Pagewing.App.Services;
using Pagewing.Core.Updates;

namespace Pagewing.App.Platform.Windows;

/// <summary>Starts programs for the updater; the tests replace it, so no test ever starts a real process.</summary>
internal interface IProcessLauncher
{
    /// <summary>Starts <paramref name="file"/> with <paramref name="arguments"/> (quoted like any .NET program expects them) and does not wait; <see langword="false"/> when it could not be started.</summary>
    bool Start(string file, IReadOnlyList<string> arguments);

    /// <summary>
    /// Runs <paramref name="file"/> with <paramref name="commandLine"/> exactly as given (a program built with Inno Setup
    /// splits its command line itself, <see cref="InnoCommandLine"/>) and returns its exit code once it ended. Throws when
    /// it could not be started.
    /// </summary>
    Task<int> RunAsync(string file, string commandLine);

    /// <summary>
    /// Waits until the process <paramref name="processId"/> has ended (at once when it does not run), at most
    /// <paramref name="timeout"/>; <see langword="false"/> when it still runs then. Throws only for cancellation.
    /// </summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The processes of the system.</summary>
internal sealed class SystemProcessLauncher : IProcessLauncher
{
    public bool Start(string file, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public async Task<int> RunAsync(string file, string commandLine)
    {
        // No time limit: the setup program may wait for the answer to the UAC prompt, and stopping it halfway would leave
        // a half-replaced program behind.
        var info = new ProcessStartInfo(file, commandLine) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty };
        using var process = Process.Start(info) ?? throw new IOException("The setup program did not start: " + file);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Not running (any more).
            return true;
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process cannot be waited for (no access); the setup program closes what holds its files.
                return false;
            }
        }
    }
}

/// <summary>
/// Command lines for a program built with Inno Setup, which splits them itself and not like the C runtime: outside quotes
/// a character up to <c>' '</c> separates, a <c>'"'</c> toggles quoting and is dropped, a backslash is literal (GetParamStr
/// in jrsoftware/issrc; the same rules as <c>InnoArguments</c> in <c>tools/package.cs</c>). An argument can therefore never
/// contain <c>'"'</c>, which no Windows path does.
/// </summary>
internal static class InnoCommandLine
{
    /// <summary>Joins the arguments, each quoted when it has to be.</summary>
    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    /// <summary>Quotes an argument that is empty or contains a space or a control character.</summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (argument.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("An argument with a quotation mark cannot be passed to Inno Setup.", nameof(argument));
        }

        return argument.Length > 0 && argument.All(c => c > ' ') ? argument : "\"" + argument + "\"";
    }
}

/// <summary>What the Windows installer of the app needs.</summary>
/// <param name="ProgramDirectory">The folder of the running program, which is copied for the helper.</param>
/// <param name="UpdaterDirectory">Where the copies go (<c>%LOCALAPPDATA%\Pagewing\updater</c>, a temporary folder in tests).</param>
/// <param name="ConfigDirectory">The config folder of the user, where <c>session.json</c> goes.</param>
/// <param name="ProcessId">The process of the app, which the helper waits for.</param>
/// <param name="CaptureSession">The windows and tabs to bring back (<see cref="Workspace.CaptureSession"/>); called on the UI thread.</param>
/// <param name="Shutdown">Ends the app once the helper runs.</param>
/// <param name="Launcher">Starts the helper.</param>
/// <param name="Clock">Decides which old copies are left behind.</param>
internal sealed record WindowsUpdateInstallerSetup(
    string ProgramDirectory,
    string UpdaterDirectory,
    string ConfigDirectory,
    int ProcessId,
    Func<SessionSnapshot> CaptureSession,
    IAppShutdown Shutdown,
    IProcessLauncher Launcher,
    TimeProvider Clock)
{
    /// <summary>Gets the <c>--lang</c> the app was started with, which the helper and the restarted app get as well.</summary>
    public string? Language { get; init; }
}

/// <summary>
/// Installs a verified update on Windows (<see cref="PackageForm.WindowsInstaller"/>). Windows cannot replace the running
/// program, so it hands over to a helper: it copies the program folder (with the native libraries next to the program)
/// to <c>%LOCALAPPDATA%\Pagewing\updater\&lt;guid&gt;\</c> (copies older than <see cref="CopyLifetime"/> are deleted
/// first), writes the session (<c>session.json</c>, only then is <c>--restore-session</c> passed on), starts the copy with
/// <c>--finish-update &lt;staging folder&gt; --after-process &lt;this process&gt;</c> and ends the app the regular way. The
/// helper (<see cref="WindowsUpdateHelper"/>) verifies again, installs and starts Pagewing again.
/// </summary>
internal sealed class WindowsUpdateInstaller(WindowsUpdateInstallerSetup setup) : IUpdateApplier
{
    /// <summary>The name of the program in its folder and in the copy.</summary>
    public const string ExeName = "Pagewing.exe";

    /// <summary>How long a copy of the helper may stay before the next installation deletes it.</summary>
    public static readonly TimeSpan CopyLifetime = TimeSpan.FromDays(1);

    /// <summary>Gets the folder of the helper copies below <paramref name="localAppData"/>.</summary>
    public static string UpdaterDirectoryIn(string localAppData) => Path.Combine(localAppData, "Pagewing", "updater");

    /// <summary>Gets the command line of the helper.</summary>
    public static IReadOnlyList<string> HelperArguments(string staging, int processId, bool restoreSession, string? language)
    {
        var arguments = new List<string> { "--finish-update", staging, "--after-process", processId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        if (restoreSession)
        {
            arguments.Add("--restore-session");
        }

        if (!string.IsNullOrEmpty(language))
        {
            arguments.Add("--lang");
            arguments.Add(language);
        }

        return arguments;
    }

    public async Task<ApplyResult> ApplyAsync(StagedUpdate staged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Form != PackageForm.WindowsInstaller)
        {
            return ApplyResult.Failed(offersDownloadPage: true);
        }

        ct.ThrowIfCancellationRequested();

        // The windows as they are at this moment, on the UI thread; everything else happens on the pool.
        SessionSnapshot snapshot;
        try
        {
            snapshot = setup.CaptureSession();
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            snapshot = SessionSnapshot.Empty;
        }

        var now = setup.Clock.GetUtcNow();
        var copy = await Task.Run(
            () =>
            {
                DeleteOldCopies(setup.UpdaterDirectory, now);
                return CopyProgram(setup.ProgramDirectory, setup.UpdaterDirectory);
            },
            ct).ConfigureAwait(true);
        if (copy is null)
        {
            return ApplyResult.Failed();
        }

        // A session file of an earlier attempt must never come back: delete it, and restore only what was written now.
        var store = new SessionStore(Path.Combine(setup.ConfigDirectory, SessionStore.FileName));
        store.Delete();
        var restore = snapshot.Windows.Count > 0 && store.TrySave(snapshot);

        if (!setup.Launcher.Start(Path.Combine(copy, ExeName), HelperArguments(staged.Directory, setup.ProcessId, restore, setup.Language)))
        {
            store.Delete();
            TryDelete(copy);
            return ApplyResult.Failed();
        }

        // Taken over: from here nothing throws (IUpdateApplier). The helper waits until this process has ended.
        setup.Shutdown.Shutdown();
        return ApplyResult.Restarting;
    }

    /// <summary>Copies the files of the program folder (the program and its native libraries); <see langword="null"/> on a file system failure.</summary>
    private static string? CopyProgram(string programDirectory, string updaterDirectory)
    {
        var copy = Path.Combine(updaterDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.EnumerateFiles(programDirectory))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            return File.Exists(Path.Combine(copy, ExeName)) ? copy : Discard(copy);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Discard(copy);
        }
    }

    private static string? Discard(string copy)
    {
        TryDelete(copy);
        return null;
    }

    /// <summary>Deletes the copies (folders named by a GUID) last written more than <see cref="CopyLifetime"/> ago.</summary>
    private static void DeleteOldCopies(string updaterDirectory, DateTimeOffset now)
    {
        try
        {
            if (!Directory.Exists(updaterDirectory))
            {
                return;
            }

            foreach (var folder in Directory.EnumerateDirectories(updaterDirectory))
            {
                if (Guid.TryParseExact(Path.GetFileName(folder), "N", out _) && now - Directory.GetLastWriteTimeUtc(folder) > CopyLifetime)
                {
                    TryDelete(folder);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cleaning up is a courtesy; the next installation tries again.
        }
    }

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that still runs cannot be deleted; the next installation tries again.
        }
    }
}
