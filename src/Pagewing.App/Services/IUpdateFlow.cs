using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>
/// An installation the progress window (<see cref="Views.UpdateWindow"/>) shows: the one of the app
/// (<see cref="UpdateCoordinator"/>, download and check before the app ends) or the one of the helper of the Windows
/// updater (<see cref="Platform.Windows.WindowsUpdateHelper"/>, which installs after it). Used on the UI thread, where
/// <see cref="Changed"/> is raised.
/// </summary>
public interface IUpdateFlow
{
    /// <summary>Raised on the UI thread when <see cref="State"/> or what goes with it changed.</summary>
    event Action? Changed;

    /// <summary>Gets where the update is.</summary>
    UpdateState State { get; }

    /// <summary>Gets how the running program was installed.</summary>
    PackageForm Form { get; }

    /// <summary>Gets the stage a failed installation was in.</summary>
    UpdateStage FailedAt { get; }

    /// <summary>Gets the text the installer gave for its failure, or <see langword="null"/> for the general one of the error.</summary>
    string? FailureMessage { get; }

    /// <summary>Gets whether the failure is one the user solves with the download page.</summary>
    bool FailureOffersDownloadPage { get; }

    /// <summary>Gets whether trying a failed installation again can help (<see cref="RetryAsync"/>).</summary>
    bool CanRetry { get; }

    /// <summary>"Install": starts the installation of the version on offer (once); never throws.</summary>
    Task BeginInstallAsync();

    /// <summary>"Cancel" (and Esc): stops what can still be stopped; later stages cannot be cancelled.</summary>
    void CancelInstall();

    /// <summary>"Try again" after a failure that may go away.</summary>
    Task RetryAsync();

    /// <summary>"Close" after a failure.</summary>
    void Dismiss();
}
