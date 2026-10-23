using System.Runtime.Versioning;
using Microsoft.Win32;
using Pagewing.App.Platform.Windows;
using Pagewing.Core.Distribution;
using Xunit;

namespace Pagewing.App.Tests;

/// <summary>Writes only below a fresh sandbox key of HKCU, never to the real registration; skipped off Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileTypeRegistryTests
{
    private const string Exe = @"C:\Apps\Pagewing\Pagewing.exe";

    [Fact]
    public void RegisterWritesEveryEntryUnderTheTestRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox((root, registry) =>
        {
            Assert.False(registry.IsRegistered);

            registry.Register(Exe);

            Assert.True(registry.IsRegistered);
            foreach (var entry in FileTypeRegistration.Entries(Exe))
            {
                using var key = root.OpenSubKey(entry.KeyPath);
                Assert.NotNull(key);
                Assert.Equal(entry.Value, key.GetValue(entry.ValueName));
            }
        });
    }

    [Fact]
    public void UnregisterRemovesThemAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox((root, registry) =>
        {
            registry.Register(Exe);

            registry.Unregister();

            Assert.False(registry.IsRegistered);
            using var classes = root.OpenSubKey(@"Software\Classes\Pagewing.PDF");
            Assert.Null(classes);
            using var pagewing = root.OpenSubKey(@"Software\Pagewing");
            Assert.Null(pagewing);
            using var openWith = root.OpenSubKey(@"Software\Classes\.pdf\OpenWithProgids");
            Assert.Null(openWith?.GetValue(FileTypeRegistration.ProgId));
            using var registered = root.OpenSubKey(@"Software\RegisteredApplications");
            Assert.Null(registered?.GetValue("Pagewing"));

            registry.Unregister();
        });
    }

    [Fact]
    public void RegisterTwiceIsHarmless()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry needs Windows.");
        WithSandbox((root, registry) =>
        {
            registry.Register(Exe);
            registry.Register(Exe);

            Assert.True(registry.IsRegistered);
            using var key = root.OpenSubKey(@"Software\Classes\Pagewing.PDF\shell\open\command");
            Assert.Equal("\"C:\\Apps\\Pagewing\\Pagewing.exe\" \"%1\"", key?.GetValue(null));
        });
    }

    private static void WithSandbox(Action<RegistryKey, WindowsFileTypeRegistry> test)
    {
        var path = @"Software\PagewingTests\" + Guid.NewGuid().ToString("N");
        using var root = Registry.CurrentUser.CreateSubKey(path);
        try
        {
            test(root, new WindowsFileTypeRegistry(root));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
    }
}
