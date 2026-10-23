using Pagewing.Core.Settings;
using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>
/// The update check of the running process, shared by all windows: starts it once after the first sharp page (or the
/// first frame of the start screen) when the setting allows it, runs it on the thread pool with a time limit, keeps the
/// result and tells the windows. Failing is silent (no network, a timeout, a refused manifest, a source that throws:
/// nothing changes and nothing is shown). Used on the UI thread, where <see cref="Changed"/> is raised.
/// </summary>
/// <param name="checker">The schedule and the checks (<see cref="UpdateChecker"/>), made over the source of the app.</param>
/// <param name="settings">The settings; only <see cref="AppSettings.UpdateCheck"/> decides whether <see cref="StartAsync"/> checks.</param>
/// <param name="form">How the running program was installed; it decides how it can update itself.</param>
public sealed class UpdateCoordinator(UpdateChecker checker, UserSettings settings, PackageForm form) : IDisposable
{
    // Beyond the limit of the check itself: a source that ignores its token must not hold the coordinator for ever.
    private static readonly TimeSpan OverallLimit = UpdateChecker.CheckTimeout + TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _running;
    private bool _disposed;

    /// <summary>Raised on the UI thread when <see cref="State"/> changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Gets the state of the update flow: nothing until the job of the start has read what the last check remembered
    /// (<c>update.json</c>), then what that or a later check found.
    /// </summary>
    public UpdateState State { get; private set; } = UpdateState.None;

    /// <summary>Gets how the running program was installed.</summary>
    public PackageForm Form => form;

    /// <summary>Gets whether the check of the start was begun: the gate that makes it run once per process, whatever the number of windows.</summary>
    public bool CheckStarted { get; private set; }

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

    /// <summary>Starts installing the version on offer; does nothing without an offer. Never throws.</summary>
    public Task BeginInstallAsync()
    {
        if (State.Stage != UpdateStage.Available)
        {
            return Task.CompletedTask;
        }

        // Task 9: download, verify, install. A placeholder until then.
        return Task.CompletedTask;
    }

    /// <summary>Cancels a running check. May be called more than once.</summary>
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
            var known = await Task.Run(checker.Known, ct).WaitAsync(ct).ConfigureAwait(true);
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
        if (State.Stage is not (UpdateStage.None or UpdateStage.Available) || State == state)
        {
            return;
        }

        State = state;
        Changed?.Invoke();
    }
}
