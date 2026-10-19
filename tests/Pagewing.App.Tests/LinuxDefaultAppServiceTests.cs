using System.Runtime.Versioning;
using Pagewing.App.Platform.Linux;
using Pagewing.Core.Distribution;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>Runs the service against stub scripts in a temp folder used as PATH; HOME and the data folder are temp too.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDefaultAppServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pwdefault-" + Guid.NewGuid().ToString("N"));
    private readonly string _bin;
    private readonly string _log;
    private readonly Dictionary<string, string?> _env = [];

    public LinuxDefaultAppServiceTests()
    {
        _bin = Path.Combine(_root, "bin");
        _log = Path.Combine(_root, "calls.txt");
        Directory.CreateDirectory(_bin);
        _env["PATH"] = _bin;
        _env["HOME"] = Path.Combine(_root, "home");
        _env["XDG_DATA_HOME"] = Path.Combine(_root, "data");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private LinuxDefaultAppService Service() => new(name => _env.GetValueOrDefault(name), TimeSpan.FromSeconds(5));

    private void Stub(string name, string body)
    {
        var path = Path.Combine(_bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private string LoggingStub(string name, string answer = "") =>
        $"for a in \"$@\"; do printf '%s|' \"$a\" >> '{_log}'; done; printf '\\n' >> '{_log}'; printf '%s\\n' '{answer}'";

    [Fact]
    public async Task StatusIsDefaultWhenXdgMimeAnswersPagewing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", LoggingStub("xdg-mime", "pagewing.desktop"));

        var status = await Service().GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppStatus.IsDefault, status);
        Assert.Equal("query|default|application/pdf|", File.ReadAllText(_log).Trim());
    }

    [Fact]
    public async Task StatusIsNotDefaultForAnotherViewer()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", LoggingStub("xdg-mime", "org.other.Viewer.desktop"));

        Assert.Equal(DefaultAppStatus.NotDefault, await Service().GetStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StatusIsUnknownWithoutXdgMime()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");

        Assert.Equal(DefaultAppStatus.Unknown, await Service().GetStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MakeDefaultIsNotAvailableWithoutXdgMime()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");

        Assert.Equal(DefaultAppResult.NotAvailable, await Service().MakeDefaultAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MakeDefaultCallsXdgMimeWithoutAShell()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", LoggingStub("xdg-mime"));
        Stub("update-desktop-database", LoggingStub("update-desktop-database"));
        Directory.CreateDirectory(Path.Combine(_root, "data", "applications"));

        var result = await Service().MakeDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppResult.Done, result);
        var calls = File.ReadAllLines(_log);
        Assert.Equal("default|pagewing.desktop|application/pdf|", calls[0]);
        Assert.Equal(2, calls.Length);
        Assert.EndsWith("applications|", calls[1], StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_root, "data", "applications", "pagewing.desktop")), "no AppImage, no desktop file");
    }

    [Fact]
    public async Task MakeDefaultFromAnAppImageWritesDesktopFileAndIcons()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", LoggingStub("xdg-mime"));
        var appDir = Path.Combine(_root, "appdir");
        var icon48 = Path.Combine(appDir, "usr", "share", "icons", "hicolor", "48x48", "apps");
        Directory.CreateDirectory(icon48);
        File.WriteAllBytes(Path.Combine(icon48, "pagewing.png"), [1, 2, 3]);
        Directory.CreateDirectory(Path.Combine(appDir, "usr", "share", "icons", "hicolor", "64x64", "apps"));
        _env["APPIMAGE"] = Path.Combine(_root, "My Apps", "Pagewing.AppImage");
        _env["APPDIR"] = appDir;

        var result = await Service().MakeDefaultAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DefaultAppResult.Done, result);
        var desktop = File.ReadAllText(Path.Combine(_root, "data", "applications", "pagewing.desktop"));
        Assert.Equal(DesktopEntry.Render(_env["APPIMAGE"]!, "pagewing"), desktop);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(_root, "data", "icons", "hicolor", "48x48", "apps", "pagewing.png")));
        Assert.False(Directory.Exists(Path.Combine(_root, "data", "icons", "hicolor", "64x64")), "missing sizes are skipped");
        Assert.False(Directory.Exists(Path.Combine(_root, "home")), "the real HOME is not used when XDG_DATA_HOME is set");
    }

    [Fact]
    public async Task DataFolderFallsBackToHomeLocalShare()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", LoggingStub("xdg-mime"));
        _env["XDG_DATA_HOME"] = null;
        _env["APPIMAGE"] = "/opt/Pagewing.AppImage";

        await Service().MakeDefaultAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, "home", ".local", "share", "applications", "pagewing.desktop")));
    }

    [Fact]
    public async Task ATimeoutEndsAsFailed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", "sleep 30");
        var service = new LinuxDefaultAppService(name => _env.GetValueOrDefault(name), TimeSpan.FromMilliseconds(300));

        Assert.Equal(DefaultAppResult.Failed, await service.MakeDefaultAsync(TestContext.Current.CancellationToken));
        Assert.Equal(DefaultAppStatus.Unknown, await service.GetStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AFailingXdgMimeEndsAsFailed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        Stub("xdg-mime", "exit 3");

        Assert.Equal(DefaultAppResult.Failed, await Service().MakeDefaultAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancellingTheCallerKillsTheChild()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        var pidFile = Path.Combine(_root, "pid.txt");
        Stub("xdg-mime", $"echo $$ > '{pidFile}'; exec sleep 30");
        using var cts = new CancellationTokenSource();
        var task = Service().MakeDefaultAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Directory.Exists($"/proc/{pid}"));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (Directory.Exists($"/proc/{pid}") && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.False(Directory.Exists($"/proc/{pid}"), "the child must be gone");
    }

    [Fact]
    public async Task BlockingWorkDoesNotRunOnTheCallersThread()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux only.");
        using var gate = new ManualResetEventSlim();
        var service = new LinuxDefaultAppService(
            name =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return _env.GetValueOrDefault(name);
            },
            TimeSpan.FromSeconds(5));

        var make = service.MakeDefaultAsync(TestContext.Current.CancellationToken);
        var status = service.GetStatusAsync(TestContext.Current.CancellationToken);

        Assert.False(make.IsCompleted);
        Assert.False(status.IsCompleted);
        gate.Set();
        Assert.Equal(DefaultAppResult.NotAvailable, await make);
        Assert.Equal(DefaultAppStatus.Unknown, await status);
    }
}
