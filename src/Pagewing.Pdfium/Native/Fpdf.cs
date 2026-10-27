using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pagewing.Pdfium.Native;

/// <summary>
/// Raw PDFium bindings (package <c>bblanchon.PDFium.*</c> 157.0.8100, headers <c>fpdfview.h</c>,
/// <c>fpdf_progressive.h</c>, <c>fpdf_text.h</c>, <c>fpdf_doc.h</c>, <c>fpdf_annot.h</c>, <c>fpdf_signature.h</c>, <c>fpdf_formfill.h</c>). <c>FPDF_BOOL</c> is <c>int</c>,
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
    public static partial int FPDF_RenderPageBitmapWithColorScheme_Start(
        nint bitmap,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags,
        FpdfColorScheme* colorScheme,
        IfsdkPause* pause);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_RenderPage_Continue(nint page, IfsdkPause* pause);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_RenderPage_Close(nint page);

    /// <summary>
    /// <c>FPDF_RenderPage</c> (<c>fpdfview.h</c>, Windows only: the export does not exist in the Linux library): draws the
    /// page on a device context (<c>HDC</c>), as vector output when the driver allows it.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_RenderPage(
        nint deviceContext,
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int flags);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDF_GetDocPermissions(nint document);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetSecurityHandlerRevision(nint document);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_SaveAsCopy(nint document, FpdfFileWrite* fileWrite, CULong flags);

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

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_DeviceToPage(
        nint page,
        int startX,
        int startY,
        int sizeX,
        int sizeY,
        int rotate,
        int deviceX,
        int deviceY,
        double* pageX,
        double* pageY);

    // Annotations (fpdf_annot.h; experimental API). FPDF_ANNOTATION_SUBTYPE and FPDF_ANNOT_APPEARANCEMODE are int,
    // FPDF_BYTESTRING is a NUL-terminated byte string (UTF-8 keys), FPDF_WIDESTRING and FPDF_WCHAR* are UTF-16LE.

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_GetAnnotCount(nint page);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPage_GetAnnot(nint page, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFPage_CloseAnnot(nint annotation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFPage_CreateAnnot(nint page, int subtype);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_RemoveAnnot(nint page, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_IsSupportedSubtype(int subtype);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetSubtype(nint annotation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetRect(nint annotation, FsRectF* rect);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_SetRect(nint annotation, FsRectF* rect);

    /// <summary><c>size_t</c> in the header, so <see cref="nuint"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nuint FPDFAnnot_CountAttachmentPoints(nint annotation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetAttachmentPoints(nint annotation, nuint quadIndex, FsQuadPointsF* quadPoints);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_AppendAttachmentPoints(nint annotation, FsQuadPointsF* quadPoints);

    /// <summary><c>type</c> is <c>FPDFANNOT_COLORTYPE</c>: 0 for <c>Color</c>, 1 for <c>InteriorColor</c>. The channels are <c>unsigned int</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetColor(nint annotation, int type, uint* r, uint* g, uint* b, uint* a);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_SetColor(nint annotation, int type, uint r, uint g, uint b, uint a);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetStringValue(nint annotation, byte* key, ushort* buffer, CULong bufferLength);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_SetStringValue(nint annotation, byte* key, ushort* value);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_HasKey(nint annotation, byte* key);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_SetAP(nint annotation, int appearanceMode, ushort* value);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetFlags(nint annotation);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_SetFlags(nint annotation, int flags);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFAnnot_GetLinkedAnnot(nint annotation, byte* key);

    /// <summary>
    /// Appends a stroke to the <c>/InkList</c> of an ink annotation (created if missing); <c>point_count</c> is <c>size_t</c>.
    /// Returns the index of the new stroke, or -1.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_AddInkStroke(nint annotation, FsPointF* points, nuint pointCount);

    /// <summary>The number of strokes of the <c>/InkList</c> of an ink annotation, 0 for other subtypes (<c>unsigned long</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetInkListCount(nint annotation);

    /// <summary>
    /// Copies stroke <c>path_index</c> of the <c>/InkList</c> into <c>buffer</c> if <c>length</c> (in points) is large enough;
    /// returns the number of points of the stroke either way (0 for other subtypes). Both are <c>unsigned long</c>.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetInkListPath(nint annotation, CULong pathIndex, FsPointF* buffer, CULong length);

    /// <summary>Writes <c>/Border [h v w]</c> and removes the appearance (<c>/AP</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_SetBorder(nint annotation, float horizontalRadius, float verticalRadius, float borderWidth);

    /// <summary>Reads <c>/Border</c>; fails without the array or with fewer than three entries (<c>/BS</c> is not read).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetBorder(nint annotation, float* horizontalRadius, float* verticalRadius, float* borderWidth);

    /// <summary>Reads a number value of the annotation's dictionary; fails when the key is missing or no number.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetNumberValue(nint annotation, byte* key, float* value);

    /// <summary>The index of an annotation in the page's <c>/Annots</c> (found by its dictionary), or -1.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPage_GetAnnotIndex(nint page, nint annotation);

    /// <summary>
    /// The content of the appearance stream of a mode as UTF-16LE text; the length is in bytes and includes the 2-byte
    /// terminator. Read only: unlike <c>FPDFAnnot_GetObjectCount</c> it does not touch the stream dictionary.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetAP(nint annotation, int appearanceMode, ushort* buffer, CULong bufferLength);

    // Fonts and glyph outlines (fpdf_edit.h; partly experimental API). Used only to read the outlines of the text box font
    // (Geist Regular) in a document that is never saved; see PdfiumGlyphs. FPDF_FONT, FPDF_GLYPHPATH and FPDF_PATHSEGMENT
    // are opaque pointers; a glyph path and its segments belong to the font and need no closing.

    /// <summary><c>FPDF_CreateNewDocument</c>: a new, empty document; closed with <see cref="FPDF_CloseDocument"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_CreateNewDocument();

    /// <summary>
    /// <c>FPDFText_LoadFont</c>: loads a TrueType (<c>font_type</c> 2) or Type 1 font from bytes (copied) into the document;
    /// <c>size</c> is <c>uint32_t</c>, <c>cid</c> an <c>FPDF_BOOL</c>. Closed with <see cref="FPDFFont_Close"/>.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFText_LoadFont(nint document, byte* data, uint size, int fontType, int cid);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFFont_Close(nint font);

    /// <summary>Experimental API: the ascent of a font at a size, in points.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFFont_GetAscent(nint font, float fontSize, float* ascent);

    /// <summary>Experimental API: the descent of a font at a size, in points (negative below the baseline).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFFont_GetDescent(nint font, float fontSize, float* descent);

    /// <summary>
    /// Experimental API: the advance width of a glyph at a size, in points. <c>glyph</c> (<c>uint32_t</c>) is the Unicode
    /// value (measured, not stated by the header; <c>NativeBindingsTests.GlyphApiTakesUnicodeAndReturnsEmUnits</c>).
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFFont_GetGlyphWidth(nint font, uint glyph, float fontSize, float* width);

    /// <summary>
    /// Experimental API: the outline of a glyph, or 0. <c>glyph</c> is the Unicode value as for
    /// <see cref="FPDFFont_GetGlyphWidth"/>; the points come in em, whatever the size (measured).
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFFont_GetGlyphPath(nint font, uint glyph, float fontSize);

    /// <summary>Experimental API: the number of segments of a glyph path, or -1.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFGlyphPath_CountGlyphSegments(nint glyphPath);

    /// <summary>Experimental API: a segment of a glyph path, or 0.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFGlyphPath_GetGlyphPathSegment(nint glyphPath, int index);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPathSegment_GetPoint(nint segment, float* x, float* y);

    /// <summary>One of <c>FPDF_SEGMENT_*</c> (<see cref="FpdfConstants.SegmentLineTo"/> and the others), -1 on error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPathSegment_GetType(nint segment);

    /// <summary>Whether the segment closes its subpath.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFPathSegment_GetClose(nint segment);

    // Signatures (fpdf_signature.h).

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetSignatureCount(nint document);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDF_GetSignatureObject(nint document, int index);

    /// <summary>Returns the DocMDP permission (1 to 3) of a signature, or 0 on error (header: <c>unsigned int</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial uint FPDFSignatureObj_GetDocMDPPermission(nint signature);

    // Forms (fpdf_formfill.h). FPDF_FORMHANDLE is an opaque pointer; see PdfiumFormEnvironment for the lifetime rules.

    /// <summary>
    /// Creates the form-fill environment of a document, or returns 0 on failure. <paramref name="formInfo"/> must stay valid
    /// until <see cref="FPDFDOC_ExitFormFillEnvironment"/> has returned.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint FPDFDOC_InitFormFillEnvironment(nint document, FpdfFormFillInfo* formInfo);

    /// <summary>Exits the form-fill environment; before <see cref="FPDF_CloseDocument"/> and after the last page was announced closed.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDFDOC_ExitFormFillEnvironment(nint form);

    /// <summary>Announces a loaded page to the environment; the first call of a document loads all its fields.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FORM_OnAfterLoadPage(nint page, nint form);

    /// <summary>Announces that a page is about to close; before <see cref="FPDF_ClosePage"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FORM_OnBeforeClosePage(nint page, nint form);

    /// <summary>
    /// Draws the widgets of a page onto a bitmap the page was rendered on, with the same area, rotation and flags as the render
    /// (<c>FPDF_RenderPageBitmap*</c> draws no widget). Widgets without an appearance get one generated when their page loads.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_FFLDraw(nint form, nint bitmap, nint page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);

    /// <summary>
    /// Sets the highlight colour of one kind of field (<c>FPDF_FORMFIELD_UNKNOWN</c>: all) for <see cref="FPDF_FFLDraw"/>.
    /// Measured (spike): the colour is <c>0x00BBGGRR</c>, not <c>0xxxrrggbb</c> as the header says.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_SetFormFieldHighlightColor(nint form, int fieldType, CULong color);

    /// <summary>Sets the opacity of the highlight (0 to 255).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_SetFormFieldHighlightAlpha(nint form, byte alpha);

    /// <summary>Removes the highlight of every kind of field.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FPDF_RemoveFormFieldHighlight(nint form);

    // Writing a value (spike, Docs/DECISIONS.md, "Formulare: Formular-Engine …", point 1): focus the widget, select all, replace,
    // leave the focus; /V and /AP are written only when the focus leaves. Check boxes and radio buttons by a click in the middle
    // of the widget (page coordinates), combo and list boxes by FORM_SetIndexSelected on the focused widget.

    /// <summary>Focuses a widget (experimental API); the annotation must belong to a page announced to the environment.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FORM_SetFocusedAnnot(nint form, nint annotation);

    /// <summary>Selects all text of the focused text field or editable combo box (experimental API).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FORM_SelectAllText(nint form, nint page);

    /// <summary>Replaces the selected text of the focused field by <paramref name="text"/> (UTF-16LE with a terminator).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void FORM_ReplaceSelection(nint form, nint page, ushort* text);

    /// <summary>Leaves the focus; a changed value is written (<c>/V</c> and <c>/AP</c>) now.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FORM_ForceToKillFocus(nint form);

    /// <summary>The left mouse button goes down at a point of the page, in PDF user space (<c>modifier</c>: <c>FWL_EVENTFLAG_*</c>).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FORM_OnLButtonDown(nint form, nint page, int modifier, double pageX, double pageY);

    /// <summary>The left mouse button goes up at a point of the page, in PDF user space.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FORM_OnLButtonUp(nint form, nint page, int modifier, double pageX, double pageY);

    /// <summary>
    /// Selects or deselects an option of the focused combo box or list box (experimental API); deselecting in a combo box is a
    /// no-op that returns false.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FORM_SetIndexSelected(nint form, nint page, int index, int selected);

    /// <summary>One of <c>FORMTYPE_*</c> (<see cref="FpdfConstants.FormTypeNone"/> and the others). Experimental API.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDF_GetFormType(nint document);

    // Form fields of widget annotations (fpdf_annot.h; experimental API). Every function takes the FPDF_FORMHANDLE of the
    // document's environment. Strings are UTF-16LE (FPDF_WCHAR*) with a 2-byte terminator; lengths are in bytes and
    // unsigned long (CULong). Read only: nothing here focuses a field (focusing a combo with many options takes seconds).

    /// <summary>One of <c>FPDF_FORMFIELD_*</c> (<see cref="FpdfConstants.FormFieldPushButton"/> and the others), -1 on error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetFormFieldType(nint form, nint annotation);

    /// <summary>The field flags (<c>/Ff</c>, inherited), or 0 on error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetFormFieldFlags(nint form, nint annotation);

    /// <summary>The fully qualified field name; 0 on error, 2 for an empty name.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetFormFieldName(nint form, nint annotation, ushort* buffer, CULong bufferLength);

    /// <summary>The alternate field name (<c>/TU</c>); 0 on error, 2 for an empty name.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetFormFieldAlternateName(nint form, nint annotation, ushort* buffer, CULong bufferLength);

    /// <summary>
    /// The field value (<c>/V</c>): the export value of a combo box's option, the state name of a check box or radio group,
    /// only the first entry of a list box's array. 0 on error, 2 for an empty value.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetFormFieldValue(nint form, nint annotation, ushort* buffer, CULong bufferLength);

    /// <summary>The export value of a check box or radio button widget; 0 on error, 2 for an empty value.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetFormFieldExportValue(nint form, nint annotation, ushort* buffer, CULong bufferLength);

    /// <summary>The number of options (<c>/Opt</c>) of a combo box or list box, -1 without one or on error.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_GetOptionCount(nint form, nint annotation);

    /// <summary>The label of an option (the header gives no export value of options); 0 on error, 2 for an empty label.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetOptionLabel(nint form, nint annotation, int index, ushort* buffer, CULong bufferLength);

    /// <summary>Whether an option of a combo box or list box is chosen (the only way to read a list box's selection).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_IsOptionSelected(nint form, nint annotation, int index);

    /// <summary>Whether a check box or radio button widget is checked.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int FPDFAnnot_IsChecked(nint form, nint annotation);

    /// <summary>
    /// The JavaScript of an additional action of the field (<c>event</c> one of <c>FPDF_ANNOT_AACTION_*</c>); 2 without a script,
    /// 0 on error. Only its length is read: no script ever runs.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial CULong FPDFAnnot_GetFormAdditionalActionJavaScript(nint form, nint annotation, int formEvent, ushort* buffer, CULong bufferLength);
}

/// <summary>
/// <c>FPDF_FORMFILLINFO</c> from <c>fpdf_formfill.h</c> (280 bytes on x64). Pagewing sets only <see cref="Version"/> = 1 and
/// leaves every pointer null: no callback is needed (Docs/DECISIONS.md, "Formulare: Formular-Engine …", point 6), and null
/// action pointers mean PDFium never opens a URI, runs a named or go-to action, or submits a form on Pagewing's behalf. The
/// function pointers are therefore bound as <see cref="nint"/>; <c>NativeBindingsTests.FormFillInfoMatchesTheHeader</c>
/// checks names, order and widths against the header.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FpdfFormFillInfo
{
    /// <summary>Version of the interface (<c>version</c>); 1 = only the stable interfaces are called.</summary>
    public int Version;

    public nint Release;

    public nint FfiInvalidate;

    public nint FfiOutputSelectedRect;

    public nint FfiSetCursor;

    public nint FfiSetTimer;

    public nint FfiKillTimer;

    public nint FfiGetLocalTime;

    public nint FfiOnChange;

    public nint FfiGetPage;

    public nint FfiGetCurrentPage;

    public nint FfiGetRotation;

    public nint FfiExecuteNamedAction;

    public nint FfiSetTextFieldFocus;

    public nint FfiDoUriAction;

    public nint FfiDoGoToAction;

    /// <summary><c>m_pJsPlatform</c>: unused without V8.</summary>
    public nint JsPlatform;

    /// <summary><c>xfa_disabled</c> (<c>FPDF_BOOL</c>); version 2 only.</summary>
    public int XfaDisabled;

    public nint FfiDisplayCaret;

    public nint FfiGetCurrentPageIndex;

    public nint FfiSetCurrentPage;

    public nint FfiGotoUrl;

    public nint FfiGetPageViewRect;

    public nint FfiPageEvent;

    public nint FfiPopupMenu;

    public nint FfiOpenFile;

    public nint FfiEmailTo;

    public nint FfiUploadTo;

    public nint FfiGetPlatform;

    public nint FfiGetLanguage;

    public nint FfiDownloadFromUrl;

    public nint FfiPostRequestUrl;

    public nint FfiPutRequestUrl;

    public nint FfiOnFocusChange;

    public nint FfiDoUriActionWithKeyboardModifier;
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

/// <summary><c>FPDF_FILEWRITE</c> from <c>fpdf_save.h</c>: receives the blocks of a saved document.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct FpdfFileWrite
{
    /// <summary>Version of the interface (<c>version</c>); must be 1.</summary>
    public int Version;

    /// <summary>Receives a block (<c>WriteBlock</c>); returns non-zero on success. The first argument is the structure itself.</summary>
    public delegate* unmanaged[Cdecl]<FpdfFileWrite*, void*, CULong, int> WriteBlock;
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

/// <summary><c>FS_POINTF</c> from <c>fpdfview.h</c>: a point; for ink strokes in PDF user space (y up).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsPointF
{
    /// <summary>The x-coordinate (<c>x</c>).</summary>
    public float X;

    /// <summary>The y-coordinate (<c>y</c>).</summary>
    public float Y;
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

/// <summary>
/// <c>FS_QUADPOINTSF</c> from <c>fpdfview.h</c>: the four corners of a quadrilateral in PDF user space (y up), in the
/// order of the header (<c>x1 y1 x2 y2 x3 y3 x4 y4</c>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FsQuadPointsF
{
    /// <summary>The x-coordinate of the first corner (<c>x1</c>).</summary>
    public float X1;

    /// <summary>The y-coordinate of the first corner (<c>y1</c>).</summary>
    public float Y1;

    /// <summary>The x-coordinate of the second corner (<c>x2</c>).</summary>
    public float X2;

    /// <summary>The y-coordinate of the second corner (<c>y2</c>).</summary>
    public float Y2;

    /// <summary>The x-coordinate of the third corner (<c>x3</c>).</summary>
    public float X3;

    /// <summary>The y-coordinate of the third corner (<c>y3</c>).</summary>
    public float Y3;

    /// <summary>The x-coordinate of the fourth corner (<c>x4</c>).</summary>
    public float X4;

    /// <summary>The y-coordinate of the fourth corner (<c>y4</c>).</summary>
    public float Y4;
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

/// <summary>
/// <c>FPDF_COLORSCHEME</c> from <c>fpdfview.h</c>: the colours that replace those of paths and text, each in 8888 ARGB
/// format. The fields are <c>FPDF_DWORD</c>, which the header defines as <c>unsigned long</c>: 32-bit on Windows,
/// 64-bit on Linux, so they are <see cref="CULong"/> like every other <c>unsigned long</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FpdfColorScheme
{
    /// <summary>Fill colour of paths (<c>path_fill_color</c>).</summary>
    public CULong PathFillColor;

    /// <summary>Stroke colour of paths (<c>path_stroke_color</c>).</summary>
    public CULong PathStrokeColor;

    /// <summary>Fill colour of text (<c>text_fill_color</c>).</summary>
    public CULong TextFillColor;

    /// <summary>Stroke colour of text (<c>text_stroke_color</c>).</summary>
    public CULong TextStrokeColor;
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
