namespace Pagewing.Core.Saving;

/// <summary>Why <see cref="DocumentSaver"/> did not save. In every case the target file is left as it was.</summary>
public enum SaveFailureReason
{
    /// <summary>The temporary file could not be created in the target folder (no access, the folder is missing or read-only).</summary>
    NotWritable,

    /// <summary>The disk (or the user's quota) is full.</summary>
    DiskFull,

    /// <summary>Writing the temporary file or replacing the target failed for another reason.</summary>
    WriteFailed,

    /// <summary>
    /// The written file failed a check: it does not start with the bytes of the original, PDFium cannot open it, the
    /// number of pages or annotations differs from what was expected, or a changed page does not render.
    /// </summary>
    VerificationFailed,

    /// <summary>The original file was changed or replaced since it was opened, and overwriting it was not confirmed.</summary>
    OriginalChanged,

    /// <summary>PDFium's writer cannot save this document. Reserved; <see cref="DocumentSaver"/> does not report it today.</summary>
    WriterNotSupported,

    /// <summary>The document is encrypted and the written file would not keep the encryption.</summary>
    EncryptedNotSupported,

    /// <summary>
    /// Reserved. A save canceled by the caller throws <see cref="OperationCanceledException"/> instead, so the caller can
    /// tell its own cancellation from a failure.
    /// </summary>
    Canceled,
}

/// <summary>A save failed; <see cref="Reason"/> says why. <see cref="Exception.Message"/> is a short English explanation.</summary>
public sealed class SaveFailureException : Exception
{
    public SaveFailureException()
        : this(SaveFailureReason.WriteFailed, "The document could not be saved.")
    {
    }

    public SaveFailureException(string message)
        : this(SaveFailureReason.WriteFailed, message)
    {
    }

    public SaveFailureException(string message, Exception innerException)
        : this(SaveFailureReason.WriteFailed, message, innerException)
    {
    }

    public SaveFailureException(SaveFailureReason reason, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Gets the reason, for example to show a localized message.</summary>
    public SaveFailureReason Reason { get; }
}
