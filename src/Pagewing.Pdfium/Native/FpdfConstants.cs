namespace Pagewing.Pdfium.Native;

/// <summary>
/// PDFium constants used by Pagewing. Values are checked against the headers of the shipped package by
/// <c>NativeBindingsTests.ConstantsMatchThePackageHeaders</c>, because they change between PDFium versions. The
/// font descriptor flags have no header define; that test checks them against the PDF specification instead.
/// </summary>
internal static class FpdfConstants
{
    /// <summary><c>FPDF_ANNOT</c>: render annotations (except widgets and popups).</summary>
    public const int RenderAnnot = 0x01;

    /// <summary>
    /// <c>FPDF_CONVERT_FILL_TO_STROKE</c>: also stroke filled paths; only used together with a <c>FPDF_COLORSCHEME</c>.
    /// </summary>
    public const int RenderConvertFillToStroke = 0x20;

    /// <summary><c>FPDF_PRINTING</c>: render for a printer (<c>FPDF_RenderPage</c> on a printer device context).</summary>
    public const int RenderPrinting = 0x800;

    /// <summary><c>FPDF_NO_INCREMENTAL</c>: write the whole file (<c>FPDF_SaveAsCopy</c>).</summary>
    public const int SaveNoIncremental = 1 << 1;

    /// <summary><c>FPDF_REMOVE_SECURITY</c>: write the file without encryption (<c>FPDF_SaveAsCopy</c>).</summary>
    public const int SaveRemoveSecurity = 1 << 2;

    /// <summary>
    /// Permission bit 3 of the permission flags (<c>FPDF_GetDocPermissions</c>): printing is allowed (PDF specification,
    /// ISO 32000-1, table 22; bit n has the value 1 &lt;&lt; (n - 1)).
    /// </summary>
    public const int PermissionPrint = 1 << 2;

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

    /// <summary><c>PDFACTION_GOTO</c> (<c>fpdf_doc.h</c>): go to a destination in the same document.</summary>
    public const int ActionGoTo = 1;

    /// <summary><c>PDFACTION_URI</c> (<c>fpdf_doc.h</c>): open a URI.</summary>
    public const int ActionUri = 3;

    /// <summary>
    /// Font descriptor flag <c>ForceBold</c> (bit 19), as returned by <c>FPDFText_GetFontInfo</c>. The PDFium headers
    /// define no name for it; the value comes from the PDF specification (ISO 32000-1, table 123), which
    /// <c>fpdf_text.h</c> refers to.
    /// </summary>
    public const int FontFlagForceBold = 0x40000;

    /// <summary>
    /// Font descriptor flag <c>Italic</c> (bit 7), as returned by <c>FPDFText_GetFontInfo</c>. Like
    /// <see cref="FontFlagForceBold"/> it comes from the PDF specification, not from a header define.
    /// </summary>
    public const int FontFlagItalic = 0x40;
}
