using System.Runtime.Versioning;
using Microsoft.Win32;
using Pagewing.App.Platform.Windows;
using Pagewing.Core.Distribution;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>Works only below a fresh sandbox key of HKCU and a fake opener; skipped off Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDefaultAppServiceTests
{
    private const string Exe = @"C:\Apps\Pagewing\Pagewing.exe";

    [Fact]
    public void MakeDefaultOpensTheDefaultAppsPageOfPagewing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox(async root =>
        {
            var opened = new List<string>();
            var notified = 0;
            var service = new WindowsDefaultAppService(root, Exe, uri => { opened.Add(uri); return Task.CompletedTask; }, () => notified++);

            var result = await service.MakeDefaultAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DefaultAppResult.OpenedSystemSettings, result);
            Assert.Equal(["ms-settings:defaultapps?registeredAppUser=Pagewing"], opened);
            Assert.True(new WindowsFileTypeRegistry(root).IsRegistered);
            Assert.Equal(1, notified);

            await service.MakeDefaultAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, notified);
        });
    }

    [Fact]
    public void StatusReadsTheProgIdOfUserChoice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox(async root =>
        {
            var service = new WindowsDefaultAppService(root, Exe, _ => Task.CompletedTask, () => { });
            Assert.Equal(DefaultAppStatus.NotDefault, await service.GetStatusAsync(TestContext.Current.CancellationToken));

            using (var key = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"))
            {
                key.SetValue("ProgId", FileTypeRegistration.ProgId);
            }

            Assert.Equal(DefaultAppStatus.IsDefault, await service.GetStatusAsync(TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public void StatusPrefersUserChoiceLatestOverTheOlderUserChoice()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox(async root =>
        {
            var service = new WindowsDefaultAppService(root, Exe, _ => Task.CompletedTask, () => { });

            // Windows 11 builds that write UserChoiceLatest leave the old UserChoice on the previous app.
            SetProgId(root, "UserChoice", "MSEdgePDF");
            SetLatestProgId(root, FileTypeRegistration.ProgId);
            Assert.Equal(DefaultAppStatus.IsDefault, await service.GetStatusAsync(TestContext.Current.CancellationToken));

            // The newer key also wins when it names another app.
            SetProgId(root, "UserChoice", FileTypeRegistration.ProgId);
            SetLatestProgId(root, "MSEdgePDF");
            Assert.Equal(DefaultAppStatus.NotDefault, await service.GetStatusAsync(TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public void MakeDefaultRegistersAgainWhenTheProgramMoved()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox(async root =>
        {
            const string OldExe = @"C:\Apps\Pagewing-0.1.0\Pagewing.exe";
            new WindowsFileTypeRegistry(root).Register(OldExe);
            var notified = 0;
            var service = new WindowsDefaultAppService(root, Exe, _ => Task.CompletedTask, () => notified++);

            await service.MakeDefaultAsync(TestContext.Current.CancellationToken);

            Assert.True(new WindowsFileTypeRegistry(root).IsCurrentFor(Exe));
            Assert.Equal(1, notified);
        });
    }

    [Fact]
    public void StatusIsNotDefaultWhenTheRegisteredCommandPointsElsewhere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox(async root =>
        {
            new WindowsFileTypeRegistry(root).Register(@"C:\Apps\Pagewing-0.1.0\Pagewing.exe");
            using (var key = root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\UserChoice"))
            {
                key.SetValue("ProgId", FileTypeRegistration.ProgId);
            }

            var service = new WindowsDefaultAppService(root, Exe, _ => Task.CompletedTask, () => { });
            Assert.Equal(DefaultAppStatus.NotDefault, await service.GetStatusAsync(TestContext.Current.CancellationToken));

            new WindowsFileTypeRegistry(root).Register(Exe);
            Assert.Equal(DefaultAppStatus.IsDefault, await service.GetStatusAsync(TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public void MakeDefaultReturnsBeforeTheBlockingShellCallEnds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox(async root =>
        {
            using var gate = new ManualResetEventSlim();
            var service = new WindowsDefaultAppService(root, Exe, _ => Task.CompletedTask, () => gate.Wait(TimeSpan.FromSeconds(10)));

            var task = service.MakeDefaultAsync(TestContext.Current.CancellationToken);

            Assert.False(task.IsCompleted);
            gate.Set();
            Assert.Equal(DefaultAppResult.OpenedSystemSettings, await task);
        });
    }

    private const string FileExtsPdf = @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.pdf\";

    private static void SetProgId(RegistryKey root, string choiceKey, string progId)
    {
        using var key = root.CreateSubKey(FileExtsPdf + choiceKey);
        key.SetValue("ProgId", progId);
    }

    // UserChoiceLatest keeps the ProgId one level deeper: ...\UserChoiceLatest\ProgId, value "ProgId".
    private static void SetLatestProgId(RegistryKey root, string progId)
    {
        using var key = root.CreateSubKey(FileExtsPdf + @"UserChoiceLatest\ProgId");
        key.SetValue("ProgId", progId);
    }

    private static void WithSandbox(Func<RegistryKey, Task> body)
    {
        var name = @"Software\PagewingTests\" + Guid.NewGuid().ToString("N");
        using var sandbox = Registry.CurrentUser.CreateSubKey(name);
        try
        {
            body(sandbox).GetAwaiter().GetResult();
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
    }
}
