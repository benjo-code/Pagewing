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
    public static UpdateCoordinator Create(UserSettings settings)
    {
        var processPath = Environment.ProcessPath;
        var form = PackageFormDetector.Detect(OperatingSystem.IsWindows(), processPath, File.Exists, Environment.GetEnvironmentVariable);
        var config = UserDataPaths.ConfigDirectory();
        var store = new UpdateStatusStore(Path.Combine(config, UpdateStatusStore.FileName));
        var source = GitHubUpdateSource.Create();
        var checker = new UpdateChecker(source, store, TimeProvider.System, RunningVersion(), UpdateTrust.Keys);
        var install = new UpdateInstallSetup(source, config, CreateApplier, TimeProvider.System)
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
    public static Func<UpdateCoordinator?>? FactoryFor(bool isMeasurement, UserSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return isMeasurement ? null : () => Create(settings);
    }

    /// <summary>
    /// The installer of this system, made when a verified update is ready. None yet: the Windows installer and the AppImage
    /// follow with tasks 10 and 11 of Milestone 8.1; until then the progress window says that this installation cannot
    /// install itself and points to the download page.
    /// </summary>
    private static IUpdateApplier? CreateApplier() => null;

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
