namespace Pagewing.Core.Ocr;

/// <summary>
/// The run was canceled. It is an <see cref="OperationCanceledException"/>, so callers that only care that it ended treat it like any cancel; the
/// pages that were recognized before the cancel are in <see cref="Partial"/>, for the user to use or discard (nothing is ever written without asking).
/// </summary>
public sealed class OcrRunCanceledException : OperationCanceledException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="partial">What was done before the cancel.</param>
    /// <param name="token">The token that was canceled.</param>
    public OcrRunCanceledException(OcrRunResult partial, CancellationToken token)
        : base("The text recognition was canceled.", token)
    {
        Partial = partial;
    }

    /// <summary>Gets the pages that were recognized (and the skipped ones) before the cancel.</summary>
    public OcrRunResult Partial { get; }
}
