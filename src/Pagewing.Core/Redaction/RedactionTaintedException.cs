namespace Pagewing.Core.Redaction;

/// <summary>
/// The document was changed by a redaction that did not finish cleanly (refused after the first edit, failed, or canceled), so it
/// holds content that is only partly redacted. It must not be written or redacted further: discard it and start again from the file.
/// </summary>
public sealed class RedactionTaintedException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    public RedactionTaintedException()
        : this("The document was changed by an unfinished redaction and must be discarded.")
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public RedactionTaintedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public RedactionTaintedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
