namespace Pagewing.Core.Text;

/// <summary>The text around a search hit, for a list of hits.</summary>
/// <param name="Before">Up to 30 characters before the match, from the line of its first character.</param>
/// <param name="Match">The matched characters; line ends inside the match become one space, removed line-end hyphens are left out.</param>
/// <param name="After">Up to 30 characters after the match, from the line of its last character.</param>
public sealed record SearchSnippet(string Before, string Match, string After);

/// <summary>A match of a search on one page.</summary>
/// <param name="PageIndex">The 0-based page index.</param>
/// <param name="FirstChar">The index of the first matched character of the page.</param>
/// <param name="LastChar">The index of the last matched character of the page (inclusive).</param>
/// <param name="Rects">The rectangles of the match in page points from the top left, one per line, in text order.</param>
/// <param name="Snippet">The text around the match.</param>
public sealed record SearchHit(int PageIndex, int FirstChar, int LastChar, IReadOnlyList<PageRect> Rects, SearchSnippet Snippet);
