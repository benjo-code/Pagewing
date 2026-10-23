namespace Pagewing.Core.Text;

/// <summary>What a link leads to.</summary>
public abstract record LinkTarget;

/// <summary>A position in a page of the same document.</summary>
/// <param name="PageIndex">The 0-based target page.</param>
/// <param name="Top">The target position in points from the top of the target page; <see langword="null"/> means the top of the page.</param>
public sealed record PageTarget(int PageIndex, double? Top) : LinkTarget;

/// <summary>An address outside the document. The scheme is not checked here; whoever opens it must.</summary>
/// <param name="Url">The address.</param>
public sealed record UriTarget(string Url) : LinkTarget;

/// <summary>A clickable link of a page.</summary>
/// <param name="Rects">The clickable areas in page points.</param>
/// <param name="Target">What the link leads to.</param>
public sealed record PageLink(IReadOnlyList<PageRect> Rects, LinkTarget Target);
