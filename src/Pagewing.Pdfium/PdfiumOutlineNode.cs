namespace Pagewing.Pdfium;

/// <summary>One bookmark of the document outline, with its children.</summary>
/// <param name="Title">The title with control characters replaced by spaces; may be empty.</param>
/// <param name="PageIndex">The zero-based target page, or -1 when the bookmark has no (valid) target.</param>
/// <param name="IsOpen">Whether the bookmark is expanded by default (<c>FPDFBookmark_GetCount</c> greater than 0).</param>
/// <param name="Children">The child bookmarks.</param>
internal sealed record PdfiumOutlineNode(string Title, int PageIndex, bool IsOpen, IReadOnlyList<PdfiumOutlineNode> Children);
