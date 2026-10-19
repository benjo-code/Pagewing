using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.App.Platform.Windows;

/// <summary><c>DOCINFOW</c> from <c>wingdi.h</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DocInfo
{
    /// <summary>Size of the structure (<c>cbSize</c>).</summary>
    public int Size;

    /// <summary>Name of the job (<c>lpszDocName</c>).</summary>
    public char* DocName;

    /// <summary>File to print to instead of the printer (<c>lpszOutput</c>), or null.</summary>
    public char* Output;

    /// <summary>Data type (<c>lpszDatatype</c>), or null.</summary>
    public char* DataType;

    /// <summary>Flags (<c>fwType</c>).</summary>
    public int Type;
}

/// <summary>The few GDI functions the printing needs (<c>wingdi.h</c>). Windows only.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Gdi
{
    /// <summary><c>HORZRES</c>: width of the printable area in pixels.</summary>
    public const int HorizontalResolution = 8;

    /// <summary><c>VERTRES</c>: height of the printable area in pixels.</summary>
    public const int VerticalResolution = 10;

    /// <summary><c>LOGPIXELSX</c>: horizontal dots per inch.</summary>
    public const int LogicalPixelsX = 88;

    /// <summary><c>LOGPIXELSY</c>: vertical dots per inch.</summary>
    public const int LogicalPixelsY = 90;

    [LibraryImport("gdi32.dll")]
    public static partial int GetDeviceCaps(nint dc, int index);

    [LibraryImport("gdi32.dll", EntryPoint = "StartDocW", SetLastError = true)]
    public static partial int StartDoc(nint dc, DocInfo* info);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int StartPage(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int EndPage(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int EndDoc(nint dc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    public static partial int AbortDoc(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateDCW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint CreateDC(string? driver, string? device, string? output, nint devMode);
}

/// <summary>Memory handles of the dialog (<c>kernel32.dll</c>). Windows only.</summary>
[SupportedOSPlatform("windows")]
internal static partial class GlobalMemory
{
    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll")]
    public static partial nint GlobalFree(nint memory);
}
