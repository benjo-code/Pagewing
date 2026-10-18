using Pagewing.Core.Text;

namespace Pagewing.App.Controls;

/// <summary>What a highlight on a page stands for; it decides how <see cref="DocumentCanvas"/> draws it.</summary>
internal enum HighlightKind
{
    /// <summary>A search match: filled with <c>search.hit</c>.</summary>
    SearchHit,

    /// <summary>The current search match: filled with <c>search.current</c> and framed with <c>search.on</c>.</summary>
    SearchCurrent,

    /// <summary>Selected text: the translucent <c>selection.page</c> over the page.</summary>
    Selection,
}

/// <summary>
/// A rectangle the canvas marks on a page: the 0-based page, the rectangle in PDF points from the top left of the page
/// as displayed (after rotation and crop box, the space of <see cref="PageText"/>) and its kind.
/// </summary>
internal readonly record struct PageHighlight(int Page, PageRect Rect, HighlightKind Kind);
