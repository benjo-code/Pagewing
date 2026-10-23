using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Cli.Commands;

/// <summary>
/// <c>pagewing search</c>: one line per hit, <c>&lt;page&gt;\t&lt;first char&gt;\t&lt;before&gt;[&lt;match&gt;]&lt;after&gt;</c>.
/// The selected pages are searched in ascending order and every hit is printed (no hit limit, no wrap-around), so
/// the output is complete and ordered by page and character index.
/// </summary>
internal static class SearchCommand
{
    public static async Task RunAsync(PdfDocument document, CliOptions options, TextWriter output, CancellationToken ct)
    {
        var query = options.Query ?? throw new UsageException("The 'search' command needs a non-empty query.");
        var searchOptions = new SearchOptions(options.MatchCase, options.WholeWord);
        foreach (var page in CliApp.SelectPages(options.Pages, document.PageCount))
        {
            var hits = await document.SearchPageAsync(page, query, searchOptions, ct).ConfigureAwait(false);
            foreach (var hit in hits)
            {
                await output.WriteAsync($"{FormatLine(hit)}\n".AsMemory(), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Formats a hit as <c>&lt;page, 1-based&gt;\t&lt;first char&gt;\t&lt;before&gt;[&lt;match&gt;]&lt;after&gt;</c>. Control
    /// characters (including TAB, CR and LF) and Unicode line and paragraph separators in the snippet become a space,
    /// so a line always has exactly two tabs and no line break.
    /// </summary>
    internal static string FormatLine(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var snippet = hit.Snippet;
        var builder = new StringBuilder(snippet.Before.Length + snippet.Match.Length + snippet.After.Length + 24);
        builder.Append((hit.PageIndex + 1).ToString(CultureInfo.InvariantCulture)).Append('\t')
            .Append(hit.FirstChar.ToString(CultureInfo.InvariantCulture)).Append('\t');
        AppendSanitized(builder, snippet.Before);
        builder.Append('[');
        AppendSanitized(builder, snippet.Match);
        builder.Append(']');
        AppendSanitized(builder, snippet.After);
        return builder.ToString();
    }

    private static void AppendSanitized(StringBuilder builder, string text)
    {
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            builder.Append(
                char.IsControl(c) || category is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c);
        }
    }
}
