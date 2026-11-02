using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pagewing.Ocr;

/// <summary>
/// Raw bindings of the C API of Tesseract (<c>include/tesseract/capi.h</c>, tag 5.5.4). Only the functions the engine needs are bound.
/// <c>BOOL</c> is <c>int</c>. Strings are UTF-8 <c>char*</c>. There is no callback into managed code: the time limit is a deadline of
/// the monitor (<see cref="TessMonitorSetDeadlineMSecs"/>), not a cancel function. The library is resolved by <see cref="TesseractLibrary"/>
/// (a full path in one folder), never by the loader's search order.
/// </summary>
internal static unsafe partial class TesseractNative
{
    /// <summary><c>PSM_AUTO</c> of <c>TessPageSegMode</c>: fully automatic page segmentation, no orientation detection.</summary>
    public const int PageSegModeAuto = 3;

    /// <summary><c>RIL_WORD</c> of <c>TessPageIteratorLevel</c>.</summary>
    public const int LevelWord = 3;

    /// <summary><c>OEM_LSTM_ONLY</c> of <c>TessOcrEngineMode</c>: the neural network only (what <c>tessdata_fast</c> holds).</summary>
    public const int EngineModeLstmOnly = 1;

    private const string Library = "tesseract";

    static TesseractNative() =>
        NativeLibrary.SetDllImportResolver(typeof(TesseractNative).Assembly, static (name, _, _) => name == Library ? TesseractLibrary.Default.Load() : 0);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint TessBaseAPICreate();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessBaseAPIDelete(nint api);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessBaseAPIEnd(nint api);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessBaseAPIClear(nint api);

    /// <summary><c>TessBaseAPIInit4</c>: 0 on success, -1 on failure. <c>vars_vec_size</c> is <c>size_t</c>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int TessBaseAPIInit4(
        nint api,
        byte* datapath,
        byte* language,
        int mode,
        byte** configs,
        int configsSize,
        byte** varsVec,
        byte** varsValues,
        nuint varsVecSize,
        int setOnlyNonDebugParams);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessBaseAPISetPageSegMode(nint api, int mode);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessBaseAPISetImage(nint api, byte* imageData, int width, int height, int bytesPerPixel, int bytesPerLine);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessBaseAPISetSourceResolution(nint api, int ppi);

    /// <summary><c>TessBaseAPIRecognize</c>: 0 on success. <paramref name="monitor"/> may be 0.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int TessBaseAPIRecognize(nint api, nint monitor);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint TessBaseAPIGetIterator(nint api);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessResultIteratorDelete(nint iterator);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int TessResultIteratorNext(nint iterator, int level);

    /// <summary>The text of the element at the level as UTF-8; freed with <see cref="TessDeleteText"/>.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint TessResultIteratorGetUTF8Text(nint iterator, int level);

    /// <summary>The confidence of the element in percent (0 to 100).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial float TessResultIteratorConfidence(nint iterator, int level);

    /// <summary>The page iterator inside a result iterator; owned by it, not freed.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint TessResultIteratorGetPageIterator(nint iterator);

    /// <summary>The bounding box of the element at the level in pixels from the top left of the image; 0 when there is none.</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial int TessPageIteratorBoundingBox(nint pageIterator, int level, int* left, int* top, int* right, int* bottom);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessDeleteText(nint text);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial nint TessMonitorCreate();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessMonitorDelete(nint monitor);

    /// <summary>Sets the end time to this many milliseconds from now (recognition stops early at it).</summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    public static partial void TessMonitorSetDeadlineMSecs(nint monitor, int deadline);
}
