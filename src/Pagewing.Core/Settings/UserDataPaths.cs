namespace Pagewing.Core.Settings;

/// <summary>Where Pagewing keeps its local user files (settings, recent files). Nothing leaves this folder.</summary>
public static class UserDataPaths
{
    /// <summary>The config folder of the current user on this system.</summary>
    public static string ConfigDirectory() =>
        ConfigDirectory(Environment.GetEnvironmentVariable, OperatingSystem.IsWindows());

    /// <summary>
    /// The config folder: <c>%APPDATA%\Pagewing</c> on Windows, otherwise <c>$XDG_CONFIG_HOME/pagewing</c> with the
    /// fallback <c>$HOME/.config/pagewing</c>. An empty variable counts as not set. Without any usable variable the
    /// folder lies in the temp folder, so callers always get a path.
    /// </summary>
    public static string ConfigDirectory(Func<string, string?> getEnvironmentVariable, bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        if (isWindows)
        {
            var appData = Read(getEnvironmentVariable, "APPDATA");
            if (appData is not null)
            {
                return Path.Combine(appData, "Pagewing");
            }

            var profile = Read(getEnvironmentVariable, "USERPROFILE");
            return profile is not null
                ? Path.Combine(profile, "AppData", "Roaming", "Pagewing")
                : Path.Combine(Path.GetTempPath(), "Pagewing");
        }

        var xdg = Read(getEnvironmentVariable, "XDG_CONFIG_HOME");
        if (xdg is not null)
        {
            return Path.Combine(xdg, "pagewing");
        }

        var home = Read(getEnvironmentVariable, "HOME");
        return home is not null
            ? Path.Combine(home, ".config", "pagewing")
            : Path.Combine(Path.GetTempPath(), "pagewing");
    }

    private static string? Read(Func<string, string?> getEnvironmentVariable, string name)
    {
        var value = getEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
