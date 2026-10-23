using System.Globalization;

namespace Pagewing.Core.Pdf;

/// <summary>Parses page selections such as <c>3</c>, <c>2-5</c>, <c>1,3,5-7</c> or <c>all</c> (1-based).</summary>
public static class PageRange
{
    /// <summary>
    /// Returns the selected pages as 0-based indexes, ascending and without duplicates.
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid selection (empty, <c>0</c>, <c>5-3</c>, <c>abc</c>, <c>1-</c>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page is beyond <paramref name="pageCount"/>; <see cref="ArgumentOutOfRangeException.ActualValue"/> is that page (1-based).</exception>
    public static IReadOnlyList<int> Parse(string text, int pageCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageCount);
        var segments = ParseSegments(text);
        if (segments is null)
        {
            return Enumerable.Range(0, pageCount).ToArray();
        }

        // Check all bounds first, so an absurd range such as 1-2000000000 never allocates.
        foreach (var (_, end) in segments)
        {
            if (end > pageCount)
            {
                throw new ArgumentOutOfRangeException(nameof(text), end, $"Page {end.ToString(CultureInfo.InvariantCulture)} is out of range.");
            }
        }

        var selected = new bool[pageCount];
        foreach (var (start, end) in segments)
        {
            Array.Fill(selected, true, start - 1, end - start + 1);
        }

        var pages = new List<int>();
        for (var i = 0; i < selected.Length; i++)
        {
            if (selected[i])
            {
                pages.Add(i);
            }
        }

        return pages;
    }

    /// <summary>Checks the syntax only.</summary>
    /// <exception cref="FormatException">The text is not a valid selection.</exception>
    public static void Validate(string text) => ParseSegments(text);

    /// <summary>Returns the 1-based inclusive segments, or <see langword="null"/> for <c>all</c>.</summary>
    private static List<(int Start, int End)>? ParseSegments(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            throw new FormatException("The page selection is empty.");
        }

        if (trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var segments = new List<(int, int)>();
        foreach (var part in trimmed.Split(','))
        {
            var bounds = part.Split('-');
            switch (bounds.Length)
            {
                case 1:
                    var page = ParsePage(bounds[0], text);
                    segments.Add((page, page));
                    break;
                case 2:
                    var start = ParsePage(bounds[0], text);
                    var end = ParsePage(bounds[1], text);
                    if (start > end)
                    {
                        throw new FormatException($"'{part.Trim()}' is not a valid page range: the first page is after the last.");
                    }

                    segments.Add((start, end));
                    break;
                default:
                    throw new FormatException($"'{part.Trim()}' is not a valid page range.");
            }
        }

        return segments;
    }

    /// <summary>Parses a positive decimal number; a number beyond the int range counts as <see cref="int.MaxValue"/> (out of range of any document).</summary>
    private static int ParsePage(string text, string whole)
    {
        var digits = text.Trim();
        if (digits.Length == 0 || !digits.All(char.IsAsciiDigit))
        {
            throw new FormatException($"'{whole.Trim()}' is not a valid page selection. Use for example 3, 2-5, 1,3,5-7 or all.");
        }

        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var page))
        {
            return int.MaxValue;
        }

        return page >= 1
            ? page
            : throw new FormatException("Pages are numbered from 1.");
    }
}
