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
/// <item>Pixels: the image pixels under every marked area are the colour of the area. Where the plan pixelates or blurs image areas
/// (<see cref="RedactionPlan.ImageTreatment"/>), an area is accepted when it is exactly the fill colour (the area fell back to the solid
/// fill; the file does not say whether it did) or exactly the block result that <see cref="ExpectedImageArea"/> (block means of the
/// ORIGINAL pixels, worked out by <see cref="ImageBlocks"/>) gives; the original pixels, other blocks, another strength or treatment
/// and a half-treated area are findings.</item>
/// <item>Pages turned into images (<see cref="RedactionExpectation.RasterPages"/>): the page of the result has exactly one image object and
/// nothing else (no text, path, other object or annotation; links and form fields are annotations), or the check fails. A page the
/// reader cannot count fails as well. What stays in the image outside the marked areas is not checked
/// (<see cref="RedactionNotCheckedKind.RasterOutsideAreas"/>).</item>
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
    /// How far a colour channel (blue, green or red; never the alpha byte, which must be 255) of a BLURRED image area may be from the value worked out from the block means of the original. The two
    /// implementations (the one that writes and the one that expects) agree bit for bit on every test vector, so this is slack against a
    /// rounding of the reader, not part of the rule; a pixelated area has to be exact, and so has the fill colour.
    /// </summary>
    public const int BlurTolerance = 1;

    /// <summary>
    /// The overlap, in points, that a character of the result may have with a marked area without counting as inside: the page text
    /// rounds boxes to 1/16 point, and the engine removes a character as soon as its box touches the area.
    /// </summary>
    public const double PositionTolerance = 0.1;

    /// <summary>
    /// How far, in points, a path on a page that became an image may reach out of the mark rectangle that holds it. The layout keeps the
    /// text at least <see cref="RedactionOverlayLayout.Inset"/> inside; this is only slack for the single-precision numbers of the content
    /// stream (a float is accurate to about 0.001 pt at page size), so a shape that leaves the area by more than 0.05 pt fails the check.
    /// </summary>
    public const double PathSlack = 0.05;

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

        var rasterPages = expectation.RasterPages is { Count: > 0 } raster ? raster.Distinct().Order().ToArray() : [];
        await CheckPagesAsync(reopened, plan, rasterPages, texts, findings, cancellationToken).ConfigureAwait(false);
        await CheckRasterPagesAsync(reopened, plan, rasterPages, findings, notChecked, cancellationToken).ConfigureAwait(false);
        CheckTextRule(texts, findings);
        await CheckRawAsync(file, texts, limits, findings, notChecked, cancellationToken).ConfigureAwait(false);
        await CheckPixelsAsync(reopened, expectation, findings, notChecked, cancellationToken).ConfigureAwait(false);

        // The overlay text is drawn as shapes: nothing reads it back. A page redacted in place is compared with the original outside the
        // marked areas (a shape outside would show there), a page that became an image only has its shapes checked to lie inside a mark.
        if (plan.Marks.Any(static m => m.OverlayText is not null))
        {
            notChecked.Add(RedactionNotCheckedKind.OverlayTextNotRead, "Overlay text: the text drawn on the areas is not read back from the written file; whether it reads as intended is not checked.");
        }

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

        // A mark on a page that became an image removes no text from the result: that page has none (its occurrences are not in
        // OccurrencesBefore either, see RemovedText.FromOriginalAsync), so the mark is no place that the count may subtract.
        var imagePages = expectation.RasterPages is { Count: > 0 } raster ? raster.ToHashSet() : [];

        void Add(string? text, bool everywhere, int before, bool isPlace, int page, string? rawForm = null)
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

            // A form is shorter than its text and has the minimum length, so its text is always one of the texts searched in the bytes
            // (CheckRawAsync adds the two raw counts of a text and has no rule for a form alone).
            if (entry.RawForm is null && rawForm is { Length: >= RedactionLimits.MinRawCheckLength } && rawForm.Length < text.Length)
            {
                // The form without separators is only the form of a text that the page shows in groups; it is looked up in the page
                // text as well, so a remaining copy that the page shows without spaces is not mistaken for a leak.
                var rawNeedle = TextMatcher.Normalize(rawForm, matchCase: true);
                if (rawNeedle.Length > 0 && !rawForm.Contains('�', StringComparison.Ordinal) && !rawForm.Contains('\0', StringComparison.Ordinal))
                {
                    entry.RawForm = rawForm;
                    entry.RawNeedle = rawNeedle;
                }
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
            Add(removed.Text, removed.Everywhere, removed.OccurrencesBefore, isPlace: !imagePages.Contains(removed.Page), removed.Page, removed.RawForm);
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

    private static async Task CheckPagesAsync(PdfDocument reopened, RedactionPlan plan, int[] rasterPages, List<CheckedText> texts, List<RedactionFinding> findings, CancellationToken cancellationToken)
    {
        var pageCount = reopened.PageCount;
        foreach (var page in plan.Pages.Where(p => p >= pageCount).Order())
        {
            findings.Add(new RedactionFinding(RedactionFindingKind.PageChangedOutsideAreas, page, "The result has fewer pages than the plan expects."));
        }

        // The needles of the texts, then those of the forms without separators (empty where a text has none: an empty needle counts nothing).
        var needles = texts.Select(static t => t.Needle).Concat(texts.Select(static t => t.RawNeedle ?? string.Empty)).ToArray();
        for (var page = 0; page < pageCount; page++)
        {
            // A page turned into an image has no text, so the position check of characters does not apply to it: a character on it is a
            // finding of CheckRasterPagesAsync (a text object), not "a character inside an area".
            var marked = plan.Pages.Contains(page) && Array.BinarySearch(rasterPages, page) < 0;
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
                var counts = new int[needles.Length];
                TextMatcher.CountNormalized(text.Chars, matchCase: true, needles, counts);
                for (var i = 0; i < texts.Count; i++)
                {
                    texts[i].RawFormOccurrences += counts[texts.Count + i];
                    if (counts[i] > 0)
                    {
                        texts[i].Occurrences += counts[i];
                        texts[i].FirstPage = texts[i].FirstPage < 0 ? page : texts[i].FirstPage;
                    }
                }
            }
        }
    }

    /// <summary>
    /// A page that became an image must be exactly that in the written file: one image object, no text, no other object, no annotation
    /// (links and form fields are annotations), and no path except the shapes of the overlay text, which are allowed only INSIDE a mark
    /// rectangle of the page (<see cref="PathSlack"/> of slack). The objects and the place of every path are read from the result, never
    /// taken from the plan. A page that cannot be counted (too many objects, unreadable) fails, and so does a path whose place is not
    /// known: a check that could not look never passes.
    /// </summary>
    private static async Task CheckRasterPagesAsync(PdfDocument reopened, RedactionPlan plan, int[] rasterPages, List<RedactionFinding> findings, NotCheckedLog notChecked, CancellationToken cancellationToken)
    {
        if (rasterPages.Length == 0)
        {
            return;
        }

        foreach (var page in rasterPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page < 0 || page >= reopened.PageCount)
            {
                findings.Add(new RedactionFinding(RedactionFindingKind.RasterPageInvalid, page, "The result has fewer pages than the plan expects, so a page that should be an image is missing."));
                continue;
            }

            PageObjectSummary summary;
            try
            {
                summary = await reopened.GetPageObjectSummaryAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
            {
                findings.Add(new RedactionFinding(RedactionFindingKind.RasterPageInvalid, page, "The objects of this page could not be counted (the page could not be read, or it has more objects than the check reads), so it cannot be shown that it is only an image."));
                continue;
            }

            var problems = new List<string>();
            if (summary.Images != 1)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{summary.Images} image object(s), exactly 1 is expected"));
            }

            if (summary.Texts != 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{summary.Texts} text object(s)"));
            }

            if (summary.Paths != 0)
            {
                if (summary.PathBounds is not { } bounds || bounds.Count != summary.Paths)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{summary.Paths} path object(s) whose place could not be read"));
                }
                else if (PathsOutside(bounds, plan.AreasOn(page)) is var outside and > 0)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{outside} path object(s) outside the marked areas"));
                }
            }

            if (summary.Others != 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{summary.Others} other object(s) (forms, shadings)"));
            }

            if (summary.Annotations != 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"{summary.Annotations} annotation(s) (links and form fields count as annotations)"));
            }

            if (problems.Count > 0)
            {
                findings.Add(new RedactionFinding(RedactionFindingKind.RasterPageInvalid, page, "The page that became an image is not only one image: " + string.Join("; ", problems) + "."));
            }
        }

        notChecked.Add(
            RedactionNotCheckedKind.RasterOutsideAreas,
            string.Create(
                CultureInfo.InvariantCulture,
                $"Pages turned into images ({rasterPages.Length} page(s)): a visible occurrence of a removed text outside the marked areas stays in the image and cannot be found without OCR; only the structure of the page (one image, no text, no annotation, link or form field, no path outside the marked areas) and the marked areas are checked."),
            rasterPages.Length);
    }

    /// <summary>How many of the paths lie (even partly) outside every one of the rectangles.</summary>
    private static int PathsOutside(IReadOnlyList<PageRect> bounds, IReadOnlyList<PageRect> areas)
    {
        var outside = 0;
        foreach (var path in bounds)
        {
            var inside = false;
            foreach (var area in areas)
            {
                if (path.Left >= area.Left - PathSlack && path.Right <= area.Right + PathSlack && path.Top >= area.Top - PathSlack && path.Bottom <= area.Bottom + PathSlack)
                {
                    inside = true;
                    break;
                }
            }

            if (!inside)
            {
                outside++;
            }
        }

        return outside;
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

        // The forms without separators of pattern hits are searched next to the texts (a text under the minimum length has none that
        // is long enough, so this adds nothing for it).
        var withRawForm = texts.Where(static t => t.RawForm is not null).ToList();
        if (searched.Count == 0 && withRawForm.Count == 0)
        {
            if (texts.Count == 0)
            {
                notChecked.Add(RedactionNotCheckedKind.RawNothingRemoved, "Raw bytes: no text was removed, so there was nothing to search for in the file bytes.");
            }

            return;
        }

        var terms = searched.Select(static t => t.Text).Concat(withRawForm.Select(static t => t.RawForm!)).ToArray();
        var result = await Task.Run(() => RawContentScanner.Scan(file, terms, limits, cancellationToken), cancellationToken).ConfigureAwait(false);

        // One rule per text. For a word the file bytes may hold it at most as often as the page text shows it. For the text of a pattern
        // hit that is laid out in groups the form without separators counts with it: a copy that stays on the page is in the bytes once,
        // either with its spaces (the text itself) or without them (the spacing between the groups is not in the content stream, or the
        // page shows the form), never once in each writing. So the two raw counts are added and measured against the page text, which
        // is as often as the text occurs there plus as often as the form occurs there. A copy that the scanner could not recognise
        // (split over a line break) is one of those and never fails the check; a copy in an attachment, metadata or an old revision is
        // not and does. Evaluating the two writings separately would let one legitimate copy hide a leak in the other writing.
        var rawFormCounts = new Dictionary<CheckedText, long>();
        for (var i = 0; i < withRawForm.Count; i++)
        {
            rawFormCounts[withRawForm[i]] = result.Counts[searched.Count + i];
        }

        for (var i = 0; i < searched.Count; i++)
        {
            var text = searched[i];
            var raw = result.Counts[i];
            var hasForm = rawFormCounts.TryGetValue(text, out var rawForm);
            var allowed = text.Everywhere ? 0 : text.Occurrences + (hasForm ? text.RawFormOccurrences : 0);
            if (raw + rawForm > allowed)
            {
                findings.Add(new RedactionFinding(
                    RedactionFindingKind.RawBytesStillPresent,
                    null,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Removed text #{text.Index + 1} ({text.Text.Length} characters) is still in the bytes of the file {raw + rawForm} time(s) (in an old revision, metadata, an attachment, a pattern or a stream{(hasForm ? ", also without the separators between its groups" : string.Empty)}); the page text accounts for {allowed}."),
                    [.. text.Pages]));
            }
        }

        // The copies of the form without separators that the page shows count with the text itself: that many leaks could hide behind them.
        var remaining = searched.Where(static t => !t.Everywhere && (t.Occurrences > 0 || t.RawFormOccurrences > 0)).ToList();
        if (remaining.Count > 0)
        {
            var total = remaining.Sum(static t => t.Occurrences + t.RawFormOccurrences);
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

    private static async Task CheckPixelsAsync(PdfDocument reopened, RedactionExpectation expectation, List<RedactionFinding> findings, NotCheckedLog notChecked, CancellationToken cancellationToken)
    {
        var plan = expectation.Plan;
        var fill = expectation.Fill;
        var treatment = plan.ImageTreatment;
        var expectedByArea = new Dictionary<(int, PageRect), List<ExpectedImageArea>>();
        if (treatment != RedactionImageTreatment.Solid && expectation.ImageAreas is { } expectedAreas)
        {
            foreach (var expected in expectedAreas)
            {
                if (!expectedByArea.TryGetValue((expected.Page, expected.Area), out var list))
                {
                    list = [];
                    expectedByArea[(expected.Page, expected.Area)] = list;
                }

                list.Add(expected);
            }
        }

        var seen = new HashSet<(int, PageRect)>();
        var checkedAreas = 0;
        var skipped = 0;
        var tally = new ImageTally();
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
                    var candidates = expectedByArea.TryGetValue((mark.Page, area), out var found) ? found : [];
                    foreach (var image in images)
                    {
                        CheckImage(mark.Page, image, fill, plan, candidates, findings, tally);
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

        notChecked.Add(RedactionNotCheckedKind.PixelsOnlyImages, "Pixels: the colour of the fill is read back only in images that touch a marked area. For an area without an image (text or vector content only) the removal of the content is checked, but the colour of the drawn rectangle is not read back from the written file.");
        if (treatment != RedactionImageTreatment.Solid)
        {
            notChecked.Add(
                RedactionNotCheckedKind.ImageTextLegible,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Image areas: {tally.Treated} image part(s) read back were pixelated or blurred and {tally.Solid} filled solid (an image under several marks counts once for each, pages turned into images count too, and the analysis counts areas, so the numbers can differ). Their pixels were compared with the block averages of the original; whether text in a pixelated or blurred image can still be read is not checked, and Pagewing does not recognise text in images. For text in an image choose the solid fill."),
                tally.Treated,
                tally.Solid);
        }
    }

    /// <summary>How many image areas the read-back found treated and filled solid.</summary>
    private sealed class ImageTally
    {
        public int Treated { get; set; }

        public int Solid { get; set; }
    }

    private static void CheckImage(int page, ImageAreaPixels image, RedactionFill fill, RedactionPlan plan, IReadOnlyList<ExpectedImageArea> candidates, List<RedactionFinding> findings, ImageTally tally)
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
            // Exact: blue, green, red and an opaque alpha. Another colour, even a near one, fails.
            if (image.Bgra[i] != fill.Blue || image.Bgra[i + 1] != fill.Green || image.Bgra[i + 2] != fill.Red || image.Bgra[i + 3] != 255)
            {
                wrong++;
                if (first < 0)
                {
                    first = i / 4;
                }
            }
        }

        if (wrong == 0)
        {
            tally.Solid++;
            return;
        }

        // The area is not the fill colour. Where the plan pixelates or blurs it may still be the block result of one of the images the
        // original held under this area in this size; a size no original image had under the area (an area that was too small to treat
        // has no candidate either) is judged by the fill colour alone.
        var treatment = plan.ImageTreatment;
        List<ExpectedImageArea> sameSize = treatment == RedactionImageTreatment.Solid ? [] : candidates.Where(c => c.Width == image.Width && c.Height == image.Height).ToList();
        if (sameSize.Count == 0)
        {
            findings.Add(new RedactionFinding(
                RedactionFindingKind.ImagePixelNotOverwritten,
                page,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{wrong} of {image.Width * image.Height} pixels of an image under a marked area are not the fill colour; the first is in column {first % image.Width} of row {first / image.Width}.")));
            return;
        }

        var tolerance = treatment == RedactionImageTreatment.Blur ? BlurTolerance : 0;
        var grid = ImageBlocks.Plan(image.Width, image.Height, plan.ImageStrength);
        var nearest = int.MaxValue;
        if (grid is not null)
        {
            foreach (var candidate in sameSize)
            {
                byte[] expected;
                try
                {
                    expected = ImageBlocks.ExpectedFromMeans(candidate.BlockMeans, grid, treatment);
                }
                catch (ArgumentException)
                {
                    // Means that do not fit the grid are no expectation: this candidate cannot accept anything.
                    continue;
                }

                var different = 0;
                for (var i = 0; i < length; i++)
                {
                    // The alpha byte is exact for every treatment (a treated area is opaque); only the colour of a blur has the slack.
                    if (Math.Abs(image.Bgra[i] - expected[i]) > ((i & 3) == 3 ? 0 : tolerance))
                    {
                        different++;
                    }
                }

                if (different == 0)
                {
                    tally.Treated++;
                    return;
                }

                nearest = Math.Min(nearest, different);
            }
        }

        findings.Add(new RedactionFinding(
            RedactionFindingKind.ImageTreatmentWrong,
            page,
            nearest == int.MaxValue
                ? "An image area under a mark is not the fill colour and no block result could be worked out for it from the original, so it is not what pixelating or blurring writes."
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"An image area under a mark is neither the fill colour nor the block result worked out from the original: at best {nearest} of its {length} colour values differ from it.")));
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

        /// <summary>Gets or sets the form without separators for the raw-bytes check, or <see langword="null"/>.</summary>
        public string? RawForm { get; set; }

        /// <summary>Gets or sets the normalized <see cref="RawForm"/> for the page text.</summary>
        public string? RawNeedle { get; set; }

        /// <summary>Gets or sets how often <see cref="RawNeedle"/> occurs in the page text of the result.</summary>
        public int RawFormOccurrences { get; set; }

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
