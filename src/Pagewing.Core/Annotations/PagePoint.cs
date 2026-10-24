namespace Pagewing.Core.Annotations;

/// <summary>A point in PDF user space: the origin is the bottom left of the page and y grows upwards.</summary>
/// <param name="X">The x coordinate in points.</param>
/// <param name="Y">The y coordinate in points.</param>
public readonly record struct PagePoint(double X, double Y);
