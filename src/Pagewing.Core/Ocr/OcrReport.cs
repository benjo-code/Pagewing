namespace Pagewing.Core.Ocr;

/// <summary>Why a page was not recognized.</summary>
public enum OcrSkipReason
{
    /// <summary>The page has text of its own (<see cref="OcrPlanner.TextThreshold"/> characters or more).</summary>
    HasText,

    /// <summary>Pagewing's own layer is already on the page.</summary>
    AlreadyRecognized,

    /// <summary>The page is so large that even the lowest resolution does not fit the pixel limit, or it has more words or characters than are handled.</summary>
    TooLarge,

    /// <summary>The resolution would be below <see cref="OcrResolution.MinDpi"/>.</summary>
    ResolutionTooLow,

    /// <summary>The page took longer than the time limit.</summary>
    Timeout,

    /// <summary>The page could not be rendered or recognized.</summary>
    Failed,

    /// <summary>Every word has a character the font of the layer lacks, so nothing could be written.</summary>
    NoSupportedCharacters,
}

/// <summary>A page that was not recognized, and why.</summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="Reason">Why.</param>
/// <param name="Detail">More for the reason (the resolution a page would have had), or <see langword="null"/>.</param>
public sealed record OcrSkippedPage(int Page, OcrSkipReason Reason, string? Detail = null);

/// <summary>What the engine found on one page.</summary>
/// <param name="Page">The 0-based page.</param>
/// <param name="Words">The words in reading order, in points of the displayed page.</param>
/// <param name="Dpi">The resolution the page was rendered at.</param>
/// <param name="Lowered">Whether the pixel limit lowered it.</param>
/// <param name="MeanConfidence">The mean confidence of the words (0 to 100), 0 without words.</param>
public sealed record OcrPageResult(int Page, IReadOnlyList<OcrPageWord> Words, int Dpi, bool Lowered, double MeanConfidence);

/// <summary>
/// What a run did, for the report card and the command line. It never claims accuracy: it counts pages and words, gives the engine's own
/// confidence, and lists every page that was not recognized with the reason.
/// </summary>
public sealed record OcrReport
{
    /// <summary>The mean confidence below which a page is listed as possibly turned or hard to read.</summary>
    public const double LowConfidenceThreshold = 60;

    /// <summary>Gets the number of pages that were recognized (pages without words included).</summary>
    public int PagesRecognized { get; init; }

    /// <summary>Gets the number of words found.</summary>
    public int Words { get; init; }

    /// <summary>Gets the mean confidence of all words (0 to 100), 0 without words.</summary>
    public double MeanConfidence { get; init; }

    /// <summary>Gets the pages that were not recognized, by page.</summary>
    public IReadOnlyList<OcrSkippedPage> Skipped { get; init; } = [];

    /// <summary>Gets the recognized pages with a mean confidence below <see cref="LowConfidenceThreshold"/>, by page.</summary>
    public IReadOnlyList<int> LowConfidencePages { get; init; } = [];

    /// <summary>Gets the recognized pages that had no word at all (blank, or nothing the engine could read), by page.</summary>
    public IReadOnlyList<int> PagesWithoutWords { get; init; } = [];

    /// <summary>Gets the recognized pages that had a little text of their own (a page number), by page; that text stays untouched.</summary>
    public IReadOnlyList<int> LittleTextPages { get; init; } = [];

    /// <summary>Gets the recognized pages whose resolution the pixel limit lowered, by page.</summary>
    public IReadOnlyList<int> LoweredPages { get; init; } = [];

    /// <summary>Gets how many words were left out of the layer because of a character its font lacks.</summary>
    public int WordsLeftOut { get; init; }

    /// <summary>Builds the report from the pages of a run.</summary>
    /// <param name="pages">The recognized pages.</param>
    /// <param name="skipped">The pages that were not.</param>
    /// <param name="littleText">The 0-based pages that had a little text of their own.</param>
    public static OcrReport Build(IReadOnlyList<OcrPageResult> pages, IReadOnlyList<OcrSkippedPage> skipped, IReadOnlyList<int> littleText)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(skipped);
        ArgumentNullException.ThrowIfNull(littleText);
        var ordered = pages.OrderBy(static p => p.Page).ToList();
        var words = ordered.Sum(static p => p.Words.Count);
        var recognizedPages = ordered.Select(static p => p.Page).ToHashSet();
        return new OcrReport
        {
            PagesRecognized = ordered.Count,
            Words = words,
            MeanConfidence = words == 0 ? 0 : ordered.Sum(static p => p.Words.Sum(static w => (double)w.Confidence)) / words,
            Skipped = [.. skipped.OrderBy(static s => s.Page)],
            LowConfidencePages = [.. ordered.Where(static p => p.Words.Count > 0 && p.MeanConfidence < LowConfidenceThreshold).Select(static p => p.Page)],
            PagesWithoutWords = [.. ordered.Where(static p => p.Words.Count == 0).Select(static p => p.Page)],
            LittleTextPages = [.. littleText.Where(recognizedPages.Contains).Order()],
            LoweredPages = [.. ordered.Where(static p => p.Lowered).Select(static p => p.Page)],
        };
    }
}
