using Pagewing.Pdfium.Native;

namespace Pagewing.Pdfium;

/// <summary>Error codes of <c>FPDF_GetLastError</c> (<c>FPDF_ERR_*</c> in <c>fpdfview.h</c>).</summary>
internal enum PdfiumError
{
    /// <summary><c>FPDF_ERR_SUCCESS</c>.</summary>
    Success = FpdfConstants.ErrSuccess,

    /// <summary><c>FPDF_ERR_UNKNOWN</c>.</summary>
    Unknown = FpdfConstants.ErrUnknown,

    /// <summary><c>FPDF_ERR_FILE</c>: the file could not be found or read.</summary>
    File = FpdfConstants.ErrFile,

    /// <summary><c>FPDF_ERR_FORMAT</c>: not a PDF or damaged.</summary>
    Format = FpdfConstants.ErrFormat,

    /// <summary><c>FPDF_ERR_PASSWORD</c>: password required or incorrect.</summary>
    Password = FpdfConstants.ErrPassword,

    /// <summary><c>FPDF_ERR_SECURITY</c>: unsupported security handler.</summary>
    Security = FpdfConstants.ErrSecurity,

    /// <summary><c>FPDF_ERR_PAGE</c>: page not found or content error.</summary>
    Page = FpdfConstants.ErrPage,
}

/// <summary>PDFium could not open a document or page.</summary>
internal sealed class PdfiumOpenException : Exception
{
    public PdfiumOpenException()
        : this(PdfiumError.Unknown)
    {
    }

    public PdfiumOpenException(string message)
        : base(message)
    {
        Error = PdfiumError.Unknown;
    }

    public PdfiumOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = PdfiumError.Unknown;
    }

    public PdfiumOpenException(PdfiumError error)
        : this(error, $"PDFium could not open the document ({error}).")
    {
    }

    public PdfiumOpenException(PdfiumError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Gets the PDFium error code.</summary>
    public PdfiumError Error { get; }
}
