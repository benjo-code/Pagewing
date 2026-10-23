using System.Reflection;

namespace Pagewing.Core;

/// <summary>What the program knows about itself.</summary>
public static class AppInfo
{
    /// <summary>
    /// Gets the version of the program: the informational version of the assembly without the build metadata after the
    /// plus sign, or "0.0.0" when none is set.
    /// </summary>
    public static string Version { get; } =
        WithoutBuildMetadata(typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Returns <paramref name="informationalVersion"/> up to the plus sign, or "0.0.0" when it is empty.</summary>
    /// <param name="informationalVersion">An informational version such as "1.2.3+abc", or <see langword="null"/>.</param>
    public static string WithoutBuildMetadata(string? informationalVersion)
    {
        var version = informationalVersion?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }
}
