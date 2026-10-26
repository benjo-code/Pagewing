namespace Pagewing.Core.Annotations;

/// <summary>A change to an annotation; <see langword="null"/> leaves a property unchanged.</summary>
/// <param name="Contents">The new comment; an empty string deletes the comment. For a text box it is the new text (never empty).</param>
/// <param name="Color">The new colour; a drawing or a text box gets it by being replaced (alpha always 255).</param>
/// <param name="Geometry">
/// The new shape of a drawing (moved, resized or with another width), of the same kind; the drawing is replaced. Only for
/// drawings.
/// </param>
/// <param name="TextBox">
/// The whole new text box (box, text, size, colour); it replaces <paramref name="Contents"/> and <paramref name="Color"/>
/// when given. Only for text boxes.
/// </param>
public sealed record AnnotationEdit(string? Contents = null, AnnotationColor? Color = null, DrawingGeometry? Geometry = null, TextBoxSpec? TextBox = null);
