using System.Text;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>
/// The update check of the running process, shared by all windows: starts it once after the first sharp page (or the
/// first frame of the start screen) when the setting allows it, runs it on the thread pool with a time limit, keeps the
/// result and tells the windows. Failing is silent (no network, a timeout, a refused manifest, a source that throws:
/// nothing changes and nothing is shown). After "Install" it runs the installation once for the whole process
/// (<see cref="BeginInstallAsync"/>), where a failure is shown and can be tried again. Used on the UI thread, where
/// <see cref="Changed"/> is raised.
/// </summary>
/// <param name="checker">The schedule and the checks (<see cref="UpdateChecker"/>), made over the source of the app.</param>
/// <param name="settings">The settings; only <see cref="AppSettings.UpdateCheck"/> decides whether <see cref="StartAsync"/> checks.</param>
/// <param name="form">How the running program was installed; it decides how it can update itself.</param>
/// <param name="install">What installing needs; without it the coordinator only checks and "Install" does nothing.</param>
public sealed class UpdateCoordinator(UpdateChecker checker, UserSettings settings, PackageForm form, UpdateInstallSetup? install = null) : IDisposable, IUpdateFlow
{
    /// <summary>How long a download may go without receiving a byte before it counts as failed.</summary>
    public static readonly TimeSpan DownloadStallTimeout = TimeSpan.FromSeconds(60);

    // Beyond the limit of the check itself: a source that ignores its token must not hold the coordinator for ever.
    private static readonly TimeSpan OverallLimit = UpdateChecker.CheckTimeout + TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _running;
    private bool _disposed;

    // The installation: the one that runs (all windows and clicks share it), the source that cancels its download, the
    // number of the attempt (late progress of an earlier one is ignored), and where a failure goes back to.
    private Task? _install;
    private CancellationTokenSource? _installCancel;
    private int _attempt;
    private UpdateState _afterFailure = UpdateState.None;
    private bool _retryable;

    /// <summary>Raised on the UI thread when <see cref="State"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the state of the update flow: nothing until the job of the start has read what the last check remembered
    /// (<c>update.json</c>), then what that or a later check found.
    /// </summary>
    public UpdateState State { get; private set; } = UpdateState.None;

    /// <summary>Gets how many handlers follow <see cref="Changed"/> (one per open window that follows the coordinator).</summary>
    internal int ListenerCount => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>Gets how the running program was installed.</summary>
    public PackageForm Form => form;

    /// <summary>Gets whether the check of the start was begun: the gate that makes it run once per process, whatever the number of windows.</summary>
    public bool CheckStarted { get; private set; }

    /// <summary>Gets the stage a failed installation was in (<see cref="UpdateStage.Downloading"/> to <see cref="UpdateStage.Installing"/>).</summary>
    public UpdateStage FailedAt { get; private set; }

    /// <summary>Gets the text the installer gave for its failure, or <see langword="null"/> for the general one of the error.</summary>
    public string? FailureMessage { get; private set; }

    /// <summary>Gets whether the failure is one the user solves with the download page.</summary>
    public bool FailureOffersDownloadPage { get; private set; }

    /// <summary>Gets whether trying a failed installation again can help (<see cref="RetryAsync"/>).</summary>
    public bool CanRetry => State.Stage == UpdateStage.Failed && _retryable;

    /// <summary>
    /// Gets or sets where the progress window lies (position in physical pixels, size in device-independent pixels), for the
    /// request of the installer, whose helper window takes the same place; set by the window while it is open.
    /// </summary>
    internal Func<(int Left, int Top, double Width, double Height)?>? ProgressWindowBounds { get; set; }

    /// <summary>
    /// The check of the start: does nothing the second time, nor when the setting is off (no source is asked, no file
    /// is read). Otherwise it reads the remembered status, then checks if one is due. Never throws.
    /// </summary>
    public Task StartAsync()
    {
        if (CheckStarted)
        {
            return _running ?? Task.CompletedTask;
        }

        CheckStarted = true;
        return settings.Current.UpdateCheck ? RunAsync(force: false) : Task.CompletedTask;
    }

    /// <summary>
    /// The command "Check for updates": checks now, also with the background check switched off and whatever the
    /// schedule says. A check that is running already is the one the caller waits for. Never throws.
    /// </summary>
    public Task CheckNowAsync() => RunAsync(force: true);

    /// <summary>
    /// Installs the version on offer: fetches and verifies the manifest again (it must still name the version on offer),
    /// picks the file of the installation form, downloads it into the staging folder (<see cref="UpdateStage.Downloading"/>
    /// with the bytes received), checks size and SHA-256 (<see cref="UpdateStage.Verifying"/>), writes the signed manifest,
    /// its signature and the request for the installer next to it (<see cref="UpdateStage.Ready"/>) and only then hands it
    /// to the installer of the system (<see cref="UpdateStage.Installing"/>), which ends the app
    /// (<see cref="UpdateStage.Restarting"/>). Once per process: while it runs, every further call (a second click, another
    /// window) gets the running one; without an offer it does nothing. A failure deletes what was downloaded and ends in
    /// <see cref="UpdateStage.Failed"/>; cancelling (<see cref="CancelInstall"/>) goes back to the offer. Never throws.
    /// </summary>
    public Task BeginInstallAsync()
    {
        if (_install is { IsCompleted: false } running)
        {
            return running;
        }

        if (_disposed || install is null || State is not { Stage: UpdateStage.Available, Version: { } version })
        {
            return Task.CompletedTask;
        }

        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _installCancel = cancel;
        var attempt = ++_attempt;
        FailureMessage = null;
        FailureOffersDownloadPage = false;

        // The stage leaves "Available" before anything is awaited, so a second call cannot start a second installation.
        SetState(new UpdateState(UpdateStage.Downloading, version, null, 0, null));
        return _install = InstallAsync(install, version, attempt, cancel, _lifetime.Token);
    }

    /// <summary>"Cancel" (and Esc, and closing the window): stops a download or check that runs; the offer stays. Later stages cannot be cancelled.</summary>
    public void CancelInstall()
    {
        if (State.Stage is UpdateStage.Downloading or UpdateStage.Verifying)
        {
            _installCancel?.Cancel();
        }
    }

    /// <summary>"Try again" after a failure that may go away (<see cref="CanRetry"/>): installs again, in the same window.</summary>
    public Task RetryAsync()
    {
        if (!CanRetry || _disposed)
        {
            return Task.CompletedTask;
        }

        // Straight from the failure into the next attempt, without telling the windows of the offer in between (the
        // progress window would close).
        State = _afterFailure;
        return BeginInstallAsync();
    }

    /// <summary>
    /// "Close" after a failure: back to the offer, or to what the failure found (a withdrawn release offers nothing, or
    /// the version that took its place).
    /// </summary>
    public void Dismiss()
    {
        if (State.Stage == UpdateStage.Failed)
        {
            SetState(_afterFailure);
        }
    }

    /// <summary>Cancels a running check and a download that runs. May be called more than once.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }

    private Task RunAsync(bool force) =>
        _running is { IsCompleted: false } running ? running : _running = CheckAsync(force);

    private async Task CheckAsync(bool force)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            limit.CancelAfter(OverallLimit);
            var ct = limit.Token;

            // Both on the pool: update.json is read there and the source does its work there; the UI thread only sets the result.
            // What earlier updates left behind goes first (UpdateStaging.DeleteOld).
            var known = await Task.Run(
                () =>
                {
                    DeleteOldStaging();
                    return checker.Known();
                },
                ct).WaitAsync(ct).ConfigureAwait(true);
            Apply(known);

            var state = await Task.Run(() => CheckOffTheUiThreadAsync(force, ct), ct).WaitAsync(ct).ConfigureAwait(true);
            if (state is not null)
            {
                Apply(state);
            }
        }
        catch (Exception)
        {
            // A check that fails, times out or is cancelled changes nothing and shows nothing.
        }
    }

    private async Task<UpdateState?> CheckOffTheUiThreadAsync(bool force, CancellationToken ct)
    {
        try
        {
            return await checker.CheckAsync(force, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The checker does not catch a source that throws; its failure must not end as an unobserved exception.
            return null;
        }
    }

    // Only the stages before a download are the business of a check: it never touches a flow that has begun.
    private void Apply(UpdateState state)
    {
        if (State.Stage is UpdateStage.None or UpdateStage.Available)
        {
            SetState(state);
        }
    }

    private void SetState(UpdateState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        if (Changed is not { } handlers)
        {
            return;
        }

        // Every window hears it, even when the handler of another one throws.
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // A window's handler must not throw (UpdateViewModel.OnChanged); one that does must not stop the others.
            }
        }
    }

    private async Task InstallAsync(UpdateInstallSetup setup, UpdateVersion offered, int attempt, CancellationTokenSource cancel, CancellationToken lifetime)
    {
        var ct = cancel.Token;
        var directory = UpdateStaging.DirectoryFor(setup.ConfigDirectory, offered);
        var stage = UpdateStage.Downloading;
        try
        {
            // (1) The manifest again, verified: the offer may be a day old, and only a verified manifest says what to download.
            var (result, documents) = await Task.Run(() => checker.FetchVerifiedDocumentsAsync(ct), ct).ConfigureAwait(true);
            if (result.Manifest is not { } manifest || documents is null)
            {
                if (result.Rejection == ManifestRejection.NotNewer)
                {
                    Fail(UpdateFailure.Withdrawn, stage, directory, afterwards: UpdateState.None, retryable: false);
                }
                else
                {
                    var error = result.Rejection is null ? UpdateFailure.Unreachable : UpdateFailure.Rejected;
                    Fail(error, stage, directory, afterwards: UpdateState.Offer(offered), retryable: true);
                }

                return;
            }

            // The user agreed to this version: another one (the verifier made sure it is newer) is offered afresh, not installed.
            var parsed = UpdateVersion.TryParse(manifest.Version, out var found);
            if (!parsed || found != offered)
            {
                Fail(UpdateFailure.Withdrawn, stage, directory, afterwards: parsed ? UpdateState.Offer(found) : UpdateState.None, retryable: false);
                return;
            }

            // (2) The file of this installation form; a release without one has nothing for this program.
            if (UpdateChecker.Pick(manifest, form) is not { } file)
            {
                Fail(UpdateFailure.Withdrawn, stage, directory, afterwards: UpdateState.None, retryable: false);
                return;
            }

            // (3) Into a fresh staging folder, with a limit on how long no byte may arrive.
            SetState(new UpdateState(UpdateStage.Downloading, offered, file, 0, null));
            var target = Path.Combine(directory, file.Name);
            await Task.Run(
                () =>
                {
                    DeleteOldStaging();
                    PrepareStaging(directory);
                },
                ct).ConfigureAwait(true);
            if (!await DownloadAsync(setup, offered, file, target, attempt, ct).ConfigureAwait(true))
            {
                Fail(UpdateFailure.Unreachable, stage, directory, afterwards: UpdateState.Offer(offered), retryable: true);
                return;
            }

            // (4) Size and SHA-256 of the signed manifest. Nothing has been changed before this matched.
            stage = UpdateStage.Verifying;
            SetState(new UpdateState(UpdateStage.Verifying, offered, file, file.Size, null));
            if (!await Task.Run(() => ChecksumVerifier.MatchesAsync(target, file.Sha256, file.Size, ct), ct).ConfigureAwait(true))
            {
                Fail(UpdateFailure.Checksum, stage, directory, afterwards: UpdateState.Offer(offered), retryable: true);
                return;
            }

            // (5) The signed manifest, its signature and the request next to the file, for the installer to verify again.
            stage = UpdateStage.Ready;
            var request = Request(setup, offered, file);
            if (!await Task.Run(() => WriteStaging(directory, documents, request), ct).ConfigureAwait(true))
            {
                Fail(UpdateFailure.Storage, stage, directory, afterwards: UpdateState.Offer(offered), retryable: true);
                return;
            }

            // (6) Ready; the installer of the system takes over from here (it writes the session and ends the app).
            SetState(new UpdateState(UpdateStage.Ready, offered, file, file.Size, null));
            if (setup.ApplierFactory() is not { } applier)
            {
                Fail(UpdateFailure.NoInstaller, stage, directory, afterwards: UpdateState.Offer(offered), retryable: false, offersDownloadPage: true);
                return;
            }

            stage = UpdateStage.Installing;
            SetState(new UpdateState(UpdateStage.Installing, offered, file, file.Size, null));
            var applied = await ApplyAsync(applier, new StagedUpdate(directory, offered, file, form), lifetime).ConfigureAwait(true);
            if (applied.IsRestarting)
            {
                SetState(new UpdateState(UpdateStage.Restarting, offered, file, file.Size, null));
                return;
            }

            Fail(
                UpdateFailure.InstallFailed,
                stage,
                directory,
                afterwards: UpdateState.Offer(offered),
                retryable: true,
                offersDownloadPage: applied.OffersDownloadPage,
                message: applied.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // "Cancel", or the app ends: nothing of the download stays, the running version was never touched. Once the
            // installer has it, the staging folder belongs to the installer: it may have ended the app (which cancels this)
            // and its helper reads the folder next; the next run removes what is left (UpdateStaging.DeleteOld).
            if (stage != UpdateStage.Installing)
            {
                DeleteStaging(directory);
            }

            if (!_disposed)
            {
                SetState(UpdateState.Offer(offered));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Fail(UpdateFailure.Storage, stage, directory, afterwards: UpdateState.Offer(offered), retryable: true);
        }
        catch (Exception)
        {
            // Never an exception out of the flow: whatever else went wrong is a failure of its step.
            var error = stage switch
            {
                UpdateStage.Downloading => UpdateFailure.Unreachable,
                UpdateStage.Verifying => UpdateFailure.Checksum,
                _ => UpdateFailure.InstallFailed,
            };
            Fail(error, stage, directory, afterwards: UpdateState.Offer(offered), retryable: true);
        }
        finally
        {
            if (ReferenceEquals(_installCancel, cancel))
            {
                _installCancel = null;
            }

            cancel.Dispose();
        }
    }

    /// <summary>
    /// Downloads the file; <see langword="false"/> when it did not arrive completely, also when no byte came for
    /// <see cref="DownloadStallTimeout"/>. Throws for cancelling and for the file system (a full disk).
    /// </summary>
    private async Task<bool> DownloadAsync(UpdateInstallSetup setup, UpdateVersion version, UpdateFile file, string target, int attempt, CancellationToken ct)
    {
        using var stall = new CancellationTokenSource(DownloadStallTimeout, setup.Clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stall.Token);
        var progress = new DownloadProgress(this, attempt, stall, file.Size);
        try
        {
            return await Task.Run(() => setup.Source.DownloadAsync(version, file, target, progress, linked.Token), linked.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    // Never throws; without an installer setup there is no staging folder to clean.
    private void DeleteOldStaging()
    {
        if (install is { } setup)
        {
            UpdateStaging.DeleteOld(setup.ConfigDirectory, checker.Current, setup.Clock.GetUtcNow());
        }
    }

    private static async Task<ApplyResult> ApplyAsync(IUpdateApplier applier, StagedUpdate staged, CancellationToken ct)
    {
        try
        {
            return await applier.ApplyAsync(staged, ct).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ApplyResult.Failed();
        }
    }

    // The bytes of a download, posted from the downloading thread; late ones of an earlier attempt or stage are dropped.
    private void OnBytesReceived(int attempt, long bytes)
    {
        if (attempt == _attempt && State.Stage == UpdateStage.Downloading && State.File is not null && bytes > State.BytesReceived)
        {
            SetState(State with { BytesReceived = bytes });
        }
    }

    private void Fail(
        string error,
        UpdateStage at,
        string directory,
        UpdateState afterwards,
        bool retryable,
        bool offersDownloadPage = false,
        string? message = null)
    {
        DeleteStaging(directory);
        FailedAt = at;
        FailureMessage = message;
        FailureOffersDownloadPage = offersDownloadPage;
        _afterFailure = afterwards;
        _retryable = retryable;
        SetState(State with { Stage = UpdateStage.Failed, Error = error });
    }

    private UpdateRequest Request(UpdateInstallSetup setup, UpdateVersion version, UpdateFile file)
    {
        var bounds = ProgressWindowBounds?.Invoke();
        return new UpdateRequest
        {
            Version = version.ToString(),
            FileName = file.Name,
            Form = form,
            ExePath = setup.ProcessPath,
            TargetDirectory = setup.TargetDirectory,
            Left = bounds?.Left,
            Top = bounds?.Top,
            Width = bounds?.Width,
            Height = bounds?.Height,
        };
    }

    // A staging folder left by an earlier attempt (or a crash) is ours: it goes, so nothing old is mixed in.
    private static void PrepareStaging(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
    }

    private static bool WriteStaging(string directory, UpdateDocuments documents, UpdateRequest request)
    {
        try
        {
            File.WriteAllBytes(Path.Combine(directory, UpdateStaging.ManifestFileName), documents.Manifest);
            File.WriteAllText(Path.Combine(directory, UpdateStaging.SignatureFileName), documents.Signature, new UTF8Encoding(false));
            return request.TryWrite(Path.Combine(directory, UpdateStaging.RequestFileName));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteStaging(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // What cannot be deleted now is replaced by the next attempt (PrepareStaging).
        }
    }

    /// <summary>
    /// Reports the bytes of a download to the coordinator on the thread it was made on (the UI thread), at most once per
    /// percent, and holds the time limit of a stalled download off while bytes come.
    /// </summary>
    private sealed class DownloadProgress(UpdateCoordinator owner, int attempt, CancellationTokenSource stall, long size) : IProgress<long>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;
        private int _percent = -1;

        public void Report(long value)
        {
            try
            {
                stall.CancelAfter(DownloadStallTimeout);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            var percent = size > 0 ? (int)Math.Min(100, value * 100 / size) : 0;
            if (Interlocked.Exchange(ref _percent, percent) == percent && value < size)
            {
                return;
            }

            if (_context is null)
            {
                owner.OnBytesReceived(attempt, value);
            }
            else
            {
                _context.Post(static state => ((Action)state!)(), (Action)(() => owner.OnBytesReceived(attempt, value)));
            }
        }
    }
}
