namespace Pagewing.Core.Printing;

/// <summary>How far a print job is.</summary>
/// <param name="Done">Pages handled so far (printed or skipped).</param>
/// <param name="Total">Pages in the job.</param>
/// <param name="FailedPages">Pages that could not be printed and were skipped, 0-based, in the order they failed.</param>
public sealed record PrintProgress(int Done, int Total, IReadOnlyList<int> FailedPages);
