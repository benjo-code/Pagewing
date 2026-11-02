using Pagewing.Ocr;
using Xunit;

namespace Pagewing.Core.Tests.Ocr;

/// <summary>Guards the constants of the bindings against the header the library is built from.</summary>
public sealed class TesseractBindingsTests
{
    // The enumerators of include/tesseract/capi.h at tag 5.5.4 (commit 7e548e6eff172f38f97664afe7f7ce40db36c8d6), in the order of the header:
    // a C enum counts from 0. A new Tesseract version is a new look at the header.
    private static readonly string[] PageSegModes =
    [
        "PSM_OSD_ONLY", "PSM_AUTO_OSD", "PSM_AUTO_ONLY", "PSM_AUTO", "PSM_SINGLE_COLUMN", "PSM_SINGLE_BLOCK_VERT_TEXT", "PSM_SINGLE_BLOCK",
        "PSM_SINGLE_LINE", "PSM_SINGLE_WORD", "PSM_CIRCLE_WORD", "PSM_SINGLE_CHAR", "PSM_SPARSE_TEXT", "PSM_SPARSE_TEXT_OSD", "PSM_RAW_LINE", "PSM_COUNT",
    ];

    private static readonly string[] IteratorLevels = ["RIL_BLOCK", "RIL_PARA", "RIL_TEXTLINE", "RIL_WORD", "RIL_SYMBOL"];

    [Fact]
    public void NativeConstantsMatchTheHeaders()
    {
        Assert.Equal(TesseractNative.PageSegModeAuto, Array.IndexOf(PageSegModes, "PSM_AUTO"));
        Assert.Equal(TesseractNative.LevelWord, Array.IndexOf(IteratorLevels, "RIL_WORD"));
        Assert.Equal(3, TesseractNative.PageSegModeAuto);
        Assert.Equal(3, TesseractNative.LevelWord);
    }

    [Fact]
    public void TheLibraryNameIsPerSystem()
    {
        Assert.Equal(OperatingSystem.IsWindows() ? "tesseract.dll" : "libtesseract.so", TesseractLibrary.FileName);
    }
}
