namespace Pagewing.Core.Pdf;

/// <summary>What a page is made of, counted by kind (see <see cref="PdfDocument.GetPageObjectSummaryAsync"/>).</summary>
/// <param name="Images">The image objects.</param>
/// <param name="Paths">The path objects.</param>
/// <param name="Texts">The text objects.</param>
/// <param name="Others">The objects of any other kind: shadings, form objects and unknown ones.</param>
/// <param name="Annotations">The annotations (links and form widgets included).</param>
public readonly record struct PageObjectSummary(int Images, int Paths, int Texts, int Others, int Annotations);
