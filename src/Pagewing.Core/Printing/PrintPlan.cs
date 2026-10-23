using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;

namespace Pagewing.Core.Printing;

/// <summary>Which pages of a document are printed, and in which order.</summary>
public sealed class PrintPlan
{
    /// <summary>The most pages one job may list (pages times copies); more is refused instead of allocating.</summary>
    public const int MaxSequenceLength = 1_000_000;

    private PrintPlan(IReadOnlyList<int> pages, IReadOnlyList<int> sequence, string cupsPageRanges, bool isWholeDocument)
    {
        Pages = pages;
        Sequence = sequence;
        CupsPageRanges = cupsPageRanges;
        IsWholeDocument = isWholeDocument;
    }

    /// <summary>Gets the pages of one copy, 0-based, ascending and without duplicates.</summary>
    public IReadOnlyList<int> Pages { get; }

    /// <summary>Gets every page to print in print order, 0-based, with copies and collation applied.</summary>
    public IReadOnlyList<int> Sequence { get; }

    /// <summary>Gets the pages of one copy as CUPS <c>page-ranges</c> (1-based, such as <c>1-3,5</c>).</summary>
    public string CupsPageRanges { get; }

    /// <summary>Gets whether one copy is the whole document (CUPS then needs no <c>page-ranges</c>).</summary>
    public bool IsWholeDocument { get; }

    /// <summary>Gets the number of pages in <see cref="Sequence"/>.</summary>
    public int PageCountToPrint => Sequence.Count;

    /// <summary>Plans a job.</summary>
    /// <param name="pageCount">The pages of the document; at least 1.</param>
    /// <param name="currentPage">The page the user is looking at, 0-based; used for <see cref="PrintPageChoice.CurrentPage"/>.</param>
    /// <param name="settings">What the user chose.</param>
    /// <exception cref="ArgumentOutOfRangeException">The document has no pages, <paramref name="currentPage"/> is not one of them, or the range names a page beyond the last (the actual value is that page, 1-based).</exception>
    /// <exception cref="FormatException">The range is not a valid page selection.</exception>
    /// <exception cref="InvalidOperationException">The job would list more than <see cref="MaxSequenceLength"/> pages.</exception>
    public static PrintPlan Create(int pageCount, int currentPage, PrintSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        settings = settings.Normalized();

        IReadOnlyList<int> pages;
        switch (settings.Pages)
        {
            case PrintPageChoice.CurrentPage:
                ArgumentOutOfRangeException.ThrowIfNegative(currentPage);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(currentPage, pageCount);
                pages = [currentPage];
                break;
            case PrintPageChoice.Range:
                pages = PageRange.Parse(settings.Range, pageCount);
                break;
            default:
                pages = Enumerable.Range(0, pageCount).ToArray();
                break;
        }

        if ((long)pages.Count * settings.Copies > MaxSequenceLength)
        {
            throw new InvalidOperationException("The job would print more pages than Pagewing allows in one job.");
        }

        var sequence = new List<int>(pages.Count * settings.Copies);
        if (settings.Collate)
        {
            for (var copy = 0; copy < settings.Copies; copy++)
            {
                sequence.AddRange(pages);
            }
        }
        else
        {
            foreach (var page in pages)
            {
                for (var copy = 0; copy < settings.Copies; copy++)
                {
                    sequence.Add(page);
                }
            }
        }

        return new PrintPlan(pages, sequence, FormatRanges(pages), pages.Count == pageCount);
    }

    private static string FormatRanges(IReadOnlyList<int> pages)
    {
        var text = new StringBuilder();
        var i = 0;
        while (i < pages.Count)
        {
            var start = pages[i];
            var end = start;
            while (i + 1 < pages.Count && pages[i + 1] == end + 1)
            {
                end = pages[++i];
            }

            if (text.Length > 0)
            {
                text.Append(',');
            }

            text.Append((start + 1).ToString(CultureInfo.InvariantCulture));
            if (end > start)
            {
                text.Append('-').Append((end + 1).ToString(CultureInfo.InvariantCulture));
            }

            i++;
        }

        return text.ToString();
    }
}
