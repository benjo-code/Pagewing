using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>
/// What the coordinator needs to install an update after "Install": where the file comes from, the config folder whose
/// <c>updates</c> folder takes the download (<see cref="UpdateStaging"/>), what installs it on this system, and the clock of
/// the time limits. Without it a coordinator only checks.
/// </summary>
/// <param name="Source">Where manifest and file come from (the same source the checker asks).</param>
/// <param name="ConfigDirectory">The config folder of the user (a temporary folder in tests).</param>
/// <param name="ApplierFactory">Makes the installer of this system, or returns <c>null</c> where there is none.</param>
/// <param name="Clock">The clock of the time limits; tests drive it.</param>
public sealed record UpdateInstallSetup(IUpdateSource Source, string ConfigDirectory, Func<IUpdateApplier?> ApplierFactory, TimeProvider Clock)
{
    /// <summary>Gets the path of the running executable, for the request of the installer.</summary>
    public string? ProcessPath { get; init; }

    /// <summary>Gets the folder the program is installed in (for an AppImage the folder of the AppImage), for the request.</summary>
    public string? TargetDirectory { get; init; }

    /// <summary>
    /// Gets what runs on the thread pool right before the signed manifest, its signature and the request are written into
    /// the staging folder; tests hold the write there (nothing in the app sets it).
    /// </summary>
    internal Action? StagingWriteStarted { get; init; }
}
