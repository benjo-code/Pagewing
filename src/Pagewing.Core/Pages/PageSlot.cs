namespace Pagewing.Core.Pages;

/// <summary>Names the document a page comes from: 0 is the open document, every other value a file inserted into the plan.</summary>
/// <param name="Value">The number of the source; 0 is the open document (primary).</param>
public readonly record struct PageSourceId(int Value);

/// <summary>The size of a blank page in points.</summary>
/// <param name="Width">The width in points.</param>
/// <param name="Height">The height in points.</param>
public sealed record BlankPage(double Width, double Height);

/// <summary>
/// One page of the result: a page of a source or a blank page. The same source page may appear in many slots (duplicating).
/// </summary>
/// <param name="Source">The document the page comes from (ignored for a blank page).</param>
/// <param name="SourceIndex">The 0-based page index in the source; -1 for a blank page.</param>
/// <param name="ExtraRotation">The rotation added to the page's own rotation: 0, 90, 180 or 270.</param>
/// <param name="Blank">The size of a blank page, or <see langword="null"/> for a page of a source.</param>
public sealed record PageSlot(PageSourceId Source, int SourceIndex, int ExtraRotation, BlankPage? Blank);
