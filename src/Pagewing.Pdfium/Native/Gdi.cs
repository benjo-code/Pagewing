using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pagewing.Pdfium.Native;

/// <summary>
/// The few GDI functions the printing of form pages needs (<c>wingdi.h</c>): where the device context draws, and copying a
/// strip of pixels onto it. Windows only; called on the PDFium thread, on the device context PDFium itself draws on there.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Gdi
{
    /// <summary><c>ERROR</c>: <see cref="GetClipBox"/> failed.</summary>
    public const int RegionError = 0;

    /// <summary><c>NULLREGION</c>: nothing of the device context can be drawn.</summary>
    public const int NullRegion = 1;

    /// <summary><c>DIB_RGB_COLORS</c>.</summary>
    public const uint DibRgbColors = 0;

    /// <summary><c>SRCCOPY</c>.</summary>
    public const uint SourceCopy = 0x00CC0020;

    /// <summary><c>BI_RGB</c>: uncompressed; with 32 bits per pixel the fourth byte is not used.</summary>
    public const int BiRgb = 0;

    private const string Library = "gdi32.dll";

    /// <summary><c>GetClipBox</c>: the smallest rectangle around what can be drawn, in logical units.</summary>
    [LibraryImport(Library)]
    public static partial int GetClipBox(nint deviceContext, GdiRect* rect);

    /// <summary><c>StretchDIBits</c>: copies device-independent pixels onto the device context.</summary>
    /// <returns>The number of scan lines copied; 0 or <c>GDI_ERROR</c> (-1) on failure.</returns>
    [LibraryImport(Library)]
    public static partial int StretchDIBits(
        nint deviceContext,
        int destinationX,
        int destinationY,
        int destinationWidth,
        int destinationHeight,
        int sourceX,
        int sourceY,
        int sourceWidth,
        int sourceHeight,
        void* bits,
        BitmapInfoHeader* info,
        uint usage,
        uint rasterOperation);
}

/// <summary><c>RECT</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GdiRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

/// <summary><c>BITMAPINFOHEADER</c>; a 32-bit <c>BI_RGB</c> bitmap has no colour table, so it stands for <c>BITMAPINFO</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public int Size;
    public int Width;
    public int Height;
    public short Planes;
    public short BitCount;
    public int Compression;
    public int SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public int ClrUsed;
    public int ClrImportant;
}
