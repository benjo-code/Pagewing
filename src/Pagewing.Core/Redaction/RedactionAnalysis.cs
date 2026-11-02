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

    /// <summary>The overlay text of a mark contains a text that is redacted (<see cref="RedactionOverlayRule"/>); the pages are those of the marks that carry the text.</summary>
    OverlayRepeatsTerm,
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
/// <param name="ImageAreasTreated">The image areas (one image under one rectangle) that were pixelated or blurred. 0 when the plan treats image areas solid.</param>
/// <param name="ImageAreasSolid">The image areas that got the solid fill although pixelating or blurring was chosen, because the rule does not apply (see <see cref="ImageSolidReason"/>; one entry in <paramref name="ImageSolidReasons"/> each). 0 when the plan treats image areas solid: nothing falls back then.</param>
/// <param name="OverlaysNotShown">The areas that got no overlay text because a treated image lies under them.</param>
/// <param name="OverlaysNotFitting">The areas whose overlay text does not fit, the areas over the limit of the document included (they stay filled without text).</param>
/// <param name="ImageSolidReasons">Why each of the <paramref name="ImageAreasSolid"/> areas fell back, in the order the page was processed; <see langword="null"/> for none.</param>
/// <param name="OverlaysDrawn">The areas that got their overlay text.</param>
/// <param name="OverlaysOverLimit">Of <paramref name="OverlaysNotFitting"/>, the areas that got no text because the limit of the page (<see cref="RedactionLimits.MaxOverlayPathsPerPage"/> path objects or <see cref="RedactionLimits.MaxOverlaySegmentsPerPage"/> segments) or of the document (<see cref="RedactionLimits.MaxOverlayPaths"/>, <see cref="RedactionLimits.MaxOverlaySegments"/>) was reached.</param>
/// <param name="OverlayPaths">The path objects of the overlay text on this page.</param>
/// <param name="OverlaySegments">The path segments of the overlay text on this page.</param>
/// <param name="OverlayLimitReached">Whether the limit of the document was reached on this page or before it.</param>
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
    IReadOnlyList<Pagewing.Core.Text.PageRect>? RemovedAreas = null,
    int ImageAreasTreated = 0,
    int ImageAreasSolid = 0,
    int OverlaysNotShown = 0,
    int OverlaysNotFitting = 0,
    IReadOnlyList<ImageSolidReason>? ImageSolidReasons = null,
    int OverlaysDrawn = 0,
    int OverlaysOverLimit = 0,
    int OverlayPaths = 0,
    long OverlaySegments = 0,
    bool OverlayLimitReached = false);

/// <summary>Why an image area got the solid fill although pixelating or blurring was chosen.</summary>
public enum ImageSolidReason
{
    /// <summary>The pixel rectangle under the mark is smaller than 2 x 2 blocks of <see cref="RedactionLimits.MinBlockPixels"/> pixels.</summary>
    TooSmall,

    /// <summary>The image is turned or sheared, so the pixels under the mark cannot be told apart; it is removed as a whole.</summary>
    TurnedOrSheared,

    /// <summary>The image is cut by a clipping path, so what it shows is not its pixel rectangle.</summary>
    Clipped,

    /// <summary>The pixel rectangle under the mark overlaps the one of another mark on the same image.</summary>
    Overlapping,

    /// <summary>Pagewing could not read or write the pixels of the image safely (its size, matrix or pixels are unreadable); it is removed as a whole.</summary>
    Unreadable,

    /// <summary>So many treated images lie under the mark that the opaque area could not be cut around them within the limit.</summary>
    TooManyImages,
}

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
/// <param name="AttachmentAnnotations">How many paperclip annotations (<c>/FileAttachment</c>) the file has: up to that many embedded files come with the pages and stay in the file (not cleaned).</param>
/// <param name="Raster">What turning the pages of <see cref="RedactionPlan.RasterPages"/> into images costs, one entry per page in page order; <see langword="null"/> when the plan has none.</param>
/// <param name="ImageTreatment">What the plan does with the image areas (<see cref="RedactionPlan.ImageTreatment"/>): the report words its counts and its caution about text in images by it.</param>
public sealed record RedactionAnalysis(
    IReadOnlyList<PageRedactionImpact> Pages,
    IReadOnlyList<CleanupItem> Cleanup,
    bool CleanupComplete,
    IReadOnlyList<RedactionLoss> Losses,
    IReadOnlyList<RedactionRefusal> Refusals,
    bool PageKeysChecked,
    int Marks,
    int MarksWithoutTerm,
    int AttachmentAnnotations = 0,
    IReadOnlyList<RasterPageInfo>? Raster = null,
    RedactionImageTreatment ImageTreatment = RedactionImageTreatment.Solid)
{
    /// <summary>Gets whether the file must not be redacted (any refusal).</summary>
    public bool IsRefused => Refusals.Count > 0;

    /// <summary>Gets how many areas get no overlay text because a treated image lies under them.</summary>
    public int OverlaysNotShown => Pages.Sum(static p => p.OverlaysNotShown);

    /// <summary>Gets how many areas get no overlay text because it does not fit (the areas over the limit of the document included), on the pages redacted in place and on the pages turned into images.</summary>
    public int OverlaysNotFitting => Pages.Sum(static p => p.OverlaysNotFitting) + (Raster?.Sum(static r => r.OverlaysNotFitting) ?? 0);

    /// <summary>Gets how many of <see cref="OverlaysNotFitting"/> are left out because the limit of the document was reached.</summary>
    public int OverlaysOverLimit => Pages.Sum(static p => p.OverlaysOverLimit) + (Raster?.Sum(static r => r.OverlaysOverLimit) ?? 0);

    /// <summary>Gets everything that is removed as a whole instead of precisely, with the reason, in page order.</summary>
    public IReadOnlyList<RedactionFallback> Fallbacks => [.. Pages.SelectMany(static p => p.Fallbacks)];
}

/// <summary>
/// What turning one page into an image does (milestone 14.1), told before anything is applied: the resolution that is really used, the
/// size, and what the page loses besides its text. The counts are read from the ORIGINAL page.
/// </summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="Dpi">The resolution, in dots per inch, the page is rendered at.</param>
/// <param name="WidthPx">The width of the image in pixels.</param>
/// <param name="HeightPx">The height of the image in pixels.</param>
/// <param name="Lowered">Whether <paramref name="Dpi"/> is lower than the one the plan asks for because the page would exceed <see cref="RedactionLimits.MaxRasterPixels"/> (never silent: the card names it).</param>
/// <param name="EstimatedBytes">An upper estimate of the size the image adds to the file: the uncompressed RGB size (Flate rarely does worse; most pages are far smaller).</param>
/// <param name="Annotations">How many annotation objects the page has (links and form field widgets included): all of them are gone from the page.</param>
/// <param name="FormFields">How many form field widgets the page has: they are not fillable afterwards.</param>
/// <param name="Links">How many links the page has: they stop working.</param>
/// <param name="HasText">Whether the page has text: all of it is gone from the page.</param>
/// <param name="OverlaysNotFitting">The areas of the page whose overlay text does not fit, the areas over the limit of the document included.</param>
/// <param name="OverlaysOverLimit">Of those, the areas left out because the limit of the document was reached.</param>
/// <param name="OverlaysDrawn">The areas of the page that get their overlay text.</param>
/// <param name="OverlayPaths">The path objects of the overlay text on the page.</param>
/// <param name="OverlaySegments">The path segments of the overlay text on the page.</param>
/// <param name="OverlayLimitReached">Whether the limit of the document was reached on this page or before it.</param>
public sealed record RasterPageInfo(
    int Page,
    int Dpi,
    int WidthPx,
    int HeightPx,
    bool Lowered,
    long EstimatedBytes,
    int Annotations = 0,
    int FormFields = 0,
    int Links = 0,
    bool HasText = false,
    int OverlaysNotFitting = 0,
    int OverlaysOverLimit = 0,
    int OverlaysDrawn = 0,
    int OverlayPaths = 0,
    long OverlaySegments = 0,
    bool OverlayLimitReached = false);

/// <summary>What the cleanup removes and what the rebuild loses, read from a document and its file.</summary>
/// <param name="Cleanup">What the cleanup removes, in the order of <see cref="CleanupKind"/>.</param>
/// <param name="Losses">What the rebuild loses, in the order of <see cref="RedactionLossKind"/>; only losses that apply.</param>
/// <param name="Complete">Whether the scan of the file could read every stream; <see langword="false"/> means the cleanup list may miss entries (the card must not say "nothing else").</param>
/// <param name="AttachmentAnnotations">How many paperclip annotations (<c>/FileAttachment</c>) the file has: up to that many embedded files come with the pages and are not cleaned.</param>
public sealed record RedactionInventory(IReadOnlyList<CleanupItem> Cleanup, IReadOnlyList<RedactionLoss> Losses, bool Complete, int AttachmentAnnotations = 0);
