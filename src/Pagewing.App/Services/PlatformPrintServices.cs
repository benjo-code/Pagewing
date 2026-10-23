using Pagewing.App.Platform.Linux;
using Pagewing.App.Platform.Windows;
using Pagewing.Core.Printing;

namespace Pagewing.App.Services;

/// <summary>Chooses the print service of the operating system; made on the first print, never at startup.</summary>
internal static class PlatformPrintServices
{
    /// <summary>Creates the service of this system, or <see langword="null"/> where Pagewing cannot print.</summary>
    public static IPrintService? Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPrintService();
        }

        if (OperatingSystem.IsLinux())
        {
            return new CupsPrintService();
        }

        return null;
    }
}
