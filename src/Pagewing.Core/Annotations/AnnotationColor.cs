namespace Pagewing.Core.Annotations;

/// <summary>An opaque RGB colour of an annotation.</summary>
/// <param name="R">The red channel.</param>
/// <param name="G">The green channel.</param>
/// <param name="B">The blue channel.</param>
public readonly record struct AnnotationColor(byte R, byte G, byte B);
