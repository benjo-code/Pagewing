namespace Pagewing.Core.Updates;

/// <summary>
/// An update that is downloaded and verified: in <see cref="Directory"/> (<see cref="UpdateStaging.DirectoryFor"/>) lie
/// the file, the signed manifest with its signature and the request for the installer (<see cref="UpdateRequest"/>).
/// </summary>
/// <param name="Directory">The staging folder.</param>
/// <param name="Version">The version of the update.</param>
/// <param name="File">The file of the manifest for this installation form; it lies in <paramref name="Directory"/> under its name.</param>
/// <param name="Form">How the running program was installed.</param>
public sealed record StagedUpdate(string Directory, UpdateVersion Version, UpdateFile File, PackageForm Form)
{
    /// <summary>Gets the path of the verified file.</summary>
    public string FilePath => Path.Combine(Directory, File.Name);

    /// <summary>Gets the path of the request for the installer.</summary>
    public string RequestPath => Path.Combine(Directory, UpdateStaging.RequestFileName);
}

/// <summary>What an <see cref="IUpdateApplier"/> made of a staged update.</summary>
/// <param name="IsRestarting">
/// Whether the installer took over: it starts the new version (or a helper that installs it) and this process ends.
/// </param>
/// <param name="Message">A text for the user (in the language of the interface) on a failure, or <c>null</c> for the general one.</param>
/// <param name="OffersDownloadPage">Whether the failure is one the user solves by installing by hand from the download page.</param>
public sealed record ApplyResult(bool IsRestarting, string? Message, bool OffersDownloadPage)
{
    /// <summary>Gets the result "the installer took over".</summary>
    public static ApplyResult Restarting { get; } = new(true, null, false);

    /// <summary>Creates the result "installing failed; the running version stays".</summary>
    public static ApplyResult Failed(string? message = null, bool offersDownloadPage = false) => new(false, message, offersDownloadPage);
}

/// <summary>
/// Installs a staged update on this system (Windows: the setup program through a helper copy of the app; Linux: the
/// AppImage replaces itself). It is called only after the file matched the signed manifest; before it is called nothing
/// has been changed and no session has been written. It ends the app itself when it returns
/// <see cref="ApplyResult.Restarting"/>.
/// </summary>
public interface IUpdateApplier
{
    /// <summary>Installs <paramref name="staged"/>. Throws only for cancellation; any other exception counts as a failure.</summary>
    Task<ApplyResult> ApplyAsync(StagedUpdate staged, CancellationToken ct);
}
