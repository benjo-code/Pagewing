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

    /// <summary>A freehand drawing (subtype Ink).</summary>
    Ink,

    /// <summary>A rectangle outline (subtype Square).</summary>
    Rectangle,

    /// <summary>An ellipse outline (subtype Circle).</summary>
    Ellipse,

    /// <summary>A straight line; in the file an Ink annotation of one stroke of two points.</summary>
    Line,

    /// <summary>An arrow; in the file an Ink annotation of a shaft and a head polyline.</summary>
    Arrow,

    /// <summary>A text box (subtype FreeText).</summary>
    TextBox,

    /// <summary>Any other annotation; it is kept untouched and listed read-only.</summary>
    Other,
}
