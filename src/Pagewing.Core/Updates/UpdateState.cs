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

    /// <summary>Downloading, verifying or starting failed; <see cref="UpdateState.Error"/> says why.</summary>
    Failed,
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
