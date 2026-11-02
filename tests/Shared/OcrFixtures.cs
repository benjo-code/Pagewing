using System.Globalization;

namespace Pagewing.TestSupport;

/// <summary>A word of a scan fixture and its ink box in points from the top left of the displayed page (page is 0-based).</summary>
internal sealed record KnownWord(string File, int Page, string Text, double Left, double Top, double Right, double Bottom);

/// <summary>
/// The scan fixtures of milestone 15 (<c>ocr-scan.pdf</c>, <c>ocr-scan-rotated.pdf</c>, <c>ocr-mixed.pdf</c>, <c>ocr-hostile.pdf</c>) and the words
/// they show, as <c>tools/make-testpdfs.cs</c> drew them (<c>testdata/pdfs/ocr-words.tsv</c>: file, page from 1, word, left, top, right, bottom).
/// </summary>
internal static class OcrFixtures
{
    private static readonly Lazy<IReadOnlyList<KnownWord>> Words = new(Read);

    /// <summary>Gets every known word of every fixture.</summary>
    public static IReadOnlyList<KnownWord> KnownWords => Words.Value;

    /// <summary>The known words of one page (0-based) of a fixture, in reading order.</summary>
    public static IReadOnlyList<KnownWord> WordsOf(string file, int page) =>
        [.. KnownWords.Where(word => word.File == file && word.Page == page)];

    private static IReadOnlyList<KnownWord> Read() =>
    [
        .. File.ReadAllLines(Path.Combine(TestData.Root, "testdata", "pdfs", "ocr-words.tsv"))
            .Where(static line => line.Length > 0)
            .Select(static line => line.Split('\t'))
            .Select(static parts => new KnownWord(
                parts[0],
                int.Parse(parts[1], CultureInfo.InvariantCulture) - 1,
                parts[2],
                double.Parse(parts[3], CultureInfo.InvariantCulture),
                double.Parse(parts[4], CultureInfo.InvariantCulture),
                double.Parse(parts[5], CultureInfo.InvariantCulture),
                double.Parse(parts[6], CultureInfo.InvariantCulture))),
    ];
}
