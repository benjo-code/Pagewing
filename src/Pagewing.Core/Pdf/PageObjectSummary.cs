using Pagewing.Core.Text;

namespace Pagewing.Core.Pdf;

/// <summary>What a page is made of, counted by kind (see <see cref="PdfDocument.GetPageObjectSummaryAsync"/>).</summary>
/// <param name="Images">The image objects.</param>
/// <param name="Paths">The path objects.</param>
/// <param name="Texts">The text objects.</param>
/// <param name="Others">The objects of any other kind: shadings, form objects and unknown ones.</param>
/// <param name="Annotations">The annotations (links and form widgets included).</param>
/// <param name="PathBounds">
/// The bounds of every path object in points from the top left of the displayed page, in the order of the objects; <see langword="null"/> when
/// the page has no path object, has more than 20,000 of them, or the position of one could not be worked out. The check of a page that became
/// an image reads them: a path there is allowed only inside a marked area.
/// </param>
public readonly record struct PageObjectSummary(int Images, int Paths, int Texts, int Others, int Annotations, IReadOnlyList<PageRect>? PathBounds = null);
