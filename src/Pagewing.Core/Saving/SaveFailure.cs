namespace Pagewing.Core.Saving;

/// <summary>Why <see cref="DocumentSaver"/> did not save. In every case the target file is left as it was.</summary>
public enum SaveFailureReason
{
    /// <summary>
    /// The target is read-only, or the folder denies access or is missing, so the temporary file cannot be created there or
    /// the target cannot be replaced.
    /// </summary>
    NotWritable,

    /// <summary>The disk (or the user's quota) is full.</summary>
    DiskFull,

    /// <summary>
    /// Creating or writing the temporary file or replacing the target failed for another reason, for example because another
    /// program has the target open without sharing it.
    /// </summary>
    WriteFailed,

    /// <summary>
    /// The written file failed a check: it does not start with the bytes of the original, PDFium cannot open it, the
    /// number of pages or annotations differs from what was expected, or a changed page does not render.
    /// </summary>
    VerificationFailed,

    /// <summary>
    /// Another file stands at the path of the original since it was opened (it was replaced or renamed; the opened file
    /// itself is untouched), and overwriting it was not confirmed.
    /// </summary>
    OriginalChanged,

    /// <summary>
    /// The opened file itself was rewritten in place since it was opened. PDFium reads it lazily, so a save would mix its new
    /// bytes with the old structure; there is no override, the document has to be opened again.
    /// </summary>
    SourceModified,

    /// <summary>PDFium's writer cannot save this document. Reserved; <see cref="DocumentSaver"/> does not report it today.</summary>
    WriterNotSupported,

    /// <summary>The document is encrypted and the written file would not keep the encryption.</summary>
    EncryptedNotSupported,

    /// <summary>
    /// Reserved. A save canceled by the caller throws <see cref="OperationCanceledException"/> instead, so the caller can
    /// tell its own cancellation from a failure.
    /// </summary>
    Canceled,

    /// <summary>
    /// A file stands at the target and the save was told not to replace an existing file
    /// (<see cref="RewriteRequest.ReplaceExisting"/> is <see langword="false"/>), for example because another program created
    /// it while the file was written. The file at the target is untouched.
    /// </summary>
    TargetExists,

    /// <summary>
    /// The written file passed the checks every rewrite makes, but the extra check of the caller
    /// (<see cref="RewriteRequest.ExtraCheck"/>, the redaction check) found something or could not be completed. Nothing is
    /// replaced. <see cref="SaveFailureException.Details"/> carries what the check attached, <see cref="Exception.InnerException"/>
    /// the exception it threw.
    /// </summary>
    RedactionFailed,
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

    /// <summary>
    /// Gets the file the failure is about when it is not the document being saved (a file inserted into the page plan that is no
    /// longer the file that was inserted), or <see langword="null"/>.
    /// </summary>
    public string? FilePath { get; init; }

    /// <summary>
    /// Gets what the extra check of a rewrite (<see cref="RewriteRequest.ExtraCheck"/>) attached to its failure, for example the
    /// findings of the redaction check, or <see langword="null"/>. The type is the contract between the check and its caller; the
    /// saver only passes it on (<see cref="SaveFailureReason.RedactionFailed"/>), so it knows no type of the redaction.
    /// </summary>
    public object? Details { get; init; }
}
