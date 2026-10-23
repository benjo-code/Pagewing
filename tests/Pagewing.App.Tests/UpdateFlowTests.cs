using System.Text;
using Avalonia.Headless.XUnit;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// The installation after "Install" (Milestone 8.1, task 9): the manifest is fetched and verified again, the file of the
/// installation form is downloaded into the staging folder and checked against size and SHA-256, the signed manifest and
/// the request for the installer are written, and only then is the update handed to the installer, which ends the app.
/// Every failure deletes what was downloaded, leaves the running version alone and can be tried again. Network and
/// installer are fakes; the staging folder lies in a temporary folder, never in the one of the user.
/// </summary>
// The host sets the culture of Strings, which is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class UpdateFlowTests : UpdateTestHost
{
    private static readonly UpdateVersion Offered = new(0, 2, 0);

    [AvaloniaFact]
    public async Task DownloadsVerifiesThenAppliesInThatOrder()
    {
        var coordinator = await OfferedAsync();
        var steps = new List<string>();
        coordinator.Changed += () =>
        {
            var stage = coordinator.State.Stage.ToString();
            if (steps.Count == 0 || steps[^1] != stage)
            {
                steps.Add(stage);
            }
        };
        Applier.OnApply = _ => steps.Add("apply");

        await coordinator.BeginInstallAsync();

        Assert.Equal(["Downloading", "Verifying", "Ready", "Installing", "apply", "Restarting"], steps);
        Assert.Equal(1, Applier.Calls);
        var staged = Applier.Staged!;
        Assert.Equal(StagingOf("0.2.0"), staged.Directory);
        Assert.Equal(Offered, staged.Version);
        Assert.Equal(UpdateFileKinds.WindowsSetup, staged.File.Kind);
        Assert.Equal(PackageForm.WindowsInstaller, staged.Form);
        Assert.Equal(Source.Content, Applier.StagedBytes);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
    }

    [AvaloniaFact]
    public async Task TheAppImageGetsItsOwnFile()
    {
        var coordinator = await OfferedAsync(form: PackageForm.AppImage);

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateFileKinds.LinuxAppImage, Applier.Staged!.File.Kind);
        Assert.Equal(PackageForm.AppImage, Applier.Staged.Form);
    }

    [AvaloniaFact]
    public async Task TheStagingFolderHoldsTheSignedManifestAndTheRequest()
    {
        var coordinator = await OfferedAsync();
        coordinator.ProgressWindowBounds = () => (120, 80, 460, 360);
        var documents = Source.Documents!;
        UpdateRequest? request = null;
        Applier.OnApply = staged => request = UpdateRequest.Load(Path.Combine(staged.Directory, UpdateStaging.RequestFileName));

        await coordinator.BeginInstallAsync();

        var staging = StagingOf("0.2.0");
        Assert.Equal(documents.Manifest, File.ReadAllBytes(Path.Combine(staging, UpdateStaging.ManifestFileName)));
        Assert.Equal(documents.Signature, File.ReadAllText(Path.Combine(staging, UpdateStaging.SignatureFileName), Encoding.UTF8));

        // What the installer of task 10 and 11 needs: what to install, from where to where, and where its window goes.
        Assert.NotNull(request);
        Assert.Equal("0.2.0", request.Version);
        Assert.Equal("Pagewing-0.2.0-win-x64-setup.exe", request.FileName);
        Assert.Equal(PackageForm.WindowsInstaller, request.Form);
        Assert.Equal(ProcessPath, request.ExePath);
        Assert.Equal(Path.GetDirectoryName(ProcessPath), request.TargetDirectory);
        Assert.Equal<(int?, int?, double?, double?)>((120, 80, 460, 360), (request.Left, request.Top, request.Width, request.Height));

        // The verified manifest in the folder still verifies with the keys of the program.
        var again = ManifestVerifier.Verify(File.ReadAllBytes(Path.Combine(staging, UpdateStaging.ManifestFileName)), documents.Signature, Source.Keys, Running);
        Assert.NotNull(again.Manifest);
    }

    [AvaloniaFact]
    public async Task TheAppIsNotClosedBeforeTheChecksumMatched()
    {
        var coordinator = await OfferedAsync();
        Source.Corrupt = true;

        await coordinator.BeginInstallAsync();

        // The installer ends the app; it is never reached with a file that does not match the signed manifest.
        Assert.Equal(1, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
    }

    [AvaloniaFact]
    public async Task RejectsAWithdrawnVersion()
    {
        var coordinator = await OfferedAsync();

        // Between the check and the click the release was taken back: the latest one is not newer any more.
        Source.Offer("0.1.0");
        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.Withdrawn, coordinator.State.Error);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.False(coordinator.CanRetry);

        // Closing the message drops the offer: there is nothing to install.
        coordinator.Dismiss();
        Assert.Equal(UpdateState.None, coordinator.State);
    }

    [AvaloniaFact]
    public async Task RejectsAManifestOfAnotherVersionThanOffered()
    {
        var coordinator = await OfferedAsync();

        // The user agreed to 0.2.0; a manifest of 0.3.0 is not installed in its place.
        Source.Offer("0.3.0");
        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.Withdrawn, coordinator.State.Error);
        Assert.Equal(Offered, coordinator.State.Version);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
        Assert.False(coordinator.CanRetry);

        // Closing the message offers the version that is there now, for the user to choose again.
        coordinator.Dismiss();
        Assert.Equal(UpdateState.Offer(new UpdateVersion(0, 3, 0)), coordinator.State);
    }

    [AvaloniaFact]
    public async Task ARefusedManifestIsAnError()
    {
        var coordinator = await OfferedAsync();
        Source.Documents = Source.Documents! with { Signature = "test-key:" + Convert.ToBase64String(new byte[64]) };

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateFailure.Rejected, coordinator.State.Error);
        Assert.Equal(0, Source.DownloadCalls);
        Assert.Equal(0, Applier.Calls);
    }

    [AvaloniaFact]
    public async Task WrongChecksumLeavesNothingBehindAndShowsAnError()
    {
        var coordinator = await OfferedAsync();
        Source.Corrupt = true;

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.Checksum, coordinator.State.Error);
        Assert.Equal(UpdateStage.Verifying, coordinator.FailedAt);
        Assert.False(File.Exists(Source.LastDestination));
        Assert.False(File.Exists(Source.LastPart));
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
        Assert.True(coordinator.CanRetry);
    }

    [AvaloniaFact]
    public async Task RetryAfterAnErrorWorks()
    {
        var coordinator = await OfferedAsync();
        Source.DownloadBreaks = true;

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateFailure.Unreachable, coordinator.State.Error);
        Assert.Equal(UpdateStage.Downloading, coordinator.FailedAt);
        Assert.False(File.Exists(Source.LastPart));
        Assert.True(coordinator.CanRetry);

        Source.DownloadBreaks = false;
        await coordinator.RetryAsync();

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(2, Source.DownloadCalls);
        Assert.Equal(1, Applier.Calls);
    }

    [AvaloniaFact]
    public async Task ClosingTheErrorGoesBackToTheOffer()
    {
        var coordinator = await OfferedAsync();
        Source.DownloadBreaks = true;
        await coordinator.BeginInstallAsync();

        coordinator.Dismiss();

        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
    }

    [AvaloniaFact]
    public async Task DoubleClickStartsOneInstall()
    {
        var coordinator = await OfferedAsync();
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var update = new UpdateViewModel(coordinator, _ => { });

        // Two clicks on "Install" (and a direct second call) while the first one runs.
        update.InstallCommand.Execute(null);
        update.InstallCommand.Execute(null);
        var again = coordinator.BeginInstallAsync();
        await PumpUntilAsync(() => Source.DownloadCalls == 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Source.DownloadGate.SetResult();
        await again;

        Assert.Equal(1, Source.DownloadCalls);
        Assert.Equal(1, Applier.Calls);
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
    }

    [AvaloniaFact]
    public async Task TwoWindowsStartOneInstall()
    {
        Source.Offer("0.2.0");
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = CountingFactory();
        var gate = new UpdateStartGate();
        var (first, firstModel, _) = await ShowAppAsync(factory: factory, beforeShow: model => model.UpdateStartGate = gate);
        await WaitSharpAsync(first, firstModel);
        var (second, secondModel, _) = await ShowAppAsync(factory: factory, beforeShow: model => model.UpdateStartGate = gate);
        await WaitUntilAsync(second, () => firstModel.Update?.ShowButton == true && secondModel.Update?.ShowButton == true);
        var coordinator = factory()!;

        // "Install" in both windows: one download, one progress window, one installation.
        firstModel.Update!.InstallCommand.Execute(null);
        secondModel.Update!.InstallCommand.Execute(null);
        await WaitUntilAsync(first, () => Source.DownloadCalls == 1);
        var window = UpdateWindow.For(coordinator);
        Assert.NotNull(window);
        OwnWindow(window);
        Assert.True(window.IsVisible);

        Source.DownloadGate.SetResult();
        await WaitUntilAsync(first, () => coordinator.State.Stage == UpdateStage.Restarting);

        Assert.Equal(1, Source.DownloadCalls);
        Assert.Equal(1, Applier.Calls);
        Assert.Same(window, UpdateWindow.For(coordinator));
    }

    [AvaloniaFact]
    public async Task CancelDuringDownloadDeletesThePartFileAndKeepsTheOldVersion()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProcessPath)!);
        File.WriteAllText(ProcessPath, "the running version");
        var coordinator = await OfferedAsync();
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var install = coordinator.BeginInstallAsync();
        await PumpUntilAsync(() => File.Exists(Source.LastPart) && coordinator.State.BytesReceived > 0);
        Assert.Equal(UpdateStage.Downloading, coordinator.State.Stage);

        coordinator.CancelInstall();
        await install;

        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        Assert.False(File.Exists(Source.LastPart));
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
        Assert.Equal(0, Applier.Calls);
        Assert.Equal("the running version", File.ReadAllText(ProcessPath));

        // The offer stays: installing again works.
        Source.DownloadGate = null;
        await coordinator.BeginInstallAsync();
        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
    }

    [AvaloniaFact]
    public async Task DiskFullShowsAnErrorAndRetryWorks()
    {
        var coordinator = await OfferedAsync();
        Source.DownloadThrows = new IOException("There is not enough space on the disk.");

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.Storage, coordinator.State.Error);
        Assert.False(File.Exists(Source.LastPart));
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
        Assert.Equal(0, Applier.Calls);
        Assert.True(coordinator.CanRetry);

        Source.DownloadThrows = null;
        await coordinator.RetryAsync();

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(1, Applier.Calls);
    }

    [AvaloniaFact]
    public async Task AFullDiskInTheRealSourceIsAStorageErrorAndRetryWorks()
    {
        // The shipped source on a fake handler (no network): the manifest of the fake, the file of 200,000 bytes, written
        // to a disk that is full after the first chunk.
        Source.Content = [.. Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251))];
        Source.Offer("0.2.0");
        var full = true;
        using var github = new GitHubUpdateSource(new ReleaseHandler(Source))
        {
            OpenDestination = path => new FullDiskFileStream(path, () => full),
        };
        var coordinator = NewCoordinator(source: github);
        await coordinator.CheckNowAsync();
        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);

        await coordinator.BeginInstallAsync();

        // Not "check the internet connection": the disk is the problem, and nothing of the download stays.
        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.Storage, coordinator.State.Error);
        Assert.Equal(UpdateStage.Downloading, coordinator.FailedAt);
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
        Assert.Equal(0, Applier.Calls);
        Assert.True(coordinator.CanRetry);

        // Space was made: trying again installs.
        full = false;
        await coordinator.RetryAsync();

        Assert.Equal(UpdateStage.Restarting, coordinator.State.Stage);
        Assert.Equal(Source.Content, Applier.StagedBytes);
    }

    [AvaloniaFact]
    public async Task AStalledDownloadEndsWithAnError()
    {
        var clock = new PrintClock();
        var coordinator = await OfferedAsync(clock: clock);
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var install = coordinator.BeginInstallAsync();
        await PumpUntilAsync(() => coordinator.State.BytesReceived > 0);
        clock.Advance(UpdateCoordinator.DownloadStallTimeout + TimeSpan.FromSeconds(1));
        await install;

        Assert.Equal(UpdateFailure.Unreachable, coordinator.State.Error);
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
        Assert.True(coordinator.CanRetry);
    }

    [AvaloniaFact]
    public async Task NoSessionIsWrittenBeforeTheApplyStarts()
    {
        var session = Path.Combine(ConfigDirectory, SessionStore.FileName);
        var coordinator = await OfferedAsync();
        Source.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? existedAtApply = null;
        Applier.OnApply = _ => existedAtApply = File.Exists(session);

        var install = coordinator.BeginInstallAsync();
        await PumpUntilAsync(() => coordinator.State.BytesReceived > 0);
        Assert.False(File.Exists(session));
        Source.DownloadGate.SetResult();
        await install;

        // The session is the business of the installer (tasks 10 and 11), which writes it right before it restarts.
        Assert.False(existedAtApply);
        Assert.False(File.Exists(session));
    }

    [AvaloniaFact]
    public async Task WithoutAnInstallerTheErrorPointsToTheDownloadPage()
    {
        var coordinator = await OfferedAsync(noApplier: true);

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateStage.Failed, coordinator.State.Stage);
        Assert.Equal(UpdateFailure.NoInstaller, coordinator.State.Error);
        Assert.True(coordinator.FailureOffersDownloadPage);
        Assert.False(coordinator.CanRetry);
        Assert.False(Directory.Exists(StagingOf("0.2.0")));
    }

    [AvaloniaFact]
    public async Task AFailedInstallerShowsItsMessage()
    {
        var coordinator = await OfferedAsync();
        Applier.Result = ApplyResult.Failed("The folder is read-only.", offersDownloadPage: true);

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateFailure.InstallFailed, coordinator.State.Error);
        Assert.Equal(UpdateStage.Installing, coordinator.FailedAt);
        Assert.Equal("The folder is read-only.", coordinator.FailureMessage);
        Assert.True(coordinator.FailureOffersDownloadPage);
        Assert.True(coordinator.CanRetry);
    }

    [AvaloniaFact]
    public async Task AnInstallerThatThrowsIsAnError()
    {
        var coordinator = await OfferedAsync();
        Applier.Throws = new InvalidOperationException("broken");

        await coordinator.BeginInstallAsync();

        Assert.Equal(UpdateFailure.InstallFailed, coordinator.State.Error);
        Assert.Null(coordinator.FailureMessage);
    }

    [AvaloniaFact]
    public async Task StagingSurvivesAnApplierThatShutsTheAppDown()
    {
        var coordinator = await OfferedAsync();

        // The installer ends the app (the workspace disposes the coordinator) and then reports the cancellation: the helper
        // it started reads the staging folder, which must stay.
        Applier.OnApply = _ => coordinator.Dispose();
        Applier.Throws = new OperationCanceledException();

        await coordinator.BeginInstallAsync();

        Assert.True(File.Exists(Path.Combine(StagingOf("0.2.0"), UpdateStaging.RequestFileName)));
        Assert.True(File.Exists(Path.Combine(StagingOf("0.2.0"), "Pagewing-0.2.0-win-x64-setup.exe")));
    }

    [AvaloniaFact]
    public async Task OldStagingFoldersAreCleanedUp()
    {
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var updates = Path.Combine(ConfigDirectory, UpdateStaging.DirectoryName);
        var ages = new Dictionary<string, TimeSpan>
        {
            ["0.0.9"] = TimeSpan.FromHours(1),  // older than the running 0.1.0
            ["0.1.0"] = TimeSpan.FromHours(1),  // the running one
            ["0.3.0"] = TimeSpan.FromHours(1),  // newer and fresh: a helper may still use it
            ["0.4.0"] = TimeSpan.FromDays(2),   // newer, but left behind for more than a day
            ["leftover"] = TimeSpan.FromHours(1),
        };
        foreach (var (name, age) in ages)
        {
            var folder = Path.Combine(updates, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "request.json"), "{}");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "request.json"), (now - age).UtcDateTime);
            Directory.SetLastWriteTimeUtc(folder, (now - age).UtcDateTime);
        }

        Source.Offer("0.2.0");
        var coordinator = NewCoordinator(clock: new FixedClock(now));
        await coordinator.CheckNowAsync();

        Assert.Equal("0.3.0", Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(updates))));
    }

    [AvaloniaFact]
    public async Task AThrowingWindowDoesNotStopTheOthersFromHearing()
    {
        Source.Offer("0.2.0");
        var coordinator = NewCoordinator();
        var heard = 0;
        coordinator.Changed += () => throw new InvalidOperationException("a broken window");
        coordinator.Changed += () => heard++;

        await coordinator.CheckNowAsync();

        Assert.Equal(1, heard);
        Assert.Equal(UpdateStage.Available, coordinator.State.Stage);
    }

    /// <summary>
    /// Answers the requests of <see cref="GitHubUpdateSource"/> like the release storage: the latest release names 0.2.0,
    /// manifest and signature are those of <paramref name="fake"/>, every other address is the file (its content).
    /// </summary>
    private sealed class ReleaseHandler(FakeUpdateSourceApp fake) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            byte[] body = url.EndsWith("/releases/latest", StringComparison.Ordinal) ? Encoding.UTF8.GetBytes("""{"tag_name":"v0.2.0"}""")
                : url.EndsWith("/" + UpdateStaging.ManifestFileName, StringComparison.Ordinal) ? fake.Documents!.Manifest
                : url.EndsWith("/" + UpdateStaging.SignatureFileName, StringComparison.Ordinal) ? Encoding.UTF8.GetBytes(fake.Documents!.Signature)
                : fake.Content;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>A file on a disk that is full after the first write while <paramref name="full"/> says so.</summary>
    private sealed class FullDiskFileStream(string path, Func<bool> full) : FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true)
    {
        private int _writes;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _writes++ > 0 && full() ? throw new IOException("There is not enough space on the disk.") : base.WriteAsync(buffer, cancellationToken);
    }

    private async Task<UpdateCoordinator> OfferedAsync(PackageForm form = PackageForm.WindowsInstaller, TimeProvider? clock = null, bool noApplier = false)
    {
        Source.Offer("0.2.0");
        var coordinator = NewCoordinator(form: form, clock: clock, noApplier: noApplier);
        await coordinator.CheckNowAsync();
        Assert.Equal(UpdateState.Offer(Offered), coordinator.State);
        return coordinator;
    }
}
