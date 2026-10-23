namespace Pagewing.Core.Updates;

/// <summary>Where an update is on its way from "found" to "installed".</summary>
public enum UpdateStage
{
    /// <summary>Nothing newer is known.</summary>
    None,

    /// <summary>A newer version is known and not yet fetched.</summary>
    Available,

    /// <summary>The file is being downloaded.</summary>
    Downloading,

    /// <summary>The file is complete and being checked against its checksum.</summary>
    Verifying,

    /// <summary>The file is downloaded and verified; installing can start.</summary>
    Ready,

    /// <summary>The installer is being started.</summary>
    Installing,

    /// <summary>The installer took over and starts the new version; this process ends.</summary>
    Restarting,

    /// <summary>Downloading, verifying or starting failed; <see cref="UpdateState.Error"/> says why (<see cref="UpdateFailure"/>).</summary>
    Failed,
}

/// <summary>Why an installation failed: the values of <see cref="UpdateState.Error"/> in <see cref="UpdateStage.Failed"/>.</summary>
public static class UpdateFailure
{
    /// <summary>The manifest or the file was not received completely (no network, a timeout, a stalled download).</summary>
    public const string Unreachable = "unreachable";

    /// <summary>The release on offer is not the latest any more (taken back, or another version took its place).</summary>
    public const string Withdrawn = "withdrawn";

    /// <summary>The manifest received again was refused (signature, format, file names).</summary>
    public const string Rejected = "rejected";

    /// <summary>The downloaded file does not have the size and SHA-256 of the signed manifest.</summary>
    public const string Checksum = "checksum";

    /// <summary>The update could not be written (a full disk, no access).</summary>
    public const string Storage = "storage";

    /// <summary>This system has no installer for the form of the running program.</summary>
    public const string NoInstaller = "no-installer";

    /// <summary>The installer failed.</summary>
    public const string InstallFailed = "install-failed";
}

/// <summary>A snapshot of the update flow.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Version">The version on offer, if any.</param>
/// <param name="File">The file chosen for this installation form, once one is chosen.</param>
/// <param name="BytesReceived">Bytes downloaded so far while <see cref="UpdateStage.Downloading"/>.</param>
/// <param name="Error">A short reason when the stage is <see cref="UpdateStage.Failed"/>.</param>
public sealed record UpdateState(UpdateStage Stage, UpdateVersion? Version, UpdateFile? File, long BytesReceived, string? Error)
{
    /// <summary>Gets the state in which nothing newer is known.</summary>
    public static UpdateState None { get; } = new(UpdateStage.None, null, null, 0, null);

    /// <summary>Creates the state "this version is on offer".</summary>
    public static UpdateState Offer(UpdateVersion version) => new(UpdateStage.Available, version, null, 0, null);
}
