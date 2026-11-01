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
/// <param name="PageKeysUnverified">Whether the page keys could not be checked (only with <see cref="RedactionRefusalKind.UnremovablePageKeys"/>): the file may or may not carry such keys. Interfaces read this flag, never the pages or the text.</param>
public sealed record RedactionRefusal(RedactionRefusalKind Kind, IReadOnlyList<int> Pages, string Reason, bool PageKeysUnverified = false);

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

/// <summary>What the cleanup of a redaction removes from the file in every case ("cleaning is not a switch"): the document-level data that a rebuild into a new document leaves behind.</summary>
public enum CleanupKind
{
    /// <summary>The author of the information dictionary.</summary>
    Author,

    /// <summary>The title of the information dictionary.</summary>
    Title,

    /// <summary>The subject of the information dictionary.</summary>
    Subject,

    /// <summary>The keywords of the information dictionary.</summary>
    Keywords,

    /// <summary>The creator of the information dictionary.</summary>
    Creator,

    /// <summary>The producer of the information dictionary.</summary>
    Producer,

    /// <summary>XMP metadata (the document's own; the count can include XMP of objects inside the pages, which stays).</summary>
    Xmp,

    /// <summary>Bookmarks (the outline).</summary>
    Bookmarks,

    /// <summary>Attached files.</summary>
    Attachments,

    /// <summary>JavaScript (document actions and named scripts; the count can include actions of annotations, which stay).</summary>
    JavaScript,

    /// <summary>Page labels.</summary>
    PageLabels,
}

/// <summary>One thing the cleanup removes, as it is present in this file.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Detail">For a field of the information dictionary its text (at most 200 characters); for the others how many entries the scan found, as a number.</param>
public sealed record CleanupItem(CleanupKind Kind, string Detail);

/// <summary>What the rebuild into a new document loses (the cleanup is separate: <see cref="CleanupItem"/>). An encrypted file is a refusal, not a loss.</summary>
public enum RedactionLossKind
{
    /// <summary>Internal links (to a page of the document) lose their target: PDFium copies the link, not where it leads.</summary>
    InternalLinksLoseTheirTarget,

    /// <summary>Form fields are not reliably fillable afterwards: the widgets come along as annotations, the form does not.</summary>
    FormFieldsNotFillable,

    /// <summary>Images that are overwritten under a mark are written again as RGB (Flate), whatever colour space they had; CMYK may look different in other viewers. The impact of the pages states it, not the inventory.</summary>
    ColorSpaceBecomesRgb,

    /// <summary>The document has digital signatures; any change makes them show as invalid (whatever the permission level).</summary>
    SignatureInvalidated,
}

/// <summary>One loss of the rebuild with how many things it concerns.</summary>
/// <param name="Kind">What is lost.</param>
/// <param name="Count">How many links, fields, images or signatures.</param>
public sealed record RedactionLoss(RedactionLossKind Kind, int Count);

/// <summary>
/// What redacting a plan will do, worked out on a throwaway working copy before anything is applied (<see cref="RedactionExecutor.AnalyzeAsync(RedactionPlan, string, string?, CancellationToken)"/>):
/// the effect on every marked page, what the cleanup removes, what the rebuild loses and every reason the file is refused. It is
/// the content of the card before "Apply" and of the report of the command line. <b>Every caller honours <see cref="IsRefused"/></b>:
/// building and applying do not read the analysis again.
/// </summary>
/// <param name="Pages">What redacting each marked page does, in page order. After a refusal the counts of the pages behind it come from a dry run.</param>
/// <param name="Cleanup">What the cleanup removes, as it is present in this file (always removed, never a switch).</param>
/// <param name="CleanupComplete">Whether the scan of the file could read every stream; <see langword="false"/> means the cleanup list may miss entries.</param>
/// <param name="Losses">What the rebuild loses, in the order of <see cref="RedactionLossKind"/>; only losses that apply.</param>
/// <param name="Refusals">Why the file cannot be redacted, one entry per reason; empty when it can. <c>UnremovableContent</c> is not one of them: the content check finds it only after writing.</param>
/// <param name="PageKeysChecked">Whether the early scan of the written working copy for page keys PDFium cannot remove ran and could check the file (the final output is checked again when applying).</param>
/// <param name="Marks">The number of marks of the plan.</param>
/// <param name="MarksWithoutTerm">The number of marks without a term (a rectangle dragged on the page): the text under them is removed, but the check has no text to search for.</param>
public sealed record RedactionAnalysis(
    IReadOnlyList<PageRedactionImpact> Pages,
    IReadOnlyList<CleanupItem> Cleanup,
    bool CleanupComplete,
    IReadOnlyList<RedactionLoss> Losses,
    IReadOnlyList<RedactionRefusal> Refusals,
    bool PageKeysChecked,
    int Marks,
    int MarksWithoutTerm)
{
    /// <summary>Gets whether the file must not be redacted (any refusal).</summary>
    public bool IsRefused => Refusals.Count > 0;

    /// <summary>Gets everything that is removed as a whole instead of precisely, with the reason, in page order.</summary>
    public IReadOnlyList<RedactionFallback> Fallbacks => [.. Pages.SelectMany(static p => p.Fallbacks)];
}

/// <summary>What the cleanup removes and what the rebuild loses, read from a document and its file.</summary>
/// <param name="Cleanup">What the cleanup removes, in the order of <see cref="CleanupKind"/>.</param>
/// <param name="Losses">What the rebuild loses, in the order of <see cref="RedactionLossKind"/>; only losses that apply.</param>
/// <param name="Complete">Whether the scan of the file could read every stream; <see langword="false"/> means the cleanup list may miss entries (the card must not say "nothing else").</param>
public sealed record RedactionInventory(IReadOnlyList<CleanupItem> Cleanup, IReadOnlyList<RedactionLoss> Losses, bool Complete);
