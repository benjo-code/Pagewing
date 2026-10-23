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

    /// <summary>
    /// Gets what makes the service for the windows of a run: <see cref="Create"/>, or <see langword="null"/> in a
    /// measurement run, which neither asks the system nor shows the card.
    /// </summary>
    /// <param name="isMeasurement">Whether this is a measurement run.</param>
    public static Func<IDefaultAppService?>? FactoryFor(bool isMeasurement) => isMeasurement ? null : Create;
}
