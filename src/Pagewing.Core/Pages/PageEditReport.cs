namespace Pagewing.Core.Pages;

/// <summary>
/// What applying a page plan loses, couples or invalidates. <see cref="PageLoss.Detail"/> is never text for the user: the
/// interface words each loss by its <see cref="PageLoss.Kind"/> (in both languages) and uses the detail as a key.
/// </summary>
public enum PageLossKind
{
    /// <summary>The bookmarks of an inserted file are not taken over (only those of the primary file stay). Detail: <c>source:N</c>.</summary>
    ForeignOutlineDropped,

    /// <summary>A bookmark of the primary file points to a page that is deleted; it stays as an entry without a target. Pages: the deleted pages (0-based, of the open document) some bookmark points to.</summary>
    BookmarkToDeletedPage,

    /// <summary>
    /// Pages with form fields are copied: their fields appear on more than one page (one field with several widgets, the value
    /// is shared), or arrive with inserted pages without being entered into the form of the file, so another program may not
    /// let the user fill them. Detail: <c>duplicated</c> (a page of the open document or of an inserted file appears more than once)
    /// or <c>inserted</c> (pages of an inserted file). Pages: the positions in the result.
    /// </summary>
    DuplicatedFormFields,

    /// <summary>Form fields of the same name come from different files; the form engine reads them as one field. Pages: the positions in the result. Detail: <c>unchecked</c> when there were too many names to compare, else <see langword="null"/>.</summary>
    FormFieldNameCollision,

    /// <summary>The digital signature of the primary file is shown as invalid afterwards (the file is written anew). Detail: <c>docmdp:N</c> (the most restrictive change level the signatures declare) or <see langword="null"/>.</summary>
    SignatureInvalidated,

    /// <summary>
    /// The protection of an inserted, encrypted file is not carried over: the result has the protection of the primary file
    /// (or none). Detail: <c>source:N</c>. (An encrypted primary file stays encrypted.)
    /// </summary>
    EncryptionNotPreserved,

    /// <summary>
    /// An internal link (a go-to action) is left without a target: the target page is deleted (Detail <c>deleted-page</c>,
    /// Pages: the pages that carry the link, positions in the result), or the link sits on a copied page (Detail
    /// <c>copied-page</c>; PDFium keeps the action but not its destination).
    /// </summary>
    InternalLinkLost,
}

/// <summary>One loss, coupling or invalidation of a page plan.</summary>
/// <param name="Kind">What kind of loss; the interface chooses the words.</param>
/// <param name="Detail">A short English key that refines the kind (see <see cref="PageLossKind"/>), or <see langword="null"/>. Never document content.</param>
/// <param name="Pages">The 0-based pages concerned (what they number depends on the kind), or <see langword="null"/> for a loss of the whole file.</param>
public sealed record PageLoss(PageLossKind Kind, string? Detail, IReadOnlyList<int>? Pages);

/// <summary>Everything the user must be told before a page plan is applied. An empty list means nothing is lost.</summary>
/// <param name="Losses">The losses, in the order of <see cref="PageLossKind"/>.</param>
public sealed record PageEditReport(IReadOnlyList<PageLoss> Losses);
