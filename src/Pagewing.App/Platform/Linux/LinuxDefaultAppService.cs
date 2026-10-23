using System.Diagnostics;
using System.Runtime.Versioning;
using Pagewing.Core.Distribution;

namespace Pagewing.App.Platform.Linux;

/// <summary>
/// Default app for PDF files on Linux through <c>xdg-mime</c>. From an AppImage it first installs the desktop file and
/// the icons into the user's data folder. Programs start without a shell and with a time limit.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxDefaultAppService : IDefaultAppService
{
    private const string DesktopFileName = "pagewing.desktop";
    private const string IconName = "pagewing";
    private const string MimeType = "application/pdf";

    private static readonly int[] IconSizes = [16, 22, 24, 32, 48, 64, 96, 128, 256, 512];

    private readonly Func<string, string?> _environment;
    private readonly TimeSpan _timeout;

    public LinuxDefaultAppService()
        : this(Environment.GetEnvironmentVariable, TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>Creates the service with its own view of the environment (PATH, HOME, XDG_DATA_HOME, APPIMAGE, APPDIR) and time limit.</summary>
    public LinuxDefaultAppService(Func<string, string?> environment, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _timeout = timeout;
    }

    public bool IsSupported => OperatingSystem.IsLinux();

    public Task<DefaultAppStatus> GetStatusAsync(CancellationToken cancellationToken) =>
        Task.Run(() => QueryStatusAsync(cancellationToken), CancellationToken.None);

    public Task<DefaultAppResult> MakeDefaultAsync(CancellationToken cancellationToken) =>
        Task.Run(() => SetDefaultAsync(cancellationToken), CancellationToken.None);

    // Both run on the thread pool: the file work and the PATH search must never block the caller (the UI thread).
    private async Task<DefaultAppStatus> QueryStatusAsync(CancellationToken cancellationToken)
    {
        var xdgMime = FindOnPath("xdg-mime");
        if (xdgMime is null)
        {
            return DefaultAppStatus.Unknown;
        }

        var run = await RunAsync(xdgMime, ["query", "default", MimeType], cancellationToken).ConfigureAwait(false);
        if (run is not { ExitCode: 0 })
        {
            return DefaultAppStatus.Unknown;
        }

        if (run.Output.Trim() != DesktopFileName)
        {
            return DefaultAppStatus.NotDefault;
        }

        // From an AppImage the choice is only good while the user's desktop file starts this very file: after the
        // AppImage was moved or renamed, the old Exec= points nowhere. NotDefault brings the card back; setting the
        // default again writes the file anew (TryInstallDesktopFile).
        var appImage = _environment("APPIMAGE");
        return string.IsNullOrEmpty(appImage) || DesktopFileStartsAppImage(appImage)
            ? DefaultAppStatus.IsDefault
            : DefaultAppStatus.NotDefault;
    }

    private bool DesktopFileStartsAppImage(string appImage)
    {
        var dataHome = DataHome();
        if (dataHome is null)
        {
            return true;
        }

        try
        {
            var file = Path.Combine(dataHome, "applications", DesktopFileName);
            return File.Exists(file) && ExecLine(File.ReadAllText(file)) == ExecLine(DesktopEntry.Render(appImage, IconName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // The Exec= line only: other lines may change with a newer Pagewing without making the choice stale.
    private static string? ExecLine(string desktopFile) =>
        desktopFile.Split('\n').FirstOrDefault(line => line.StartsWith("Exec=", StringComparison.Ordinal));

    private async Task<DefaultAppResult> SetDefaultAsync(CancellationToken cancellationToken)
    {
        var xdgMime = FindOnPath("xdg-mime");
        if (xdgMime is null)
        {
            return DefaultAppResult.NotAvailable;
        }

        var dataHome = DataHome();
        var applications = dataHome is null ? null : Path.Combine(dataHome, "applications");
        var appImage = _environment("APPIMAGE");
        if (!string.IsNullOrEmpty(appImage))
        {
            if (dataHome is null || applications is null || !TryInstallDesktopFile(appImage, dataHome, applications))
            {
                return DefaultAppResult.Failed;
            }
        }

        // xdg-mime default writes the choice into mimeapps.list without looking for the desktop file; without one
        // (a build run from a folder, a ZIP) the choice would point at nothing and the card would claim success.
        if (string.IsNullOrEmpty(appImage) && !DesktopFileExists(dataHome))
        {
            return DefaultAppResult.NotAvailable;
        }

        var run = await RunAsync(xdgMime, ["default", DesktopFileName, MimeType], cancellationToken).ConfigureAwait(false);
        if (run is not { ExitCode: 0 })
        {
            return DefaultAppResult.Failed;
        }

        // Optional: refreshes the cache of desktop files; a failure here does not undo the choice.
        var update = FindOnPath("update-desktop-database");
        if (update is not null && applications is not null && Directory.Exists(applications))
        {
            await RunAsync(update, [applications], cancellationToken).ConfigureAwait(false);
        }

        return DefaultAppResult.Done;
    }

    private string? DataHome()
    {
        var data = _environment("XDG_DATA_HOME");
        if (!string.IsNullOrEmpty(data) && Path.IsPathRooted(data))
        {
            return data;
        }

        var home = _environment("HOME");
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, ".local", "share");
    }

    // The user's data folder, then XDG_DATA_DIRS (default /usr/local/share:/usr/share, where the .deb installs).
    private bool DesktopFileExists(string? dataHome)
    {
        var folders = new List<string>();
        if (dataHome is not null)
        {
            folders.Add(dataHome);
        }

        var dirs = _environment("XDG_DATA_DIRS");
        folders.AddRange((string.IsNullOrEmpty(dirs) ? "/usr/local/share:/usr/share" : dirs)
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathRooted));

        return folders.Any(folder => File.Exists(Path.Combine(folder, "applications", DesktopFileName)));
    }

    private bool TryInstallDesktopFile(string appImage, string dataHome, string applications)
    {
        try
        {
            Directory.CreateDirectory(applications);
            File.WriteAllText(Path.Combine(applications, DesktopFileName), DesktopEntry.Render(appImage, IconName));

            var appDir = _environment("APPDIR");
            if (string.IsNullOrEmpty(appDir))
            {
                return true;
            }

            foreach (var size in IconSizes)
            {
                var relative = Path.Combine("icons", "hicolor", $"{size}x{size}", "apps", IconName + ".png");
                var source = Path.Combine(appDir, "usr", "share", relative);
                if (!File.Exists(source))
                {
                    continue;
                }

                var target = Path.Combine(dataHome, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? FindOnPath(string program)
    {
        var path = _environment("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var folder in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(folder, program);
            try
            {
                if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                {
                    return candidate;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable PATH entry is just skipped.
            }
        }

        return null;
    }

    private async Task<ProcessResult?> RunAsync(string fileName, string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        Process? process = null;
        try
        {
            process = Process.Start(info);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, text);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Kill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
