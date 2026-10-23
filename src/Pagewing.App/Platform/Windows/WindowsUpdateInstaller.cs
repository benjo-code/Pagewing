using Pagewing.App.Services;
using Pagewing.Core.Updates;

namespace Pagewing.App.Platform.Windows;

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
        var snapshot = UpdateApplierSupport.Capture(setup.CaptureSession);

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
        var restore = UpdateApplierSupport.WriteSession(setup.ConfigDirectory, snapshot);

        if (!setup.Launcher.Start(Path.Combine(copy, ExeName), HelperArguments(staged.Directory, setup.ProcessId, restore, setup.Language)))
        {
            UpdateApplierSupport.DeleteSession(setup.ConfigDirectory);
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
