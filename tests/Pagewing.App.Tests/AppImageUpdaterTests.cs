using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Pagewing.App.Platform.Linux;
using Pagewing.App.Resources;
using Pagewing.App.Services;
using Pagewing.Core.Updates;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>
/// Installing an AppImage (Milestone 8.1, task 11): the verified file is copied next to the running AppImage, checked
/// again, and renamed over it; the new AppImage starts with <c>--restore-session</c> once the old process ended. The file
/// system is a temporary folder and processes are fakes (<see cref="FakeProcessLauncher"/>): nothing is started and
/// nothing outside the temporary folder is touched, least of all the real <c>APPIMAGE</c>.
/// </summary>
// The failure texts read Strings, whose culture is process-wide (see LocalizationTests).
[Collection("Localization")]
public sealed class AppImageUpdaterTests : UpdateTestHost
{
    private const int OldProcess = 4242;
    private const string AppImageName = "Pagewing-0.2.0-x86_64.AppImage";
    private static readonly UpdateVersion Offered = new(0, 2, 0);
    private static readonly byte[] OldBytes = Encoding.ASCII.GetBytes("the running AppImage");

    private readonly FakeProcessLauncher _launcher = new();
    private readonly FakeShutdown _shutdown = new();

    private string Folder => Path.Combine(ConfigDirectory, "apps");

    private string Target => Path.Combine(Folder, "Pagewing.AppImage");

    private string SessionPath => Path.Combine(ConfigDirectory, SessionStore.FileName);

    private static SessionSnapshot OneTab(string path) => new([new SessionWindow([new SessionTab(path, null)], 0)]);

    [Fact]
    public async Task ReplacesTheFileAtomically()
    {
        MakeTarget();

        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.True(result.IsRestarting);
        Assert.Equal(Source.Content, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".new"));
        Assert.Equal([Target], Directory.EnumerateFiles(Folder));
        Assert.Equal(1, _shutdown.Calls);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task ReplacesTheFileAndKeepsItExecutable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes exist on Linux only.");
        MakeTarget();
        File.SetUnixFileMode(Target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        using (var runningFile = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

            Assert.True(result.IsRestarting);

            // The running program keeps its file (its open inode); the path has the new one.
            var running = new byte[OldBytes.Length];
            runningFile.ReadExactly(running);
            Assert.Equal(OldBytes, running);
        }

        Assert.Equal(Source.Content, File.ReadAllBytes(Target));
        var mode = File.GetUnixFileMode(Target);
        Assert.True(mode.HasFlag(UnixFileMode.UserExecute), mode.ToString());
        Assert.True(mode.HasFlag(UnixFileMode.UserRead | UnixFileMode.UserWrite), mode.ToString());
        Assert.True(mode.HasFlag(UnixFileMode.GroupRead), mode.ToString());
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AFileOfTheOldModeWithoutExecuteBecomesExecutableForTheOwner()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes exist on Linux only.");
        MakeTarget();
        File.SetUnixFileMode(Target, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.True(File.GetUnixFileMode(Target).HasFlag(UnixFileMode.UserExecute));
    }

    [Fact]
    public async Task LeavesTheOldFileOnAWriteFailure()
    {
        MakeTarget();

        // A folder where the new file goes: it cannot be written, and it is not ours to delete.
        Directory.CreateDirectory(Target + ".new");
        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(result.IsRestarting);
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.True(Directory.Exists(Target + ".new"));
        Assert.Empty(_launcher.Started);
        Assert.Equal(0, _shutdown.Calls);
        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task ReadOnlyFolderShowsTheManualHint()
    {
        MakeTarget();
        Directory.CreateDirectory(Target + ".new");

        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(result.IsRestarting);
        Assert.True(result.OffersDownloadPage);
        Assert.Equal(
            string.Format(Strings.Culture, System.Text.CompositeFormat.Parse(Strings.UpdateErrorFolderWriteProtected), Folder),
            result.Message);
        Assert.Contains(Folder, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task AFolderWithoutWritePermissionShowsTheManualHint()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes exist on Linux only.");
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "Root writes into any folder.");
        MakeTarget();
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

            Assert.False(result.IsRestarting);
            Assert.True(result.OffersDownloadPage);
            Assert.Contains(Folder, result.Message, StringComparison.Ordinal);
            Assert.Equal(OldBytes, File.ReadAllBytes(Target));
            Assert.Equal([Target], Directory.EnumerateFiles(Folder));
        }
        finally
        {
            File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task AStaleNewFileOfAnEarlierAttemptIsReplaced()
    {
        MakeTarget();
        File.WriteAllText(Target + ".new", "half a copy of an earlier attempt that is longer than the file it was meant to be");

        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.True(result.IsRestarting);
        Assert.Equal(Source.Content, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".new"));
    }

    [Fact]
    public async Task StartsTheNewFileWithRestoreSession()
    {
        MakeTarget();

        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.True(result.IsRestarting);
        var (file, arguments) = Assert.Single(_launcher.Started);
        Assert.Equal(Target, file);

        // The new program waits for this process itself (the switch), so the single-instance election finds the lock free.
        Assert.Equal(["--after-process", "4242", "--restore-session"], arguments);
        Assert.Equal(1, _shutdown.Calls);
        Assert.Empty(_launcher.Runs);
    }

    [Fact]
    public async Task TheLanguageOptionGoesWithTheNewFile()
    {
        MakeTarget();

        await Updater(language: "de").ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.Equal(["--lang", "de"], _launcher.Started[0].Arguments[^2..]);
    }

    [Fact]
    public async Task WritesTheSessionBeforeStarting()
    {
        MakeTarget();
        var pdf = Path.Combine(ConfigDirectory, "a.pdf");
        SessionSnapshot? atStart = null;
        byte[]? fileAtStart = null;
        _launcher.OnStart = (_, _) =>
        {
            atStart = new SessionStore(SessionPath).Load();
            fileAtStart = File.ReadAllBytes(Target);
        };

        await Updater(capture: () => OneTab(pdf)).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.NotNull(atStart);
        Assert.Equal(pdf, Assert.Single(atStart.Windows).Active.Path);
        Assert.Contains("--restore-session", _launcher.Started[0].Arguments);

        // What starts is the new file, not the old one.
        Assert.Equal(Source.Content, fileAtStart);
    }

    [Fact]
    public async Task StaleSessionIsDeletedBeforeWriting()
    {
        MakeTarget();
        File.WriteAllText(SessionPath, """{ "Windows": [ { "ActiveIndex": 0, "Tabs": [ { "Path": "C:\\old.pdf" } ] } ] }""");

        // Writing fails (a folder where the temporary file goes): the old file must not be restored by the new version.
        Directory.CreateDirectory(SessionPath + ".tmp");
        await Updater(capture: () => OneTab(@"C:\new.pdf")).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task NoRestoreSwitchWhenTheSessionCouldNotBeWritten()
    {
        MakeTarget();
        Directory.CreateDirectory(SessionPath + ".tmp");

        var result = await Updater(capture: () => OneTab(@"C:\a.pdf")).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.True(result.IsRestarting);
        Assert.DoesNotContain("--restore-session", _launcher.Started[0].Arguments);
    }

    [Fact]
    public async Task NoRestoreSwitchWithoutWindows()
    {
        MakeTarget();

        await Updater(capture: () => SessionSnapshot.Empty).ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.DoesNotContain("--restore-session", _launcher.Started[0].Arguments);
        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task ANewFileThatDoesNotStartLeavesTheAppRunning()
    {
        MakeTarget();
        _launcher.StartFails = true;

        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(result.IsRestarting);
        Assert.Equal(0, _shutdown.Calls);

        // The file is replaced already (the next start of the user is the new version), but no session waits for anybody.
        Assert.Equal(Source.Content, File.ReadAllBytes(Target));
        Assert.False(File.Exists(SessionPath));
    }

    [Fact]
    public async Task RefusesAChangedFile()
    {
        MakeTarget();
        var staging = Stage();
        var staged = Path.Combine(staging, AppImageName);
        var bytes = File.ReadAllBytes(staged);
        bytes[100] ^= 0x01;
        File.WriteAllBytes(staged, bytes);

        var result = await Updater().ApplyAsync(Staged(staging), CancellationToken.None);

        AssertNothingChanged(result);
        Assert.Equal(Strings.UpdateErrorChecksum, result.Message);
    }

    [Fact]
    public async Task RefusesAManifestOfAnotherKey()
    {
        MakeTarget();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keys = new[] { new UpdateKey(Source.Key.Id, other.ExportSubjectPublicKeyInfo()) };

        var result = await Updater(keys: keys).ApplyAsync(Staged(Stage()), CancellationToken.None);

        AssertNothingChanged(result);
        Assert.Equal(Strings.UpdateErrorRejected, result.Message);
    }

    [Fact]
    public async Task RefusesAManifestThatIsNotNewerThanTheRunningVersion()
    {
        MakeTarget();

        var result = await Updater(running: new UpdateVersion(0, 2, 0)).ApplyAsync(Staged(Stage()), CancellationToken.None);

        AssertNothingChanged(result);
    }

    [Fact]
    public async Task RefusesAManifestOfAnotherVersionThanTheOneStaged()
    {
        MakeTarget();
        var staging = Stage();

        var result = await Updater().ApplyAsync(Staged(staging) with { Version = new UpdateVersion(0, 3, 0) }, CancellationToken.None);

        AssertNothingChanged(result);
    }

    [Fact]
    public async Task RefusesAStagingFolderOutsideTheUpdatesFolder()
    {
        MakeTarget();
        var elsewhere = Path.Combine(ConfigDirectory, "elsewhere", "0.2.0");
        CopyStaging(Stage(), elsewhere);

        var result = await Updater().ApplyAsync(Staged(elsewhere), CancellationToken.None);

        AssertNothingChanged(result);
        Assert.True(Directory.Exists(elsewhere));
    }

    [Fact]
    public async Task RefusesAFileNameWithAPath()
    {
        MakeTarget();
        var staging = Stage();
        var staged = Staged(staging) with { File = new UpdateFile(UpdateFileKinds.LinuxAppImage, "../" + AppImageName, Source.Content.Length, new string('0', 64)) };

        var result = await Updater().ApplyAsync(staged, CancellationToken.None);

        AssertNothingChanged(result);
    }

    [Fact]
    public async Task AMissingStagedFileFailsAndLeavesNothingBehind()
    {
        MakeTarget();
        var staging = Stage();
        File.Delete(Path.Combine(staging, AppImageName));

        var result = await Updater().ApplyAsync(Staged(staging), CancellationToken.None);

        AssertNothingChanged(result);
    }

    [Fact]
    public async Task ATargetThatIsNotThereFailsWithTheDownloadPage()
    {
        var result = await Updater().ApplyAsync(Staged(Stage()), CancellationToken.None);

        Assert.False(result.IsRestarting);
        Assert.True(result.OffersDownloadPage);
        Assert.Empty(_launcher.Started);
        Assert.Equal(0, _shutdown.Calls);
        Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData(PackageForm.Deb)]
    [InlineData(PackageForm.WindowsPortable)]
    [InlineData(PackageForm.WindowsInstaller)]
    [InlineData(PackageForm.Unknown)]
    public async Task OtherFormsAreRefusedWithTheDownloadPage(PackageForm form)
    {
        MakeTarget();

        var result = await Updater().ApplyAsync(Staged(Stage()) with { Form = form }, CancellationToken.None);

        Assert.False(result.IsRestarting);
        Assert.True(result.OffersDownloadPage);
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task CancellationBeforeTheSwapChangesNothing()
    {
        MakeTarget();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Updater().ApplyAsync(Staged(Stage()), cancel.Token));

        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".new"));
        Assert.Empty(_launcher.Started);
    }

    [Fact]
    public async Task TheStagingFolderGoesAfterTheSwap()
    {
        MakeTarget();
        var staging = Stage();

        await Updater().ApplyAsync(Staged(staging), CancellationToken.None);

        Assert.False(Directory.Exists(staging));
    }

    [Fact]
    public async Task TheStagingFolderStaysWhenTheSwapFails()
    {
        MakeTarget();
        var staging = Stage();
        Directory.CreateDirectory(Target + ".new");

        await Updater().ApplyAsync(Staged(staging), CancellationToken.None);

        // The coordinator removes it for a failed result; the applier does not guess.
        Assert.True(Directory.Exists(staging));
    }

    [Theory]
    [InlineData(PackageForm.Deb)]
    [InlineData(PackageForm.WindowsPortable)]
    [InlineData(PackageForm.Unknown)]
    public void NoApplierForDebPortableUnknown(PackageForm form)
    {
        var host = new UpdateAppHost(() => SessionSnapshot.Empty, _shutdown);

        Assert.Null(PlatformUpdate.CreateApplier(form, isWindows: false, isLinux: true, null, Target, ConfigDirectory, host));
        Assert.Null(PlatformUpdate.CreateApplier(form, isWindows: true, isLinux: false, ProcessPath, Target, ConfigDirectory, host));
    }

    [Fact]
    public void TheAppImageGetsItsApplierOnLinuxOnly()
    {
        var host = new UpdateAppHost(() => SessionSnapshot.Empty, _shutdown);

        Assert.IsType<AppImageUpdater>(PlatformUpdate.CreateApplier(PackageForm.AppImage, isWindows: false, isLinux: true, null, Target, ConfigDirectory, host));
        Assert.Null(PlatformUpdate.CreateApplier(PackageForm.AppImage, isWindows: true, isLinux: false, null, Target, ConfigDirectory, host));
        Assert.Null(PlatformUpdate.CreateApplier(PackageForm.AppImage, isWindows: false, isLinux: true, null, null, ConfigDirectory, host));
        Assert.Null(PlatformUpdate.CreateApplier(PackageForm.AppImage, isWindows: false, isLinux: true, null, string.Empty, ConfigDirectory, host));
        Assert.Null(PlatformUpdate.CreateApplier(PackageForm.AppImage, isWindows: false, isLinux: true, null, Target, ConfigDirectory, host: null));
    }

    [Fact]
    public async Task TheNewProgramWaitsForTheOldProcessBeforeItElects()
    {
        var gate = new TaskCompletionSource();
        _launcher.ExitGate = gate;
        var options = StartupOptions.Parse(["--after-process", "4242", "--restore-session"]);

        var waiting = Task.Run(() => Program.WaitForPredecessor(options, _launcher, TimeSpan.FromSeconds(30)), TestContext.Current.CancellationToken);
        await PumpUntilAsync(() => _launcher.Waited.Count == 1);

        Assert.False(waiting.IsCompleted);
        Assert.Equal([4242], _launcher.Waited);
        gate.SetResult();
        await waiting;
    }

    [Fact]
    public void NoWaitWithoutTheSwitchOrForTheHelperOrAMeasurement()
    {
        string[][] launches =
        [
            [],
            ["a.pdf"],
            ["--finish-update", ConfigDirectory, "--after-process", "4242"],
            ["--measure-startup", "a.pdf", "--after-process", "4242"],
        ];
        foreach (var args in launches)
        {
            Program.WaitForPredecessor(StartupOptions.Parse(args), _launcher, TimeSpan.FromSeconds(30));
        }

        Assert.Empty(_launcher.Waited);
    }

    private static void CopyStaging(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        }
    }

    private void AssertNothingChanged(ApplyResult result)
    {
        Assert.False(result.IsRestarting);
        Assert.Equal(OldBytes, File.ReadAllBytes(Target));
        Assert.False(File.Exists(Target + ".new"));
        Assert.Empty(_launcher.Started);
        Assert.Equal(0, _shutdown.Calls);
        Assert.False(File.Exists(SessionPath));
    }

    private void MakeTarget()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Target, OldBytes);
    }

    /// <summary>Lays out the staging folder of 0.2.0 as the coordinator leaves it, signed with the key of <see cref="UpdateTestHost.Source"/>.</summary>
    private string Stage()
    {
        Source.Offer("0.2.0");
        var staging = Path.Combine(ConfigDirectory, UpdateStaging.DirectoryName, "0.2.0");
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, AppImageName), Source.Content);
        File.WriteAllBytes(Path.Combine(staging, UpdateStaging.ManifestFileName), Source.Documents!.Manifest);
        File.WriteAllText(Path.Combine(staging, UpdateStaging.SignatureFileName), Source.Documents.Signature, new UTF8Encoding(false));
        var request = new UpdateRequest
        {
            Version = "0.2.0",
            FileName = AppImageName,
            Form = PackageForm.AppImage,
            ExePath = Target,
            TargetDirectory = Folder,
        };
        Assert.True(request.TryWrite(Path.Combine(staging, UpdateStaging.RequestFileName)));
        return staging;
    }

    private StagedUpdate Staged(string staging) =>
        new(staging, Offered, new UpdateFile(UpdateFileKinds.LinuxAppImage, AppImageName, Source.Content.Length, new string('0', 64)), PackageForm.AppImage);

    private AppImageUpdater Updater(
        Func<SessionSnapshot>? capture = null,
        string? language = null,
        IReadOnlyList<UpdateKey>? keys = null,
        UpdateVersion? running = null) =>
        new(new AppImageUpdaterSetup(
            Target, ConfigDirectory, keys ?? Source.Keys, running ?? Running, OldProcess, capture ?? (() => OneTab(Path.Combine(ConfigDirectory, "a.pdf"))),
            _shutdown, _launcher)
        {
            Language = language,
        });

    private sealed class FakeShutdown : IAppShutdown
    {
        public int Calls { get; private set; }

        public void Shutdown() => Calls++;
    }
}
