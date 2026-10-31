namespace Pagewing.App.Services;

/// <summary>What a check found, for the command "Check for updates" (<see cref="UpdateCoordinator.CheckNowAsync"/>).</summary>
public enum UpdateCheckOutcome
{
    /// <summary>Asking failed: no network, a timeout, a server answer that is not a manifest, a refused manifest.</summary>
    Failed,

    /// <summary>Nothing newer than the running version.</summary>
    UpToDate,

    /// <summary>A newer version is on offer.</summary>
    Available,
}
