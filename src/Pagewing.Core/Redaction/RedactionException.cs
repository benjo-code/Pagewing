namespace Pagewing.Core.Redaction;

/// <summary>Why a mark or an edit of the plan was refused; the plan stays as it was.</summary>
public enum RedactionError
{
    /// <summary>The edit would exceed <see cref="RedactionLimits"/>.</summary>
    LimitExceeded,

    /// <summary>A rectangle is not finite or has no size, a mark has no rectangle, or the page is negative.</summary>
    InvalidArea,

    /// <summary>A text mark has no term, or a term that is empty or only white space.</summary>
    EmptyTerm,

    /// <summary>The overlay text has a character that the text box font cannot show (refused as a whole, never changed).</summary>
    OverlayInvalid,
}

/// <summary>A mark or edit that the redaction plan refuses; the plan is unchanged.</summary>
public sealed class RedactionException : Exception
{
    /// <summary>Creates the exception.</summary>
    public RedactionException()
        : this(RedactionError.InvalidArea)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public RedactionException(string message)
        : this(RedactionError.InvalidArea, message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public RedactionException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = RedactionError.InvalidArea;
    }

    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    /// <param name="error">Why the mark was refused.</param>
    public RedactionException(RedactionError error)
        : this(error, $"The redaction mark was refused: {error}.")
    {
    }

    /// <summary>Creates the exception for <paramref name="error"/> with a message.</summary>
    /// <param name="error">Why the mark was refused.</param>
    /// <param name="message">The message.</param>
    public RedactionException(RedactionError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Gets why the mark was refused.</summary>
    public RedactionError Error { get; }
}
