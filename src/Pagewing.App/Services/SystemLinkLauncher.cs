using System.ComponentModel;
using System.Diagnostics;
using Pagewing.Core.Text;

namespace Pagewing.App.Services;

/// <summary>
/// Opens a confirmed address with the program the system has for it. Windows: the shell (ShellExecute with the
/// address as the file and no parameters); elsewhere <c>xdg-open</c> with the address as its only argument, started
/// without a shell. The string handed over is always <see cref="ExternalLink.Url"/> itself, never a form parsed and
/// written again (<see cref="Uri.ToString"/> would turn <c>%22</c> into a quote). <see cref="ExternalLink.TryParse"/>
/// lets no quote, space, backslash or control character through, so the address cannot break out of either.
/// </summary>
/// <remarks>
/// Not Avalonia's <c>ILauncher.LaunchUriAsync</c>: it takes a <see cref="Uri"/> and hands on a string made from it, not
/// the one the user saw.
/// </remarks>
internal sealed class SystemLinkLauncher : ILinkLauncher
{
    /// <inheritdoc />
    public Task LaunchAsync(ExternalLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var info = CreateStartInfo(link, OperatingSystem.IsWindows());

        // The shell may take a moment (it can start the browser); never on the UI thread.
        return Task.Run(() => Start(info));
    }

    /// <summary>What is started for <paramref name="link"/> on Windows or on another system (for tests).</summary>
    internal static ProcessStartInfo CreateStartInfo(ExternalLink link, bool windows)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (windows)
        {
            return new ProcessStartInfo { FileName = link.Url, UseShellExecute = true };
        }

        var info = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
        info.ArgumentList.Add(link.Url);
        return info;
    }

    private static void Start(ProcessStartInfo info)
    {
        try
        {
            // The shell may hand the address to a running program and start nothing (null).
            using var process = Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException or IOException)
        {
            // No program for the address, or no xdg-open: nothing opens, and the window must not go with it.
        }
    }
}
