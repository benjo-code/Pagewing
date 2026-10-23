using System.Text;
using Pagewing.App.Services;
using Pagewing.Core.Updates;

namespace Pagewing.App.Platform.Windows;

/// <summary>What the helper of the Windows updater needs (<c>--finish-update</c>).</summary>
/// <param name="StagingDirectory">The folder of the command line; only a folder directly below <c>&lt;config&gt;\updates</c> is accepted.</param>
/// <param name="ConfigDirectory">The config folder of the user (a temporary folder in tests).</param>
/// <param name="Keys">The keys the manifest must be signed with (<see cref="UpdateTrust.Keys"/>; test keys only in tests).</param>
/// <param name="Running">The version of the helper, which is the installed one: the manifest must be newer.</param>
/// <param name="Launcher">Runs the setup program and starts Pagewing again.</param>
/// <param name="Shutdown">Ends the helper.</param>
internal sealed record WindowsUpdateHelperSetup(
    string StagingDirectory,
    string ConfigDirectory,
    IReadOnlyList<UpdateKey> Keys,
    UpdateVersion Running,
    IProcessLauncher Launcher,
    IAppShutdown Shutdown)
{
    /// <summary>How long the helper waits for the app that started it to end before it installs anyway.</summary>
    public static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Gets the process of the app that started the helper (<c>--after-process</c>).</summary>
    public int? AfterProcess { get; init; }

    /// <summary>Gets whether the app wrote the session (<c>--restore-session</c>): Pagewing then starts again with it.</summary>
    public bool RestoreSession { get; init; }

    /// <summary>Gets the <c>--lang</c> of the app, passed on to the restarted one.</summary>
    public string? Language { get; init; }

    /// <summary>Gets how long the helper waits for the app to end.</summary>
    public TimeSpan ExitTimeout { get; init; } = DefaultExitTimeout;
}

/// <summary>
/// The helper of the Windows updater: a copy of Pagewing started with <c>--finish-update &lt;staging folder&gt;</c> after
/// the app downloaded and verified an update and ended (<see cref="WindowsUpdateInstaller"/>). It takes nothing but the
/// <c>request.json</c> of a folder directly below <c>&lt;config&gt;\updates</c>, waits until the app has ended, verifies
/// the signature of the saved manifest with the keys of the program and the SHA-256 of the setup program again (the files
/// lay on the disk in between) and keeps the setup program open against writing until it ended, then runs it silently
/// (<see cref="InnoArguments"/>; Inno Setup keeps the install mode and folder of the installation through its fixed
/// <c>AppId</c>). Exit code 0: Pagewing starts again from the installed folder, with <c>--restore-session</c> when the app
/// wrote the session, and the helper ends. Anything else (the UAC prompt declined, a failing setup program): the installed
/// version starts again and the window shows the error with "Try again" (the setup program once more) and "Close". The
/// staging folder is deleted only after success or "Close". The progress window shows it (<see cref="IUpdateFlow"/>);
/// used on the UI thread.
/// </summary>
internal sealed class WindowsUpdateHelper : IUpdateFlow, IDisposable
{
    /// <summary>The log of the setup program in the staging folder.</summary>
    public const string LogFileName = "setup.log";

    private readonly WindowsUpdateHelperSetup _setup;
    private readonly string? _staging;
    private readonly UpdateVersion? _version;
    private readonly CancellationTokenSource _cancel = new();
    private UpdateFile? _file;
    private byte[]? _session;
    private Task? _running;
    private bool _retryable;
    private bool _installedStarted;

    public WindowsUpdateHelper(WindowsUpdateHelperSetup setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        _setup = setup;
        _staging = UpdateApplierSupport.AcceptedStaging(setup.StagingDirectory, setup.ConfigDirectory);
        var request = _staging is null ? null : UpdateRequest.Load(Path.Combine(_staging, UpdateStaging.RequestFileName));
        if (request is not null && Usable(request, _staging!) is { } version)
        {
            Request = request;
            _version = version;
            State = new UpdateState(UpdateStage.Verifying, version, null, 0, null);
        }
        else
        {
            // Not an update of ours: nothing is run, nothing is started, and "Close" deletes nothing outside the updates.
            FailedAt = UpdateStage.Verifying;
            State = new UpdateState(UpdateStage.Failed, null, null, 0, UpdateFailure.Rejected);
        }
    }

    public event Action? Changed;

    /// <summary>Gets the request of the app (where its progress window was), or <see langword="null"/> when it was refused.</summary>
    public UpdateRequest? Request { get; }

    public UpdateState State { get; private set; }

    public PackageForm Form => PackageForm.WindowsInstaller;

    public UpdateStage FailedAt { get; private set; }

    public string? FailureMessage => null;

    public bool FailureOffersDownloadPage => false;

    public bool CanRetry => State.Stage == UpdateStage.Failed && _retryable;

    private string InstalledExe => Path.Combine(Request!.TargetDirectory!, WindowsUpdateInstaller.ExeName);

    private string SessionPath => Path.Combine(_setup.ConfigDirectory, SessionStore.FileName);

    /// <summary>Gets the command line of the setup program: very silent, no message boxes, no restart, the log in <paramref name="staging"/>.</summary>
    public static string InnoArguments(string staging) =>
        InnoCommandLine.Join(["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=" + Path.Combine(staging, LogFileName)]);

    /// <summary>
    /// Waits for the app, then verifies and installs (see the class). Once: a second call gets the running one. Never throws.
    /// </summary>
    public Task RunAsync() => _running ??= RunCoreAsync();

    public Task BeginInstallAsync() => Task.CompletedTask;

    /// <summary>
    /// "Cancel" (also closing the window) while the helper waits for the app or verifies: the installed version starts
    /// again, the update goes and the helper ends, at once (the process may end with the window before the check stops).
    /// </summary>
    public void CancelInstall()
    {
        if (State.Stage == UpdateStage.Verifying)
        {
            _cancel.Cancel();
            Abandon();
        }
    }

    /// <summary>"Try again": the setup program once more, after verifying again.</summary>
    public Task RetryAsync() => CanRetry ? _running = AttemptAsync() : Task.CompletedTask;

    /// <summary>"Close" after a failure: deletes the update and ends the helper (the installed version runs already).</summary>
    public void Dismiss()
    {
        if (State.Stage != UpdateStage.Failed)
        {
            return;
        }

        DeleteStaging();
        SetState(UpdateState.None);
        _setup.Shutdown.Shutdown();
    }

    /// <summary>Releases the cancellation of the helper (when the process ends).</summary>
    public void Dispose() => _cancel.Dispose();

    private static UpdateVersion? Usable(UpdateRequest request, string staging)
    {
        var target = request.TargetDirectory;
        var exe = request.ExePath;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var usable = UpdateVersion.TryParse(request.Version, out var version)
            && UpdateVersion.TryParse(Path.GetFileName(staging), out var folder) && folder == version
            && request.Form == PackageForm.WindowsInstaller
            && target is not null && Path.IsPathFullyQualified(target)
            && exe is not null && Path.IsPathFullyQualified(exe)
            && string.Equals(Path.GetFileName(exe), WindowsUpdateInstaller.ExeName, comparison)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(exe) ?? string.Empty), Path.TrimEndingDirectorySeparator(target), comparison);
        return usable ? version : null;
    }

    private async Task RunCoreAsync()
    {
        if (State.Stage != UpdateStage.Verifying)
        {
            return;
        }

        // The session the app wrote, kept for every restart (the installed version uses the file up after a failure).
        _session = _setup.RestoreSession ? UpdateApplierSupport.ReadSmall(SessionPath) : null;
        try
        {
            if (_setup.AfterProcess is { } process)
            {
                // A version that still runs would hold the files, and the restarted one would hand over to it.
                await _setup.Launcher.WaitForExitAsync(process, _setup.ExitTimeout, _cancel.Token).ConfigureAwait(true);
            }

            _cancel.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // Abandoned by "Cancel"; what was held open is free now, the rest of the update goes.
            DeleteStaging();
            return;
        }

        await AttemptAsync().ConfigureAwait(true);
    }

    private async Task AttemptAsync()
    {
        var ct = _cancel.Token;
        SetState(new UpdateState(UpdateStage.Verifying, _version, _file, _file?.Size ?? 0, null));
        int exitCode;
        try
        {
            var verified = await Task.Run(() => VerifyAsync(ct), ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                // "Cancel" came after the check, before the setup program (both on the UI thread): it never runs.
                verified.Hold?.Dispose();
                DeleteStaging();
                return;
            }

            if (verified.Error is { } error)
            {
                StartInstalled();
                Fail(error, UpdateStage.Verifying, retryable: false);
                return;
            }

            // Held open against writing from before the check until the setup program ended: what runs is what was checked.
            using (verified.Hold)
            {
                _file = verified.File;
                SetState(new UpdateState(UpdateStage.Installing, _version, _file, _file!.Size, null));
                exitCode = await RunSetupAsync(verified.Path!).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Abandoned by "Cancel"; what was held open is free now, the rest of the update goes.
            DeleteStaging();
            return;
        }

        // The setup program closes a Pagewing that holds its files (CloseApplications): what was started before may be gone.
        _installedStarted = false;
        if (exitCode == 0)
        {
            SetState(new UpdateState(UpdateStage.Restarting, _version, _file, _file!.Size, null));
            DeleteStaging();
            StartInstalled();
            _setup.Shutdown.Shutdown();
            return;
        }

        // The UAC prompt declined (Inno Setup reports a code other than 0) or the setup program failed.
        StartInstalled();
        Fail(UpdateFailure.InstallFailed, UpdateStage.Installing, retryable: true);
    }

    private async Task<int> RunSetupAsync(string path)
    {
        try
        {
            return await _setup.Launcher.RunAsync(path, InnoArguments(_staging!)).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Could not be started: as a failure of the setup program.
            return -1;
        }
    }

    private async Task<Verified> VerifyAsync(CancellationToken ct)
    {
        var staging = _staging!;
        var manifestBytes = UpdateApplierSupport.ReadSmall(Path.Combine(staging, UpdateStaging.ManifestFileName));
        var signature = UpdateApplierSupport.ReadSmall(Path.Combine(staging, UpdateStaging.SignatureFileName));
        if (manifestBytes is null || signature is null)
        {
            return Verified.Refused(UpdateFailure.Rejected);
        }

        var result = ManifestVerifier.Verify(manifestBytes, Encoding.UTF8.GetString(signature), _setup.Keys, _setup.Running);
        if (result.Manifest is not { } manifest
            || !UpdateVersion.TryParse(manifest.Version, out var version) || version != _version
            || UpdateChecker.Pick(manifest, PackageForm.WindowsInstaller) is not { } file)
        {
            return Verified.Refused(UpdateFailure.Rejected);
        }

        var path = Path.Combine(staging, file.Name);
        FileStream hold;
        try
        {
            hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Verified.Refused(UpdateFailure.Checksum);
        }

        try
        {
            if (await ChecksumVerifier.MatchesAsync(path, file.Sha256, file.Size, ct).ConfigureAwait(false))
            {
                return new Verified(null, file, path, hold);
            }
        }
        catch (OperationCanceledException)
        {
            await hold.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await hold.DisposeAsync().ConfigureAwait(false);
        return Verified.Refused(UpdateFailure.Checksum);
    }

    /// <summary>"Cancel": the installed version starts again, the update goes, the helper ends.</summary>
    private void Abandon()
    {
        StartInstalled();
        DeleteStaging();
        SetState(UpdateState.None);
        _setup.Shutdown.Shutdown();
    }

    /// <summary>
    /// Starts Pagewing from the installed folder, with the session of the app when it wrote one and the language it was
    /// started with; once until the setup program runs again (a second start would only hand over to the first).
    /// </summary>
    private void StartInstalled()
    {
        if (Request is null || _installedStarted)
        {
            return;
        }

        _installedStarted = true;
        var arguments = new List<string>();
        if (_session is { } session && TryWriteSession(session))
        {
            arguments.Add("--restore-session");
        }

        if (!string.IsNullOrEmpty(_setup.Language))
        {
            arguments.Add("--lang");
            arguments.Add(_setup.Language);
        }

        _setup.Launcher.Start(InstalledExe, arguments);
    }

    // The session file again, as the app wrote it: a restarted version has used it up (Program.LoadSession deletes it).
    private bool TryWriteSession(byte[] session)
    {
        var temp = SessionPath + ".tmp";
        try
        {
            File.WriteAllBytes(temp, session);
            File.Move(temp, SessionPath, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception again) when (again is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do.
            }

            return false;
        }
    }

    private void Fail(string error, UpdateStage at, bool retryable)
    {
        FailedAt = at;
        _retryable = retryable;
        SetState(State with { Stage = UpdateStage.Failed, Error = error });
    }

    private void DeleteStaging()
    {
        if (_staging is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_staging, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next run of Pagewing removes what is left (UpdateStaging.DeleteOld).
        }
    }

    private void SetState(UpdateState state)
    {
        State = state;
        if (Changed is not { } handlers)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // A handler of the window must not throw; one that does must not stop the installation.
            }
        }
    }

    /// <summary>The setup program after the check: refused with an error, or its path and the open file that holds it.</summary>
    private sealed record Verified(string? Error, UpdateFile? File, string? Path, FileStream? Hold)
    {
        public static Verified Refused(string error) => new(error, null, null, null);
    }
}
