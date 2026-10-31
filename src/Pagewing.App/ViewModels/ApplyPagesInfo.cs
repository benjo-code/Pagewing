using System.Text;
using Pagewing.App.Resources;
using Pagewing.Core.Pages;

namespace Pagewing.App.ViewModels;

/// <summary>
/// What the card of "Apply" says about a page plan (milestone 13, spec 5.3 and 5.5): the summary line ("12 → 10 pages, 3 turned,
/// 1 file inserted") and one plain sentence for every <see cref="PageLoss"/> of the report, in the language of the interface,
/// with pages as page numbers (1-based). Nothing the report lists is left out, and no sentence calls a signature valid.
/// </summary>
public sealed class ApplyPagesInfo
{
    // More groups of pages than this end in an ellipsis: the sentence stays readable on a card.
    private const int MaxPageGroups = 10;

    private ApplyPagesInfo(string summary, IReadOnlyList<string> lines)
    {
        Summary = summary;
        Lines = lines;
    }

    /// <summary>Gets the summary line: the count of pages before and after, and what is turned, blank, duplicated or inserted.</summary>
    public string Summary { get; }

    /// <summary>Gets one sentence per loss of the report, in the order of the report.</summary>
    public IReadOnlyList<string> Lines { get; }

    /// <summary>Gets whether the report lists anything.</summary>
    public bool HasLosses => Lines.Count > 0;

    /// <summary>Makes the text for <paramref name="plan"/> and what applying it loses (<paramref name="report"/>).</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="report">What applying it loses.</param>
    /// <param name="fileNames">The names of the inserted files by the number of their source, so that a sentence names the file the user chose; without a name the number stands.</param>
    public static ApplyPagesInfo Create(PageEditPlan plan, PageEditReport report, IReadOnlyDictionary<int, string>? fileNames = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(report);
        return new ApplyPagesInfo(SummaryOf(plan), [.. report.Losses.Select(loss => LineOf(loss, fileNames))]);
    }

    /// <summary>The sentence for one loss, by its kind and detail; the pages it names are page numbers.</summary>
    internal static string LineOf(PageLoss loss, IReadOnlyDictionary<int, string>? fileNames = null)
    {
        ArgumentNullException.ThrowIfNull(loss);
        var detail = loss.Detail ?? string.Empty;
        return loss.Kind switch
        {
            PageLossKind.ForeignOutlineDropped => Format(Strings.ApplyLossForeignOutline, SourceOf(detail, fileNames)),
            PageLossKind.BookmarkToDeletedPage => Format(Strings.ApplyLossBookmarkToDeletedPage, PagesText(loss.Pages, original: true)),
            PageLossKind.DuplicatedFormFields when detail == PageLossDetail.Inserted => Format(Strings.ApplyLossInsertedFields, PagesText(loss.Pages)),
            PageLossKind.DuplicatedFormFields => Format(Strings.ApplyLossDuplicatedFields, PagesText(loss.Pages)),
            PageLossKind.FormFieldNameCollision when detail == PageLossDetail.Unchecked => Strings.ApplyLossFieldCollisionUnchecked,
            PageLossKind.FormFieldNameCollision => Format(Strings.ApplyLossFieldCollision, PagesText(loss.Pages)),
            PageLossKind.SignatureInvalidated => Strings.ApplyLossSignature,
            PageLossKind.EncryptionNotPreserved => Format(Strings.ApplyLossEncryption, SourceOf(detail, fileNames)),
            PageLossKind.InternalLinkLost when detail == PageLossDetail.CopiedPage => Format(Strings.ApplyLossLinkCopied, PagesText(loss.Pages)),
            PageLossKind.InternalLinkLost => Format(Strings.ApplyLossLinkDeleted, PagesText(loss.Pages)),
            PageLossKind.PageLabelsShifted when detail.StartsWith("source:", StringComparison.Ordinal) => Format(Strings.ApplyLossLabelsSource, SourceOf(detail, fileNames)),
            PageLossKind.PageLabelsShifted => Strings.ApplyLossLabelsShifted,
            PageLossKind.OrphanFormFields => Format(Strings.ApplyLossOrphanFields, PagesText(loss.Pages, original: true)),
            _ => Strings.ApplyLossOther,
        };
    }

    /// <summary>
    /// "page 3 of the new file" or "pages 3, 5–7 of the new file" for 0-based <paramref name="pages"/> (empty when there are
    /// none). The pages of a loss are in one of two index spaces (<see cref="PageLossKind"/>): pages of the <em>original</em>
    /// document (a bookmark's deleted target, orphaned form fields; "originally page 3") or positions in the <em>new</em> file
    /// (everything else); the words say which, so a deleted page is never mistaken for a page that is still there.
    /// </summary>
    internal static string PagesText(IReadOnlyList<int>? pages, bool original = false)
    {
        if (pages is null || pages.Count == 0)
        {
            return string.Empty;
        }

        var sorted = pages.Distinct().Order().ToArray();
        var text = new StringBuilder();
        var groups = 0;
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            if (groups == MaxPageGroups)
            {
                text.Append(", …");
                break;
            }

            if (groups > 0)
            {
                text.Append(", ");
            }

            text.Append((sorted[i] + 1).ToString(Strings.Culture));
            if (end > i)
            {
                text.Append(end == i + 1 ? ", " : "–").Append((sorted[end] + 1).ToString(Strings.Culture));
            }

            groups++;
            i = end + 1;
        }

        var format = original
            ? (sorted.Length == 1 ? Strings.PagesOriginalOne : Strings.PagesOriginalMany)
            : (sorted.Length == 1 ? Strings.PagesNewOne : Strings.PagesNewMany);
        return Format(format, text.ToString());
    }

    private static string SummaryOf(PageEditPlan plan)
    {
        var slots = plan.Slots;
        var rotated = slots.Count(slot => slot.ExtraRotation != 0);
        var blank = slots.Count(slot => slot.Blank is not null);
        var distinct = new HashSet<(int Source, int Index)>();
        var files = new HashSet<int>();
        foreach (var slot in slots.Where(slot => slot.Blank is null))
        {
            distinct.Add((slot.Source.Value, slot.SourceIndex));
            if (slot.Source.Value != 0)
            {
                files.Add(slot.Source.Value);
            }
        }

        var copies = slots.Count - blank - distinct.Count;
        var now = slots.Count;
        var counter = now == plan.PrimaryPageCount
            ? Format(now == 1 ? Strings.PageCounterSameOne : Strings.PageCounterSame, Number(now))
            : Format(now == 1 ? Strings.PageCounterChangeOne : Strings.PageCounterChange, Number(plan.PrimaryPageCount), Number(now));
        var parts = new List<string> { counter };
        if (rotated > 0)
        {
            parts.Add(Format(Strings.ApplySummaryRotated, Number(rotated)));
        }

        if (blank > 0)
        {
            parts.Add(Format(blank == 1 ? Strings.ApplySummaryBlankOne : Strings.ApplySummaryBlank, Number(blank)));
        }

        if (copies > 0)
        {
            parts.Add(Format(Strings.ApplySummaryCopies, Number(copies)));
        }

        if (files.Count > 0)
        {
            parts.Add(Format(files.Count == 1 ? Strings.ApplySummaryFilesOne : Strings.ApplySummaryFiles, Number(files.Count)));
        }

        return string.Join(", ", parts);
    }

    // The number of the source in the detail ("source:2"), or the name of its file when the tab knows it.
    private static string SourceOf(string detail, IReadOnlyDictionary<int, string>? fileNames)
    {
        var colon = detail.IndexOf(':', StringComparison.Ordinal);
        var number = colon >= 0 ? detail[(colon + 1)..] : detail;
        return fileNames is not null && int.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && fileNames.TryGetValue(id, out var name)
            ? name
            : number;
    }

    private static string Number(int value) => value.ToString("N0", Strings.Culture);

    private static string Format(string format, params string[] arguments) =>
        string.Format(Strings.Culture, CompositeFormat.Parse(format), [.. arguments.Cast<object>()]);
}
