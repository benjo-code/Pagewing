namespace Pagewing.Core.Printing;

/// <summary>Printing failed; the message is meant for the user.</summary>
public sealed class PrintException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PrintException"/> class.</summary>
    public PrintException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PrintException"/> class.</summary>
    /// <param name="message">What went wrong, in words for the user.</param>
    public PrintException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PrintException"/> class.</summary>
    /// <param name="message">What went wrong, in words for the user.</param>
    /// <param name="innerException">The cause.</param>
    public PrintException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
