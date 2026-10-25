namespace Pagewing.Core.Annotations;

/// <summary>The kind of an annotation as far as Pagewing handles it.</summary>
public enum AnnotationKind
{
    /// <summary>A text highlight (subtype Highlight).</summary>
    Highlight,

    /// <summary>An underline (subtype Underline).</summary>
    Underline,

    /// <summary>A strike-out (subtype StrikeOut).</summary>
    StrikeOut,

    /// <summary>Any other annotation; it is kept untouched and listed read-only.</summary>
    Other,
}
