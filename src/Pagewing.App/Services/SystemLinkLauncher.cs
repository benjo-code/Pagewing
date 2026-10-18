using System.ComponentModel;
using System.Diagnostics;
using Pagewing.Core.Text;

namespace Pagewing.App.Services;

/// <summary>
/// Opens a confirmed address with the program the system has for it. Windows: the shell (ShellExecute with the
/// address as the file and no parameters); elsewhere <c>xdg-open</c> with the address as its only argument, started
/// without a shell from the absolute path found in a rooted <c>PATH</c> entry (<see cref="FindOnPath"/>), never from the
/// current or the app's directory. The string handed over is always <see cref="ExternalLink.Url"/> itself, never a form
/// parsed and written again (<see cref="Uri.ToString"/> would turn <c>%22</c> into a quote).
/// <see cref="ExternalLink.TryParse"/> lets no quote, space, backslash or control character through, so the address
/// cannot break out of either.
/// </summary>
/// <remarks>
/// Not Avalonia's <c>ILauncher.LaunchUriAsync</c>: it takes a <see cref="Uri"/> and hands on a string made from it, not
/// the one the user saw.
/// </remarks>
internal sealed class SystemLinkLauncher : ILinkLauncher
{
    private const string XdgOpen = "xdg-open";

    /// <summary>The separator of <c>PATH</c> outside Windows (the only place xdg-open is started).</summary>
    private const char UnixPathSeparator = ':';

    /// <summary>The <c>PATH</c> xdg-open gets when no rooted entry is left of the current one.</summary>
    internal const string FallbackPath = "/usr/local/bin:/usr/bin:/bin";

    /// <summary>The working directory of xdg-open: fixed, never the directory Pagewing was started in.</summary>
    internal const string SafeWorkingDirectory = "/";

    private readonly Func<ProcessStartInfo, Process?> _start;
    private readonly bool _windows;
    private readonly Func<string?> _path;
    private readonly Func<string, bool> _isExecutable;

    public SystemLinkLauncher()
        : this(Process.Start, OperatingSystem.IsWindows(), static () => Environment.GetEnvironmentVariable("PATH"), IsExecutableFile)
    {
    }

    /// <summary>
    /// Creates a launcher that starts processes with <paramref name="start"/>, as on Windows or not, with the given
    /// <c>PATH</c> and test for an executable file (for tests).
    /// </summary>
    internal SystemLinkLauncher(Func<ProcessStartInfo, Process?> start, bool windows, Func<string?> path, Func<string, bool> isExecutable)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(isExecutable);
        _start = start;
        _windows = windows;
        _path = path;
        _isExecutable = isExecutable;
    }

    /// <inheritdoc />
    public Task LaunchAsync(ExternalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var path = _windows ? null : _path();
        var xdgOpen = _windows ? null : FindOnPath(path, UnixPathSeparator, XdgOpen, _isExecutable);
        if (CreateStartInfo(link, _windows, xdgOpen, path) is not { } info)
        {
            // No xdg-open in a rooted PATH entry: nothing opens (fail closed).
            return Task.CompletedTask;
        }

        // The shell may take a moment (it can start the browser); never on the UI thread.
        return Task.Run(() => Start(info));
    }

    /// <summary>
    /// What is started for <paramref name="link"/> on Windows or on another system (for tests). Elsewhere it needs the
    /// absolute path of <c>xdg-open</c> and is <see langword="null"/> without one; xdg-open then runs in
    /// <see cref="SafeWorkingDirectory"/> with only the rooted entries of <paramref name="currentPath"/> as its
    /// <c>PATH</c> (or <see cref="FallbackPath"/>), because it calls its helpers (xdg-mime, gio, x-www-browser, ...) by
    /// name. Every other environment variable is inherited.
    /// </summary>
    internal static ProcessStartInfo? CreateStartInfo(ExternalLink link, bool windows, string? xdgOpenPath, string? currentPath)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (windows)
        {
            return new ProcessStartInfo { FileName = link.Url, UseShellExecute = true };
        }

        if (xdgOpenPath is null || !Path.IsPathRooted(xdgOpenPath))
        {
            return null;
        }

        var info = new ProcessStartInfo
        {
            FileName = xdgOpenPath,
            UseShellExecute = false,
            WorkingDirectory = SafeWorkingDirectory,
        };
        info.ArgumentList.Add(link.Url);
        var rooted = RootedEntries(currentPath, UnixPathSeparator);
        info.Environment["PATH"] = rooted.Count > 0 ? string.Join(UnixPathSeparator, rooted) : FallbackPath;
        return info;
    }

    /// <summary>The entries of a <c>PATH</c> value that are rooted, in their order (empty and relative ones mean the current directory).</summary>
    internal static List<string> RootedEntries(string? path, char separator)
    {
        var entries = new List<string>();
        if (string.IsNullOrEmpty(path))
        {
            return entries;
        }

        foreach (var entry in path.Split(separator))
        {
            if (entry.Length > 0 && Path.IsPathRooted(entry))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>
    /// Finds <paramref name="name"/> in the entries of a <c>PATH</c> value, in their order: only rooted entries count
    /// (an empty or relative entry would mean the current directory, where a downloaded archive may have put a program of
    /// that name). Returns the first absolute path for which <paramref name="isExecutable"/> holds, or <see langword="null"/>.
    /// </summary>
    internal static string? FindOnPath(string? path, char separator, string name, Func<string, bool> isExecutable)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(isExecutable);
        foreach (var entry in RootedEntries(path, separator))
        {
            var candidate = Path.Join(entry, name);
            if (isExecutable(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsExecutableFile(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        const UnixFileMode anyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExecute) != 0;
    }

    private void Start(ProcessStartInfo info)
    {
        try
        {
            // The shell may hand the address to a running program and start nothing (null).
            using var process = _start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            // No program for the address, or xdg-open failed to start: nothing opens, and the window must not go with it.
        }
    }
}
