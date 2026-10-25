namespace Pagewing.Core.Annotations;

/// <summary>A change to an annotation; <see langword="null"/> leaves a property unchanged.</summary>
/// <param name="Contents">The new comment; an empty string deletes the comment.</param>
/// <param name="Color">The new colour; a drawing gets it by being replaced (alpha always 255).</param>
/// <param name="Geometry">
/// The new shape of a drawing (moved, resized or with another width), of the same kind; the drawing is replaced. Only for
/// drawings.
/// </param>
public sealed record AnnotationEdit(string? Contents = null, AnnotationColor? Color = null, DrawingGeometry? Geometry = null);
