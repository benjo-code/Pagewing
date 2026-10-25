namespace Pagewing.Core.Annotations;

/// <summary>
/// A point in page points as the user sees the page: the origin is the top left of the displayed page (after
/// rotation and CropBox) and y grows downwards, like <see cref="Pagewing.Core.Text.PageRect"/>.
/// </summary>
/// <param name="X">The x coordinate in points.</param>
/// <param name="Y">The y coordinate in points.</param>
public readonly record struct DisplayPoint(double X, double Y);
