using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.App.Platform.Windows;

/// <summary>The one shell function the file type registration needs (<c>shlobj_core.h</c>). Windows only, never loaded at startup.</summary>
[SupportedOSPlatform("windows")]
internal static partial class ShellNative
{
    private const int AssociationChanged = 0x08000000;
    private const uint IdList = 0;
    private const uint FlushNoWait = 0x3000;

    [LibraryImport("shell32.dll")]
    private static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>Tells the shell that file type associations changed so Explorer and the settings page re-read them.</summary>
    public static void NotifyAssociationsChanged() => SHChangeNotify(AssociationChanged, IdList | FlushNoWait, 0, 0);
}
