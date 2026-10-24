using Pagewing.Core.Text;

namespace Pagewing.Core.Annotations;

/// <summary>An annotation of a page as read from the document.</summary>
/// <param name="Page">The 0-based page index.</param>
/// <param name="Index">The index in the page's annotation list; it changes when annotations are added or removed.</param>
/// <param name="Name">The unique name (NM entry), or <see langword="null"/> if there is none.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Subtype">The PDF subtype name, for example "Highlight" or "Link".</param>
/// <param name="Color">The colour, or <see langword="null"/> if the annotation has none.</param>
/// <param name="Quads">The quadrilaterals in PDF user space; empty for a kind without them.</param>
/// <param name="Bounds">The bounds in page points with the origin at the top left, like <see cref="PageRect"/>.</param>
/// <param name="Contents">The comment, or <see langword="null"/> if there is none.</param>
/// <param name="Author">The author (T entry), or <see langword="null"/>.</param>
/// <param name="Modified">The modification date, or <see langword="null"/> if unknown.</param>
/// <param name="Editable">Whether Pagewing may change or delete it.</param>
public sealed record PdfAnnotation(
    int Page,
    int Index,
    string? Name,
    AnnotationKind Kind,
    string Subtype,
    AnnotationColor? Color,
    IReadOnlyList<Quad> Quads,
    PageRect Bounds,
    string? Contents,
    string? Author,
    DateTimeOffset? Modified,
    bool Editable);
