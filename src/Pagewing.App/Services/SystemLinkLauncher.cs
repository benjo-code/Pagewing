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

    private readonly Func<ProcessStartInfo, Process?> _start;

    public SystemLinkLauncher()
        : this(Process.Start)
    {
    }

    /// <summary>Creates a launcher that starts processes with <paramref name="start"/> (for tests).</summary>
    internal SystemLinkLauncher(Func<ProcessStartInfo, Process?> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        _start = start;
    }

    /// <inheritdoc />
    public Task LaunchAsync(ExternalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var windows = OperatingSystem.IsWindows();
        var xdgOpen = windows ? null : FindOnPath(Environment.GetEnvironmentVariable("PATH"), Path.PathSeparator, XdgOpen, IsExecutableFile);
        if (CreateStartInfo(link, windows, xdgOpen) is not { } info)
        {
            // No xdg-open in a rooted PATH entry: nothing opens (fail closed).
            return Task.CompletedTask;
        }

        // The shell may take a moment (it can start the browser); never on the UI thread.
        return Task.Run(() => Start(info));
    }

    /// <summary>
    /// What is started for <paramref name="link"/> on Windows or on another system (for tests); elsewhere it needs the
    /// absolute path of <c>xdg-open</c> and is <see langword="null"/> without one.
    /// </summary>
    internal static ProcessStartInfo? CreateStartInfo(ExternalLink link, bool windows, string? xdgOpenPath)
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

        var info = new ProcessStartInfo { FileName = xdgOpenPath, UseShellExecute = false };
        info.ArgumentList.Add(link.Url);
        return info;
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
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var entry in path.Split(separator))
        {
            if (entry.Length == 0 || !Path.IsPathRooted(entry))
            {
                continue;
            }

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
