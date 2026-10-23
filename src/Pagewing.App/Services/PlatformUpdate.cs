using Pagewing.Core;
using Pagewing.Core.Settings;
using Pagewing.Core.Updates;

namespace Pagewing.App.Services;

/// <summary>Chooses what the update check of this system runs on; made on first use, never at startup.</summary>
internal static class PlatformUpdate
{
    /// <summary>
    /// Creates the coordinator of the shipped program: the release storage of the project as the source, the keys and the
    /// status file of the user. Making it creates the HTTP client but sends nothing.
    /// </summary>
    /// <param name="settings">The settings of the app.</param>
    public static UpdateCoordinator Create(UserSettings settings)
    {
        var form = PackageFormDetector.Detect(OperatingSystem.IsWindows(), Environment.ProcessPath, File.Exists, Environment.GetEnvironmentVariable);
        var store = new UpdateStatusStore(Path.Combine(UserDataPaths.ConfigDirectory(), UpdateStatusStore.FileName));
        var checker = new UpdateChecker(GitHubUpdateSource.Create(), store, TimeProvider.System, RunningVersion(), UpdateTrust.Keys);
        return new UpdateCoordinator(checker, settings, form);
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
