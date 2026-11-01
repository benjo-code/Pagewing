using System.Globalization;
using System.Text;
using Pagewing.Core.Pages;

namespace Pagewing.Cli.Commands;

/// <summary>
/// Words the losses of a page plan for the command line (English, one line each, stable wording for scripts). The
/// interface of the app words the same losses itself; both go by <see cref="PageLossKind"/>. No text calls a signature
/// "valid" or anything "legally binding".
/// </summary>
internal static class PageLossText
{
    /// <summary>Writes every loss of <paramref name="report"/> as a <c>warning:</c> line.</summary>
    /// <param name="report">The report.</param>
    /// <param name="error">Where the lines go.</param>
    /// <param name="fileName">Names an inserted file by its number (<c>merge</c>), or <see langword="null"/> to say the number.</param>
    /// <param name="prefix">Text after <c>warning: </c> and before the sentence, such as the name of a part (<c>split</c>), or <see langword="null"/>.</param>
    public static void WriteWarnings(PageEditReport report, TextWriter error, Func<int, string>? fileName = null, string? prefix = null)
    {
        foreach (var loss in report.Losses)
        {
            error.WriteLine($"warning: {prefix}{Describe(loss, fileName)}");
        }
    }

    /// <summary>Gets the reason a report that <see cref="PageEditReport.IsRefused"/> is refused.</summary>
    public static string RefusalOf(PageEditReport report, Func<int, string>? fileName = null) =>
        string.Join(" ", report.Losses.Where(static loss => loss.Blocks).Select(loss => Describe(loss, fileName)));

    /// <summary>Words one loss as a single line.</summary>
    /// <param name="loss">The loss.</param>
    /// <param name="fileName">Names an inserted file by its number (<c>merge</c>), or <see langword="null"/> to say the number.</param>
    public static string Describe(PageLoss loss, Func<int, string>? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(loss);
        var pages = loss.Pages is { Count: > 0 } list ? list : null;
        var inResult = pages is null ? string.Empty : $" ({PagesOf(pages, "page", "pages")} of the output file)";
        var original = pages is null ? string.Empty : $" ({PagesOf(pages, "original page", "original pages")})";
        var source = SourceLabel(loss.Detail, fileName);
        return loss.Kind switch
        {
            PageLossKind.SignatureInvalidated =>
                "The file is digitally signed. The signature is shown as invalid after the file is changed, because it is written anew.",
            PageLossKind.PageLabelsShifted when source is not null =>
                $"The page labels of inserted file {source} are not taken over.",
            PageLossKind.PageLabelsShifted =>
                "The file has page labels (such as \"iv\" or \"A-3\"). They are a list by position and do not follow the pages, so they now name other pages.",
            PageLossKind.ForeignOutlineDropped =>
                $"The bookmarks of inserted file {source ?? "?"} are not taken over.",
            PageLossKind.BookmarkToDeletedPage =>
                $"Bookmarks that point to deleted pages stay as entries without a target{original}.",
            PageLossKind.DuplicatedFormFields when loss.Detail == PageLossDetail.Inserted =>
                $"Pages of inserted files have form fields. They are not entered into the form of the file, so another program may not let you fill them{inResult}.",
            PageLossKind.DuplicatedFormFields =>
                $"Copied pages have form fields. A field then appears on more than one page and its value is shared{inResult}.",
            PageLossKind.FormFieldNameCollision when loss.Detail == PageLossDetail.Unchecked =>
                "There were too many form field names to check whether fields of the same name come from different files.",
            PageLossKind.FormFieldNameCollision =>
                $"Form fields of the same name come from different files. They are read as one field{inResult}.",
            PageLossKind.EncryptionNotPreserved when loss.Blocks =>
                $"Inserted file {source ?? "?"} is encrypted, but the output file would not be. The content of a protected file would be written unprotected.",
            PageLossKind.EncryptionNotPreserved =>
                $"Inserted file {source ?? "?"} is encrypted. Its pages are protected by the password and permissions of the main file, not by their own.",
            PageLossKind.InternalLinkLost when loss.Detail == PageLossDetail.CopiedPage =>
                $"Internal links on copied pages lose their destination{inResult}.",
            PageLossKind.InternalLinkLost =>
                $"Internal links lose their target because the target page is deleted{inResult}.",
            PageLossKind.OrphanFormFields =>
                $"Form fields on deleted pages stay in the form of the file without a page{original}.",
            _ => $"The page changes lose something ({loss.Kind}).",
        };
    }

    /// <summary>Formats 0-based pages as 1-based text, consecutive pages as a range: <c>1-3, 6, 8-9</c>.</summary>
    public static string FormatPages(IReadOnlyList<int> zeroBasedPages)
    {
        ArgumentNullException.ThrowIfNull(zeroBasedPages);
        var sorted = zeroBasedPages.Distinct().Order().ToArray();
        var text = new StringBuilder();
        for (var i = 0; i < sorted.Length;)
        {
            var end = i;
            while (end + 1 < sorted.Length && sorted[end + 1] == sorted[end] + 1)
            {
                end++;
            }

            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append((sorted[i] + 1).ToString(CultureInfo.InvariantCulture));
            if (end > i)
            {
                text.Append('-').Append((sorted[end] + 1).ToString(CultureInfo.InvariantCulture));
            }

            i = end + 1;
        }

        return text.ToString();
    }

    private static string PagesOf(IReadOnlyList<int> pages, string one, string many) =>
        $"{(pages.Distinct().Count() == 1 ? one : many)} {FormatPages(pages)}";

    // The number of an inserted file, or its name in quotes when the command can tell it.
    private static string? SourceLabel(string? detail, Func<int, string>? fileName)
    {
        if (detail is null || !detail.StartsWith("source:", StringComparison.Ordinal))
        {
            return null;
        }

        var number = detail["source:".Length..];
        return fileName is not null && int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            ? $"'{fileName(index)}'"
            : number;
    }
}
