using Pagewing.App.Platform.Linux;
using Pagewing.App.Platform.Windows;
using Pagewing.Core.Distribution;

namespace Pagewing.App.Services;

/// <summary>Chooses the default app service of the operating system; made on first use, never at startup.</summary>
internal static class PlatformDefaultAppServices
{
    /// <summary>Creates the service of this system, or <see langword="null"/> where Pagewing cannot set the default app.</summary>
    public static IDefaultAppService? Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDefaultAppService();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxDefaultAppService();
        }

        return null;
    }
}
