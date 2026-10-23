namespace Pagewing.Core.Viewing;

/// <summary>
/// Where a document was shown when its tab was left: the spot at the top of the viewport and the horizontal distance
/// of the viewport's centre from the centre line of the pages, so the view can be restored at any zoom and window width.
/// </summary>
/// <param name="Anchor">The page and the fraction of it at the top edge of the viewport.</param>
/// <param name="HorizontalFromCentre">The horizontal distance of the viewport's centre from the centre line of the pages, in DIPs.</param>
public sealed record ViewState(PageAnchor Anchor, double HorizontalFromCentre);
