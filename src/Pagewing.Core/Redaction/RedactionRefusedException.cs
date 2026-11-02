namespace Pagewing.Core.Redaction;

/// <summary>
/// Applying a redaction was refused before anything was written (an encrypted file, page keys PDFium cannot remove, a page that
/// cannot be written again without changing it): the target is as it was, the plan is untouched, and
/// <see cref="Refusals"/> says why, with the pages. The analysis lists the same refusals beforehand; this is what a caller that did
/// not consult it, or whose file changed meanwhile, gets.
/// </summary>
public sealed class RedactionRefusedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RedactionRefusedException()
        : this([])
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public RedactionRefusedException(string message)
        : base(message)
    {
        Refusals = [];
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public RedactionRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Refusals = [];
    }

    /// <summary>Creates the exception for refusals.</summary>
    /// <param name="refusals">Why the redaction is refused.</param>
    public RedactionRefusedException(IReadOnlyList<RedactionRefusal> refusals)
        : base(RedactionRefusalText.Of(refusals ?? throw new ArgumentNullException(nameof(refusals))))
    {
        Refusals = refusals;
    }

    /// <summary>Gets why the redaction is refused.</summary>
    public IReadOnlyList<RedactionRefusal> Refusals { get; }
}

/// <summary>
/// The redacted file was written and checked, but the session could not take it as its document (or something else failed after the
/// write). The file at <see cref="TargetPath"/> is the redacted one; the session may still show the old document.
/// </summary>
public sealed class RedactionAdoptException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RedactionAdoptException()
        : this(string.Empty, null)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public RedactionAdoptException(string message)
        : this(string.Empty, null)
    {
        _ = message;
    }

    /// <summary>Creates the exception for a written file.</summary>
    /// <param name="targetPath">The path of the redacted file.</param>
    /// <param name="inner">What failed after the write.</param>
    public RedactionAdoptException(string targetPath, Exception? inner)
        : base("The redacted file was written, but the document could not be taken over.", inner)
    {
        TargetPath = targetPath;
    }

    /// <summary>Gets the path of the file that was written.</summary>
    public string TargetPath { get; }
}
