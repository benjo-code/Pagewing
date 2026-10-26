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

    /// <summary><c>FPDF_INCREMENTAL</c>: append the changes to the original bytes (<c>FPDF_SaveAsCopy</c>).</summary>
    public const int SaveIncremental = 1 << 0;

    /// <summary><c>FPDF_NO_INCREMENTAL</c>: write the whole file (<c>FPDF_SaveAsCopy</c>).</summary>
    public const int SaveNoIncremental = 1 << 1;

    /// <summary><c>FPDF_REMOVE_SECURITY</c>: write the file without encryption (<c>FPDF_SaveAsCopy</c>).</summary>
    public const int SaveRemoveSecurity = 1 << 2;

    /// <summary>
    /// Permission bit 3 of the permission flags (<c>FPDF_GetDocPermissions</c>): printing is allowed (PDF specification,
    /// ISO 32000-1, table 22; bit n has the value 1 &lt;&lt; (n - 1)).
    /// </summary>
    public const int PermissionPrint = 1 << 2;

    /// <summary>
    /// Permission bit 6 of the permission flags (<c>FPDF_GetDocPermissions</c>): adding or modifying annotations is allowed
    /// (PDF specification, ISO 32000-1, table 22; bit n has the value 1 &lt;&lt; (n - 1)). The PDFium headers define no name for it.
    /// </summary>
    public const int PermissionAnnotate = 1 << 5;

    /// <summary><c>FPDF_ANNOT_TEXT</c> (<c>fpdf_annot.h</c>): a text (sticky note) annotation.</summary>
    public const int AnnotText = 1;

    /// <summary><c>FPDF_ANNOT_LINK</c>: a link annotation.</summary>
    public const int AnnotLink = 2;

    /// <summary><c>FPDF_ANNOT_FREETEXT</c>: a free text annotation.</summary>
    public const int AnnotFreeText = 3;

    /// <summary><c>FPDF_ANNOT_SQUARE</c>: a square (rectangle) annotation.</summary>
    public const int AnnotSquare = 5;

    /// <summary><c>FPDF_ANNOT_CIRCLE</c>: a circle (ellipse) annotation.</summary>
    public const int AnnotCircle = 6;

    /// <summary><c>FPDF_ANNOT_HIGHLIGHT</c>: a highlight annotation.</summary>
    public const int AnnotHighlight = 9;

    /// <summary><c>FPDF_ANNOT_UNDERLINE</c>: an underline annotation.</summary>
    public const int AnnotUnderline = 10;

    /// <summary><c>FPDF_ANNOT_SQUIGGLY</c>: a squiggly underline annotation.</summary>
    public const int AnnotSquiggly = 11;

    /// <summary><c>FPDF_ANNOT_STRIKEOUT</c>: a strikeout annotation.</summary>
    public const int AnnotStrikeout = 12;

    /// <summary><c>FPDF_ANNOT_INK</c>: an ink (freehand) annotation.</summary>
    public const int AnnotInk = 15;

    /// <summary><c>FPDF_ANNOT_POPUP</c>: a popup annotation.</summary>
    public const int AnnotPopup = 16;

    /// <summary><c>FPDF_ANNOT_WIDGET</c>: a form field widget.</summary>
    public const int AnnotWidget = 20;

    /// <summary><c>FPDF_ANNOT_FLAG_HIDDEN</c>: the annotation is neither shown nor printed (annotation flag <c>F</c>, bit 2).</summary>
    public const int AnnotFlagHidden = 1 << 1;

    /// <summary><c>FPDF_ANNOT_FLAG_PRINT</c>: the annotation is printed (annotation flag <c>F</c>, bit 3).</summary>
    public const int AnnotFlagPrint = 1 << 2;

    /// <summary><c>FPDF_ANNOT_FLAG_NOVIEW</c>: the annotation is not shown on screen (annotation flag <c>F</c>, bit 6).</summary>
    public const int AnnotFlagNoView = 1 << 5;

    /// <summary><c>FPDF_ANNOT_APPEARANCEMODE_NORMAL</c>: the normal appearance (<c>FPDFAnnot_SetAP</c>).</summary>
    public const int AnnotAppearanceNormal = 0;

    /// <summary><c>FPDF_SEGMENT_LINETO</c> (<c>fpdf_edit.h</c>): a line to the point.</summary>
    public const int SegmentLineTo = 0;

    /// <summary><c>FPDF_SEGMENT_BEZIERTO</c>: a point of a cubic Bézier curve (two control points, then the end).</summary>
    public const int SegmentBezierTo = 1;

    /// <summary><c>FPDF_SEGMENT_MOVETO</c>: the start of a subpath.</summary>
    public const int SegmentMoveTo = 2;

    /// <summary><c>FPDF_FONT_TRUETYPE</c>: a TrueType font for <c>FPDFText_LoadFont</c>.</summary>
    public const int FontTrueType = 2;

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

    /// <summary><c>FORMTYPE_NONE</c> (<c>fpdf_formfill.h</c>, <c>FPDF_GetFormType</c>): the document has no form.</summary>
    public const int FormTypeNone = 0;

    /// <summary><c>FORMTYPE_ACRO_FORM</c>: the form is an AcroForm.</summary>
    public const int FormTypeAcroForm = 1;

    /// <summary><c>FORMTYPE_XFA_FULL</c>: the form uses the entire XFA specification (the AcroForm fields stay fillable).</summary>
    public const int FormTypeXfaFull = 2;

    /// <summary><c>FORMTYPE_XFA_FOREGROUND</c>: the form uses the XFAF subset (the AcroForm fields stay fillable).</summary>
    public const int FormTypeXfaForeground = 3;

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
