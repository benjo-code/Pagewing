using System.Globalization;

namespace Pagewing.Core.Pages;

/// <summary>
/// What applying a page plan loses, couples or invalidates. <see cref="PageLoss.Detail"/> is never text for the user: the
/// interface words each loss by its <see cref="PageLoss.Kind"/> (in both languages) and uses the detail as a key
/// (<see cref="PageLossDetail"/>).
/// </summary>
/// <remarks>
/// <para>
/// The index space of <see cref="PageLoss.Pages"/>, which differs by kind (all 0-based): for
/// <see cref="BookmarkToDeletedPage"/> and <see cref="OrphanFormFields"/> they are pages of the <em>open document</em> (before
/// the plan, the pages that are deleted); for <see cref="DuplicatedFormFields"/>, <see cref="FormFieldNameCollision"/> and
/// <see cref="InternalLinkLost"/> they are <em>positions in the result</em>; the kinds of a whole file
/// (<see cref="ForeignOutlineDropped"/>, <see cref="SignatureInvalidated"/>, <see cref="EncryptionNotPreserved"/>,
/// <see cref="PageLabelsShifted"/>) have none.
/// </para>
/// <para>
/// A producer of PDFium in the information dictionary after an import is deliberately not a loss (ruling of milestone 13).
/// </para>
/// </remarks>
public enum PageLossKind
{
    /// <summary>The bookmarks of an inserted file are not taken over (only those of the primary file stay). Detail: <c>source:N</c>.</summary>
    ForeignOutlineDropped,

    /// <summary>A bookmark of the primary file points to a page that is deleted; it stays as an entry without a target. Pages: the deleted pages of the open document some bookmark points to.</summary>
    BookmarkToDeletedPage,

    /// <summary>
    /// Pages with form fields are copied: their fields appear on more than one page (one field with several widgets, the value
    /// is shared), or arrive with inserted pages without being entered into the form of the file, so another program may not
    /// let the user fill them. Detail: <see cref="PageLossDetail.Duplicated"/> (a page appears more than once) or
    /// <see cref="PageLossDetail.Inserted"/> (pages of an inserted file). Pages: positions in the result.
    /// </summary>
    DuplicatedFormFields,

    /// <summary>Form fields of the same name come from different files; the form engine reads them as one field. Pages: positions in the result. Detail: <see cref="PageLossDetail.Unchecked"/> when there were too many names to compare, else <see langword="null"/>.</summary>
    FormFieldNameCollision,

    /// <summary>The digital signature of the primary file is shown as invalid afterwards (the file is written anew). Detail: <c>docmdp:N</c> (the most restrictive change level the signatures declare) or <see langword="null"/>. This warns, it does not block.</summary>
    SignatureInvalidated,

    /// <summary>
    /// An inserted, encrypted file brings its pages into a result that carries the protection of the primary file only.
    /// Detail: <c>source:N</c>. <see cref="PageLoss.Blocks"/> is <see langword="true"/> when the primary file is not encrypted
    /// (the content of the protected file would be written unprotected); with an encrypted primary file it only warns (the
    /// pages are protected by the primary file's password and permissions, not by their own).
    /// </summary>
    EncryptionNotPreserved,

    /// <summary>
    /// An internal link (a go-to action) is left without a target: the target page is deleted (Detail
    /// <see cref="PageLossDetail.DeletedPage"/>, Pages: the pages that carry the link), or the link sits on a copied page (Detail
    /// <see cref="PageLossDetail.CopiedPage"/>; PDFium keeps the action but not its destination). Pages: positions in the result.
    /// </summary>
    InternalLinkLost,

    /// <summary>
    /// Page labels (the numbering shown to the reader, such as "iv" or "A-3") are a list by position and do not follow the
    /// pages: with Detail <see cref="PageLossDetail.Shifted"/> the primary file has labels and the plan is not position
    /// preserving, so labels now name other pages (or none); with Detail <c>source:N</c> an inserted file has labels, which are
    /// not carried over.
    /// </summary>
    PageLabelsShifted,

    /// <summary>
    /// Form fields whose pages are all deleted stay in the form of the file as entries without a page (the name, and the value
    /// the field had, remain in the file). Pages: the deleted pages of the open document that carry such fields.
    /// </summary>
    OrphanFormFields,
}

/// <summary>The keys of <see cref="PageLoss.Detail"/> (English, never document content).</summary>
public static class PageLossDetail
{
    /// <summary>A page with form fields appears more than once in the result.</summary>
    public const string Duplicated = "duplicated";

    /// <summary>Pages with form fields come from an inserted file.</summary>
    public const string Inserted = "inserted";

    /// <summary>There were too many field names to compare them.</summary>
    public const string Unchecked = "unchecked";

    /// <summary>The target page of an internal link is deleted.</summary>
    public const string DeletedPage = "deleted-page";

    /// <summary>An internal link sits on a copied page.</summary>
    public const string CopiedPage = "copied-page";

    /// <summary>The page labels of the primary file name other pages afterwards.</summary>
    public const string Shifted = "shifted";

    /// <summary>Names an inserted file: <c>source:N</c>.</summary>
    public static string Source(int id) => string.Create(CultureInfo.InvariantCulture, $"source:{id}");

    /// <summary>Names the change level of a signature: <c>docmdp:N</c>.</summary>
    public static string DocMdp(int level) => string.Create(CultureInfo.InvariantCulture, $"docmdp:{level}");

    /// <summary>Orders details so that <c>source:2</c> comes before <c>source:10</c>; <see langword="null"/> first.</summary>
    public static int Compare(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return a is null ? (b is null ? 0 : -1) : 1;
        }

        var colon = a.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon == b.IndexOf(':', StringComparison.Ordinal)
            && a.AsSpan(0, colon).SequenceEqual(b.AsSpan(0, colon))
            && int.TryParse(a.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var numberA)
            && int.TryParse(b.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var numberB))
        {
            return numberA.CompareTo(numberB);
        }

        return string.CompareOrdinal(a, b);
    }
}

/// <summary>One loss, coupling or invalidation of a page plan.</summary>
/// <param name="Kind">What kind of loss; the interface chooses the words.</param>
/// <param name="Detail">A short English key that refines the kind (see <see cref="PageLossDetail"/>), or <see langword="null"/>. Never document content.</param>
/// <param name="Pages">The 0-based pages concerned (the index space depends on the kind, see <see cref="PageLossKind"/>), or <see langword="null"/> for a loss of the whole file.</param>
/// <param name="Blocks">Whether applying the plan must be refused because of this loss (it is more than a warning). Every apply flow (the page panel, the CLI) refuses with the reason; today only an inserted, encrypted file into an unencrypted primary file blocks.</param>
public sealed record PageLoss(PageLossKind Kind, string? Detail, IReadOnlyList<int>? Pages, bool Blocks = false);

/// <summary>Everything the user must be told before a page plan is applied. An empty list means nothing is lost.</summary>
/// <param name="Losses">The losses, in the order of <see cref="PageLossKind"/>.</param>
public sealed record PageEditReport(IReadOnlyList<PageLoss> Losses)
{
    /// <summary>
    /// Gets whether the plan must not be applied: some loss <see cref="PageLoss.Blocks"/>. The apply flows (page panel, CLI)
    /// refuse and give the reason; nothing is written, and the plan stays.
    /// </summary>
    public bool IsRefused => Losses.Any(static loss => loss.Blocks);
}
