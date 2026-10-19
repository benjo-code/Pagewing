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
