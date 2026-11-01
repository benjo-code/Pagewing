using System.Globalization;
using System.Text;
using Pagewing.Core.Pdf;
using Pagewing.Core.Text;

namespace Pagewing.Core.Redaction;

/// <summary>
/// Checks the written result of a redaction (milestone 14): it reads the file that was written, opened again, never the working
/// copy, on independent paths and reports what it found and what it could not check.
/// <list type="bullet">
/// <item>Text: every removed text occurs in the page text of the result at most as often as it did before minus the places that were
/// removed (with <see cref="RemovedText.Everywhere"/>: never), and no character of the result lies in a marked area.</item>
/// <item>Raw bytes: the file is searched for each removed text (see <see cref="RawContentScanner"/>: string syntax decoded, streams
/// unpacked, old revisions and metadata included). The text may occur there at most as often as the page text shows it (with
/// <see cref="RemovedText.Everywhere"/>: never), so a legitimate occurrence that was not marked does not fail the check, and a copy
/// in an attachment, in metadata or in an old revision does.</item>
/// <item>Pixels: the image pixels under every marked area are the colour of the area.</item>
/// </list>
/// The report never promises more than this: <see cref="RedactionCheck.NotChecked"/> always says what stays outside it.
/// </summary>
public static class RedactionVerifier
{
    /// <summary>The most distinct removed texts that are compared; further ones are listed as not checked.</summary>
    public const int MaxCheckedTexts = 2_000;

    /// <summary>The most areas whose image pixels are read back; further ones are listed as not checked.</summary>
    public const int MaxPixelAreas = 2_000;

    /// <summary>
    /// The overlap, in points, that a character of the result may have with a marked area without counting as inside: the page text
    /// rounds boxes to 1/16 point, and the engine removes a character as soon as its box touches the area.
    /// </summary>
    public const double PositionTolerance = 0.1;

    /// <summary>Checks the result of a redaction.</summary>
    /// <param name="reopened">The written result, opened again.</param>
    /// <param name="file">The bytes of that same file (seekable); it is read, never changed.</param>
    /// <param name="expectation">What was applied and removed.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>The findings (none: the check passed) and what was not checked.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public static Task<RedactionCheck> VerifyAsync(PdfDocument reopened, Stream file, RedactionExpectation expectation, CancellationToken cancellationToken = default) =>
        VerifyAsync(reopened, file, expectation, RawScanLimits.Default, cancellationToken);

    /// <summary>Checks the result of a redaction with other limits for the raw-bytes scan (for tests).</summary>
    internal static async Task<RedactionCheck> VerifyAsync(PdfDocument reopened, Stream file, RedactionExpectation expectation, RawScanLimits limits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reopened);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(expectation);
        ArgumentNullException.ThrowIfNull(expectation.Plan);
        ArgumentNullException.ThrowIfNull(expectation.Removed);
        if (!file.CanSeek || !file.CanRead)
        {
            throw new ArgumentException("The file must be a readable, seekable stream.", nameof(file));
        }

        var findings = new List<RedactionFinding>();
        var notChecked = new NotCheckedLog();
        var plan = expectation.Plan;
        var texts = PrepareTexts(expectation, notChecked, findings);

        await CheckPagesAsync(reopened, plan, texts, findings, cancellationToken).ConfigureAwait(false);
        CheckTextRule(texts, findings);
        await CheckRawAsync(file, texts, limits, findings, notChecked, cancellationToken).ConfigureAwait(false);
        await CheckPixelsAsync(reopened, plan, expectation.Fill, findings, notChecked, cancellationToken).ConfigureAwait(false);

        notChecked.Add(RedactionNotCheckedKind.RawFontMapping, "Raw bytes: text in fonts with their own code mapping (subset fonts) is not readable in the streams; only the page text covers it.");
        notChecked.Add(RedactionNotCheckedKind.RawEncodings, "Raw bytes: only the removed texts are searched, as given and in composed and decomposed form, in Latin-1, Windows-ANSI, PDFDocEncoding, UTF-8 and UTF-16; text in any other encoding, or split by other operators, is not found.");
        notChecked.Add(RedactionNotCheckedKind.RawSplitText, "Raw bytes: a text split across different text objects (BT blocks), text in XMP written as XML entities, and case variants that were not planned are not found.");
        notChecked.Add(RedactionNotCheckedKind.RawImageAttachments, "Raw bytes: image data is searched as bytes only, and an attachment is not looked into (a compressed file inside an attachment is not unpacked).");
        notChecked.Add(RedactionNotCheckedKind.OtherViewer, "Another program may read the file differently; open the result in a second viewer to be sure.");
        return new RedactionCheck(findings, notChecked.Lines, notChecked.Items);
    }

    /// <summary>Cuts a text to <see cref="RedactionLimits.MaxTermLength"/> characters (not inside a surrogate pair), as the check does.</summary>
    internal static string Truncate(string text)
    {
        if (text.Length <= RedactionLimits.MaxTermLength)
        {
            return text;
        }

        var cut = RedactionLimits.MaxTermLength;
        return char.IsHighSurrogate(text[cut - 1]) ? text[..(cut - 1)] : text[..cut];
    }

    private static List<CheckedText> PrepareTexts(RedactionExpectation expectation, NotCheckedLog notChecked, List<RedactionFinding> findings)
    {
        var byText = new Dictionary<string, CheckedText>(StringComparer.Ordinal);
        var blank = 0;
        var unreadable = 0;
        var unverifiable = new SortedDictionary<int, int>();
        var truncated = 0;

        void Add(string? text, bool everywhere, int before, bool isPlace, int page)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                blank++;
                return;
            }

            if (text.Contains('�', StringComparison.Ordinal) || text.Contains('\0', StringComparison.Ordinal))
            {
                unreadable++;
                unverifiable[page] = unverifiable.GetValueOrDefault(page) + 1;
                return;
            }

            if (text.Length > RedactionLimits.MaxTermLength)
            {
                text = Truncate(text);
                truncated++;
            }

            if (!byText.TryGetValue(text, out var entry))
            {
                var needle = TextMatcher.Normalize(text, matchCase: true);
                if (needle.Length == 0)
                {
                    blank++;
                    return;
                }

                entry = new CheckedText(text, needle);
                byText[text] = entry;
            }

            entry.Everywhere |= everywhere;
            entry.Pages.Add(page);
            entry.Before = Math.Max(entry.Before, before);
            if (isPlace)
            {
                entry.Places++;
            }
        }

        foreach (var removed in expectation.Removed)
        {
            ArgumentNullException.ThrowIfNull(removed);
            Add(removed.Text, removed.Everywhere, removed.OccurrencesBefore, isPlace: true, removed.Page);
        }

        foreach (var mark in expectation.Plan.Marks)
        {
            if (mark.Everywhere && mark.Term is not null)
            {
                Add(mark.Term, everywhere: true, before: 0, isPlace: false, mark.Page);
            }
        }

        var texts = byText.Values.OrderByDescending(static t => t.Everywhere).ToList();
        if (blank > 0)
        {
            notChecked.Add(RedactionNotCheckedKind.BlankTexts, Count(blank, "removed text consists of white space only and was not compared", "removed texts consist of white space only and were not compared"), blank);
        }

        foreach (var (page, number) in unverifiable)
        {
            findings.Add(new RedactionFinding(
                RedactionFindingKind.RemovedTextUnverifiable,
                page,
                string.Create(CultureInfo.InvariantCulture, $"{number} removed text(s) of this page contain a character the engine could not read exactly (U+FFFD or NUL), so the text cannot be verified in the result; the real text must be supplied.")));
        }

        if (unreadable > 0)
        {
            notChecked.Add(RedactionNotCheckedKind.UnreadableChars, Count(unreadable, "removed text contains a character the engine could not read exactly (U+FFFD or NUL) and was not compared", "removed texts contain a character the engine could not read exactly (U+FFFD or NUL) and were not compared"), unreadable);
        }

        if (truncated > 0)
        {
            notChecked.Add(RedactionNotCheckedKind.TruncatedTexts, Count(truncated, "removed text is longer than the limit and was compared by its first characters only", "removed texts are longer than the limit and were compared by their first characters only"), truncated);
        }

        if (texts.Count > MaxCheckedTexts)
        {
            notChecked.Add(RedactionNotCheckedKind.FurtherTexts, string.Create(CultureInfo.InvariantCulture, $"{texts.Count - MaxCheckedTexts} further removed texts were not compared (limit {MaxCheckedTexts} distinct texts)."), texts.Count - MaxCheckedTexts, MaxCheckedTexts);
            texts.RemoveRange(MaxCheckedTexts, texts.Count - MaxCheckedTexts);
        }

        for (var i = 0; i < texts.Count; i++)
        {
            texts[i].Index = i;
        }

        return texts;
    }

    private static string Count(int number, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{number} {(number == 1 ? one : many)}.");

    private static async Task CheckPagesAsync(PdfDocument reopened, RedactionPlan plan, List<CheckedText> texts, List<RedactionFinding> findings, CancellationToken cancellationToken)
    {
        var pageCount = reopened.PageCount;
        foreach (var page in plan.Pages.Where(p => p >= pageCount).Order())
        {
            findings.Add(new RedactionFinding(RedactionFindingKind.PageChangedOutsideAreas, page, "The result has fewer pages than the plan expects."));
        }

        var needles = texts.Select(static t => t.Needle).ToArray();
        for (var page = 0; page < pageCount; page++)
        {
            var marked = plan.Pages.Contains(page);
            if (!marked && texts.Count == 0)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            PageText text;
            try
            {
                text = await reopened.GetPageTextAsync(page, PdfPriority.Background, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                findings.Add(new RedactionFinding(RedactionFindingKind.UnreadableStream, page, "The page could not be read for the text check, so it cannot be shown that nothing is left on it."));
                continue;
            }

            if (marked)
            {
                var inside = CharsInsideAreas(text, plan.AreasOn(page));
                if (inside.Count > 0)
                {
                    findings.Add(new RedactionFinding(
                        RedactionFindingKind.CharInsideArea,
                        page,
                        string.Create(CultureInfo.InvariantCulture, $"{inside.Count} character(s) of the result lie inside a marked area, the first at x {inside.X:0.#} pt and y {inside.Y:0.#} pt from the top left.")));
                }
            }

            if (texts.Count > 0)
            {
                var counts = new int[texts.Count];
                TextMatcher.CountNormalized(text.Chars, matchCase: true, needles, counts);
                for (var i = 0; i < counts.Length; i++)
                {
                    if (counts[i] > 0)
                    {
                        texts[i].Occurrences += counts[i];
                        texts[i].FirstPage = texts[i].FirstPage < 0 ? page : texts[i].FirstPage;
                    }
                }
            }
        }
    }

    private static void CheckTextRule(List<CheckedText> texts, List<RedactionFinding> findings)
    {
        foreach (var text in texts)
        {
            var allowed = text.Everywhere ? 0 : Math.Max(0, text.Before - text.Places);
            if (text.Occurrences > allowed)
            {
                findings.Add(new RedactionFinding(
                    RedactionFindingKind.TextStillPresent,
                    text.FirstPage,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Removed text #{text.Index + 1} ({text.Text.Length} characters) still occurs {text.Occurrences} time(s) in the page text of the result; at most {allowed} would be expected. First on page {text.FirstPage + 1}.")));
            }
        }
    }

    private static async Task CheckRawAsync(Stream file, List<CheckedText> texts, RawScanLimits limits, List<RedactionFinding> findings, NotCheckedLog notChecked, CancellationToken cancellationToken)
    {
        var searched = texts.Where(static t => t.Text.Length >= RedactionLimits.MinRawCheckLength).ToList();
        var short1 = texts.Count - searched.Count;
        if (short1 > 0)
        {
            notChecked.Add(
                RedactionNotCheckedKind.RawShortTexts,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Raw bytes: {short1} removed text(s) are shorter than {RedactionLimits.MinRawCheckLength} characters and were not searched in the file bytes (they would match by chance); only the page text covers them."),
                short1,
                RedactionLimits.MinRawCheckLength);
        }

        if (searched.Count == 0)
        {
            if (texts.Count == 0)
            {
                notChecked.Add(RedactionNotCheckedKind.RawNothingRemoved, "Raw bytes: no text was removed, so there was nothing to search for in the file bytes.");
            }

            return;
        }

        var terms = searched.Select(static t => t.Text).ToArray();
        var result = await Task.Run(() => RawContentScanner.Scan(file, terms, limits, cancellationToken), cancellationToken).ConfigureAwait(false);
        for (var i = 0; i < searched.Count; i++)
        {
            var text = searched[i];
            var raw = result.Counts[i];
            var allowed = text.Everywhere ? 0 : text.Occurrences;
            if (raw > allowed)
            {
                findings.Add(new RedactionFinding(
                    RedactionFindingKind.RawBytesStillPresent,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Removed text #{text.Index + 1} ({text.Text.Length} characters) is still in the bytes of the file {raw} time(s) (in an old revision, metadata, an attachment, a pattern or a stream); the page text accounts for {allowed}."),
                    [.. text.Pages]));
            }
        }

        var remaining = searched.Where(static t => !t.Everywhere && t.Occurrences > 0).ToList();
        if (remaining.Count > 0)
        {
            var total = remaining.Sum(static t => t.Occurrences);
            notChecked.Add(
                RedactionNotCheckedKind.RawStillOccur,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Raw bytes: {remaining.Count} removed text(s) that are not marked everywhere still occur in the page text, {total} time(s) in all; the file bytes cannot tell a leaked copy from these legitimate ones (a copy in a font with its own code mapping, split by a hyphen or a ligature, is not found in the bytes), so up to {total} leaked copies could hide in the file."),
                remaining.Count,
                total);
        }

        foreach (var note in result.Notes.Items)
        {
            if (note.CouldHideText)
            {
                findings.Add(new RedactionFinding(
                    RedactionFindingKind.UnreadableStream,
                    null,
                    string.Create(CultureInfo.InvariantCulture, $"{note.Kind}: {note.Count} stream(s) could not be read completely, the first at byte {note.FirstOffset}; text may hide in them ({DetailOf(note)}).")));
            }
        }

        foreach (var note in result.Notes.Items)
        {
            notChecked.Add(NoteKind(note.Kind), RawScanNotes.Describe(note), note.Count, 0, note.Detail, note.FirstOffset);
        }
    }

    private static RedactionNotCheckedKind NoteKind(RawScanNoteKind kind) => kind switch
    {
        RawScanNoteKind.UnsupportedFilter => RedactionNotCheckedKind.RawFilter,
        RawScanNoteKind.ImageData => RedactionNotCheckedKind.RawImageStreams,
        RawScanNoteKind.UnreadableStream => RedactionNotCheckedKind.RawUnreadableStream,
        RawScanNoteKind.StreamTooLarge => RedactionNotCheckedKind.RawStreamTooLarge,
        RawScanNoteKind.TotalLimitReached => RedactionNotCheckedKind.RawTotalLimit,
        RawScanNoteKind.Predictor => RedactionNotCheckedKind.RawPredictor,
        RawScanNoteKind.Encrypted => RedactionNotCheckedKind.RawEncrypted,
        _ => RedactionNotCheckedKind.Other,
    };

    private static string DetailOf(RawScanNote note) => note.Detail.Length == 0 ? "no detail" : note.Detail;

    private static async Task CheckPixelsAsync(PdfDocument reopened, RedactionPlan plan, RedactionFill fill, List<RedactionFinding> findings, NotCheckedLog notChecked, CancellationToken cancellationToken)
    {
        var expected = fill == RedactionFill.White ? (byte)255 : (byte)0;
        var seen = new HashSet<(int, PageRect)>();
        var checkedAreas = 0;
        var skipped = 0;
        foreach (var mark in plan.Marks)
        {
            if (mark.Page >= reopened.PageCount)
            {
                continue;
            }

            foreach (var area in mark.Areas)
            {
                if (!seen.Add((mark.Page, area)))
                {
                    continue;
                }

                if (checkedAreas >= MaxPixelAreas)
                {
                    skipped++;
                    continue;
                }

                checkedAreas++;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var images = await reopened.GetImageAreaPixelsAsync(mark.Page, area, cancellationToken).ConfigureAwait(false);
                    foreach (var image in images)
                    {
                        CheckImage(mark.Page, image, expected, findings);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or RedactionException)
                {
                    findings.Add(new RedactionFinding(
                        RedactionFindingKind.ImagePixelNotOverwritten,
                        mark.Page,
                        "The images under a marked area could not be read back, so it cannot be shown that their pixels were overwritten."));
                }
            }
        }

        if (skipped > 0)
        {
            notChecked.Add(RedactionNotCheckedKind.PixelsBeyond, string.Create(CultureInfo.InvariantCulture, $"Pixels: {skipped} marked area(s) beyond the first {MaxPixelAreas} were not read back."), skipped, MaxPixelAreas);
        }

        notChecked.Add(RedactionNotCheckedKind.PixelsOnlyImages, "Pixels: only images that touch a marked area are read back; a color or shading that is not an image is covered by the render check of the apply step, not here.");
    }

    private static void CheckImage(int page, ImageAreaPixels image, byte expected, List<RedactionFinding> findings)
    {
        if (image.Width <= 0 || image.Height <= 0 || image.Bgra.Length < image.Width * image.Height * 4)
        {
            findings.Add(new RedactionFinding(
                RedactionFindingKind.ImagePixelNotOverwritten,
                page,
                "An image under a marked area could not be read back, so it cannot be shown that its pixels were overwritten."));
            return;
        }

        var wrong = 0;
        var first = -1;
        var length = image.Width * image.Height * 4;
        for (var i = 0; i < length; i += 4)
        {
            if (image.Bgra[i] != expected || image.Bgra[i + 1] != expected || image.Bgra[i + 2] != expected || image.Bgra[i + 3] != 255)
            {
                wrong++;
                if (first < 0)
                {
                    first = i / 4;
                }
            }
        }

        if (wrong > 0)
        {
            findings.Add(new RedactionFinding(
                RedactionFindingKind.ImagePixelNotOverwritten,
                page,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{wrong} of {image.Width * image.Height} pixels of an image under a marked area are not the fill colour; the first is in column {first % image.Width} of row {first / image.Width}.")));
        }
    }

    private static (int Count, double X, double Y) CharsInsideAreas(PageText text, IReadOnlyList<PageRect> areas)
    {
        if (areas.Count == 0 || text.Chars.Count == 0)
        {
            return default;
        }

        var grid = new AreaGrid(areas, text.Width, text.Height);
        var count = 0;
        double firstX = 0;
        double firstY = 0;
        foreach (var c in text.Chars)
        {
            if (c.IsGenerated || (Rune.IsValid(c.CodePoint) && Rune.IsWhiteSpace(new Rune(c.CodePoint))))
            {
                continue;
            }

            var box = c.Box;
            if (!double.IsFinite(box.Left) || !double.IsFinite(box.Top) || !double.IsFinite(box.Right) || !double.IsFinite(box.Bottom))
            {
                continue;
            }

            if (grid.Hits(box))
            {
                if (count == 0)
                {
                    firstX = box.Left;
                    firstY = box.Top;
                }

                count++;
            }
        }

        return (count, firstX, firstY);
    }

    private sealed class CheckedText(string text, string needle)
    {
        public string Text { get; } = text;

        public string Needle { get; } = needle;

        public int Index { get; set; }

        public bool Everywhere { get; set; }

        public SortedSet<int> Pages { get; } = [];

        public int Places { get; set; }

        public int Before { get; set; }

        public int Occurrences { get; set; }

        public int FirstPage { get; set; } = -1;
    }

    /// <summary>The rectangles of a page in a coarse grid, so that a page with thousands of areas and characters is not compared pairwise.</summary>
    private sealed class AreaGrid
    {
        private const int Cells = 64;
        private const int WideCells = 256;

        private readonly IReadOnlyList<PageRect> areas;
        private readonly double width;
        private readonly double height;
        private readonly List<int>?[] cells = new List<int>?[Cells * Cells];
        private readonly List<int> wide = [];

        public AreaGrid(IReadOnlyList<PageRect> areas, double width, double height)
        {
            this.areas = areas;
            this.width = double.IsFinite(width) && width > 0 ? width : 1;
            this.height = double.IsFinite(height) && height > 0 ? height : 1;
            for (var i = 0; i < areas.Count; i++)
            {
                var (x0, y0, x1, y1) = Range(areas[i]);
                if ((long)(x1 - x0 + 1) * (y1 - y0 + 1) > WideCells)
                {
                    wide.Add(i);
                    continue;
                }

                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                    {
                        (cells[(y * Cells) + x] ??= []).Add(i);
                    }
                }
            }
        }

        public bool Hits(PageRect box)
        {
            var (x0, y0, x1, y1) = Range(box);
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    if (cells[(y * Cells) + x] is { } list)
                    {
                        foreach (var index in list)
                        {
                            if (Overlaps(box, areas[index]))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            foreach (var index in wide)
            {
                if (Overlaps(box, areas[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool Overlaps(PageRect box, PageRect area)
        {
            var overlapX = Math.Min(box.Right, area.Right) - Math.Max(box.Left, area.Left);
            var overlapY = Math.Min(box.Bottom, area.Bottom) - Math.Max(box.Top, area.Top);
            if (overlapX > PositionTolerance && overlapY > PositionTolerance)
            {
                return true;
            }

            // A box without size (a combining mark) counts when its centre is inside the area.
            var centreX = (box.Left + box.Right) / 2;
            var centreY = (box.Top + box.Bottom) / 2;
            return centreX > area.Left && centreX < area.Right && centreY > area.Top && centreY < area.Bottom;
        }

        private (int X0, int Y0, int X1, int Y1) Range(PageRect rect) =>
            (Cell(rect.Left, width), Cell(rect.Top, height), Cell(rect.Right, width), Cell(rect.Bottom, height));

        private static int Cell(double value, double size)
        {
            var cell = Math.Floor(value / size * Cells);
            return double.IsNaN(cell) ? 0 : (int)Math.Clamp(cell, 0, Cells - 1);
        }
    }
}
