namespace Pagewing.Core.Annotations;

/// <summary>A change to an annotation; <see langword="null"/> leaves a property unchanged.</summary>
/// <param name="Contents">The new comment; an empty string deletes the comment.</param>
/// <param name="Color">The new colour.</param>
public sealed record AnnotationEdit(string? Contents = null, AnnotationColor? Color = null);
