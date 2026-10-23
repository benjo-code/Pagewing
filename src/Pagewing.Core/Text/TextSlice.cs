namespace Pagewing.Core.Text;

/// <summary>The characters [<paramref name="Start"/>, <paramref name="End"/>) of one page.</summary>
/// <param name="Page">The text of the page.</param>
/// <param name="Start">The first character (inclusive).</param>
/// <param name="End">The end of the range (exclusive).</param>
public readonly record struct TextSlice(PageText Page, int Start, int End);
