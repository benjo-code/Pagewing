namespace Pagewing.Core.Pdf;

/// <summary>Why a PDF could not be opened.</summary>
public enum PdfOpenError
{
    /// <summary>The file or its directory does not exist.</summary>
    FileNotFound,

    /// <summary>The file exists but cannot be read (no access, a directory, locked, too large).</summary>
    CannotRead,

    /// <summary>The file is not a PDF or is damaged.</summary>
    NotAPdf,

    /// <summary>The file is encrypted and no password was given.</summary>
    PasswordRequired,

    /// <summary>The given password is wrong.</summary>
    PasswordIncorrect,

    /// <summary>The file uses an encryption method that is not supported.</summary>
    UnsupportedEncryption,

    /// <summary>Any other failure.</summary>
    Unknown,
}

/// <summary>A PDF could not be opened. <see cref="Exception.Message"/> is a short English explanation.</summary>
public sealed class PdfOpenException : Exception
{
    public PdfOpenException()
        : this(PdfOpenError.Unknown)
    {
    }

    public PdfOpenException(string message)
        : base(message)
    {
        Error = PdfOpenError.Unknown;
    }

    public PdfOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = PdfOpenError.Unknown;
    }

    public PdfOpenException(PdfOpenError error, Exception? innerException = null)
        : base(DescribeError(error), innerException)
    {
        Error = error;
    }

    /// <summary>Gets the reason, for example to show a localized message.</summary>
    public PdfOpenError Error { get; }

    private static string DescribeError(PdfOpenError error) => error switch
    {
        PdfOpenError.FileNotFound => "The file does not exist.",
        PdfOpenError.CannotRead => "The file cannot be read.",
        PdfOpenError.NotAPdf => "The file is not a PDF or is damaged.",
        PdfOpenError.PasswordRequired => "The file is encrypted and needs a password.",
        PdfOpenError.PasswordIncorrect => "The password is incorrect.",
        PdfOpenError.UnsupportedEncryption => "The file uses an unsupported encryption method.",
        _ => "The file could not be opened.",
    };
}

/// <summary>
/// A page could not be rendered: the bitmap would be larger than 268 435 456 pixels (256 Mpx), it could not
/// be created, or PDFium failed. <see cref="Exception.Message"/> is a short English explanation.
/// </summary>
public sealed class PdfRenderException : Exception
{
    public PdfRenderException()
        : base("The page could not be rendered.")
    {
    }

    public PdfRenderException(string message)
        : base(message)
    {
    }

    public PdfRenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An annotation could not be found again: no annotation of the page has the name of the locator, or the page has no
/// annotation at its index. <see cref="Exception.Message"/> is a short English explanation.
/// </summary>
public sealed class PdfAnnotationNotFoundException : Exception
{
    public PdfAnnotationNotFoundException()
        : base("The annotation was not found.")
    {
    }

    public PdfAnnotationNotFoundException(string message)
        : base(message)
    {
    }

    public PdfAnnotationNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An annotation was not created because the document already lists the most annotations Pagewing reads
/// (<see cref="PdfDocument.MaxAnnotations"/>): a new one would not be listed. <see cref="Exception.Message"/> is a short
/// English explanation.
/// </summary>
public sealed class PdfAnnotationLimitException : Exception
{
    public PdfAnnotationLimitException()
        : base("The document already has as many annotations as Pagewing lists.")
    {
    }

    public PdfAnnotationLimitException(string message)
        : base(message)
    {
    }

    public PdfAnnotationLimitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// A form field could not be found: the document has no field of that name (or it has no widget on the page asked for).
/// <see cref="Exception.Message"/> is a short English explanation that names the field.
/// </summary>
public sealed class FormFieldNotFoundException : Exception
{
    public FormFieldNotFoundException()
        : base("The form field was not found.")
    {
    }

    public FormFieldNotFoundException(string message)
        : base(message)
    {
    }

    public FormFieldNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
