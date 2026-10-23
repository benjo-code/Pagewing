using Avalonia.Threading;
using Pagewing.App.Services;

namespace Pagewing.App.ViewModels;

/// <summary>
/// What the windows of one process share for the update check: whether it was started already. The check starts once
/// per process, from the first window that has a sharp page (or shows the start screen), however many windows follow.
/// </summary>
internal sealed class UpdateStartGate
{
    /// <summary>Gets or sets whether a window has started the check.</summary>
    public bool Started { get; set; }
}

/// <summary>
/// The update check in a window: starts it once after the first sharp page (or the first frame of the start screen)
/// and offers the command "Check for updates". The coordinator is made when it is first needed, never at startup, and
/// with the setting off nothing of it is made at all unless the user asks with the command.
/// </summary>
public sealed partial class MainViewModel
{
    private UpdateCoordinator? _updateCoordinator;
    private bool _updateCoordinatorMade;

    /// <summary>
    /// Gets what makes the coordinator of the update check, or <see langword="null"/> for a run that has none (a
    /// measurement run, a test; the factory may return <see langword="null"/> too). The workspace sets it and shares
    /// the one coordinator between the windows.
    /// </summary>
    internal Func<UpdateCoordinator?>? UpdateCoordinatorFactory { get; set; }

    /// <summary>Gets what the windows of the process share, so the check starts once (<see cref="UpdateStartGate"/>).</summary>
    internal UpdateStartGate UpdateStartGate { get; set; } = new();

    /// <summary>
    /// Gets or sets the coordinator of the update check, made by <see cref="UpdateCoordinatorFactory"/> on first use, or
    /// <see langword="null"/> where there is none. Reading it can make it: ask <see cref="CanCheckForUpdates"/> to know
    /// whether there is one without making it.
    /// </summary>
    internal UpdateCoordinator? UpdateCoordinator
    {
        get
        {
            if (!_updateCoordinatorMade)
            {
                _updateCoordinator = UpdateCoordinatorFactory?.Invoke();
                _updateCoordinatorMade = true;
            }

            return _updateCoordinator;
        }

        set
        {
            _updateCoordinator = value;
            _updateCoordinatorMade = true;
        }
    }

    /// <summary>Gets whether the window has an update check to run (cheap: makes nothing).</summary>
    internal bool CanCheckForUpdates() =>
        !_disposed && (_updateCoordinatorMade ? _updateCoordinator is not null : UpdateCoordinatorFactory is not null);

    /// <summary>
    /// The first sharp page or the first frame of the start screen is there: starts the check, once per process, in a job
    /// of <see cref="DispatcherPriority.Background"/> (after rendering), if the setting allows it and the window has an
    /// updater. With the setting off nothing happens: no coordinator, no HTTP client, no file.
    /// </summary>
    internal void PostUpdateCheck()
    {
        if (_disposed || UpdateStartGate.Started || !CanCheckForUpdates() || !Settings.Current.UpdateCheck)
        {
            return;
        }

        UpdateStartGate.Started = true;
        Dispatcher.UIThread.Post(StartUpdateCheck, DispatcherPriority.Background);
    }

    private void StartUpdateCheck()
    {
        try
        {
            if (!_disposed && UpdateCoordinator is { } coordinator)
            {
                _ = coordinator.StartAsync();
            }
        }
        catch (Exception)
        {
            // No updater, no check: never a card or an error at startup.
        }
    }

    /// <summary>The command "Check for updates": checks now, whatever the setting says; a failing check is silent.</summary>
    internal void CheckForUpdatesFromCommand()
    {
        try
        {
            if (UpdateCoordinator is { } coordinator)
            {
                _ = coordinator.CheckNowAsync();
            }
        }
        catch (Exception)
        {
            // The check never throws; a factory that does is as silent.
        }
    }
}
