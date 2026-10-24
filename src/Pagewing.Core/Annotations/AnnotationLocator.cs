namespace Pagewing.Core.Annotations;

/// <summary>Finds an annotation again: by its name if it has one, else by its index on the page.</summary>
/// <param name="Page">The 0-based page index.</param>
/// <param name="Index">The index in the page's annotation list.</param>
/// <param name="Name">The unique name, or <see langword="null"/>.</param>
public readonly record struct AnnotationLocator(int Page, int Index, string? Name);
