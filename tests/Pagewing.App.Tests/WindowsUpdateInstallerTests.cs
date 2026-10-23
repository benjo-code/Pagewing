using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Pagewing.App.Platform.Windows;
using Pagewing.App.Services;
using Pagewing.App.ViewModels;
using Pagewing.App.Views;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Installing on Windows (Milestone 8.1, task 10): the app copies its program folder, writes the session, starts the copy
/// in the helper mode (<c>--finish-update</c>) and ends; the helper verifies manifest and setup program again, runs the
/// setup program silently and starts the new version, or the old one again after a failure. Processes are fakes
/// (<see cref="FakeProcessLauncher"/>): nothing is started, installed or written outside a temporary folder.
/// </summary>
// The helper window reads Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class WindowsUpdateInstallerTests : UpdateTestHost
{
    private const int OldProcess = 4242;
    private static readonly UpdateVersion Offered = new(0, 2, 0);

    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeShutdown _shutdown = new();

    private string ProgramDirectory => Path.Combine(ConfigDirectory, "Program Files", "Pagewing");

    private string InstalledExe => Path.Combine(ProgramDirectory, "Pagewing.exe");

    private string UpdaterDirectory => Path.Combine(ConfigDirectory, "Local", "Pagewing", "updater");

    private string SessionPath => Path.Combine(ConfigDirectory, SessionStore.FileName);

    private static SessionSnapshot OneTab(string path) => new([new SessionWindow([new SessionTab(path, null)], 0)]);

    [Fact]
    public async Task CopiesTheProgramFolderAndStartsTheCopyWithTheSwitch()
    {
        MakeProgramFolder();
        var staging = Stage();

        var result = await Installer().ApplyAsync(Staged(staging), CancellationToken.None);

        Assert.True(result.IsRestarting);
        var (file, arguments) = Assert.Single(_launcher.Started);
        var copy = Path.GetDirectoryName(file)!;
        Assert.Equal(UpdaterDirectory, Path.GetDirectoryName(copy));
        Assert.True(Guid.TryParseExact(Path.GetFileName(copy), "N", out _));
        Assert.Equal("Pagewing.exe", Path.GetFileName(file));

        // The helper runs from the copy with everything the program needs next to it (native libraries), never from the
        // program folder the setup program replaces.
        foreach (var name in new[] { "Pagewing.exe", "pdfium.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll" })
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(ProgramDirectory, name)), File.ReadAllBytes(Path.Combine(copy, name)));
        }

        Assert.Equal(["--finish-update", staging, "--after-process", "4242", "--restore-session"], arguments);
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    public async Task TheLanguageOptionGoesWithTheHelper()
    {
        MakeProgramFolder();

        await Installer(language: "de").ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.Equal(["--lang", "de"], _launcher.Started[0].Arguments[^2..]);
    }

    [Fact]
    public async Task WritesTheSessionBeforeStarting()
    {
        MakeProgramFolder();
        var pdf = Path.Combine(ConfigDirectory, "a.pdf");
        SessionSnapshot? atStart = null;
        _launcher.OnStart = (_, _) => atStart = new SessionStore(SessionPath).Load();

        await Installer(capture: () => OneTab(pdf)).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.NotNull(atStart);
        Assert.Equal(pdf, Assert.Single(atStart.Windows).Active.Path);
        Assert.Contains("--restore-session", _launcher.Started[0].Arguments);
    }

    [Fact]
    public async Task StaleSessionIsDeletedBeforeWriting()
    {
        MakeProgramFolder();
        File.WriteAllText(SessionPath, """{ "Windows": [ { "ActiveIndex": 0, "Tabs": [ { "Path": "C:\\old.pdf" } ] } ] }""");

        // Writing fails (a folder where the temporary file goes): the old file must not be restored by the new version.
        Directory.CreateDirectory(SessionPath + ".tmp");
        await Installer(capture: () => OneTab(@"C:\new.pdf")).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task NoRestoreSwitchWhenTheSessionCouldNotBeWritten()
    {
        MakeProgramFolder();
        Directory.CreateDirectory(SessionPath + ".tmp");

        var result = await Installer(capture: () => OneTab(@"C:\a.pdf")).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.True(result.IsRestarting);
        Assert.DoesNotContain("--restore-session", _launcher.Started[0].Arguments);
    }

    [Fact]
    public async Task AHelperThatDoesNotStartLeavesTheAppRunning()
    {
        MakeProgramFolder();
        _launcher.StartFails = true;

        var result = await Installer(capture: () => OneTab(@"C:\a.pdf")).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(result.IsRestarting);
        Assert.Equal(0, _shutdown.Calls);
        Assert.False(File.Exists(SessionPath));
        Assert.Empty(Directory.EnumerateDirectories(UpdaterDirectory));
    }

    [Fact]
    public async Task OldCopiesAreCleanedUp()
    {
        MakeProgramFolder();
        var now = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var old = Path.Combine(UpdaterDirectory, Guid.NewGuid().ToString("N"));
        var recent = Path.Combine(UpdaterDirectory, Guid.NewGuid().ToString("N"));
        foreach (var (folder, age) in new[] { (old, TimeSpan.FromDays(2)), (recent, TimeSpan.FromHours(2)) })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "Pagewing.exe"), "copy");
            File.SetLastWriteTimeUtc(Path.Combine(folder, "Pagewing.exe"), (now - age).UtcDateTime);
            Directory.SetLastWriteTimeUtc(folder, (now - age).UtcDateTime);
        }

        await Installer(clock: new FixedClock(now)).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.Equal(2, Directory.EnumerateDirectories(UpdaterDirectory).Count());
    }

    [Fact]
    public async Task HelperRefusesAFolderOutsideTheUpdatesFolder()
    {
        var elsewhere = Path.Combine(ConfigDirectory, "elsewhere", "0.2.0");
        CopyStaging(Stage(), elsewhere);
        foreach (var refused in new[] { elsewhere, Path.Combine(ConfigDirectory, UpdateStaging.DirectoryName, "..", "elsewhere", "0.2.0"), ConfigDirectory })
        {
            var helper = Helper(refused);

            await helper.RunAsync();

            Assert.Equal(UpdateStage.Failed, helper.State.Stage);
            Assert.Equal(UpdateFailure.Rejected, helper.State.Error);
            Assert.False(helper.CanRetry);

            // "Close" never deletes a folder that is not one of the updates.
            helper.Dismiss();
            Assert.True(Directory.Exists(refused));
        }

        Assert.Empty(_launcher.Runs);
        Assert.Empty(_launcher.Started);
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public async Task HelperRefusesAChangedInstaller()
    {
        var staging = Stage();
        WriteSession();
        var setup = Path.Combine(staging, SetupName);
        var bytes = File.ReadAllBytes(setup);
        bytes[100] ^= 0x01;
        File.WriteAllBytes(setup, bytes);
        var helper = Helper(staging);

        await helper.RunAsync();

        Assert.Empty(_launcher.Runs);
        Assert.Equal(UpdateStage.Failed, helper.State.Stage);
        Assert.Equal(UpdateFailure.Checksum, helper.State.Error);
        Assert.Equal(UpdateStage.Verifying, helper.FailedAt);
        Assert.False(helper.CanRetry);

        // The app ended already: the version that is there comes back with its tabs.
        Assert.Equal([InstalledExe + " --restore-session"], Started());
    }

    [Fact]
    public async Task HelperRefusesAChangedManifest()
    {
        var staging = Stage();
        var manifest = Path.Combine(staging, UpdateStaging.ManifestFileName);
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"size\":1000", "\"size\":1001", StringComparison.Ordinal));
        var helper = Helper(staging);

        await helper.RunAsync();

        Assert.Empty(_launcher.Runs);
        Assert.Equal(UpdateFailure.Rejected, helper.State.Error);
        Assert.Equal([InstalledExe], Started());
    }

    [Fact]
    public async Task HelperRefusesAManifestOfAnotherVersionThanTheRequest()
    {
        // Signed and newer, but not the version the request (and the user) agreed to.
        var staging = Stage();
        Source.Offer("0.3.0");
        File.WriteAllBytes(Path.Combine(staging, UpdateStaging.ManifestFileName), Source.Documents!.Manifest);
        File.WriteAllText(Path.Combine(staging, UpdateStaging.SignatureFileName), Source.Documents.Signature);
        var helper = Helper(staging);

        await helper.RunAsync();

        Assert.Empty(_launcher.Runs);
        Assert.Equal(UpdateFailure.Rejected, helper.State.Error);
    }

    [Fact]
    public async Task HelperRunsInnoSilentWithTheLogOption()
    {
        // A config folder with a space, an umlaut and an ampersand, as user names can have.
        var config = Path.Combine(ConfigDirectory, "Benutzer Ä&", "Pagewing");
        var staging = Stage(config: config);
        var helper = Helper(staging, config: config);

        await helper.RunAsync();

        var (file, commandLine) = Assert.Single(_launcher.Runs);
        Assert.Equal(Path.Combine(staging, SetupName), file);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART \"/LOG=" + Path.Combine(staging, "setup.log") + "\"", commandLine);
    }

    [Fact]
    public async Task TheInstallerCannotBeChangedWhileItRuns()
    {
        var staging = Stage();
        var setup = Path.Combine(staging, SetupName);
        Exception? writing = null;
        _launcher.OnRun = _ =>
        {
            try
            {
                File.WriteAllBytes(setup, [1, 2, 3]);
            }
            catch (IOException exception)
            {
                writing = exception;
            }
        };

        await Helper(staging).RunAsync();

        // Verified and run while the helper holds it open against writing (no change between the check and the start).
        Assert.Single(_launcher.Runs);
        Assert.IsAssignableFrom<IOException>(writing);
    }

    [Fact]
    public async Task SuccessStartsTheNewVersionWithRestoreSession()
    {
        var staging = Stage();
        var pdf = Path.Combine(ConfigDirectory, "a.pdf");
        WriteSession(pdf);
        SessionSnapshot? atStart = null;
        _launcher.OnStart = (_, _) => atStart = new SessionStore(SessionPath).Load();
        var helper = Helper(staging, language: "de");

        await helper.RunAsync();

        Assert.Equal(UpdateStage.Restarting, helper.State.Stage);
        Assert.Equal([InstalledExe + " --restore-session --lang de"], Started());
        Assert.Equal(pdf, atStart?.Windows[0].Active.Path);
        Assert.Equal(1, _shutdown.Calls);
        Assert.False(Directory.Exists(staging));
        Assert.Equal([OldProcess], _launcher.Waited);
    }

    [Fact]
    public async Task FailureStartsTheOldVersionWithRestoreSessionAndShowsRetry()
    {
        var staging = Stage();
        var pdf = Path.Combine(ConfigDirectory, "a.pdf");
        WriteSession(pdf);
        _launcher.ExitCodes.Enqueue(4);
        var helper = Helper(staging);

        await helper.RunAsync();

        Assert.Equal(UpdateStage.Failed, helper.State.Stage);
        Assert.Equal(UpdateFailure.InstallFailed, helper.State.Error);
        Assert.Equal(UpdateStage.Installing, helper.FailedAt);
        Assert.True(helper.CanRetry);
        Assert.Equal([InstalledExe + " --restore-session"], Started());
        Assert.Equal(0, _shutdown.Calls);
        Assert.True(Directory.Exists(staging));

        // The window shows the message with "Try again" and "Close".
        using (var viewModel = new UpdateViewModel(helper, _ => { }))
        {
            Assert.True(viewModel.IsFailed);
            Assert.True(viewModel.CanRetry);
            Assert.False(viewModel.OffersDownloadPage);
            Assert.False(viewModel.IsLocked);
        }

        // The old version used the session file; "Try again" brings it back for the new one.
        new SessionStore(SessionPath).Delete();
        await helper.RetryAsync();

        Assert.Equal(2, _launcher.Runs.Count);
        Assert.Equal(UpdateStage.Restarting, helper.State.Stage);
        Assert.Equal(InstalledExe + " --restore-session", Started()[^1]);
        Assert.Equal(pdf, new SessionStore(SessionPath).Load().Windows[0].Active.Path);
        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task CloseAfterAFailureDeletesTheUpdateAndEnds()
    {
        var staging = Stage();
        _launcher.ExitCodes.Enqueue(2);
        var helper = Helper(staging);
        var changes = 0;
        helper.Changed += () => changes++;

        await helper.RunAsync();
        helper.Dismiss();

        Assert.Equal(UpdateStage.None, helper.State.Stage);
        Assert.False(Directory.Exists(staging));
        Assert.Single(_launcher.Runs);
        Assert.True(changes > 0);
    }

    [Theory]
    [InlineData(1223)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(-1)]
    public async Task UacDeclinedCountsAsAFailure(int exitCode)
    {
        var staging = Stage();
        _launcher.ExitCodes.Enqueue(exitCode);
        var helper = Helper(staging);

        await helper.RunAsync();

        Assert.Equal(UpdateFailure.InstallFailed, helper.State.Error);
        Assert.True(helper.CanRetry);
        Assert.Equal(InstalledExe, Assert.Single(_launcher.Started).File);
        Assert.True(Directory.Exists(staging));
    }

    [Fact]
    public async Task WaitsForTheOldProcessToExit()
    {
        var staging = Stage();
        _launcher.ExitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = Helper(staging);

        var run = helper.RunAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Neither installed nor restarted while the old version still runs (the new one would start without its pipe).
        Assert.False(run.IsCompleted);
        Assert.Empty(_launcher.Runs);
        Assert.Empty(_launcher.Started);
        Assert.Equal([OldProcess], _launcher.Waited);

        _launcher.ExitGate.SetResult();
        await run;
        Assert.Single(_launcher.Runs);
        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task CancelWhileWaitingKeepsTheOldVersion()
    {
        var staging = Stage();
        WriteSession();
        _launcher.ExitGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helper = Helper(staging);

        var run = helper.RunAsync();
        helper.CancelInstall();

        // At once, not after the wait has stopped: the window closes with "Cancel", and the process may end with it.
        Assert.Equal(UpdateStage.None, helper.State.Stage);
        Assert.Equal([InstalledExe + " --restore-session"], Started());
        Assert.Equal(1, _shutdown.Calls);
        await run;

        Assert.Empty(_launcher.Runs);
        Assert.Equal([InstalledExe + " --restore-session"], Started());
        Assert.Equal(UpdateStage.None, helper.State.Stage);
        Assert.False(Directory.Exists(staging));
    }

    [AvaloniaFact]
    public async Task TheHelperWindowTakesThePlaceOfTheProgressWindowAndShowsTheFailure()
    {
        var staging = Stage(left: 140, top: 90);
        _launcher.ExitCodes.Enqueue(1223);
        var helper = Helper(staging);

        var window = UpdateWindow.ShowHelper(helper);
        OwnWindow(window);
        Settle(window);
        Assert.Equal(new PixelPoint(140, 90), window.Position);
        Assert.Equal("Updating Pagewing", Find<TextBlock>(window, "UpdateHeading").Text);

        await helper.RunAsync();
        Settle(window);

        Assert.Equal("The update did not go through", Find<TextBlock>(window, "UpdateHeading").Text);
        Assert.True(Find<Button>(window, "UpdateRetryButton").IsEffectivelyVisible);
        Assert.True(Find<Button>(window, "UpdateCloseButton").IsEffectivelyVisible);
        Assert.False(Find<Button>(window, "UpdateDownloadPageButton").IsEffectivelyVisible);

        // "Close" ends the helper: the window goes and the update with it.
        Find<Button>(window, "UpdateCloseButton").Command!.Execute(null);
        Settle(window);
        Assert.False(window.IsVisible);
        Assert.False(Directory.Exists(staging));
    }

    private const string SetupName = "Pagewing-0.2.0-win-x64-setup.exe";

    private static void CopyStaging(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    // Each start as "<program> <arguments>".
    private string[] Started() => [.. _launcher.Started.Select(start => string.Join(' ', [start.File, .. start.Arguments]))];

    private void MakeProgramFolder()
    {
        Directory.CreateDirectory(Path.Combine(ProgramDirectory, "cli"));
        foreach (var name in new[] { "Pagewing.exe", "pdfium.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll", "unins000.exe" })
        {
            File.WriteAllText(Path.Combine(ProgramDirectory, name), "bytes of " + name);
        }

        File.WriteAllText(Path.Combine(ProgramDirectory, "cli", "pagewing.exe"), "cli");
    }

    private void WriteSession(string? pdf = null) =>
        Assert.True(new SessionStore(SessionPath).TrySave(OneTab(pdf ?? Path.Combine(ConfigDirectory, "a.pdf"))));

    /// <summary>Lays out the staging folder of 0.2.0 as the coordinator leaves it, signed with the key of <see cref="UpdateTestHost.Source"/>.</summary>
    private string Stage(string? config = null, int? left = null, int? top = null)
    {
        Source.Offer("0.2.0");
        var staging = Path.Combine(config ?? ConfigDirectory, UpdateStaging.DirectoryName, "0.2.0");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, SetupName), Source.Content);
        File.WriteAllBytes(Path.Combine(staging, UpdateStaging.ManifestFileName), Source.Documents!.Manifest);
        File.WriteAllText(Path.Combine(staging, UpdateStaging.SignatureFileName), Source.Documents.Signature, new UTF8Encoding(false));
        var request = new UpdateRequest
        {
            Version = "0.2.0",
            FileName = SetupName,
            Form = PackageForm.WindowsInstaller,
            ExePath = InstalledExe,
            TargetDirectory = ProgramDirectory,
            Left = left,
            Top = top,
            Width = left is null ? null : 460,
            Height = left is null ? null : 360,
        };
        Assert.True(request.TryWrite(Path.Combine(staging, UpdateStaging.RequestFileName)));
        return staging;
    }

    private StagedUpdate Staged(string staging) =>
        new(staging, Offered, new UpdateFile(UpdateFileKinds.WindowsSetup, SetupName, Source.Content.Length, new string('0', 64)), PackageForm.WindowsInstaller);

    private WindowsUpdateInstaller Installer(Func<SessionSnapshot>? capture = null, string? language = null, TimeProvider? clock = null) =>
        new(new WindowsUpdateInstallerSetup(
            ProgramDirectory, UpdaterDirectory, ConfigDirectory, OldProcess, capture ?? (() => OneTab(Path.Combine(ConfigDirectory, "a.pdf"))),
            _shutdown, _launcher, clock ?? TimeProvider.System)
        {
            Language = language,
        });

    private WindowsUpdateHelper Helper(string staging, string? config = null, string? language = null)
    {
        var helper = new WindowsUpdateHelper(new WindowsUpdateHelperSetup(staging, config ?? ConfigDirectory, Source.Keys, Running, _launcher, _shutdown)
        {
            AfterProcess = OldProcess,
            RestoreSession = File.Exists(Path.Combine(config ?? ConfigDirectory, SessionStore.FileName)),
            Language = language,
        });
        Own(helper);
        return helper;
    }

    private sealed class FakeShutdown : IAppShutdown
    {
        public int Calls { get; private set; }

        public void Shutdown() => Calls++;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
