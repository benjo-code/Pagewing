namespace Pagewing.Core.Pages;

/// <summary>Why a page edit was refused; the plan stays as it was.</summary>
public enum PageEditError
{
    /// <summary>The edit would remove the last page; at least one page stays.</summary>
    LastPage,

    /// <summary>The edit would exceed <see cref="PageEditLimits"/>.</summary>
    LimitExceeded,

    /// <summary>A position, a source index, a destination or a size is out of range.</summary>
    OutOfRange,

    /// <summary>The rotation is not a multiple of 90 degrees.</summary>
    InvalidRotation,
}

/// <summary>A page edit that the plan refuses; the plan is unchanged.</summary>
public sealed class PageEditException : Exception
{
    /// <summary>Creates the exception.</summary>
    public PageEditException()
        : this(PageEditError.OutOfRange)
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">The message.</param>
    public PageEditException(string message)
        : this(PageEditError.OutOfRange, message)
    {
    }

    /// <summary>Creates the exception with a message and a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public PageEditException(string message, Exception innerException)
        : base(message, innerException)
    {
        Error = PageEditError.OutOfRange;
    }

    /// <summary>Creates the exception for <paramref name="error"/>.</summary>
    /// <param name="error">Why the edit was refused.</param>
    public PageEditException(PageEditError error)
        : this(error, $"The page edit was refused: {error}.")
    {
    }

    /// <summary>Creates the exception for <paramref name="error"/> with a message.</summary>
    /// <param name="error">Why the edit was refused.</param>
    /// <param name="message">The message.</param>
    public PageEditException(PageEditError error, string message)
        : base(message)
    {
        Error = error;
    }

    /// <summary>Gets why the edit was refused.</summary>
    public PageEditError Error { get; }
}
