namespace Pagewing.Core.Redaction;

/// <summary>What the engine did to a piece of content as a whole because it could not do it precisely.</summary>
public enum RedactionFallbackKind
{
    /// <summary>A text object with characters under a rectangle was removed completely: the characters that would have stayed could not be rewritten and read back.</summary>
    TextObjectRemovedWhole,

    /// <summary>An image was removed completely instead of being overwritten under the rectangle.</summary>
    ImageRemovedWhole,

    /// <summary>A path was removed completely instead of being cut around the rectangle.</summary>
    PathRemovedWhole,

    /// <summary>A form object (a form XObject placed on the page) was removed completely instead of being taken apart.</summary>
    FormObjectRemovedWhole,
}

/// <summary>One piece of content that the engine removed as a whole, and why. The analysis names every one before anything is applied.</summary>
/// <param name="Kind">What was removed.</param>
/// <param name="Page">The 0-based page.</param>
/// <param name="Reason">A short English explanation.</param>
public sealed record RedactionFallback(RedactionFallbackKind Kind, int Page, string Reason);

/// <summary>Why redaction is refused, with the pages it concerns. A refusal is never silent: nothing is written.</summary>
public enum RedactionRefusalKind
{
    /// <summary>The file is encrypted (milestone 14 does not decrypt silently).</summary>
    Encrypted,

    /// <summary>A page has a key that PDFium cannot remove (<c>/Thumb</c>, <c>/PieceInfo</c>, a page <c>/Metadata</c>, <c>/AA</c>), or the keys could not be checked.</summary>
    UnremovablePageKeys,

    /// <summary>A page cannot be rewritten without changing it outside the marks (or beyond the limits against hostile pages).</summary>
    PageNotRedactable,
}

/// <summary>A reason to refuse redaction, with the 0-based pages it concerns.</summary>
/// <param name="Kind">The kind of refusal.</param>
/// <param name="Pages">The 0-based pages it concerns; empty when it concerns the file.</param>
/// <param name="Reason">A short English explanation.</param>
public sealed record RedactionRefusal(RedactionRefusalKind Kind, IReadOnlyList<int> Pages, string Reason);

/// <summary>
/// What redacting a page does (or did): the counts, the content that is removed as a whole, the text that goes, and a refusal
/// if the page cannot be redacted. Counts that belong to content types the engine does not handle yet are 0.
/// </summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="CharsRemoved">The characters removed from the page (hit characters, and all characters of a text object that is removed whole).</param>
/// <param name="TextObjectsSplit">Text objects replaced by new ones that hold only the characters that stay.</param>
/// <param name="TextObjectsRemovedWhole">Text objects removed completely: every character was hit, or the rest could not be rewritten (then listed in <paramref name="Fallbacks"/>).</param>
/// <param name="ImagesOverwritten">Images whose pixels under the rectangle were overwritten.</param>
/// <param name="ImagesRemovedWhole">Images removed completely.</param>
/// <param name="PathsCropped">Paths cut around the rectangle.</param>
/// <param name="PathsRemovedWhole">Paths removed completely.</param>
/// <param name="FormObjectsTouched">Form objects taken apart or removed because content in them is under a rectangle.</param>
/// <param name="AnnotationsRemoved">Annotations removed: those in a rectangle, with their popups and replies.</param>
/// <param name="Fallbacks">Everything removed as a whole instead of precisely, with the reason.</param>
/// <param name="RemovedText">The text that goes, one entry per run of removed characters (the whole text for an object removed as a whole); the check of the result uses it.</param>
/// <param name="Refusal">Why the page cannot be redacted, or <see langword="null"/>. A refused page was not changed by the analysis; after a refusal from <c>RedactPageAsync</c> the document may have changed and must be discarded.</param>
/// <param name="RemovedAreas">Where content went besides the rectangles, in points from the top left of the displayed page: the boxes of the characters that were removed (a character counts as soon as its box touches a rectangle, so its box can reach out of the rectangle) and the bounds of what was removed as a whole. The render check leaves these out too (<see cref="RedactionPageAreas.For"/>).</param>
public sealed record PageRedactionImpact(
    int Page,
    int CharsRemoved,
    int TextObjectsSplit,
    int TextObjectsRemovedWhole,
    int ImagesOverwritten,
    int ImagesRemovedWhole,
    int PathsCropped,
    int PathsRemovedWhole,
    int FormObjectsTouched,
    int AnnotationsRemoved,
    IReadOnlyList<RedactionFallback> Fallbacks,
    IReadOnlyList<string> RemovedText,
    RedactionRefusal? Refusal,
    IReadOnlyList<Pagewing.Core.Text.PageRect>? RemovedAreas = null);
