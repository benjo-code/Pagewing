using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pagewing.Pdfium.Native;

/// <summary>
/// Raw PDFium bindings (package <c>bblanchon.PDFium.*</c> 157.0.8100, headers <c>fpdfview.h</c>,
/// <c>fpdf_progressive.h</c>, <c>fpdf_text.h</c>, <c>fpdf_doc.h</c>). Only functions Pagewing needs are bound.
/// Every call must run on the <see cref="PdfiumThread"/>. C <c>unsigned long</c> is bound as
/// <see cref="CULong"/> because it is 32-bit on Windows and 64-bit on Linux.
/// </summary>
internal static unsafe partial class Fpdf
{
    private const string Library = "pdfium";

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_InitLibrary();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_DestroyLibrary();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_LoadCustomDocument(FpdfFileAccess* fileAccess, byte* password);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_CloseDocument(nint document);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDF_GetLastError();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetFileVersion(nint document, int* fileVersion);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetPageCount(nint document);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetPageSizeByIndexF(nint document, int pageIndex, FsSizeF* size);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_LoadPage(nint document, int pageIndex);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_ClosePage(nint page);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial float FPDF_GetPageWidthF(nint page);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial float FPDF_GetPageHeightF(nint page);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBitmap_CreateEx(int width, int height, int format, void* firstScan, int stride);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_RenderPageBitmap_Start(
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags,
        IfsdkPause* pause);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_RenderPage_Continue(nint page, IfsdkPause* pause);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_RenderPage_Close(nint page);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFText_LoadPage(nint page);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFText_ClosePage(nint textPage);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_CountChars(nint textPage);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_GetText(nint textPage, int startIndex, int count, ushort* result);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDF_GetMetaText(nint document, byte* tag, void* buffer, CULong bufferLength);
}

/// <summary><c>FPDF_FILEACCESS</c> from <c>fpdfview.h</c>: random read access to a document.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfFileAccess
{
    /// <summary>File length in bytes (<c>m_FileLen</c>).</summary>
    public CULong FileLen;

    /// <summary>
    /// Reads <c>size</c> bytes at <c>position</c> into the buffer (<c>m_GetBlock</c>); returns non-zero on success.
    /// </summary>
    public delegate* unmanaged[Cdecl]<void*, CULong, byte*, CULong, int> GetBlock;

    /// <summary>Passed as the first argument of <see cref="GetBlock"/> (<c>m_Param</c>).</summary>
    public void* Param;
}

/// <summary><c>FS_SIZEF</c> from <c>fpdfview.h</c>: a size in points.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsSizeF
{
    /// <summary>Width in points (<c>width</c>).</summary>
    public float Width;

    /// <summary>Height in points (<c>height</c>).</summary>
    public float Height;
}

/// <summary><c>IFSDK_PAUSE</c> from <c>fpdf_progressive.h</c>: lets progressive rendering yield.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IfsdkPause
{
    /// <summary>Interface version; must be 1.</summary>
    public int Version;

    /// <summary>Returns non-zero to pause now, 0 to continue.</summary>
    public delegate* unmanaged[Cdecl]<IfsdkPause*, int> NeedToPauseNow;

    /// <summary>User data, may be null.</summary>
    public void* User;
}
