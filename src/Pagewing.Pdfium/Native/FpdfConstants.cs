namespace Pagewing.Pdfium.Native;

/// <summary>
/// PDFium constants used by Pagewing. Values are checked against the headers of the shipped package by
/// <c>NativeBindingsTests.ConstantsMatchThePackageHeaders</c>, because they change between PDFium versions.
/// </summary>
internal static class FpdfConstants
{
    /// <summary><c>FPDF_ANNOT</c>: render annotations (except widgets and popups).</summary>
    public const int RenderAnnot = 0x01;

    /// <summary><c>FPDF_ERR_SUCCESS</c>.</summary>
    public const int ErrSuccess = 0;

    /// <summary><c>FPDF_ERR_UNKNOWN</c>.</summary>
    public const int ErrUnknown = 1;

    /// <summary><c>FPDF_ERR_FILE</c>: file not found or could not be opened.</summary>
    public const int ErrFile = 2;

    /// <summary><c>FPDF_ERR_FORMAT</c>: not a PDF or corrupted.</summary>
    public const int ErrFormat = 3;

    /// <summary><c>FPDF_ERR_PASSWORD</c>: password required or incorrect.</summary>
    public const int ErrPassword = 4;

    /// <summary><c>FPDF_ERR_SECURITY</c>: unsupported security scheme.</summary>
    public const int ErrSecurity = 5;

    /// <summary><c>FPDF_ERR_PAGE</c>: page not found or content error.</summary>
    public const int ErrPage = 6;

    /// <summary><c>FPDFBitmap_BGRx</c>: 4 bytes per pixel, blue, green, red, unused.</summary>
    public const int BitmapBgrx = 3;

    /// <summary><c>FPDF_RENDER_READY</c>.</summary>
    public const int RenderReady = 0;

    /// <summary><c>FPDF_RENDER_TOBECONTINUED</c>.</summary>
    public const int RenderToBeContinued = 1;

    /// <summary><c>FPDF_RENDER_DONE</c>.</summary>
    public const int RenderDone = 2;

    /// <summary><c>FPDF_RENDER_FAILED</c>.</summary>
    public const int RenderFailed = 3;
}
