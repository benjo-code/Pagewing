using System.Globalization;
using System.Text;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Updates;

namespace Pagewing.App.Platform.Linux;

/// <summary>What the installer of an AppImage needs.</summary>
/// <param name="TargetPath">The running AppImage: the path in the <c>APPIMAGE</c> variable (a temporary file in tests).</param>
/// <param name="ConfigDirectory">The config folder of the user, whose <c>updates</c> folder holds the staged update and where <c>session.json</c> goes.</param>
/// <param name="Keys">The keys the manifest must be signed with (<see cref="UpdateTrust.Keys"/>; test keys only in tests).</param>
/// <param name="Running">The running version: the manifest must be newer.</param>
/// <param name="ProcessId">The process of the app, which the new program waits for (<c>--after-process</c>).</param>
/// <param name="CaptureSession">The windows and tabs to bring back (<see cref="Workspace.CaptureSession"/>); called on the UI thread.</param>
/// <param name="Shutdown">Ends the app once the new program runs.</param>
/// <param name="Launcher">Starts the new AppImage.</param>
internal sealed record AppImageUpdaterSetup(
    string TargetPath,
    string ConfigDirectory,
    IReadOnlyList<UpdateKey> Keys,
    UpdateVersion Running,
    int ProcessId,
    Func<SessionSnapshot> CaptureSession,
    IAppShutdown Shutdown,
    IProcessLauncher Launcher)
{
    /// <summary>Gets the <c>--lang</c> the app was started with, which the new program gets as well.</summary>
    public string? Language { get; init; }
}

/// <summary>
/// Installs a verified update of an AppImage (<see cref="PackageForm.AppImage"/>): the AppImage replaces itself. The
/// running file stays valid through its open inode, so the file can be swapped while the app runs, before it ends.
/// Steps, none of which touches the old file before the new one is complete and checked: the manifest of the staging
/// folder is verified again with the keys of the program; the staged file is copied to <c>&lt;AppImage&gt;.new</c> next
/// to the old one (the same file system, so the last step is atomic) and given the execute bit; the copy is checked
/// against the size and SHA-256 of the manifest; the session is written (<c>session.json</c>, only then is
/// <c>--restore-session</c> passed on); the copy is renamed over the old file; the new AppImage starts with
/// <c>--after-process &lt;this process&gt;</c> (<see cref="Program.WaitForPredecessor"/>: it waits until this process has
/// ended before it elects the instance) and the app ends the regular way.
/// </summary>
/// <remarks>
/// A folder this user cannot write to (a read-only folder or mount) fails with the text for the download page; any other
/// failure leaves the old file and the app as they are, the window shows the error with "Try again" and "Close". The
/// staging folder is left to the coordinator on a failure (it removes it) and deleted here once the file is replaced.
/// </remarks>
internal sealed class AppImageUpdater(AppImageUpdaterSetup setup) : IUpdateApplier
{
    /// <summary>The suffix of the copy next to the AppImage.</summary>
    public const string NewSuffix = ".new";

    public async Task<ApplyResult> ApplyAsync(StagedUpdate staged, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(staged);
        if (staged.Form != PackageForm.AppImage)
        {
            return ApplyResult.Failed(offersDownloadPage: true);
        }

        ct.ThrowIfCancellationRequested();

        // The windows as they are at this moment, on the UI thread; everything else happens on the pool.
        var snapshot = UpdateApplierSupport.Capture(setup.CaptureSession);
        var result = await Task.Run(() => SwapAsync(staged, snapshot, ct), ct).ConfigureAwait(true);
        if (result.IsRestarting)
        {
            // Taken over: from here nothing throws (IUpdateApplier). The new program waits until this process has ended.
            setup.Shutdown.Shutdown();
        }

        return result;
    }

    /// <summary>Gets the command line of the new AppImage.</summary>
    public static IReadOnlyList<string> NewProgramArguments(int processId, bool restoreSession, string? language)
    {
        var arguments = new List<string> { "--after-process", processId.ToString(CultureInfo.InvariantCulture) };
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

    private static ApplyResult Refused(string message, bool offersDownloadPage = false) => ApplyResult.Failed(message, offersDownloadPage);

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that cannot be deleted is overwritten by the next attempt.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next run of Pagewing removes what is left (UpdateStaging.DeleteOld).
        }
    }

    private async Task<ApplyResult> SwapAsync(StagedUpdate staged, SessionSnapshot snapshot, CancellationToken ct)
    {
        var verified = VerifyManifest(staged);
        if (verified.Failure is { } failure)
        {
            return failure;
        }

        var target = setup.TargetPath;
        if (!Path.IsPathFullyQualified(target) || Path.GetDirectoryName(target) is not { Length: > 0 } folder || !File.Exists(target))
        {
            // Not an AppImage we can replace (the variable points nowhere): only the download page helps.
            return Refused(Strings.UpdateErrorNoInstaller, offersDownloadPage: true);
        }

        var copy = target + NewSuffix;
        var file = verified.File!;
        var created = false;
        try
        {
            if (Copy(Path.Combine(verified.Staging!, file.Name), copy, folder, target, out var failed) is { } copyFailure)
            {
                created = failed;
                return copyFailure;
            }

            created = true;

            // The copy is what runs next, so the copy is what is checked (the staged file may have changed since).
            if (!await ChecksumVerifier.MatchesAsync(copy, file.Sha256, file.Size, ct).ConfigureAwait(false))
            {
                return Refused(Strings.UpdateErrorChecksum);
            }

            // From here the update is taken: nothing below observes the cancellation token.
            var restore = UpdateApplierSupport.WriteSession(setup.ConfigDirectory, snapshot);
            try
            {
                File.Move(copy, target, overwrite: true);
                created = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                UpdateApplierSupport.DeleteSession(setup.ConfigDirectory);
                return Refused(Strings.UpdateErrorInstallFailed);
            }

            if (!setup.Launcher.Start(target, NewProgramArguments(setup.ProcessId, restore, setup.Language)))
            {
                // The file is replaced (the next start is the new version) but nothing waits for the session.
                UpdateApplierSupport.DeleteSession(setup.ConfigDirectory);
                return Refused(Strings.UpdateErrorInstallFailed);
            }

            TryDeleteDirectory(verified.Staging!);
            return ApplyResult.Restarting;
        }
        finally
        {
            if (created)
            {
                TryDeleteFile(copy);
            }
        }
    }

    /// <summary>
    /// The manifest of the staging folder, verified again: the folder is one of the updates, the signature holds with the
    /// keys of the program, the version is the one staged (and newer than the running one) and the file is the one
    /// staged, with no path in its name.
    /// </summary>
    private Verified VerifyManifest(StagedUpdate staged)
    {
        if (UpdateApplierSupport.AcceptedStaging(staged.Directory, setup.ConfigDirectory) is not { } staging)
        {
            return Verified.Refused(Refused(Strings.UpdateErrorRejected));
        }

        var manifestBytes = UpdateApplierSupport.ReadSmall(Path.Combine(staging, UpdateStaging.ManifestFileName));
        var signature = UpdateApplierSupport.ReadSmall(Path.Combine(staging, UpdateStaging.SignatureFileName));
        if (manifestBytes is null || signature is null)
        {
            return Verified.Refused(Refused(Strings.UpdateErrorRejected));
        }

        var result = ManifestVerifier.Verify(manifestBytes, Encoding.UTF8.GetString(signature), setup.Keys, setup.Running);
        if (result.Manifest is not { } manifest
            || !UpdateVersion.TryParse(manifest.Version, out var version) || version != staged.Version
            || !UpdateVersion.TryParse(Path.GetFileName(staging), out var folderVersion) || folderVersion != version
            || UpdateChecker.Pick(manifest, PackageForm.AppImage) is not { } file
            || file.Name != staged.File.Name || file.Name != Path.GetFileName(file.Name))
        {
            return Verified.Refused(Refused(Strings.UpdateErrorRejected));
        }

        return new Verified(null, file, staging);
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="copy"/> and sets the mode of the old file plus the execute
    /// bit; <see langword="null"/> when it worked, else the failure (<paramref name="copyExists"/> says whether a file
    /// of ours is left to delete).
    /// </summary>
    private static ApplyResult? Copy(string source, string copy, string folder, string target, out bool copyExists)
    {
        copyExists = false;
        FileStream destination;
        try
        {
            destination = new FileStream(copy, FileMode.Create, FileAccess.Write, FileShare.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing could be created: a write-protected folder (or a read-only mount), which the user solves by hand.
            return Refused(
                string.Format(Strings.Culture, CompositeFormat.Parse(Strings.UpdateErrorFolderWriteProtected), folder),
                offersDownloadPage: true);
        }

        copyExists = true;
        try
        {
            using (destination)
            {
                using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                input.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(copy, ExecutableModeOf(target));
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A full disk or a staged file that is gone.
            return Refused(Strings.UpdateErrorStorage);
        }
    }

    // The mode of the old file with the execute bit for the owner (an AppImage that is not executable cannot start).
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static UnixFileMode ExecutableModeOf(string target)
    {
        try
        {
            return File.GetUnixFileMode(target) | UnixFileMode.UserExecute;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        }
    }

    /// <summary>The manifest after the check: refused with a result, or the file to install and the staging folder.</summary>
    private sealed record Verified(ApplyResult? Failure, UpdateFile? File, string? Staging)
    {
        public static Verified Refused(ApplyResult failure) => new(failure, null, null);
    }
}
