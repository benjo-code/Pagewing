using Pagewing.App.Platform.Linux;
using Pagewing.App.Platform.Windows;
using Pagewing.Core;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>Chooses what the update check of this system runs on; made on first use, never at startup.</summary>
internal static class PlatformUpdate
{
    /// <summary>
    /// Creates the coordinator of the shipped program: the release storage of the project as the source, the keys, the
    /// status file of the user and its folder for downloaded updates. Making it creates the HTTP client but sends nothing.
    /// </summary>
    /// <param name="settings">The settings of the app.</param>
    /// <param name="host">What an installer needs of the app (its session, ending it); without it nothing installs.</param>
    public static UpdateCoordinator Create(UserSettings settings, UpdateAppHost? host = null)
    {
        var processPath = Environment.ProcessPath;
        var form = PackageFormDetector.Detect(OperatingSystem.IsWindows(), processPath, File.Exists, Environment.GetEnvironmentVariable);
        var config = UserDataPaths.ConfigDirectory();
        var store = new UpdateStatusStore(Path.Combine(config, UpdateStatusStore.FileName));
        var source = GitHubUpdateSource.Create();
        var checker = new UpdateChecker(source, store, TimeProvider.System, RunningVersion(), UpdateTrust.Keys);
        var install = new UpdateInstallSetup(source, config, () => CreateApplier(form, processPath, config, host), TimeProvider.System)
        {
            ProcessPath = processPath,
            TargetDirectory = TargetDirectory(form, processPath, Environment.GetEnvironmentVariable("APPIMAGE")),
        };
        return new UpdateCoordinator(checker, settings, form, install);
    }

    /// <summary>
    /// Gets what makes the coordinator for the windows of a run: <see cref="Create"/>, or <see langword="null"/> in a
    /// measurement run, which never checks for updates and offers no command for it.
    /// </summary>
    /// <param name="isMeasurement">Whether this is a measurement run.</param>
    /// <param name="settings">The settings of the app.</param>
    /// <param name="host">What an installer needs of the app; without it the coordinator checks and downloads, but installs nothing.</param>
    public static Func<UpdateCoordinator?>? FactoryFor(bool isMeasurement, UserSettings settings, UpdateAppHost? host = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return isMeasurement ? null : () => Create(settings, host);
    }

    /// <summary>
    /// The installer of this system, made when a verified update is ready: on Windows for an installation of the setup
    /// program the helper copy (<see cref="WindowsUpdateInstaller"/>), on Linux for an AppImage the replacement of its own
    /// file (<see cref="AppImageUpdater"/>). Every other form (<c>.deb</c>, portable archive, unknown) gets none, and the
    /// progress window points to the download page.
    /// </summary>
    private static IUpdateApplier? CreateApplier(PackageForm form, string? processPath, string config, UpdateAppHost? host) =>
        CreateApplier(
            form, OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), processPath, Environment.GetEnvironmentVariable("APPIMAGE"), config, host);

    /// <summary>
    /// <see cref="CreateApplier(PackageForm, string?, string, UpdateAppHost?)"/> with the system and the <c>APPIMAGE</c>
    /// variable as arguments (tests).
    /// </summary>
    internal static IUpdateApplier? CreateApplier(
        PackageForm form,
        bool isWindows,
        bool isLinux,
        string? processPath,
        string? appImage,
        string config,
        UpdateAppHost? host)
    {
        if (host is null)
        {
            return null;
        }

        if (isWindows && form == PackageForm.WindowsInstaller)
        {
            return CreateWindowsApplier(processPath, config, host);
        }

        return isLinux && form == PackageForm.AppImage && !string.IsNullOrEmpty(appImage)
            ? new AppImageUpdater(new AppImageUpdaterSetup(
                appImage, config, UpdateTrust.Keys, RunningVersion(), Environment.ProcessId, host.CaptureSession, host.Shutdown, new SystemProcessLauncher())
            {
                Language = StartupLanguage(),
            })
            : null;
    }

    private static WindowsUpdateInstaller? CreateWindowsApplier(string? processPath, string config, UpdateAppHost host)
    {
        if (Path.GetDirectoryName(processPath) is not { Length: > 0 } programDirectory)
        {
            return null;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.GetTempPath();
        }

        return new WindowsUpdateInstaller(new WindowsUpdateInstallerSetup(
            programDirectory,
            WindowsUpdateInstaller.UpdaterDirectoryIn(localAppData),
            config,
            Environment.ProcessId,
            host.CaptureSession,
            host.Shutdown,
            new SystemProcessLauncher(),
            TimeProvider.System)
        {
            Language = StartupLanguage(),
        });
    }

    // Only a --lang of the command line goes on; the setting is read again by the helper and the new version.
    private static string? StartupLanguage() => StartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray()).Language;

    /// <summary>Where the program is installed: the folder of the AppImage for an AppImage, else the folder of the executable.</summary>
    private static string? TargetDirectory(PackageForm form, string? processPath, string? appImage)
    {
        var file = form == PackageForm.AppImage ? appImage : processPath;
        return string.IsNullOrEmpty(file) ? null : Path.GetDirectoryName(file);
    }

    /// <summary>
    /// The running version: <see cref="AppInfo.Version"/> up to a pre-release suffix (a version such as 0.2.0-rc.1 counts
    /// as 0.2.0, which is a plain release version); an unreadable one counts as 0.0.0.
    /// </summary>
    internal static UpdateVersion RunningVersion()
    {
        var text = AppInfo.Version;
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        return UpdateVersion.TryParse(dash < 0 ? text : text[..dash], out var version) ? version : default;
    }
}

/// <summary>What an installer of an update needs of the app: the windows and tabs to bring back, and the way to end the app.</summary>
/// <param name="CaptureSession">The session of all windows (<see cref="Workspace.CaptureSession"/>), on the UI thread.</param>
/// <param name="Shutdown">Ends the app once the installer took over.</param>
public sealed record UpdateAppHost(Func<SessionSnapshot> CaptureSession, IAppShutdown Shutdown);
