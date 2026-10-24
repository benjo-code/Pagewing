using System.ComponentModel;
using System.Diagnostics;

namespace Pagewing.App.Services;

/// <summary>Starts programs for the updater; the tests replace it, so no test ever starts a real process.</summary>
internal interface IProcessLauncher
{
    /// <summary>Starts <paramref name="file"/> with <paramref name="arguments"/> (quoted like any .NET program expects them) and does not wait; <see langword="false"/> when it could not be started.</summary>
    bool Start(string file, IReadOnlyList<string> arguments);

    /// <summary>
    /// Runs <paramref name="file"/> with <paramref name="commandLine"/> exactly as given (a program built with Inno Setup
    /// splits its command line itself, <c>InnoCommandLine</c>) and returns its exit code once it ended. Throws when
    /// it could not be started.
    /// </summary>
    Task<int> RunAsync(string file, string commandLine);

    /// <summary>
    /// Waits until the process <paramref name="processId"/> has ended (at once when it does not run), at most
    /// <paramref name="timeout"/>; <see langword="false"/> when it still runs then. Throws only for cancellation.
    /// </summary>
    Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct);
}

/// <summary>The processes of the system.</summary>
internal sealed class SystemProcessLauncher : IProcessLauncher
{
    public bool Start(string file, IReadOnlyList<string> arguments)
    {
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty };
            foreach (var argument in arguments)
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public async Task<int> RunAsync(string file, string commandLine)
    {
        // No time limit: the setup program may wait for the answer to the UAC prompt, and stopping it halfway would leave
        // a half-replaced program behind.
        var info = new ProcessStartInfo(file, commandLine) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(file) ?? string.Empty };
        using var process = Process.Start(info) ?? throw new IOException("The setup program did not start: " + file);
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Not running (any more).
            return true;
        }

        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // The process cannot be waited for (no access); the setup program closes what holds its files.
                return false;
            }
        }
    }
}
