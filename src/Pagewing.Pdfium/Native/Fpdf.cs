using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pagewing.Pdfium.Native;

/// <summary>
/// Raw PDFium bindings (package <c>bblanchon.PDFium.*</c> 157.0.8100, headers <c>fpdfview.h</c>,
/// <c>fpdf_progressive.h</c>, <c>fpdf_text.h</c>, <c>fpdf_doc.h</c>). <c>FPDF_BOOL</c> is <c>int</c>,
/// <c>FS_FLOAT</c> is <c>float</c>. Only functions Pagewing needs are bound.
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

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBookmark_GetFirstChild(nint document, nint bookmark);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBookmark_GetNextSibling(nint document, nint bookmark);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFBookmark_GetTitle(nint bookmark, void* buffer, CULong bufferLength);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFBookmark_GetCount(nint bookmark);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBookmark_GetDest(nint document, nint bookmark);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFBookmark_GetAction(nint bookmark);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAction_GetType(nint action);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFAction_GetDest(nint document, nint action);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFDest_GetDestPageIndex(nint document, nint dest);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_PageToDevice(
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        double pageX,
        double pageY,
        int* deviceX,
        int* deviceY);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint FPDFText_GetUnicode(nint textPage, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_IsGenerated(nint textPage, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_IsHyphen(nint textPage, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial double FPDFText_GetFontSize(nint textPage, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFText_GetFontInfo(nint textPage, int index, void* buffer, CULong bufferLength, int* flags);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_GetFontWeight(nint textPage, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_GetCharBox(nint textPage, int index, double* left, double* right, double* bottom, double* top);

    /// <summary>Experimental API: the effective transformation matrix of a character (text matrix and CTM, without the font size).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFText_GetMatrix(nint textPage, int index, FsMatrix* matrix);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFLink_LoadWebLinks(nint textPage);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFLink_CountWebLinks(nint linkPage);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFLink_GetURL(nint linkPage, int linkIndex, ushort* buffer, int bufferLength);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFLink_CountRects(nint linkPage, int linkIndex);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFLink_GetRect(nint linkPage, int linkIndex, int rectIndex, double* left, double* top, double* right, double* bottom);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFLink_CloseWebLinks(nint linkPage);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFLink_Enumerate(nint page, int* startPosition, nint* linkAnnotation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFLink_GetAnnotRect(nint linkAnnotation, FsRectF* rect);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFLink_GetAction(nint link);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFLink_GetDest(nint document, nint link);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAction_GetURIPath(nint document, nint action, void* buffer, CULong bufferLength);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFDest_GetLocationInPage(
        nint dest,
        int* hasX,
        int* hasY,
        int* hasZoom,
        float* x,
        float* y,
        float* zoom);
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

/// <summary><c>FS_RECTF</c> from <c>fpdfview.h</c>: a rectangle; for link annotations in PDF user space (y up).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsRectF
{
    /// <summary>The x-coordinate of the left edge (<c>left</c>).</summary>
    public float Left;

    /// <summary>The y-coordinate of the edge named <c>top</c>.</summary>
    public float Top;

    /// <summary>The x-coordinate of the right edge (<c>right</c>).</summary>
    public float Right;

    /// <summary>The y-coordinate of the edge named <c>bottom</c>.</summary>
    public float Bottom;
}

/// <summary><c>FS_MATRIX</c> from <c>fpdfview.h</c>: the affine matrix [a b c d e f].</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsMatrix
{
    /// <summary>The x scale (<c>a</c>).</summary>
    public float A;

    /// <summary>The y shear of the x axis (<c>b</c>).</summary>
    public float B;

    /// <summary>The x shear of the y axis (<c>c</c>).</summary>
    public float C;

    /// <summary>The y scale (<c>d</c>).</summary>
    public float D;

    /// <summary>The x translation (<c>e</c>).</summary>
    public float E;

    /// <summary>The y translation (<c>f</c>).</summary>
    public float F;
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
