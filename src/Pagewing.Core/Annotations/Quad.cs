namespace Pagewing.Core.Annotations;

/// <summary>
/// A quadrilateral of a text markup in PDF user space. The corners are stored in the order Acrobat reads them
/// (top left, top right, bottom left, bottom right): the third and fourth corner are swapped against ISO 32000,
/// which names a counter-clockwise order.
/// </summary>
/// <param name="TopLeft">The top left corner.</param>
/// <param name="TopRight">The top right corner.</param>
/// <param name="BottomLeft">The bottom left corner.</param>
/// <param name="BottomRight">The bottom right corner.</param>
public readonly record struct Quad(PagePoint TopLeft, PagePoint TopRight, PagePoint BottomLeft, PagePoint BottomRight);
