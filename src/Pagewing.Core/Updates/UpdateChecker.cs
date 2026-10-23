namespace Pagewing.Core.Updates;

/// <summary>
/// The schedule of the update check and what it makes of the answer. A check is due when nothing is remembered, when
/// the last one is at least <see cref="Interval"/> ago or when the remembered time lies in the future (a wrong clock
/// must never block checking for good). Nothing here throws except for cancellation: no network, a timeout, a
/// refused manifest all end in the state the program already knew, and a failed check leaves the remembered
/// time alone so the next start tries again.
/// </summary>
/// <param name="source">Where the manifest comes from.</param>
/// <param name="store">The remembered check time and found version (<c>update.json</c>).</param>
/// <param name="clock">The clock; tests drive it.</param>
/// <param name="current">The running version.</param>
/// <param name="keys">The keys a manifest signature may come from.</param>
public sealed class UpdateChecker(
    IUpdateSource source,
    UpdateStatusStore store,
    TimeProvider clock,
    UpdateVersion current,
    IReadOnlyList<UpdateKey> keys)
{
    /// <summary>The shortest time between two automatic checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>How long fetching the manifest may take.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Gets the running version, against which a manifest must be newer.</summary>
    public UpdateVersion Current => current;

    /// <summary>
    /// What is already known without the network: the version remembered by the last check, if it is newer than the
    /// running one. Reads only <c>update.json</c>.
    /// </summary>
    public UpdateState Known()
    {
        var status = store.Load();
        return UpdateVersion.TryParse(status.AvailableVersion, out var version) && version > current
            ? UpdateState.Offer(version)
            : UpdateState.None;
    }

    /// <summary>
    /// Checks for a newer version if one is due or <paramref name="force"/> is set, otherwise returns <see cref="Known"/>
    /// without touching the network. A manifest that is valid but not newer ends the check as well (the time is
    /// remembered, the offer is dropped); every other failure changes nothing.
    /// </summary>
    public async Task<UpdateState> CheckAsync(bool force, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var status = store.Load();
        var now = clock.GetUtcNow();
        if (!force && !IsDue(status.LastChecked, now))
        {
            return Known();
        }

        var result = await FetchVerifiedAsync(ct);
        if (result.Manifest is { } manifest && UpdateVersion.TryParse(manifest.Version, out var version))
        {
            store.TrySave(new UpdateStatus(now, version.ToString()));
            return UpdateState.Offer(version);
        }

        if (result.Rejection == ManifestRejection.NotNewer)
        {
            store.TrySave(new UpdateStatus(now, null));
            return UpdateState.None;
        }

        return Known();
    }

    /// <summary>
    /// Fetches manifest and signature, limited to <see cref="CheckTimeout"/>, and verifies them against the running
    /// version. Writes nothing. The result has a manifest or a rejection; both are <c>null</c> when nothing was
    /// received (no network, timeout, server answer that is not a manifest).
    /// </summary>
    public async Task<ManifestResult> FetchVerifiedAsync(CancellationToken ct) => (await FetchVerifiedDocumentsAsync(ct)).Result;

    /// <summary>
    /// Like <see cref="FetchVerifiedAsync"/>, and with a verified manifest also the documents exactly as received (the
    /// bytes the signature covers), so they can be kept next to the download and verified again by the installer.
    /// </summary>
    public async Task<(ManifestResult Result, UpdateDocuments? Documents)> FetchVerifiedDocumentsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = new CancellationTokenSource(CheckTimeout, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        UpdateDocuments? documents;
        try
        {
            documents = await source.FetchManifestAsync(linked.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return default;
        }

        ct.ThrowIfCancellationRequested();
        if (documents is null)
        {
            return default;
        }

        var result = ManifestVerifier.Verify(documents.Manifest, documents.Signature, keys, current);
        return (result, result.Manifest is null ? null : documents);
    }

    /// <summary>The file of the manifest that this installation form installs by itself, or <c>null</c> if it has none.</summary>
    public static UpdateFile? Pick(UpdateManifest manifest, PackageForm form)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return form switch
        {
            PackageForm.WindowsInstaller => manifest.FileOfKind(UpdateFileKinds.WindowsSetup),
            PackageForm.AppImage => manifest.FileOfKind(UpdateFileKinds.LinuxAppImage),
            _ => null,
        };
    }

    private static bool IsDue(DateTimeOffset? lastChecked, DateTimeOffset now) =>
        lastChecked is not { } last || last > now || now - last >= Interval;
}
