namespace Pagewing.Core.Updates;

/// <summary>How the running program was installed; it decides whether and how it can update itself.</summary>
public enum PackageForm
{
    /// <summary>Windows, installed by the setup program (an uninstaller sits next to the program).</summary>
    WindowsInstaller,

    /// <summary>Windows, unpacked from the portable archive.</summary>
    WindowsPortable,

    /// <summary>Linux, started from an AppImage.</summary>
    AppImage,

    /// <summary>Linux, installed by the Debian package.</summary>
    Deb,

    /// <summary>Anything else.</summary>
    Unknown,
}

/// <summary>Finds out the <see cref="PackageForm"/> of the running program from plain inputs (testable without a system).</summary>
public static class PackageFormDetector
{
    /// <summary>The name of the uninstaller the Windows setup program places next to the program.</summary>
    public const string UninstallerName = "unins000.exe";

    /// <summary>The folder the Debian package installs the program into.</summary>
    public const string DebDirectory = "/usr/lib/pagewing/";

    /// <summary>Detects the form.</summary>
    /// <param name="isWindows">Whether the system is Windows (otherwise Linux is assumed).</param>
    /// <param name="processPath">The path of the running executable, if known.</param>
    /// <param name="fileExists">Tells whether a file exists.</param>
    /// <param name="env">Reads an environment variable (<c>null</c> if unset).</param>
    public static PackageForm Detect(bool isWindows, string? processPath, Func<string, bool> fileExists, Func<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(env);

        if (isWindows)
        {
            var directory = string.IsNullOrEmpty(processPath) ? null : Path.GetDirectoryName(processPath);
            return directory is not null && fileExists(Path.Combine(directory, UninstallerName))
                ? PackageForm.WindowsInstaller
                : PackageForm.WindowsPortable;
        }

        if (!string.IsNullOrEmpty(env("APPIMAGE")))
        {
            return PackageForm.AppImage;
        }

        return processPath is not null && processPath.StartsWith(DebDirectory, StringComparison.Ordinal)
            ? PackageForm.Deb
            : PackageForm.Unknown;
    }
}
