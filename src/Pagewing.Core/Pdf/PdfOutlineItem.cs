namespace Pagewing.Core.Pdf;

/// <summary>One bookmark of the document outline (table of contents), with its children.</summary>
/// <param name="Title">The title with control characters replaced by spaces; empty when the bookmark has none.</param>
/// <param name="PageIndex">The zero-based target page, or <see langword="null"/> when the bookmark has no valid target.</param>
/// <param name="IsOpen">Whether the bookmark is expanded by default.</param>
/// <param name="Children">The child bookmarks.</param>
public sealed record PdfOutlineItem(string Title, int? PageIndex, bool IsOpen, IReadOnlyList<PdfOutlineItem> Children);
