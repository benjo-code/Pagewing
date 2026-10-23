using Avalonia.Threading;
using Pagewing.App.Services;
using Pagewing.Core.Text;

namespace Pagewing.App.ViewModels;

/// <summary>
/// What the windows of one process share for the update check: whether it was started already, and the coordinator once
/// a window has made it. The check starts once per process, from the first window that has a sharp page (or shows the
/// start screen), however many windows follow; every window shows what the one coordinator knows.
/// </summary>
internal sealed class UpdateStartGate
{
    /// <summary>Raised on the UI thread when a window has made the coordinator (<see cref="Coordinator"/>).</summary>
    public event Action? CoordinatorMade;

    /// <summary>Gets or sets whether a window has started the check.</summary>
    public bool Started { get; set; }

    /// <summary>Gets the coordinator a window of the process has made, or <see langword="null"/> while none has.</summary>
    public UpdateCoordinator? Coordinator { get; private set; }

    /// <summary>Tells the other windows that <paramref name="coordinator"/> is there, the first time only.</summary>
    public void Publish(UpdateCoordinator coordinator)
    {
        if (Coordinator is null)
        {
            Coordinator = coordinator;
            CoordinatorMade?.Invoke();
        }
    }
}

/// <summary>
/// The update check in a window: starts it once after the first sharp page (or the first frame of the start screen)
/// and offers the command "Check for updates". The coordinator is made when it is first needed, never at startup, and
/// with the setting off nothing of it is made at all unless the user asks with the command. Once a window of the
/// process has made it, every window follows it with an <see cref="UpdateViewModel"/> (the button and its card).
/// </summary>
public sealed partial class MainViewModel
{
    private UpdateCoordinator? _updateCoordinator;
    private bool _updateCoordinatorMade;
    private UpdateStartGate _updateStartGate = new();
    private UpdateViewModel? _update;

    /// <summary>
    /// Gets what makes the coordinator of the update check, or <see langword="null"/> for a run that has none (a
    /// measurement run, a test; the factory may return <see langword="null"/> too). The workspace sets it and shares
    /// the one coordinator between the windows.
    /// </summary>
    internal Func<UpdateCoordinator?>? UpdateCoordinatorFactory { get; set; }

    /// <summary>
    /// Gets or sets what the windows of the process share, so the check starts once and a later window shows what an
    /// earlier one found (<see cref="UpdateStartGate"/>).
    /// </summary>
    internal UpdateStartGate UpdateStartGate
    {
        get => _updateStartGate;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _updateStartGate.CoordinatorMade -= OnUpdateCoordinatorMade;
            _updateStartGate = value;
            value.CoordinatorMade += OnUpdateCoordinatorMade;
            OnUpdateCoordinatorMade();
        }
    }

    /// <summary>
    /// Gets the button "Update" and its card in this window, or <see langword="null"/> while no window of the process has
    /// made the coordinator of the update check (nothing of it in the startup path).
    /// </summary>
    public UpdateViewModel? Update
    {
        get => _update;
        private set
        {
            if (!ReferenceEquals(_update, value))
            {
                var old = _update;
                _update = value;
                OnPropertyChanged();
                old?.Dispose();
            }
        }
    }

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
                FollowUpdates(_updateCoordinator);
                if (_updateCoordinator is { } made)
                {
                    _updateStartGate.Publish(made);
                }
            }

            return _updateCoordinator;
        }

        set
        {
            _updateCoordinator = value;
            _updateCoordinatorMade = true;
            FollowUpdates(value);
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

    /// <summary>
    /// Asks the user before <paramref name="url"/> is opened: the confirmation card of external addresses, as for a link of
    /// the document. An address <see cref="ExternalLink.TryParse"/> refuses, or one while another waits, is dropped.
    /// </summary>
    internal void RequestExternalLink(string url)
    {
        if (!_disposed && ExternalLink.TryParse(url, out var link))
        {
            OnExternalLinkRequested(link);
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

    // Another window has made the coordinator (or this one gets the gate of a process that has one): follow it too. The
    // factory of the workspace hands out the one it made, so nothing new is made here.
    private void OnUpdateCoordinatorMade()
    {
        if (!_disposed && _update is null && _updateStartGate.Coordinator is not null)
        {
            _ = UpdateCoordinator;
        }
    }

    private void FollowUpdates(UpdateCoordinator? coordinator) =>
        Update = coordinator is null || _disposed ? null : new UpdateViewModel(coordinator, RequestExternalLink);

    /// <summary>Lets go of the coordinator of the process: the window is closing.</summary>
    private void DisposeUpdate()
    {
        _updateStartGate.CoordinatorMade -= OnUpdateCoordinatorMade;
        Update = null;
    }
}
