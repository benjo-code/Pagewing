namespace Pagewing.Pdfium;

/// <summary>PDFium was accessed from a thread other than the <see cref="PdfiumThread"/>.</summary>
internal sealed class PdfiumThreadViolationException : InvalidOperationException
{
    public PdfiumThreadViolationException()
        : base("PDFium must only be called on the dedicated PDFium thread.")
    {
    }

    public PdfiumThreadViolationException(string message)
        : base(message)
    {
    }

    public PdfiumThreadViolationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
